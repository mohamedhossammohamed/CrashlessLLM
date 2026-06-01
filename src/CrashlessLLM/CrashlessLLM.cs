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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CrashlessLLM.Interop;

// ============================================================================
// Diagnostics & Options Types
// ============================================================================

/// <summary>
/// Telemetry from the native predictive allocation gate, exposed to explain
/// why a model load was rejected without attaching a native debugger.
/// </summary>
public sealed class LlmLoadDiagnostics
{
    public long ModelFileBytes { get; init; }
    public long EstimatedKvCacheBytes { get; init; }
    public long SafetyMarginBytes { get; init; }
    public long PredictedTotalBytes { get; init; }
    public long AvailableMemoryBytes { get; init; }
    public int NativeErrorCode { get; init; }

    public override string ToString()
    {
        return $"LlmLoadDiagnostics [ModelFile={ModelFileBytes:N0} B, " +
               $"EstimatedKvCache={EstimatedKvCacheBytes:N0} B, " +
               $"SafetyMargin={SafetyMarginBytes:N0} B, " +
               $"PredictedTotal={PredictedTotalBytes:N0} B, " +
               $"AvailableMemory={AvailableMemoryBytes:N0} B, " +
               $"NativeError={NativeErrorCode}]";
    }
}

/// <summary>
/// GPU backends compiled into the native crashless_core library.
/// </summary>
[Flags]
public enum GpuBackend
{
    None   = 0,
    Metal  = 1 << 0,
    Cuda   = 1 << 1,
    Vulkan = 1 << 2,
    Rocm   = 1 << 3,
    Sycl   = 1 << 4
}

/// <summary>
/// Model architecture metadata for accurate memory estimation.
/// </summary>
public sealed class ModelArchInfo
{
    public int NLayer { get; init; }
    public int NEmbd { get; init; }
    public int NEmbdK { get; init; }
    public int NEmbdV { get; init; }
    public int NHead { get; init; }
    public int NHeadKv { get; init; }
    public int NCtxTrain { get; init; }
    public long BytesPerTokenKv { get; init; }

    public override string ToString()
    {
        return $"ModelArchInfo [n_layer={NLayer}, n_embd={NEmbd}, n_head={NHead}, " +
               $"n_head_kv={NHeadKv}, kv_bytes/token={BytesPerTokenKv}]";
    }
}

/// <summary>
/// Sampling controls for token generation. Sentinel values (null for nullable,
/// or defaults) mean "use the native default."
/// </summary>
public sealed class SamplingOptions
{
    /// <summary>
    /// Temperature for token sampling. Default 1.0. Set to 0 for greedy decoding.
    /// </summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// Top-K sampling. Default 40. Set to 1 for top-1.
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Top-P (nucleus) sampling. Default 0.95.
    /// </summary>
    public float? TopP { get; init; }

    /// <summary>
    /// Min-P sampling. Default 0.05.
    /// </summary>
    public float? MinP { get; init; }

    /// <summary>
    /// Repeat penalty. Default 1.0 (disabled). Values > 1 penalize repetition.
    /// </summary>
    public float? RepeatPenalty { get; init; }

    /// <summary>
    /// Number of last tokens to consider for repeat penalty. Default 64.
    /// </summary>
    public int RepeatLastN { get; init; } = 64;

    /// <summary>
    /// RNG seed for reproducibility. 0 means random.
    /// </summary>
    public long Seed { get; init; }
}

/// <summary>
/// Safe, explicit configuration for model loading. The zero-config overload
/// <see cref="LLM.LoadSafe(string)"/> remains the default entry point.
/// </summary>
public sealed class LlmLoadOptions
{
    /// <summary>
    /// CPU thread count. If null, defaults to <c>Environment.ProcessorCount / 2</c>.
    /// </summary>
    public int? Threads { get; init; }

    /// <summary>
    /// Context size in tokens. If null, defaults to 4096.
    /// </summary>
    public int? ContextSize { get; init; }

    /// <summary>
    /// GPU offload layer count. If null, defaults to 99 (maximum).
    /// </summary>
    public int? GpuLayers { get; init; }

    /// <summary>
    /// Fractional safety margin applied to the predicted memory footprint.
    /// Default is 0.30 (30%). Values below 0 are clamped to 0 by the native core.
    /// </summary>
    public double MemorySafetyMargin { get; init; } = 0.30;

    /// <summary>
    /// Maximum number of tokens to generate per stream call.
    /// Default 512. Set to -1 for unlimited (up to context size).
    /// </summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// Sampling controls for token generation. Null means native defaults (temperature=1.0,
    /// top-k=40, top-p=0.95, min-p=0.05, greedy if temperature=0).
    /// </summary>
    public SamplingOptions? Sampling { get; init; }
}

