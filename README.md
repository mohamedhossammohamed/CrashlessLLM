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
- Native C ABI source: `crashless_core.cpp`, `crashless_core.h`
- Console smoke sample: `samples/CrashlessLLM.ConsoleSmoke`
- Deterministic xUnit stress tests: `CrashlessLLM.StressTests`
- GitHub Actions CI scaffold: `.github/workflows/ci.yml`

Not ready for final production distribution until native binaries are built and attached for the target platforms.

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

## Build

Managed build requires .NET 8 or .NET 9:

```bash
dotnet build CrashlessLLM.sln
```

Native build requires CMake and a local `llama.cpp` checkout or installed `llama` CMake package:

```bash
cmake -S . -B build -DCRASHLESS_LLAMA_CPP_DIR=/absolute/path/to/llama.cpp
cmake --build build --config Release
```

On macOS, the produced `libcrashless_core.dylib` must be discoverable by the .NET process, for example by placing it next to the app binary or configuring the platform library search path.

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
dotnet test CrashlessLLM.StressTests/CrashlessLLM.StressTests.csproj
```

The default `TestConfig.ModelPath` remains a placeholder and should be changed or overridden with `CRASHLESS_TEST_MODEL_PATH`.

## Publish checklist

Before calling this production-ready for users:

- Build and smoke-test `crashless_core` for each target RID.
- Package native binaries under the correct runtime folders.
- Run stress tests on macOS, Linux, and Windows.
- Run at least one real raw GGUF inference smoke test per platform.
- Decide and add a repository license.
- Add release artifacts and versioning policy.
