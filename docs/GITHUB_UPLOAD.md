# GitHub Upload Guide

This file is a copy/paste checklist for creating and publishing the CrashlessLLM repository on GitHub.

## Recommended repository identity

### Best default

- **Repository name:** `CrashlessLLM`
- **Owner:** `mohamedhossammohamed`
- **Brand/author:** `MHMZAHRAN`
- **Primary announcement/contact channel:** X / Twitter `@MohamedHz72007` (`https://x.com/MohamedHz72007`)
- **Repository URL:** `https://github.com/mohamedhossammohamed/CrashlessLLM`
- **Short description:** `Crash-resistant local GGUF inference for .NET and Avalonia apps.`
- **Website / Pages path:** enable GitHub Pages from the repository root or `/docs` only if you move the landing site. The current landing site is root-level `index.html`, `styles.css`, and `script.js`.
- **Designed docs URL:** `https://mohamedhossammohamed.github.io/CrashlessLLM/docs/`
- **Topics:** `dotnet`, `avalonia`, `llm`, `gguf`, `llama-cpp`, `native-interop`, `p-invoke`, `local-ai`, `desktop-ai`, `crash-resistance`
- **Social link:** `https://x.com/MohamedHz72007`

### Alternative repo names

| Name | When to use |
| --- | --- |
| `CrashlessLLM` | Best for a package/product-style open-source repo. |
| `crashless-llm` | Best if you prefer lowercase/kebab-case GitHub names. |
| `CrashlessLLM.NET` | Best if you want the .NET focus visible in the repo name. |
| `CrashlessLLM-Avalonia` | Best only if the project becomes Avalonia-specific rather than .NET-first. |

## GitHub About text

Use this as the repository description:

```text
Crash-resistant local GGUF inference for .NET and Avalonia apps.
```

Use this as the longer social/release description:

```text
CrashlessLLM is a zero-config native embedding layer for local LLMs in .NET/Avalonia apps. It uses predictive allocation gating, bounded token backpressure, SafeHandle ownership, UTF-8 fragment reconstruction, configurable sampling, chat templates, GPU backend detection, and structured diagnostics to keep desktop apps responsive during local inference.
```

## Suggested first release title

```text
CrashlessLLM 0.2.0-alpha — crash-resistant local GGUF inference for .NET
```

## Suggested first release notes

```markdown
## Highlights

- Predictive allocation gating before native model load
- Bounded channel backpressure for UI-safe streaming
- SafeHandle-backed native session ownership
- UTF-8 fragment reconstruction across native token callbacks
- Configurable sampling: temperature, top-k, top-p, min-p, repeat penalty, seed
- Configurable `MaxTokens`
- Chat templates for Llama 3, ChatML, Mistral, Gemma, and Phi
- GPU backend detection for Metal, CUDA, Vulkan, ROCm, and SYCL
- Post-load model architecture query with accurate KV bytes/token
- Structured logging via `Microsoft.Extensions.Logging.Abstractions`
- Cross-platform native CI matrix
- NuGet publish script

## Verification

- `dotnet build CrashlessLLM.sln`
- `dotnet test CrashlessLLM.StressTests/CrashlessLLM.StressTests.csproj`
- 12/12 stress tests passing
```

## X / Twitter launch thread

A copy-ready technical launch thread is available in [docs/X_LAUNCH_THREAD.md](X_LAUNCH_THREAD.md).

## Before first push

1. Confirm all generated files are staged:

   ```bash
   git status --short
   ```

2. Confirm no local tool config, build output, models, or native build artifacts are tracked unexpectedly:

   ```bash
   git ls-files | grep -E '(^|/)(bin|obj|build)/|\.devin/config|\.nupkg$|\.gguf$'
   ```

3. Confirm formatting and build:

   ```bash
   git diff --check
   dotnet build CrashlessLLM.sln --no-restore
   ```

4. Run stress tests:

   ```bash
   CRASHLESS_TEST_MODEL_PATH=/absolute/path/to/small.gguf \
     dotnet test CrashlessLLM.StressTests/CrashlessLLM.StressTests.csproj
   ```

## Create the new GitHub repository

Create a new empty repository on GitHub with:

- Name: `CrashlessLLM`
- Visibility: public
- README: do not initialize on GitHub; this repo already has one
- License: do not initialize on GitHub; this repo already has Apache-2.0
- Gitignore: do not initialize on GitHub; this repo already has one

## Add the remote and push

```bash
git remote add origin https://github.com/mohamedhossammohamed/CrashlessLLM.git
git branch -M main
git push -u origin main
```

## After push

In GitHub repository settings:

1. Enable GitHub Pages for the root landing site.
2. Enable private vulnerability reporting if available.
3. Add repository topics from the list above.
4. Add NuGet API key as an Actions secret only when ready to publish packages.
5. Protect `main` after the first push if you want PR-required workflow.
