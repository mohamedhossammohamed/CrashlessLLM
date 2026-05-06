# Changelog

All notable changes to CrashlessLLM will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
  avoid false rejections. See `ARCHITECTURE-DEVIATIONS.md` for the full rationale.
