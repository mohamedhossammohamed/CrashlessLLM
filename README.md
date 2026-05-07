# CrashlessLLM

**The drop-in local LLM runtime that does not crash your .NET or Avalonia app.**

CrashlessLLM is a zero-config, crash-resistant native embedding layer for local GGUF models. It wraps a `llama.cpp`-style C ABI with predictive memory admission control, deterministic native cleanup, configurable sampling, chat templates, UTF-8-safe token streaming, and bounded backpressure that keeps UI threads responsive under inference load.

Created by **MHMZAHRAN**. Announcements and primary project communication happen on X: [@MohamedHz72007](https://x.com/MohamedHz72007).

> **Before/After GIF placeholder**
>
> Replace this block with `docs/assets/crashlessllm-before-after.gif`.
>
> **Before:** a naive native wrapper enters unmanaged allocation pressure and the host UI process is killed or frozen.
>
> **After:** CrashlessLLM rejects unsafe loads before allocation and streams stable tokens to Avalonia through bounded backpressure.

## The no-brainer quickstart

```bash
dotnet add package CrashlessLLM --version 0.2.0-alpha
```

Three lines of C# integration:

```csharp
using var llm = LLM.LoadSafe("models/llama-3-8b.gguf");
await foreach (var token in llm.StreamAsync("Explain stable local inference."))
    Output.Text += token;
```

Add `using CrashlessLLM.Interop;` at the top of the file. The default load path is intentionally zero-config: CrashlessLLM chooses safe CPU threading defaults, uses a conservative context size, caps generation at 512 tokens, applies native sampling defaults, and runs a predictive memory gate before the native runtime is allowed to allocate.

## Why it exists

Naive `llama.cpp` wrappers make the managed application responsible for failure modes the CLR cannot actually control:

| Failure mode | What happens in a naive wrapper | User-visible result |
| --- | --- | --- |
| Native OOM | Model weights and KV cache are allocated after crossing the FFI boundary, beyond GC visibility. | Linux OOM killer, Windows commit failure, macOS memory-pressure stalls, or an uncatchable process crash. |
| GC pressure | Token callbacks allocate aggressively while the UI is still painting. | Stutter, delayed input, runaway memory growth, and inconsistent cancellation. |
| UI thread blocking | Generation is invoked synchronously or tokens are pushed faster than the UI can consume. | Frozen Avalonia windows and “app not responding” reports. |
| Unsafe lifetime | Raw native pointers survive longer than the managed owner or are freed non-deterministically. | Use-after-free, leaks, and shutdown crashes. |
| Opaque native configuration | Callers guess sampling, token limits, and GPU offload capabilities. | Unreproducible output, runaway generations, and platform-specific surprises. |

CrashlessLLM exists so local AI features can be embedded in real desktop applications without turning the UI process into a crash boundary experiment.

## How it works

- **Predictive Allocation Gating**: before model load, the native core estimates `model file bytes + context KV-cache bytes + safety margin`. If predicted usage exceeds available physical memory, `LLM.LoadSafe()` refuses the load and throws `InsufficientHardwareMemoryException` with structured diagnostics instead of letting the OS kill the app.
- **Bounded Backpressure**: native token callbacks are copied immediately, decoded safely, and written through a bounded `Channel<string>` with a capacity of 50. If the UI falls behind, the native worker waits; the UI thread does not.
- **Configurable Sampling**: `SamplingOptions` controls temperature, top-k, top-p, min-p, repeat penalty, repeat window, and seed. `Temperature = 0` switches to greedy decoding; omitted values use native defaults.
- **Configurable Max Tokens**: `LlmLoadOptions.MaxTokens` defaults to 512. Set `-1` to allow generation up to the remaining context window.
- **SafeHandle determinism**: every native session is represented by an opaque C handle wrapped in `SafeLlmContextHandle`. `IDisposable` releases model, context, sampler, and worker state deterministically, with SafeHandle as a final safety net.
- **UTF-8 fragment streaming**: token pieces that split multi-byte UTF-8 sequences are accumulated until a valid string can be emitted, preventing corrupted output during high-frequency streaming.
- **Strict C-ABI boundary**: no C++ objects, exceptions, STL containers, or ownership semantics cross into .NET. The boundary uses primitive values, opaque pointers, version checks, explicit error codes, and synchronous callbacks only.

## Diagnostics when a model is too large

```csharp
try
{
    using var llm = LLM.LoadSafe("models/too-large.gguf");
}
catch (InsufficientHardwareMemoryException ex) when (ex.Diagnostics is { } d)
{
    Console.WriteLine($"Model file:      {d.ModelFileBytes:N0} bytes");
    Console.WriteLine($"KV cache est.:   {d.EstimatedKvCacheBytes:N0} bytes");
    Console.WriteLine($"Safety margin:   {d.SafetyMarginBytes:N0} bytes");
    Console.WriteLine($"Predicted total: {d.PredictedTotalBytes:N0} bytes");
    Console.WriteLine($"Available RAM:   {d.AvailableMemoryBytes:N0} bytes");
}
```

## Production configuration

```csharp
using var llm = LLM.LoadSafe("models/app-model.gguf", new LlmLoadOptions
{
    ContextSize = 2048,
    Threads = 4,
    GpuLayers = 0,
    MaxTokens = 512,               // -1 = unlimited up to context
    MemorySafetyMargin = 0.20,
    Sampling = new SamplingOptions
    {
        Temperature = 0.7f,        // 0 = greedy
        TopK = 40,
        TopP = 0.95f,
        MinP = 0.05f,
        RepeatPenalty = 1.1f,      // > 1 penalizes repetition
        RepeatLastN = 64,
        Seed = 42                  // 0 = random
    }
});
```

### Chat templates

Use `ChatMessage` and `ChatTemplate` to format conversations for common model families:

```csharp
var messages = new[]
{
    new ChatMessage("system", "You are a helpful assistant."),
    new ChatMessage("user", "What is 2+2?")
};

string llamaPrompt = ChatTemplate.Llama3(messages);
string qwenPrompt = ChatTemplate.ChatML(messages);
string mistralPrompt = ChatTemplate.Mistral(messages);
string gemmaPrompt = ChatTemplate.Gemma(messages);
string phiPrompt = ChatTemplate.Phi(messages);
```

### GPU backend detection

Query which backends the native library was compiled with:

```csharp
GpuBackend backends = LLM.QueryGpuBackends();

if ((backends & GpuBackend.Metal) != 0)
    Console.WriteLine("Metal backend available.");
```

### Accurate model architecture query

After loading, query real model parameters for post-load memory reporting:

```csharp
using var session = LLM.LoadSafe("models/model.gguf");
ModelArchInfo? arch = session.QueryModelArchInfo();

if (arch is not null)
{
    Console.WriteLine($"KV cache: {arch.BytesPerTokenKv} bytes/token");
    Console.WriteLine($"n_layer={arch.NLayer}, n_embd={arch.NEmbd}, n_head={arch.NHead}");
}
```

The pre-load admission gate intentionally remains conservative. `QueryModelArchInfo()` is post-load introspection for diagnostics, sizing UI, and explaining actual model architecture after a safe session exists.

### Structured logging

Pass any `ILogger` from your application logging pipeline to surface session lifecycle and generation events:

```csharp
ILogger logger = loggerFactory.CreateLogger("CrashlessLLM");

using var session = LLM.LoadSafe("models/model.gguf", logger);
```

CrashlessLLM logs session creation, gate acquisition, native generation start, model-architecture query failures, generation failures, and disposal. If no logger is provided, it uses `NullLogger.Instance`.

### Concurrent sessions

Each `LLMSession` owns one native model/context pair and permits one active stream. For parallel work, create multiple sessions:

```csharp
using var sessionA = LLM.LoadSafe("models/model.gguf");
using var sessionB = LLM.LoadSafe("models/model.gguf");

static async Task ConsumeAsync(LLMSession session, string prompt)
{
    await foreach (var token in session.StreamAsync(prompt))
        Console.Write(token);
}

await Task.WhenAll(
    ConsumeAsync(sessionA, "Prompt A"),
    ConsumeAsync(sessionB, "Prompt B"));
```

## What CrashlessLLM does and does not do

| Scope | Status |
| --- | --- |
| Local raw GGUF loading | Supported through the `crashless_core` native library. |
| Configurable sampling | Supported via `SamplingOptions` and native sampler-chain construction. |
| Configurable max generation tokens | Supported via `LlmLoadOptions.MaxTokens`. |
| Chat templates | Supported for Llama 3, ChatML, Mistral, Gemma, and Phi. |
| GPU backend detection | Supported via `LLM.QueryGpuBackends()`. |
| Accurate model architecture query | Supported post-load via `LLMSession.QueryModelArchInfo()`. |
| Structured logging | Supported via `ILogger` / `Microsoft.Extensions.Logging.Abstractions`. |
| Multiple concurrent sessions | Supported: create independent `LLMSession` instances. |
| Cross-platform native CI | Supported for macOS arm64/x64, Linux x64/arm64, and Windows x64 artifacts. |
| NuGet publication | Supported via `scripts/publish-nuget.sh`. |
| Ollama or remote APIs | Not used. CrashlessLLM validates the native embedding boundary, not an HTTP runtime. |
| Avalonia UI streaming | Supported through async token enumeration and bounded backpressure. |
| Malicious GGUF sandboxing | Not claimed. Do not load untrusted model files. |
| Multiple concurrent streams per session | Rejected by design; use one active `StreamAsync` per `LLMSession`. |

## Build from source

Managed build:

```bash
dotnet build CrashlessLLM.sln
```

Native build requires CMake plus either `CRASHLESS_LLAMA_CPP_DIR` or an installed `llama` CMake package:

```bash
cmake -S native -B native/build -DCRASHLESS_LLAMA_CPP_DIR=/absolute/path/to/llama.cpp
cmake --build native/build --config Release
```

Run the Avalonia demo:

```bash
dotnet run --project samples/CrashlessLLM.AvaloniaDemo
```

Run stress tests with a real local GGUF file:

```bash
CRASHLESS_TEST_MODEL_PATH=/absolute/path/to/small.gguf \
  dotnet test CrashlessLLM.StressTests/CrashlessLLM.StressTests.csproj
```

## Publishing

Dry-run the NuGet package pipeline:

```bash
./scripts/publish-nuget.sh --dry-run
```

Publish to NuGet.org:

```bash
NUGET_API_KEY=<token> ./scripts/publish-nuget.sh
```

The script builds Release, runs stress tests, packs `CrashlessLLM`, verifies package contents, and pushes to NuGet.org or a custom `--source`.

## Contributing and upload setup

- Contributing guide: [CONTRIBUTING.md](CONTRIBUTING.md)
- Security policy: [SECURITY.md](SECURITY.md)
- Code of conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- GitHub upload guide: [docs/GITHUB_UPLOAD.md](docs/GITHUB_UPLOAD.md)

For announcements, roadmap notes, and primary public communication, follow [@MohamedHz72007 on X](https://x.com/MohamedHz72007).

## Architecture

Read the full architecture brief in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## License

Apache-2.0 — see [LICENSE](LICENSE).
