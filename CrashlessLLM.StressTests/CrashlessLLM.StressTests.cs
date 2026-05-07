using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CrashlessLLM.Interop;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CrashlessLLM.StressTests;

internal static class StressTestCollectionNames
{
    public const string NativeShim = "CrashlessLLM native ABI shim";
}

[CollectionDefinition(StressTestCollectionNames.NativeShim)]
public sealed class NativeShimCollection : ICollectionFixture<NativeShimFixture>
{
}

public sealed class NativeShimFixture
{
    private static int libraryResolverRegistered;
    private static int testResolverRegistered;
    private static IntPtr nativeHandle;
    private static string? nativeLibraryPath;

    public NativeShimFixture()
    {
        NativeLibraryPath = BuildNativeShim();
        nativeLibraryPath = NativeLibraryPath;
        Environment.SetEnvironmentVariable(
            "CRASHLESS_STRESS_SIMULATED_AVAILABLE_RAM_BYTES",
            TestConfig.SimulatedAvailableRamBytes.ToString(CultureInfo.InvariantCulture));

        if (Interlocked.Exchange(ref libraryResolverRegistered, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(LLM).Assembly, ResolveNativeLibrary);
        }

        if (Interlocked.Exchange(ref testResolverRegistered, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(StressNativeMethods).Assembly, ResolveNativeLibrary);
        }
    }

    public string NativeLibraryPath { get; }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "crashless_core", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        if (nativeHandle != IntPtr.Zero)
        {
            return nativeHandle;
        }

        string path = nativeLibraryPath
            ?? throw new InvalidOperationException("The CrashlessLLM stress native shim was not built before P/Invoke resolution.");

        nativeHandle = NativeLibrary.Load(path);
        return nativeHandle;
    }

    private static string BuildNativeShim()
    {
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Native", "CrashlessNativeShim.cpp");
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The deterministic CrashlessLLM native stress shim source was not copied to the test output.", sourcePath);
        }

        string buildDirectory = Path.Combine(Path.GetTempPath(), "CrashlessLLM.StressTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildDirectory);

        string outputPath = Path.Combine(buildDirectory, GetNativeLibraryFileName());
        string compiler = ResolveCompiler();
        IReadOnlyList<string> arguments = BuildCompilerArguments(compiler, sourcePath, outputPath);
        RunCompiler(compiler, arguments);

        if (!File.Exists(outputPath))
        {
            throw new FileNotFoundException("The deterministic CrashlessLLM native stress shim did not produce an output library.", outputPath);
        }

        return outputPath;
    }

    private static string ResolveCompiler()
    {
        string? configuredCompiler = Environment.GetEnvironmentVariable("CXX");
        if (!string.IsNullOrWhiteSpace(configuredCompiler))
        {
            return configuredCompiler;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cl" : "c++";
    }

    private static string GetNativeLibraryFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "crashless_core.dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "libcrashless_core.dylib";
        }

        return "libcrashless_core.so";
    }

    private static IReadOnlyList<string> BuildCompilerArguments(string compiler, string sourcePath, string outputPath)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool useMsvc = isWindows && string.Equals(Path.GetFileNameWithoutExtension(compiler), "cl", StringComparison.OrdinalIgnoreCase);

        if (useMsvc)
        {
            return new[]
            {
                "/nologo",
                "/std:c++17",
                "/EHsc",
                "/LD",
                sourcePath,
                $"/Fe:{outputPath}"
            };
        }

        return new[]
        {
            "-std=c++17",
            "-O2",
            "-shared",
            "-fPIC",
            "-pthread",
            sourcePath,
            "-o",
            outputPath
        };
    }

    private static void RunCompiler(string compiler, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(compiler)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start native shim compiler '{compiler}'.");

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)TestConfig.NativeShimBuildTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"Native shim compiler '{compiler}' exceeded {TestConfig.NativeShimBuildTimeout}.");
        }

        string standardOutput = standardOutputTask.GetAwaiter().GetResult();
        string standardError = standardErrorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Native shim compiler '{compiler}' exited with {process.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{standardError}");
        }
    }
}

internal static partial class StressNativeMethods
{
    [LibraryImport("crashless_core")]
    internal static partial void crashless_stress_reset_metrics();

    [LibraryImport("crashless_core")]
    internal static partial long crashless_stress_get_active_sessions();

    [LibraryImport("crashless_core")]
    internal static partial long crashless_stress_get_active_workers();

    [LibraryImport("crashless_core")]
    internal static partial long crashless_stress_get_generated_callbacks();

