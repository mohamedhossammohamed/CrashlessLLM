using CrashlessLLM.Interop;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: dotnet run --project samples/CrashlessLLM.ConsoleSmoke -- /absolute/path/to/model.gguf [prompt]");
    Environment.ExitCode = 2;
    return;
}

string modelPath = args[0];
string prompt = args.Length > 1 ? string.Join(' ', args.Skip(1)) : "Say OK in one short sentence.";

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var session = LLM.LoadSafe(modelPath);

await foreach (string token in session.StreamAsync(prompt, cancellation.Token))
{
    Console.Write(token);
}

Console.WriteLine();
