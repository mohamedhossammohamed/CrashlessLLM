# Changelog

All notable changes to CrashlessLLM will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0-alpha] - 2025-05-07

### Added

- **Configurable sampling controls** (`SamplingOptions`)
  - Temperature, top-k, top-p, min-p, repeat penalty with configurable repeat_last_n
  - Seed support for reproducible generation
  - Greedy decoding when temperature is set to 0
  - Wired through native `crashless_sampling_params` struct and `crashless_v1_config_set_sampling_params`
- **Configurable max tokens** (`LlmLoadOptions.MaxTokens`)
  - Default 512, set to -1 for unlimited (up to context size)
  - Wired through `crashless_v1_config_set_n_predict`
- **Chat template support** (`ChatMessage` record, `ChatTemplate` static class)
  - Llama 3.x, ChatML (Qwen/DeepSeek/Yi), Mistral, Gemma, Phi formats
- **GPU backend detection API** (`LLM.QueryGpuBackends()`)
  - Returns `GpuBackend` flags enum (Metal, Cuda, Vulkan, Rocm, Sycl)
  - Native implementation uses `#if defined(GGML_USE_*)` compile-time detection
- **Accurate model architecture query** (`LLMSession.QueryModelArchInfo()`)
  - Queries `llama_model_n_layer/embd/head/head_kv` post-load
  - Computes accurate per-token KV cache bytes instead of flat 128 KB heuristic
  - Exposed as `ModelArchInfo` with `BytesPerTokenKv` property
- **Structured logging** via `Microsoft.Extensions.Logging.ILogger`
  - Session creation, generation start/stop, disposal, error events logged at Debug/Error levels
  - Falls back to `NullLogger.Instance` when no logger is provided
- **Multi-session documentation** clarifying concurrent usage pattern
  - Each `LLMSession` wraps its own native context; create multiple sessions for parallel work
- **NuGet publish script** (`scripts/publish-nuget.sh`)
  - Builds, tests, packs, and optionally pushes to nuget.org or custom feed
  - Supports `--dry-run` and `--api-key` flags
- **Cross-platform CI matrix** for native builds
  - macOS arm64, macOS x64, Linux x64, Linux arm64, Windows x64
  - Auto-clones llama.cpp, builds native core, uploads artifacts
- **7 new stress tests** covering sampling, MaxTokens, arch info, GPU query, chat templates

### Changed

- Native `CrashlessConfig` struct expanded with sampling params and `n_predict`
- `LLM.LoadSafe` wires through `MaxTokens` and `SamplingOptions` to native config
- Native sampler chain now builds dynamically based on config (greedy or temperature-based with top-k/top-p/min-p)
- Stress test native shim updated with new ABI stubs and model arch query mock

## [0.1.0-alpha] - 2025-05-07

### Added

- **Real native crashless_core** built and smoke-tested against llama.cpp on macOS arm64
  - Links to latest llama.cpp (bypass layer over `llama_model`, `llama_context`, `llama_sampler`)
  - Compatible with llama.cpp's vocab-centric API (`llama_model_get_vocab`)
  - Produces `libcrashless_core.dylib` on macOS, `.so` on Linux, `.dll` on Windows
- **Load configuration API** (`LlmLoadOptions`)
  - Configurable `Threads`, `ContextSize`, `GpuLayers`, `MemorySafetyMargin`
  - Zero-config overload `LLM.LoadSafe(path)` remains the default
- **Predictive allocation diagnostics** (`LlmLoadDiagnostics`)
  - Exposes `ModelFileBytes`, `EstimatedKvCacheBytes`, `PredictedTotalBytes`, `AvailableMemoryBytes`
  - `InsufficientHardwareMemoryException` carries a `Diagnostics` property for explainable failure
  - Native `_ex` API and `crashless_v1_get_last_load_diagnostics` for post-failure queries
- **macOS memory telemetry improvement**
  - Includes `free_count + inactive_count + speculative_count` (excludes only wired kernel pages)
  - Reduces false `InsufficientHardwareMemoryException` rejections on macOS
- **Deferred native teardown** replacing the bounded-leak self-destruction branch
  - When `Dispose()` is called from the worker thread, a detached cleanup thread polls for completion
- **Avalonia demo app** (`samples/CrashlessLLM.AvaloniaDemo`)
  - Demonstrates no-UI-thread-blocking streaming
  - Cancellation button and window-close disposal
  - Error handling for OOM and native failures
- **NuGet packaging** now conditionally includes native runtime binaries
  - `runtimes/osx-arm64/native/libcrashless_core.dylib` when pre-built
  - Infrastructure ready for `linux-x64`, `linux-arm64`, `win-x64`
- **MIT LICENSE**
- **CI matrix** expanded to macOS, Ubuntu, Windows × net8.0, net9.0
- **Pack verification job** in CI (README + LICENSE + optional native asset presence)

### Changed

- Source layout: `CrashlessLLM.cs` moved to `src/CrashlessLLM/`
- Native files moved to `native/crashless_core.cpp`, `native/crashless_core.h`, `native/CMakeLists.txt`
- `README.md` now includes known limitations, configuration examples, diagnostics examples

### Notes

- The architecture docs specify a conservative macOS policy (only `free_count`).
  The production code deviates intentionally: it includes reclaimable memory to
  avoid false rejections. The production code includes `free_count + inactive_count +
  speculative_count` (excluding only `wired_count`), which aligns better with real
  macOS memory behavior while keeping a safety margin.