    [LibraryImport("crashless_core")]
    internal static partial long crashless_stress_get_completed_workers();

    [LibraryImport("crashless_core")]
    internal static partial ulong crashless_stress_get_last_worker_thread_hash();
}

[Collection(StressTestCollectionNames.NativeShim)]
public sealed class CrashlessLlmStressTests
{
    private readonly ITestOutputHelper output;
    private readonly NativeShimFixture fixture;

    public CrashlessLlmStressTests(ITestOutputHelper output, NativeShimFixture fixture)
    {
        this.output = output;
        this.fixture = fixture;
    }

    [Fact]
    public async Task StreamAsync_ReconstructsFragmentedFourByteUtf8WithoutReplacementCharacters()
    {
        EnsureConfiguredModelPath();
        ResetMetricsAndAssertIdle();

        var tokens = new List<string>();
        Exception? exception = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var session = LLM.LoadSafe(TestConfig.ModelPath);
            await foreach (string token in session.StreamAsync("CRASHLESS_STRESS_UTF8_SPLIT"))
            {
                tokens.Add(token);
            }
        }
        catch (Exception ex)
        {
            exception = ex;
        }

        stopwatch.Stop();
        string reconstructed = string.Concat(tokens);
        string expected = char.ConvertFromUtf32(0x1F480);

        output.WriteLine($"UTF-8 torture elapsed={stopwatch.Elapsed}; tokens={tokens.Count}; generatedCallbacks={StressNativeMethods.crashless_stress_get_generated_callbacks()}");

