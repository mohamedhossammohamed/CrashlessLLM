# Security Policy

CrashlessLLM is a native/managed boundary for local GGUF inference. The project focuses on defensive stability: predictable memory admission, deterministic native cleanup, safe UTF-8 streaming, and UI-thread isolation.

## Supported versions

CrashlessLLM is currently pre-1.0. Security fixes are handled on the latest public preview/alpha line.

| Version | Supported |
| --- | --- |
| `0.2.x-alpha` | Yes |
| `0.1.x-alpha` | Best effort |

## Reporting a vulnerability

Please do not open a public GitHub issue for security-sensitive reports.

Instead, use GitHub's private vulnerability reporting feature if it is enabled on the repository. If private reporting is not enabled yet, open a minimal issue asking maintainers to enable private security reporting, without including exploit details.

Include the following information in the private report:

- Affected CrashlessLLM version or commit
- Operating system and architecture
- .NET version
- Native backend details, if relevant
- Whether the issue requires a malicious GGUF file, malformed prompt, cancellation race, disposal race, or memory-pressure condition
- Minimal reproduction steps
- Expected impact

## Scope

In scope:

- Managed/native boundary safety bugs
- Native handle lifetime issues
- Reverse P/Invoke callback safety issues
- Use-after-free, double-free, memory corruption, or disposal races in CrashlessLLM code
- Predictive allocation gate bypasses that can reliably crash the host process under normal local model use
- Token streaming bugs that can cause unbounded memory growth or UI thread starvation
- Incorrect packaging that loads an unexpected native binary from the package layout

Out of scope:

- Vulnerabilities in upstream `llama.cpp`, `ggml`, GPU drivers, operating systems, or .NET runtime components
- Malicious GGUF parser exploits outside CrashlessLLM's code
- Model output safety, prompt injection, hallucination, jailbreaks, or content moderation behavior
- Denial of service caused by intentionally loading untrusted or corrupt model files
- Issues requiring modified local source code or intentionally disabled safety checks

## Security posture

CrashlessLLM is not a sandbox. Applications should not load untrusted GGUF files. The safety boundary is designed to make local model embedding more crash-resistant, not to isolate hostile native inputs from the host process.
