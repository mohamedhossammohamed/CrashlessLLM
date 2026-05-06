#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CrashlessLLM.Interop;

// ============================================================================
// Exception Mapping & Domain Safety
// ============================================================================

/// <summary>
/// Thrown when predictive allocation gating determines that hardware RAM is
/// insufficient for the model weights, KV cache, and required safety margin.
/// </summary>
public sealed class InsufficientHardwareMemoryException : Exception
{
    public InsufficientHardwareMemoryException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when the unmanaged C-ABI returns a deterministic non-memory failure.
/// </summary>
public sealed class NativeInferenceException : Exception
{
    public NativeInferenceException(string message, int errorCode = 0) : base(message)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}

// ============================================================================
// Safe Handle Implementation
// ============================================================================

/// <summary>
/// Provides catastrophic fallback finalization for the unmanaged LLM context.
/// The public API still follows a strict IDisposable lifecycle for immediate
/// cleanup instead of relying on finalization latency.
/// </summary>
public sealed class SafeLlmContextHandle : SafeHandle
{
    public SafeLlmContextHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    internal SafeLlmContextHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        if (!IsInvalid)
        {
            NativeMethods.crashless_v1_free_session_secure(handle);
            handle = IntPtr.Zero;
        }

        return true;
    }
}

// ============================================================================
// Native Methods & C-ABI Boundary (AOT Compatible)
// ============================================================================

internal static partial class NativeMethods
{
    private const string LibraryName = "crashless_core";

    [LibraryImport(LibraryName)]
    internal static partial int crashless_get_api_version();

    [LibraryImport(LibraryName)]
    internal static partial int crashless_v1_create_config(int gpu_layers, int threads, out IntPtr out_config);

    [LibraryImport(LibraryName)]
    internal static partial void crashless_v1_free_config(IntPtr config);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int crashless_v1_load_model_safe(
        string model_path,
        IntPtr config,
        out IntPtr out_model_ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TokenCallback(IntPtr token_utf8, [MarshalAs(UnmanagedType.I1)] bool is_done);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int crashless_v1_generate_async(
        SafeLlmContextHandle model_ctx,
        string prompt,
        TokenCallback callback,
        IntPtr atomic_cancel_flag);

    [LibraryImport(LibraryName)]
    internal static partial void crashless_v1_cancel_generation(SafeLlmContextHandle model_ctx);

    [LibraryImport(LibraryName)]
    internal static partial void crashless_v1_free_session_secure(IntPtr model_ctx);

    internal const int Success = 0;
    internal const int ErrInvalidPointer = -1;
    internal const int ErrInsufficientMemoryPredicted = -100;
    internal const int ErrModelLoadFailed = -101;
    internal const int ErrInternalException = -102;
    internal const int ErrThreadSpawnFailed = -103;
    internal const int ErrGenerationInProgress = -104;
}

// ============================================================================
// Zero-Config Public Entry Point
// ============================================================================

/// <summary>
/// Public static facade delivering the Layer A zero-config illusion.
/// Abstracts FFI details, native handles, and thread topology behind one call.
/// </summary>
public static class LLM
{
    public static LLMSession LoadSafe(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (NativeMethods.crashless_get_api_version() != 1)
        {
            throw new InvalidOperationException("Incompatible crashless_core API version.");
        }

        // Avoids logical SMT overcommit and keeps UI runtimes responsive under load.
        int optimalThreads = Math.Max(1, Environment.ProcessorCount / 2);

        IntPtr configPtr = IntPtr.Zero;
        int configResult = NativeMethods.crashless_v1_create_config(99, optimalThreads, out configPtr);
        if (configResult != NativeMethods.Success || configPtr == IntPtr.Zero)
        {
            throw ToNativeException("Failed to initialize core configuration parameters.", configResult);
        }

        try
        {
            int loadResult = NativeMethods.crashless_v1_load_model_safe(
                path,
                configPtr,
                out IntPtr modelContextPtr);

            if (loadResult == NativeMethods.ErrInsufficientMemoryPredicted)
            {
                throw new InsufficientHardwareMemoryException(
                    "Predictive allocation gating prevented a catastrophic Out-Of-Memory failure. " +
                    "The hardware lacks sufficient physical RAM for the model, KV cache, and 30% safety overhead.");
            }

            if (loadResult != NativeMethods.Success || modelContextPtr == IntPtr.Zero)
            {
                throw ToNativeException("Failed to natively load model.", loadResult);
            }

            return new LLMSession(new SafeLlmContextHandle(modelContextPtr));
        }
        finally
        {
            NativeMethods.crashless_v1_free_config(configPtr);
        }
    }