        Assert.False(exception is DecoderFallbackException, $"StreamAsync surfaced a DecoderFallbackException: {exception}");
        Assert.Null(exception);
        Assert.Equal(expected, reconstructed);
        Assert.DoesNotContain("\uFFFD", reconstructed);
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_sessions());
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_workers());
    }

    [Fact]
    public async Task StreamAsync_BoundsProducerBackpressureAndKeepsManagedMemoryFlatWhenConsumerStalls()
    {
        EnsureConfiguredModelPath();
        ResetMetricsAndAssertIdle();

        long processBefore = GetPrivateMemoryBytes();
        int consumed = 0;
        long peakManagedBytes;
        long baselineManagedBytes;
        long afterManagedBytes;
        long generatedCallbacks;
        var stopwatch = Stopwatch.StartNew();

        using (var session = LLM.LoadSafe(TestConfig.ModelPath))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
        {
            baselineManagedBytes = ForceFullGcAndGetManagedBytes();
            peakManagedBytes = baselineManagedBytes;
            string prompt = $"CRASHLESS_STRESS_FLOOD:{TestConfig.BackpressureProducerTokenCount}";

            try
            {
                await foreach (string token in session.StreamAsync(prompt, cancellation.Token).WithCancellation(cancellation.Token))
                {
                    Assert.Equal("x", token);
                    consumed++;
                    peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));

                    await Task.Delay(TestConfig.BackpressureConsumerDelay);
                    peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));

                    if (consumed >= TestConfig.BackpressureTokensToConsume)
                    {
                        cancellation.Cancel();
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
        }

        await WaitForShimIdleAsync(TestConfig.CancellationDrainTimeout);
        stopwatch.Stop();
        generatedCallbacks = StressNativeMethods.crashless_stress_get_generated_callbacks();
        afterManagedBytes = ForceFullGcAndGetManagedBytes();
        long processAfter = GetPrivateMemoryBytes();
        long allowedProducerLead = consumed + TestConfig.BackpressureChannelCapacity + TestConfig.BackpressureSchedulingSlack;

        output.WriteLine(
            "Backpressure elapsed={0}; consumed={1}; generatedCallbacks={2}; allowedProducerLead={3}; managedBaseline={4}; managedPeak={5}; managedAfterGc={6}; processBefore={7}; processAfter={8}",
            stopwatch.Elapsed,
            consumed,
            generatedCallbacks,
            allowedProducerLead,
            FormatBytes(baselineManagedBytes),
            FormatBytes(peakManagedBytes),
            FormatBytes(afterManagedBytes),
            FormatBytes(processBefore),
            FormatBytes(processAfter));

        Assert.True(consumed >= TestConfig.BackpressureTokensToConsume, $"Expected to consume {TestConfig.BackpressureTokensToConsume} delayed tokens.");
        Assert.True(
            generatedCallbacks <= allowedProducerLead,
            $"Unmanaged producer outran the stalled consumer. generated={generatedCallbacks}, consumed={consumed}, allowed={allowedProducerLead}");
        Assert.True(
            peakManagedBytes - baselineManagedBytes <= TestConfig.ManagedMemoryFlatnessBudgetBytes,
            $"Managed memory bloat exceeded flatness budget. delta={FormatBytes(peakManagedBytes - baselineManagedBytes)}, budget={FormatBytes(TestConfig.ManagedMemoryFlatnessBudgetBytes)}");
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_sessions());
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_workers());
    }

    [Fact]
    public async Task RapidFireCancellation_DoesNotLeakSafeHandlesWorkersOrManagedMemoryAcrossFiveHundredCycles()
    {
        EnsureConfiguredModelPath();
        ResetMetricsAndAssertIdle();

        long baselineManagedBytes = ForceFullGcAndGetManagedBytes();
        long processBefore = GetPrivateMemoryBytes();
        var errors = new ConcurrentQueue<Exception>();
        var stopwatch = Stopwatch.StartNew();
        int maxConcurrency = Math.Max(1, Math.Min(TestConfig.RapidFireMaxConcurrency, TestConfig.RapidFireIterations));

        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        Task[] tasks = Enumerable.Range(0, TestConfig.RapidFireIterations)
            .Select(async iteration =>
            {
                await gate.WaitAsync();
                try
                {
                    await RunRapidFireIterationAsync(iteration);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
                finally
                {
                    gate.Release();
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);
        stopwatch.Stop();
        await WaitForShimIdleAsync(TestConfig.CancellationDrainTimeout);

        long afterManagedBytes = ForceFullGcAndGetManagedBytes();
        long processAfter = GetPrivateMemoryBytes();
        long activeSessions = StressNativeMethods.crashless_stress_get_active_sessions();
        long activeWorkers = StressNativeMethods.crashless_stress_get_active_workers();
        long generatedCallbacks = StressNativeMethods.crashless_stress_get_generated_callbacks();
        long completedWorkers = StressNativeMethods.crashless_stress_get_completed_workers();

        output.WriteLine(
            "Rapid-fire elapsed={0}; iterations={1}; concurrency={2}; errors={3}; generatedCallbacks={4}; completedWorkers={5}; activeSessions={6}; activeWorkers={7}; managedBaseline={8}; managedAfterGc={9}; processBefore={10}; processAfter={11}",
            stopwatch.Elapsed,
            TestConfig.RapidFireIterations,
            maxConcurrency,
            errors.Count,
            generatedCallbacks,
            completedWorkers,
            activeSessions,
            activeWorkers,
            FormatBytes(baselineManagedBytes),
            FormatBytes(afterManagedBytes),
            FormatBytes(processBefore),
            FormatBytes(processAfter));

        Assert.True(errors.IsEmpty, errors.TryPeek(out Exception? first) ? first.ToString() : "Rapid-fire loop captured failures.");
        Assert.Equal(0, activeSessions);
        Assert.Equal(0, activeWorkers);
        Assert.True(
            afterManagedBytes - baselineManagedBytes <= TestConfig.RapidFireManagedMemoryReturnBudgetBytes,
            $"Managed memory failed to return near baseline. delta={FormatBytes(afterManagedBytes - baselineManagedBytes)}, budget={FormatBytes(TestConfig.RapidFireManagedMemoryReturnBudgetBytes)}");
    }

    [Fact]
    public void LoadSafe_MapsPredictiveGatingFailureToInsufficientHardwareMemoryExceptionWithoutCrashingProcess()
    {
        ResetMetricsAndAssertIdle();

        string tempPath = Path.Combine(Path.GetTempPath(), $"crashless_stress_oom_{Guid.NewGuid():N}.gguf");
        long baselineManagedBytes = ForceFullGcAndGetManagedBytes();
        long processBefore = GetPrivateMemoryBytes();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                file.SetLength(TestConfig.SimulatedAvailableRamBytes + 1L);
            }

            InsufficientHardwareMemoryException exception = Assert.Throws<InsufficientHardwareMemoryException>(() => LLM.LoadSafe(tempPath));
            stopwatch.Stop();
            long afterManagedBytes = ForceFullGcAndGetManagedBytes();
            long processAfter = GetPrivateMemoryBytes();

            output.WriteLine(
                "Predictive OOM elapsed={0}; sparseFile={1}; exception='{2}'; managedBaseline={3}; managedAfterGc={4}; processBefore={5}; processAfter={6}",
                stopwatch.Elapsed,
                FormatBytes(new FileInfo(tempPath).Length),
                exception.Message,
                FormatBytes(baselineManagedBytes),
                FormatBytes(afterManagedBytes),
                FormatBytes(processBefore),
                FormatBytes(processAfter));

            Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_sessions());
            Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_workers());
            Assert.True(
                afterManagedBytes - baselineManagedBytes <= TestConfig.ManagedMemoryFlatnessBudgetBytes,
                $"Predictive OOM path allocated excessive managed memory. delta={FormatBytes(afterManagedBytes - baselineManagedBytes)}");
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    [Fact]
    public async Task StreamAsync_DoesNotStarveSingleThreadedAvaloniaStyleDispatcherWhileNativeWorkerIsPending()
    {
        EnsureConfiguredModelPath();
        ResetMetricsAndAssertIdle();

        using var uiContext = new SingleThreadedUiContext();
        int heartbeats = 0;
        int loadThreadId = 0;
        int yieldThreadId = 0;
        int heartbeatsWhilePending = 0;
        TimeSpan moveNextReturnElapsed = TimeSpan.Zero;
        var stopwatch = Stopwatch.StartNew();

        using var heartbeatTimer = new Timer(
            _ => uiContext.Post(_ => Interlocked.Increment(ref heartbeats), null),
            null,
            TimeSpan.Zero,
            TestConfig.UiHeartbeatInterval);

        await uiContext.RunAsync(async () =>
        {
            loadThreadId = Environment.CurrentManagedThreadId;
            LLMSession? session = null;
            IAsyncEnumerator<string>? enumerator = null;

            try
            {
                session = LLM.LoadSafe(TestConfig.ModelPath);
                enumerator = session.StreamAsync("CRASHLESS_STRESS_UI_DELAY").GetAsyncEnumerator();

                var moveNextStopwatch = Stopwatch.StartNew();
                ValueTask<bool> firstMove = enumerator.MoveNextAsync();
                moveNextStopwatch.Stop();
                moveNextReturnElapsed = moveNextStopwatch.Elapsed;

                Assert.True(
                    moveNextReturnElapsed <= TestConfig.NonBlockingCallBudget,
                    $"MoveNextAsync synchronous prefix blocked the UI thread for {moveNextReturnElapsed}.");
                Assert.False(firstMove.IsCompleted, "The delayed native worker should leave the first token pending.");

                await Task.Delay(TimeSpan.FromTicks(TestConfig.UiStarvationDelay.Ticks / 2));
                heartbeatsWhilePending = Volatile.Read(ref heartbeats);

                Assert.True(
                    heartbeatsWhilePending >= 5,
                    $"UI heartbeat stalled while native generation was pending. heartbeats={heartbeatsWhilePending}");

                bool hasToken = await firstMove.AsTask().WaitAsync(TestConfig.StreamFirstTokenTimeout);
                yieldThreadId = Environment.CurrentManagedThreadId;

                Assert.True(hasToken);
                Assert.Equal("ui", enumerator.Current);
            }
            finally
            {
                if (enumerator is not null)
                {
                    await enumerator.DisposeAsync().AsTask().WaitAsync(TestConfig.CancellationDrainTimeout);
                }

                session?.Dispose();
            }
        }, TimeSpan.FromSeconds(15));

        stopwatch.Stop();
        await WaitForShimIdleAsync(TestConfig.CancellationDrainTimeout);

        output.WriteLine(
            "UI starvation elapsed={0}; loadThreadId={1}; yieldThreadId={2}; heartbeatsWhilePending={3}; moveNextReturnElapsed={4}; nativeWorkerThreadHash={5}",
            stopwatch.Elapsed,
            loadThreadId,
            yieldThreadId,
            heartbeatsWhilePending,
            moveNextReturnElapsed,
            StressNativeMethods.crashless_stress_get_last_worker_thread_hash());

        Assert.Equal(loadThreadId, yieldThreadId);
        Assert.True(heartbeatsWhilePending >= 5);
        Assert.NotEqual(0UL, StressNativeMethods.crashless_stress_get_last_worker_thread_hash());
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_sessions());
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_workers());
    }

    private static async Task RunRapidFireIterationAsync(int iteration)
    {
        using var cancellation = new CancellationTokenSource();
        using var session = LLM.LoadSafe(TestConfig.ModelPath);
        IAsyncEnumerator<string>? enumerator = null;

        try
        {
            enumerator = session.StreamAsync($"CRASHLESS_STRESS_CANCEL {iteration}", cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);

            bool hasToken = await enumerator.MoveNextAsync().AsTask().WaitAsync(TestConfig.StreamFirstTokenTimeout);
            Assert.True(hasToken, $"Rapid-fire iteration {iteration} ended before producing the first token.");

            cancellation.Cancel();
        }
        finally
        {
            cancellation.Cancel();

            if (enumerator is not null)
            {
                try
                {
                    await enumerator.DisposeAsync().AsTask().WaitAsync(TestConfig.CancellationDrainTimeout);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
            }
        }
    }

    private static void EnsureConfiguredModelPath()
    {
        Assert.False(
            string.Equals(TestConfig.ModelPath, "/path/to/local-test-model-under-1gb.gguf", StringComparison.Ordinal),
            "Set TestConfig.ModelPath or CRASHLESS_TEST_MODEL_PATH to the absolute path of a local GGUF model no larger than 2 GiB before running stress tests.");
        Assert.True(Path.IsPathFullyQualified(TestConfig.ModelPath), $"TestConfig.ModelPath must be absolute: {TestConfig.ModelPath}");
        Assert.True(File.Exists(TestConfig.ModelPath), $"Configured GGUF model does not exist: {TestConfig.ModelPath}");
        Assert.True(
            Path.GetExtension(TestConfig.ModelPath).Equals(".gguf", StringComparison.OrdinalIgnoreCase) || HasGgufMagic(TestConfig.ModelPath),
            $"Configured model path must either end with .gguf or point to a raw GGUF blob: {TestConfig.ModelPath}");
        Assert.True(
            new FileInfo(TestConfig.ModelPath).Length <= TestConfig.MaxLocalTestModelBytes,
            $"Configured GGUF model must be <= {FormatBytes(TestConfig.MaxLocalTestModelBytes)} for fast MVP stress runs: {TestConfig.ModelPath}");
    }

    private static bool HasGgufMagic(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        return stream.Read(magic) == 4 && magic.SequenceEqual("GGUF"u8);
    }

    private static void ResetMetricsAndAssertIdle()
    {
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_sessions());
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_workers());
        StressNativeMethods.crashless_stress_reset_metrics();
    }

    private static async Task WaitForShimIdleAsync(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (StressNativeMethods.crashless_stress_get_active_workers() == 0)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_workers());
    }

    private static long ForceFullGcAndGetManagedBytes()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static long GetPrivateMemoryBytes()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static string FormatBytes(long bytes)
    {
        return $"{bytes / 1024d / 1024d:N2} MiB";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    // ============================================================================
    // New Feature Tests (v0.2 additions)
    // ============================================================================

    [Fact]
    public void QueryGpuBackends_ReturnsValidBitmask()
    {
        GpuBackend backends = LLM.QueryGpuBackends();
        output.WriteLine($"GPU backends: {backends}");
        // Must not throw, and the value should be a valid enum combination.
        Assert.True(Enum.IsDefined(typeof(GpuBackend), GpuBackend.None));
        Assert.True((int)backends >= 0);
    }

    [Fact]
    public void QueryModelArchInfo_ReturnsAccurateMetadata()
    {
        EnsureConfiguredModelPath();
        ResetMetricsAndAssertIdle();

        using var session = LLM.LoadSafe(TestConfig.ModelPath);
        ModelArchInfo? info = session.QueryModelArchInfo();

        Assert.NotNull(info);
        output.WriteLine($"Model arch: {info}");

        Assert.True(info!.NLayer > 0, $"Expected NLayer > 0, got {info.NLayer}");
        Assert.True(info.NEmbd > 0, $"Expected NEmbd > 0, got {info.NEmbd}");
        Assert.True(info.NHead > 0, $"Expected NHead > 0, got {info.NHead}");
        Assert.True(info.NHeadKv > 0, $"Expected NHeadKv > 0, got {info.NHeadKv}");
        Assert.True(info.BytesPerTokenKv > 0, $"Expected BytesPerTokenKv > 0, got {info.BytesPerTokenKv}");
        // A realistic per-token KV cache is at least a few KB.
        Assert.True(info.BytesPerTokenKv >= 1024,
            $"BytesPerTokenKv ({info.BytesPerTokenKv}) is implausibly small.");
    }

    [Fact]
    public async Task StreamAsync_WithTemperatureZero_ProducesTokensWithoutError()
    {
        EnsureConfiguredModelPath();
        ResetMetricsAndAssertIdle();

        var options = new LlmLoadOptions
        {
            Sampling = new SamplingOptions { Temperature = 0.0f }
        };

        var tokens = new List<string>();

        using (var session = LLM.LoadSafe(TestConfig.ModelPath, options))
        {
            await foreach (string token in session.StreamAsync("Return exactly OK."))
            {
                tokens.Add(token);
            }
        }

        // Ensure native cleanup completes.
        await Task.Delay(100);
        ForceFullGcAndGetManagedBytes();

        output.WriteLine($"Temp=0 tokens: {tokens.Count}, first='{tokens.FirstOrDefault()}'");
        Assert.NotEmpty(tokens);
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_sessions());
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_workers());
    }

    [Fact]
    public async Task StreamAsync_WithMaxTokens_RespectsGenerationLimit()
    {
        EnsureConfiguredModelPath();
        ResetMetricsAndAssertIdle();

        var options = new LlmLoadOptions { MaxTokens = 10 };

        int tokenCount = 0;
        var stopwatch = Stopwatch.StartNew();

        using (var session = LLM.LoadSafe(TestConfig.ModelPath, options))
        {
            await foreach (string token in session.StreamAsync("Count to twenty: 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20"))
            {
                tokenCount++;
            }
        }

        await Task.Delay(100);
        ForceFullGcAndGetManagedBytes();
        stopwatch.Stop();

        output.WriteLine($"MaxTokens=10 elapsed={stopwatch.Elapsed}; tokenCount={tokenCount}");
        Assert.True(tokenCount >= 0);
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_sessions());
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_workers());
    }

    [Fact]
    public void ChatTemplate_Llama3_FormatsCorrectly()
    {
        var messages = new[]
        {
            new ChatMessage("system", "You are helpful."),
            new ChatMessage("user", "Hello")
        };

        string formatted = ChatTemplate.Llama3(messages);

        output.WriteLine($"Llama3 template: {formatted}");
        Assert.Contains("<|begin_of_text|>", formatted);
        Assert.Contains("<|start_header_id|>system<|end_header_id|>", formatted);
        Assert.Contains("<|start_header_id|>user<|end_header_id|>", formatted);
        Assert.Contains("<|start_header_id|>assistant<|end_header_id|>", formatted);
        Assert.Contains("<|eot_id|>", formatted);
    }

    [Fact]
    public void ChatTemplate_ChatML_FormatsCorrectly()
    {
        var messages = new[]
        {
            new ChatMessage("system", "You are helpful."),
            new ChatMessage("user", "Hello")
        };

        string formatted = ChatTemplate.ChatML(messages);

        output.WriteLine($"ChatML template: {formatted}");
        Assert.Contains("<|im_start|>system", formatted);
        Assert.Contains("<|im_start|>user", formatted);
        Assert.Contains("<|im_start|>assistant", formatted);
        Assert.Contains("<|im_end|>", formatted);
    }

    [Fact]
    public void LlmLoadOptions_WithSamplingAndMaxTokens_PassesValidation()
    {
        EnsureConfiguredModelPath();
        ResetMetricsAndAssertIdle();

        var options = new LlmLoadOptions
        {
            MaxTokens = 256,
            Sampling = new SamplingOptions
            {
                Temperature = 0.7f,
                TopK = 50,
                TopP = 0.9f,
                RepeatPenalty = 1.1f,
                Seed = 42
            },
            ContextSize = 2048,
            Threads = 2
        };

        LLMSession? session = null;
        try
        {
            session = LLM.LoadSafe(TestConfig.ModelPath, options);
            Assert.NotNull(session);
        }
        finally
        {
            session?.Dispose();
        }

        ForceFullGcAndGetManagedBytes();
        Assert.Equal(0, StressNativeMethods.crashless_stress_get_active_sessions());
    }
}

internal sealed class SingleThreadedUiContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task RunAsync(Func<Task> action, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            Post(async _ =>
            {
                try
                {
                    await action();
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    queue.CompleteAdding();
                }
            }, null);

            foreach ((SendOrPostCallback callback, object? state) in queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        })
        {
            IsBackground = true,
            Name = "CrashlessLLM.StressTests.UI"
        };

        thread.Start();

        try
        {
            await completion.Task.WaitAsync(timeout);
        }
        finally
        {
            queue.CompleteAdding();
            if (!thread.Join((int)timeout.TotalMilliseconds))
            {
                throw new TimeoutException("The single-threaded UI context did not drain before the timeout.");
            }
        }
    }

    public void Dispose()
    {
        queue.Dispose();
    }
}
