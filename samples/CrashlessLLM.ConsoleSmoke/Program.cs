using CrashlessLLM.Interop;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/CrashlessLLM.ConsoleSmoke -- /absolute/path/to/model.gguf [prompt]");
    Environment.ExitCode = 2;
    return;
}

string modelPath = args[0];
string prompt = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "Say OK in one short sentence.";

// First-run Metal shader compilation can take 10-20 s; allow generous time.
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
using var session = LLM.LoadSafe(modelPath);

try
{
    await foreach (string token in session.StreamAsync(prompt, cancellation.Token))
    {
        Console.Write(token);
    }

    Console.WriteLine();
    Console.WriteLine("[done]");
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("[cancelled]");
}
catch (InsufficientHardwareMemoryException ex)
{
    Console.Error.WriteLine($"[oom] {ex.Message}");
    if (ex.Diagnostics is { } d)
    {
        Console.Error.WriteLine($"  Model file:       {d.ModelFileBytes:N0} bytes");
        Console.Error.WriteLine($"  KV estimate:      {d.EstimatedKvCacheBytes:N0} bytes");
        Console.Error.WriteLine($"  Predicted total:  {d.PredictedTotalBytes:N0} bytes");
        Console.Error.WriteLine($"  Available memory: {d.AvailableMemoryBytes:N0} bytes");
    }
    Environment.ExitCode = 3;
}
catch (NativeInferenceException ex)
{
    Console.Error.WriteLine($"[native error] {ex.Message} (code={ex.ErrorCode})");
    Environment.ExitCode = 4;
}