// ============================================================================
// Exception Mapping & Domain Safety
// ============================================================================

/// <summary>
/// Thrown when predictive allocation gating determines that hardware RAM is
/// insufficient for the model weights, KV cache, and required safety margin.
/// </summary>
public sealed class InsufficientHardwareMemoryException : Exception
{
    public InsufficientHardwareMemoryException(string message, LlmLoadDiagnostics? diagnostics = null)
        : base(message)
    {
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Native telemetry explaining why the load was rejected, if available.
    /// </summary>
    public LlmLoadDiagnostics? Diagnostics { get; }
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
// Chat Message & Template Support
// ============================================================================

/// <summary>
/// Represents a single message in a chat conversation.
/// </summary>
public sealed record ChatMessage
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";

    public ChatMessage() { }

    public ChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}

/// <summary>
/// Formats chat messages into model-specific prompt strings.
/// Supports common chat templates used by popular open-weight models.
/// </summary>
public static class ChatTemplate
{
    /// <summary>
    /// Llama 3 / 3.1 / 3.2 instruct format.
    /// </summary>
    public static string Llama3(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.Append("<|begin_of_text|>");
        foreach (var msg in messages)
        {
            sb.Append($"<|start_header_id|>{msg.Role}<|end_header_id|>\n\n{msg.Content}<|eot_id|>");
        }
        sb.Append("<|start_header_id|>assistant<|end_header_id|>\n\n");
        return sb.ToString();
    }

    /// <summary>
    /// Mistral / Mixtral instruct format (v0.1/v0.2/v0.3).
    /// </summary>
    public static string Mistral(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.Append("<s>");
        foreach (var msg in messages)
        {
            sb.Append($"[INST] {msg.Content} [/INST]");
        }
        return sb.ToString();
    }

    /// <summary>
    /// ChatML format (used by Qwen, Yi, DeepSeek, and many others).
    /// </summary>
    public static string ChatML(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.Append($"<|im_start|>{msg.Role}\n{msg.Content}<|im_end|>\n");
        }
        sb.Append("<|im_start|>assistant\n");
        return sb.ToString();
    }

