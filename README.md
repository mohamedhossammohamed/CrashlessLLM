# CrashlessLLM

CrashlessLLM is a managed boundary layer for running local GGUF LLMs through a native `llama.cpp`-style C ABI without letting native memory pressure, callback faults, or token streaming overwhelm a .NET UI process.

The MVP API is intentionally small:

```csharp
using CrashlessLLM.Interop;

using var session = LLM.LoadSafe("/absolute/path/to/model.gguf");

await foreach (string token in session.StreamAsync("Say hello."))
{
    Console.Write(token);
}
```

## Current status

This repository is now structured as a GitHub-ready MVP, not a finished NuGet binary distribution.

Ready:

- Managed library project: `src/CrashlessLLM/CrashlessLLM.csproj`
- Native C ABI source: `native/crashless_core.cpp`, `native/crashless_core.h`
- Console smoke sample: `samples/CrashlessLLM.ConsoleSmoke`
- Avalonia UI demo: `samples/CrashlessLLM.AvaloniaDemo`
- Deterministic xUnit stress tests: `CrashlessLLM.StressTests`
- GitHub Actions CI scaffold: `.github/workflows/ci.yml`

Native binaries:

- `runtimes/osx-arm64/native/libcrashless_core.dylib` is built and included in the package
- Linux x64/arm64 and Windows x64 native binaries still require cross-platform CI builds

See `ARCHITECTURE-DEVIATIONS.md` for documented differences between the research architecture and production implementation.

## Known limitations

- **Only raw GGUF local models**: CrashlessLLM loads raw `.gguf` files natively through `crashless_core`. It does not call Ollama, REST APIs, or external services.
- **No Ollama runtime dependency**: The native core links directly against llama.cpp. An Ollama installation is not required and will not be used.
- **Native binaries must be present**: The managed library uses P/Invoke to `crashless_core`. If the native library is not discoverable at runtime, `DllNotFoundException` is thrown.
- **No sandboxing against malicious GGUF files**: Security relies on llama.cpp parsing behavior. Do not load untrusted model files.
- **No tokenizer or model quality guarantees**: Invalid or corrupt GGUF files may be rejected by llama.cpp with `NativeInferenceException`.
- **Current generation limit is native-default/internal**: Maximum generation length is currently a native default (up to 512 tokens or context limit). A configurable limit will be added in a future release.
- **Concurrent streams per session are rejected/serialized**: Only one `StreamAsync` call may be active per `LLMSession` at a time. Additional calls will throw `NativeInferenceException` with `ERR_GENERATION_IN_PROGRESS`.

## Ollama note

The Ollama model `socialnetwooky/llama3.2-abliterated:1b_q8` is useful as a quick local inference sanity check, but Ollama does not exercise the CrashlessLLM native boundary. CrashlessLLM loads raw GGUF files through `crashless_core`; it does not call Ollama, REST APIs, or external services.

To smoke-test Ollama itself:

```bash
ollama run socialnetwooky/llama3.2-abliterated:1b_q8 "Return exactly OK."
```

To test CrashlessLLM, provide a local raw `.gguf` path. Ollama stores this model as a raw `GGUF` blob at the path shown by `ollama show socialnetwooky/llama3.2-abliterated:1b_q8 --modelfile`.

```bash
dotnet run --project samples/CrashlessLLM.ConsoleSmoke -- /absolute/path/to/model.gguf "Return exactly OK."
```

### Avalonia UI demo

An Avalonia sample demonstrates streaming into a TextBox without blocking the UI thread:

```bash
dotnet run --project samples/CrashlessLLM.AvaloniaDemo
```

Features: generate/cancel buttons, OOM diagnostics display, safe disposal on window close.

## Build

Managed build requires .NET 8 or .NET 9:

```bash
dotnet build CrashlessLLM.sln
```

Native build requires CMake and a local `llama.cpp` checkout or installed `llama` CMake package:

```bash
cd native
cmake -S . -B build -DCRASHLESS_LLAMA_CPP_DIR=/absolute/path/to/llama.cpp
cmake --build build --config Release
```

On macOS, the produced `libcrashless_core.dylib` must be discoverable by the .NET process, for example by placing it next to the app binary or configuring the platform library search path.

## Configuration API

Zero-config remains the default, but production callers can override parameters:

```csharp
using var session = LLM.LoadSafe("/path/to/model.gguf", new LlmLoadOptions
{
    Threads = 4,
    ContextSize = 2048,
    GpuLayers = 0,          // CPU-only
    MemorySafetyMargin = 0.20
});
```

## Diagnostics

When `InsufficientHardwareMemoryException` is thrown, detailed telemetry is available:

```csharp
try
{
    using var session = LLM.LoadSafe("/path/to/huge-model.gguf");
}
catch (InsufficientHardwareMemoryException ex) when (ex.Diagnostics is {} d)
{
    Console.WriteLine($"Model file: {d.ModelFileBytes:N0} bytes");
    Console.WriteLine($"KV estimate: {d.EstimatedKvCacheBytes:N0} bytes");
    Console.WriteLine($"Predicted total: {d.PredictedTotalBytes:N0} bytes");
    Console.WriteLine($"Available memory: {d.AvailableMemoryBytes:N0} bytes");
}
```

## Stress tests

The stress tests use a deterministic ABI-compatible native shim. They validate the managed boundary without depending on model sampling behavior:

- fragmented UTF-8 reconstruction
- bounded backpressure under stalled UI consumption
- 500 rapid load/stream/cancel/dispose cycles
- predictive OOM exception mapping
- Avalonia-style single-threaded UI starvation resistance

Run with any local GGUF file or Ollama GGUF blob no larger than 2 GiB:

```bash
CRASHLESS_TEST_MODEL_PATH=/absolute/path/to/small-model.gguf \
dotnet test CrashlessLLM.StressTests/CrashlessLLM.StressTests.csproj --configuration Release --framework net8.0
```

The default `TestConfig.ModelPath` remains a placeholder and should be changed or overridden with `CRASHLESS_TEST_MODEL_PATH`.

## Publish checklist

Before calling this production-ready for users:

- [ ] Build and smoke-test `crashless_core` for each target RID.
- [ ] Package native binaries under the correct runtime folders.
- [ ] Run stress tests on macOS, Linux, and Windows.
- [ ] Run at least one real raw GGUF inference smoke test per platform.
- [ ] Tag release as `v0.1.0-alpha` or `v0.1.0-preview`.

## License

MIT — see [LICENSE](LICENSE).
