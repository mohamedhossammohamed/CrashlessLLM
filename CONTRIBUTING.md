# Contributing to CrashlessLLM

Thank you for helping improve CrashlessLLM. This project is a native/managed safety boundary for local GGUF inference in .NET and Avalonia applications, so contributions should preserve the core stability goals: deterministic failure, no UI thread blocking, explicit native ownership, bounded streaming pressure, and clear diagnostics.

## Development requirements

- .NET 8 SDK or .NET 9 SDK
- CMake for native builds
- A local `llama.cpp` checkout or installed `llama` CMake package for real native builds
- A small local `.gguf` model for real inference smoke tests

Managed build:

```bash
dotnet build CrashlessLLM.sln
```

Native build:

```bash
cmake -S native -B native/build -DCRASHLESS_LLAMA_CPP_DIR=/absolute/path/to/llama.cpp
cmake --build native/build --config Release
```

Stress tests:

```bash
CRASHLESS_TEST_MODEL_PATH=/absolute/path/to/small.gguf \
  dotnet test CrashlessLLM.StressTests/CrashlessLLM.StressTests.csproj
```

## Design principles

1. **Managed callers should receive deterministic failures.** Native errors must be mapped to explicit managed exceptions or diagnostic return values.
2. **No C++ ownership crosses the ABI.** The native boundary must remain a flat, versioned C ABI using primitive values, opaque pointers, explicit structs, callbacks, and error codes.
3. **The UI thread must never be the pressure valve.** Token bursts should be absorbed or backpressured away from Avalonia rendering and input handling.
4. **Native memory pressure must be predicted before allocation.** Changes to model admission should preserve or improve predictive gating behavior.
5. **Callbacks must never throw across reverse P/Invoke.** Copy native bytes immediately, complete channels safely, and cancel native generation on callback failures.
6. **Disposal must be deterministic.** Any new native resource must have a clear release path through `crashless_v1_free_session_secure` and `SafeLlmContextHandle`.

## Pull request checklist

Before opening a PR, please verify:

- [ ] `dotnet build CrashlessLLM.sln` succeeds.
- [ ] Relevant stress tests pass.
- [ ] Native C ABI changes are reflected in both `native/crashless_core.h` and managed P/Invoke definitions.
- [ ] ABI changes are added to the deterministic stress-test shim.
- [ ] Public API changes are documented in `README.md` and `docs/ARCHITECTURE.md`.
- [ ] `CHANGELOG.md` includes user-visible changes.
- [ ] No secrets, local model files, build artifacts, or local tool configs are committed.

## Coding guidance

### C# managed layer

- Keep public APIs small and safe by default.
- Use nullable annotations accurately.
- Prefer `SafeHandle`, `IDisposable`, `CancellationToken`, and `IAsyncEnumerable<string>` patterns already present in the codebase.
- Avoid allocating in hot token callback paths unless necessary.
- Keep logging structured and optional through `Microsoft.Extensions.Logging` abstractions.

### Native C++ layer

- Do not expose C++ types, exceptions, STL containers, or ownership conventions across the ABI.
- Catch all exceptions before returning through exported C functions.
- Keep exported structs blittable and versionable.
- Update platform-specific memory telemetry carefully; false acceptance is worse than false rejection.
- Keep callback pointers ephemeral and documented.

### Tests

CrashlessLLM stress tests use a deterministic ABI-compatible native shim to validate the managed boundary independently from model sampling quality. Add shim coverage for new ABI functions before relying on real model behavior.

## Reporting issues

Use the GitHub issue templates for bugs and feature requests. For security-sensitive issues, do not open a public issue; follow `SECURITY.md`.