    /// <summary>
    /// Gemma instruct format.
    /// </summary>
    public static string Gemma(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            string role = msg.Role == "assistant" ? "model" : msg.Role;
            sb.Append($"<start_of_turn>{role}\n{msg.Content}<end_of_turn>\n");
        }
        sb.Append("<start_of_turn>model\n");
        return sb.ToString();
    }

    /// <summary>
    /// Phi-3 / Phi-4 chat format.
    /// </summary>
    public static string Phi(IEnumerable<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            sb.Append(msg.Role == "system"
                ? $"<|system|>\n{msg.Content}<|end|>\n"
                : $"<|{msg.Role}|>\n{msg.Content}<|end|>\n");
        }
        sb.Append("<|assistant|>\n");
        return sb.ToString();
    }
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
    internal static partial int crashless_v1_config_set_context_size(IntPtr config, int n_ctx);

    [LibraryImport(LibraryName)]
    internal static partial int crashless_v1_config_set_memory_margin(IntPtr config, double margin);

    [LibraryImport(LibraryName)]
    internal static partial int crashless_v1_config_set_sampling_params(
        IntPtr config,
        in SamplingParamsNative sampling);

    [LibraryImport(LibraryName)]
    internal static partial int crashless_v1_config_set_n_predict(IntPtr config, int n_predict);

    [LibraryImport(LibraryName)]
    internal static partial void crashless_v1_free_config(IntPtr config);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int crashless_v1_load_model_safe(
        string model_path,
        IntPtr config,
        out IntPtr out_model_ctx);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int crashless_v1_load_model_safe_ex(
        string model_path,
        IntPtr config,
        out IntPtr out_model_ctx,
        out LlmLoadDiagnosticsNative out_diagnostics);

    [LibraryImport(LibraryName)]
    internal static partial int crashless_v1_get_last_load_diagnostics(out LlmLoadDiagnosticsNative out_diagnostics);

    [LibraryImport(LibraryName)]
    internal static partial int crashless_v1_query_gpu_backends();

    [LibraryImport(LibraryName)]
    internal static partial int crashless_v1_query_model_arch_info(
        SafeLlmContextHandle model_ctx,
        out ModelArchInfoNative out_info);

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

    /// <summary>
    /// Blittable struct matching the native crashless_load_diagnostics layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LlmLoadDiagnosticsNative
    {
        internal ulong ModelFileBytes;
        internal ulong EstimatedKvCacheBytes;
        internal ulong SafetyMarginBytes;
        internal ulong PredictedTotalBytes;
        internal ulong AvailablePhysicalBytes;
        internal int NativeErrorCode;

        internal LlmLoadDiagnostics ToManaged()
        {
            return new LlmLoadDiagnostics
            {
                ModelFileBytes = (long)ModelFileBytes,
                EstimatedKvCacheBytes = (long)EstimatedKvCacheBytes,
                SafetyMarginBytes = (long)SafetyMarginBytes,
                PredictedTotalBytes = (long)PredictedTotalBytes,
                AvailableMemoryBytes = (long)AvailablePhysicalBytes,
                NativeErrorCode = NativeErrorCode,
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SamplingParamsNative
    {
        internal float Temperature;
        internal int TopK;
        internal float TopP;
        internal float MinP;
        internal float RepeatPenalty;
        internal int RepeatLastN;
        internal long Seed;

        internal static SamplingParamsNative FromOptions(SamplingOptions? options)
        {
            return new SamplingParamsNative
            {
                Temperature    = options?.Temperature ?? -1.0f,
                TopK           = options?.TopK ?? 0,
                TopP           = options?.TopP ?? -1.0f,
                MinP           = options?.MinP ?? -1.0f,
                RepeatPenalty  = options?.RepeatPenalty ?? -1.0f,
                RepeatLastN    = options?.RepeatLastN ?? 64,
                Seed           = options?.Seed ?? 0,
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModelArchInfoNative
    {
        internal int NLayer;
        internal int NEmbd;
        internal int NEmbdK;
        internal int NEmbdV;
        internal int NHead;
        internal int NHeadKv;
        internal int NCtxTrain;
        internal long BytesPerTokenKv;

        internal ModelArchInfo ToManaged()
        {
            return new ModelArchInfo
            {
                NLayer = NLayer,
                NEmbd = NEmbd,
                NEmbdK = NEmbdK,
                NEmbdV = NEmbdV,
                NHead = NHead,
                NHeadKv = NHeadKv,
                NCtxTrain = NCtxTrain,
                BytesPerTokenKv = BytesPerTokenKv,
            };
        }
    }
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
    /// <summary>
    /// Queries which GPU backends are compiled into the native crashless_core library.
    /// </summary>
    public static GpuBackend QueryGpuBackends()
    {
        return (GpuBackend)NativeMethods.crashless_v1_query_gpu_backends();
    }

    /// <summary>
    /// Loads a GGUF model with sensible zero-config defaults.
    /// </summary>
    public static LLMSession LoadSafe(string path)
    {
        return LoadSafe(path, new LlmLoadOptions(), NullLogger.Instance);
    }

    public static LLMSession LoadSafe(string path, ILogger? logger)
    {
        return LoadSafe(path, new LlmLoadOptions(), logger);
    }

    /// <summary>
    /// Loads a GGUF model with explicit safe configuration options.
    /// </summary>
    public static LLMSession LoadSafe(string path, LlmLoadOptions options)
    {
        return LoadSafe(path, options, NullLogger.Instance);
    }

    public static LLMSession LoadSafe(string path, LlmLoadOptions options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);

        if (NativeMethods.crashless_get_api_version() != 1)
        {
            throw new InvalidOperationException("Incompatible crashless_core API version.");
        }

        // Defaults: zero-config semantics.
        int gpuLayers = options.GpuLayers ?? 99;
        int threads = options.Threads ?? Math.Max(1, Environment.ProcessorCount / 2);
        int contextSize = options.ContextSize ?? 4096;
        double margin = options.MemorySafetyMargin;
        int maxTokens = options.MaxTokens;
        SamplingOptions? sampling = options.Sampling;

        IntPtr configPtr = IntPtr.Zero;
        int configResult = NativeMethods.crashless_v1_create_config(gpuLayers, threads, out configPtr);
        if (configResult != NativeMethods.Success || configPtr == IntPtr.Zero)
        {
            throw ToNativeException("Failed to initialize core configuration parameters.", configResult);
        }

        try
        {
            // Apply optional overrides to the opaque native config.
            if (contextSize != 4096)
            {
                NativeMethods.crashless_v1_config_set_context_size(configPtr, contextSize);
            }

            if (margin != 0.30)
            {
                NativeMethods.crashless_v1_config_set_memory_margin(configPtr, margin);
            }

            if (maxTokens != 512)
            {
                NativeMethods.crashless_v1_config_set_n_predict(configPtr, maxTokens);
            }

            if (sampling is not null)
            {
                var samplingNative = NativeMethods.SamplingParamsNative.FromOptions(sampling);
                NativeMethods.crashless_v1_config_set_sampling_params(configPtr, samplingNative);
            }

            LlmLoadDiagnostics? diagnostics = null;
            int loadResult = NativeMethods.crashless_v1_load_model_safe_ex(
                path,
                configPtr,
                out IntPtr modelContextPtr,
                out NativeMethods.LlmLoadDiagnosticsNative diagNative);

            diagnostics = diagNative.ToManaged();

            if (loadResult == NativeMethods.ErrInsufficientMemoryPredicted)
            {
                throw new InsufficientHardwareMemoryException(
                    $"Predictive allocation gating rejected model load. " +
                    $"Predicted need: {diagnostics.PredictedTotalBytes:N0} bytes. " +
                    $"Available: {diagnostics.AvailableMemoryBytes:N0} bytes. " +
                    $"(Model={diagnostics.ModelFileBytes:N0} B, KV estimate={diagnostics.EstimatedKvCacheBytes:N0} B, " +
                    $"margin={diagnostics.SafetyMarginBytes:N0} B). " +
                    $"Try reducing ContextSize, GpuLayers, or MemorySafetyMargin.",
                    diagnostics);
            }

            if (loadResult != NativeMethods.Success || modelContextPtr == IntPtr.Zero)
            {
                throw ToNativeException("Failed to natively load model.", loadResult);
            }

            return new LLMSession(new SafeLlmContextHandle(modelContextPtr), logger ?? NullLogger.Instance);
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
///
/// For concurrent generation, create multiple independent sessions via
/// <see cref="LLM.LoadSafe(string)"/> — each session wraps its own native context
/// and can run one stream at a time on its own thread.
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
    private readonly ILogger _logger;
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
        : this(handle, NullLogger.Instance)
    {
    }

    internal LLMSession(SafeLlmContextHandle handle, ILogger logger)
    {
        _handle = handle;
        _logger = logger;
        _pinnedCallback = HandleNativeTokenCallback;
        _logger.LogDebug("LLMSession created (handle=0x{Handle:X})", handle.DangerousGetHandle());
    }

    /// <summary>
    /// Queries accurate model architecture metadata from the native context.
    /// Must be called after a successful model load. Returns null on failure.
    /// </summary>
    public ModelArchInfo? QueryModelArchInfo()
    {
        VerifyNotDisposed();

        int result = NativeMethods.crashless_v1_query_model_arch_info(
            _handle,
            out NativeMethods.ModelArchInfoNative nativeInfo);

        if (result != NativeMethods.Success)
        {
            _logger.LogWarning("QueryModelArchInfo failed with native error {ErrorCode}", result);
            return null;
        }

        var info = nativeInfo.ToManaged();
        _logger.LogDebug("Model arch: {ArchInfo}", info);
        return info;
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

        _logger.LogDebug("StreamAsync acquiring gate (prompt preview: {Preview})",
            prompt.Length > 60 ? prompt[..60] + "..." : prompt);

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

            _logger.LogDebug("Starting native generation");
            int startResult = NativeMethods.crashless_v1_generate_async(
                _handle,
                prompt,
                callback,
                IntPtr.Zero);

            if (startResult != NativeMethods.Success)
            {
                Exception exception = CreateGenerationException(startResult);
                _logger.LogError(exception, "Native generation start failed with code {ErrorCode}", startResult);
                channel.Writer.TryComplete(exception);
                generationDone.TrySetResult();
                throw exception;
            }

            nativeStarted = true;
            _logger.LogDebug("Native generation started, streaming tokens");

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
        // ⚡ Bolt: Replaced manual byte-by-byte loop with MemoryMarshal for O(1) list resizing
        // and optimized native string length calculation, reducing CPU overhead on the fast path.
        ReadOnlySpan<byte> span = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)ptr);
        _utf8Accumulator.AddRange(span);

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

        _logger.LogDebug("LLMSession disposing");

        CancelNativeGenerationNoThrow();

        // Do not take _callbackLock here: the native callback may be synchronously
        // blocked on channel backpressure while holding it. Completing the channel
        // first unblocks the native worker and prevents dispose-time deadlocks.
        Volatile.Read(ref _channel)?.Writer.TryComplete();

        _handle.Dispose();
        _pinnedCallback = null;
        GC.SuppressFinalize(this);

        _logger.LogDebug("LLMSession disposed");
    }
}
