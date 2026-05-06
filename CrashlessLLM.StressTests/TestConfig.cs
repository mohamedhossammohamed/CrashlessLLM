namespace CrashlessLLM.StressTests;

public static class TestConfig
{
    public static readonly string ModelPath =
        Environment.GetEnvironmentVariable("CRASHLESS_TEST_MODEL_PATH")
        ?? "/path/to/local-test-model-under-1gb.gguf";

    public const long MaxLocalTestModelBytes = 2L * 1024L * 1024L * 1024L;
    public const int BackpressureProducerTokenCount = 100_000;
    public const int BackpressureTokensToConsume = 60;
    public const int RapidFireIterations = 500;
    public const int RapidFireMaxConcurrency = 8;
    public const int BackpressureChannelCapacity = 50;
    public const int BackpressureSchedulingSlack = 8;
    public const long ManagedMemoryFlatnessBudgetBytes = 32L * 1024L * 1024L;
    public const long RapidFireManagedMemoryReturnBudgetBytes = 64L * 1024L * 1024L;
    public const long SimulatedAvailableRamBytes = 512L * 1024L * 1024L;

    public static readonly TimeSpan BackpressureConsumerDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan NativeShimBuildTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StreamFirstTokenTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CancellationDrainTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan UiStarvationDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan UiHeartbeatInterval = TimeSpan.FromMilliseconds(25);
    public static readonly TimeSpan NonBlockingCallBudget = TimeSpan.FromMilliseconds(250);
}