    private static NativeInferenceException ToNativeException(string message, int errorCode)
    {
        string detail = errorCode switch
        {
            NativeMethods.ErrInvalidPointer => "The managed bridge passed an invalid native pointer.",
            NativeMethods.ErrModelLoadFailed => "llama.cpp rejected the model or failed to allocate its execution context.",
            NativeMethods.ErrInternalException => "The native safety core trapped an internal exception.",
            NativeMethods.ErrThreadSpawnFailed => "The native safety core failed to spawn a worker thread.",
            NativeMethods.ErrGenerationInProgress => "A generation is already active for this session.",
            _ => $"Native error code: {errorCode}."
        };

        return new NativeInferenceException($"{message} {detail}", errorCode);
    }
}

// ============================================================================
// Managed Session, Backpressure & Streaming Logic
// ============================================================================

/// <summary>
/// Represents an active, GC-safe LLM embedding session. Enforces immediate
/// deterministic cleanup via IDisposable and streams without blocking UI threads.
/// </summary>
public sealed class LLMSession : IDisposable
{
    private static readonly Encoding StrictUtf8Encoding = Encoding.GetEncoding(
        "UTF-8",
        EncoderFallback.ExceptionFallback,
        DecoderFallback.ExceptionFallback);

    private static readonly Encoding LenientUtf8Encoding = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    private readonly SafeLlmContextHandle _handle;
    private readonly SemaphoreSlim _streamGate = new(1, 1);
    private readonly object _callbackLock = new();
    private readonly List<byte> _utf8Accumulator = new(capacity: 128);
    private readonly Decoder _utf8Decoder = StrictUtf8Encoding.GetDecoder();

    // GC pinning: the delegate must outlive unmanaged asynchronous execution.
    private NativeMethods.TokenCallback? _pinnedCallback;

    private Channel<string>? _channel;
    private TaskCompletionSource? _generationDone;
    private int _isDisposed;

    internal LLMSession(SafeLlmContextHandle handle)
    {
        _handle = handle;
        _pinnedCallback = HandleNativeTokenCallback;
    }

    /// <summary>
    /// Initiates generation asynchronously and returns an Avalonia-safe token stream.
    /// Bounded channel backpressure synchronously halts the native worker when the
    /// UI falls more than 50 tokens behind.
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        string prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        VerifyNotDisposed();

        await _streamGate.WaitAsync(cancellationToken);

        Channel<string>? channel = null;
        TaskCompletionSource? generationDone = null;
        bool nativeStarted = false;

        try
        {
            VerifyNotDisposed();

            var options = new BoundedChannelOptions(capacity: 50)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true
            };

            channel = Channel.CreateBounded<string>(options);
            generationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_callbackLock)
            {
                _utf8Accumulator.Clear();
                _utf8Decoder.Reset();
                _channel = channel;
                _generationDone = generationDone;
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((LLMSession)state!).CancelNativeGenerationNoThrow(),
                this);

            NativeMethods.TokenCallback callback = _pinnedCallback
                ?? throw new ObjectDisposedException(nameof(LLMSession));

            int startResult = NativeMethods.crashless_v1_generate_async(
                _handle,
                prompt,
                callback,
                IntPtr.Zero);

            if (startResult != NativeMethods.Success)
            {
                Exception exception = CreateGenerationException(startResult);
                channel.Writer.TryComplete(exception);
                generationDone.TrySetResult();
                throw exception;
            }

            nativeStarted = true;

