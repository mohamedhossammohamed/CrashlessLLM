## Summary

Describe what changed and why.

## Area

- [ ] Public C# API
- [ ] Native C ABI
- [ ] Predictive allocation gating
- [ ] Token streaming / backpressure
- [ ] Sampling / max tokens
- [ ] Chat templates
- [ ] GPU backend or model architecture query
- [ ] Avalonia sample
- [ ] CI / packaging / NuGet
- [ ] Documentation / website

## Verification

Commands run:

```bash
# paste commands here
```

Results:

- [ ] `dotnet build CrashlessLLM.sln`
- [ ] Relevant stress tests pass
- [ ] Native build checked, if native code changed
- [ ] Documentation updated, if public behavior changed

## Native boundary checklist

If this PR changes native interop:

- [ ] `native/crashless_core.h` updated
- [ ] `native/crashless_core.cpp` updated
- [ ] Managed P/Invoke declarations updated
- [ ] Stress-test native shim updated
- [ ] Error codes and diagnostics documented
- [ ] No C++ types, exceptions, STL containers, or ownership semantics cross the C ABI

## Safety checklist

- [ ] No secrets, local model files, build artifacts, or local tool configs committed
- [ ] No unbounded UI-thread blocking introduced
- [ ] No unmanaged pointer lifetime regression introduced
- [ ] Cancellation/disposal behavior considered
- [ ] Backpressure behavior considered
