# Project Notes

- Managed build entry point: `dotnet build CrashlessLLM.sln`.
- Stress tests: `CRASHLESS_TEST_MODEL_PATH=/absolute/path/to/small.gguf dotnet test CrashlessLLM.StressTests/CrashlessLLM.StressTests.csproj`.
- Native core build requires CMake plus either `CRASHLESS_LLAMA_CPP_DIR` or an installed `llama` CMake package.
- This project loads raw GGUF files natively; Ollama smoke tests do not validate the CrashlessLLM native boundary.