            await foreach (string token in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return token;
            }
        }
        finally
        {
            if (channel is not null)
            {
                channel.Writer.TryComplete();
            }

            if (nativeStarted)
            {
                CancelNativeGenerationNoThrow();

                if (generationDone is not null)
                {
                    try
                    {
                        await generationDone.Task;
                    }
                    catch
                    {
                        // The stream path reports channel exceptions to the enumerator already.
                    }
                }
            }

            lock (_callbackLock)
            {
                if (ReferenceEquals(_channel, channel))
                {
                    _channel = null;
                    _generationDone = null;
                    _utf8Accumulator.Clear();
                    _utf8Decoder.Reset();
                }
            }

            _streamGate.Release();
        }
    }

    private static Exception CreateGenerationException(int errorCode)
    {
        string message = errorCode switch
        {
            NativeMethods.ErrInvalidPointer => "Native generation failed because the session, prompt, or callback pointer was invalid.",
            NativeMethods.ErrThreadSpawnFailed => "Native generation failed because the worker thread could not be created.",
            NativeMethods.ErrGenerationInProgress => "Native generation failed because a generation is already active for this session.",
            NativeMethods.ErrInternalException => "Native generation failed because the safety core trapped an internal exception.",
            _ => $"Native generation failed to initialize. Code: {errorCode}."
        };

        return new NativeInferenceException(message, errorCode);
    }

    /// <summary>
    /// The unmanaged boundary callback. Executed on the native worker thread.
    /// It must never throw across the reverse P/Invoke boundary.
    /// </summary>
    private void HandleNativeTokenCallback(IntPtr tokenUtf8Ptr, bool isEnd)
    {
        Channel<string>? channel = null;
        TaskCompletionSource? generationDone = null;

        try
        {
            lock (_callbackLock)
            {
                channel = _channel;
                generationDone = _generationDone;

                if (channel is null)
                {
                    if (isEnd)
                    {
                        generationDone?.TrySetResult();
                    }
                    return;
                }

                if (tokenUtf8Ptr != IntPtr.Zero)
                {
                    ProcessNativeUtf8Pointer(tokenUtf8Ptr, channel);
                }

                if (isEnd)
                {
                    FlushAccumulator(channel);
                    channel.Writer.TryComplete();
                    generationDone?.TrySetResult();
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                channel?.Writer.TryComplete(ex);
            }
            catch
            {
            }

            generationDone?.TrySetResult();
            CancelNativeGenerationNoThrow();
        }
    }

    /// <summary>
    /// Reads native memory immediately and caches bytes to resolve fragmented
    /// multi-byte UTF-8 token boundaries.
    /// </summary>
    private unsafe void ProcessNativeUtf8Pointer(IntPtr ptr, Channel<string> channel)
    {
        byte* current = (byte*)ptr;
        while (*current != 0)
        {
            _utf8Accumulator.Add(*current);
            current++;
        }

        TryDecodeAccumulator(channel);
    }

    private void TryDecodeAccumulator(Channel<string> channel)
    {
        if (_utf8Accumulator.Count == 0)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = CollectionsMarshal.AsSpan(_utf8Accumulator);

        try
        {
            _utf8Decoder.Reset();
            int charCount = _utf8Decoder.GetCharCount(bytes, flush: true);
            if (charCount == 0)
            {
                _utf8Accumulator.Clear();
                return;
            }

            char[] rentedChars = ArrayPool<char>.Shared.Rent(charCount);
            try
            {
                _utf8Decoder.Reset();
                int charsWritten = _utf8Decoder.GetChars(bytes, rentedChars.AsSpan(0, charCount), flush: true);
                string validToken = new(rentedChars, 0, charsWritten);
                _utf8Accumulator.Clear();

                if (validToken.Length > 0)
                {
                    WriteTokenBlocking(channel, validToken);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rentedChars, clearArray: true);
            }
        }
        catch (DecoderFallbackException)
        {
            // Incomplete UTF-8 sequence: preserve bytes for the next token callback.
            _utf8Decoder.Reset();
        }
    }

    private void FlushAccumulator(Channel<string> channel)
    {
        if (_utf8Accumulator.Count == 0)
        {
            return;
        }

        string finalString = LenientUtf8Encoding.GetString(CollectionsMarshal.AsSpan(_utf8Accumulator));
        _utf8Accumulator.Clear();

        if (finalString.Length > 0)
        {
            WriteTokenBlocking(channel, finalString);
        }
    }

    private static void WriteTokenBlocking(Channel<string> channel, string token)
    {
        try
        {
            // Synchronously halts the native worker when the bounded channel is full.
            channel.Writer.WriteAsync(token).AsTask().GetAwaiter().GetResult();
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void CancelNativeGenerationNoThrow()
    {
        try
        {
            if (!_handle.IsClosed && !_handle.IsInvalid)
            {
                NativeMethods.crashless_v1_cancel_generation(_handle);
            }
        }
        catch
        {
        }
    }

    private void VerifyNotDisposed()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            throw new ObjectDisposedException(nameof(LLMSession));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        CancelNativeGenerationNoThrow();

        // Do not take _callbackLock here: the native callback may be synchronously
        // blocked on channel backpressure while holding it. Completing the channel
        // first unblocks the native worker and prevents dispose-time deadlocks.
        Volatile.Read(ref _channel)?.Writer.TryComplete();

        _handle.Dispose();
        _pinnedCallback = null;
        GC.SuppressFinalize(this);
    }
}
