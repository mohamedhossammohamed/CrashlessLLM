/**
 * @file crashless_core.h
 * @brief Safety-Critical Boundary Layer for CrashlessLLM.
 *
 * Exposes a deterministic, crash-resistant, and version-stable C-ABI bridging
 * llama.cpp and managed UI runtimes. No C++ types cross this boundary.
 */

#ifndef CRASHLESS_CORE_H
#define CRASHLESS_CORE_H

#include <stdbool.h>
#include <stddef.h>

#if defined(_WIN32)
    #if defined(CRASHLESS_CORE_BUILD)
        #define CRASHLESS_API __declspec(dllexport)
    #else
        #define CRASHLESS_API __declspec(dllimport)
    #endif
#else
    #define CRASHLESS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// Error Codes Taxonomy
// ============================================================================
#define CRASHLESS_SUCCESS                           0
#define ERR_INVALID_POINTER                        -1
#define ERR_INSUFFICIENT_MEMORY_PREDICTED          -100
#define ERR_MODEL_LOAD_FAILED                      -101
#define ERR_INTERNAL_EXCEPTION                     -102
#define ERR_THREAD_SPAWN_FAILED                    -103
#define ERR_GENERATION_IN_PROGRESS                 -104

// ============================================================================
// Strict Callback Contract
// ============================================================================
/**
 * @brief Ephemeral callback invoked synchronously during token generation.
 * @param token_utf8 A null-terminated UTF-8 token piece.
 *                   WARNING: The pointer is valid only for the duration of the
 *                   callback. Managed callers must copy bytes immediately.
 * @param is_done    True when generation has completed, failed, or was cancelled.
 */
typedef void (*crashless_generation_callback)(const char* token_utf8, bool is_done);

// ============================================================================
// Core API Interface (Version 1)
// ============================================================================

/**
 * @brief Validates ABI stability and versioning.
 * @return Integer representing the API version. Returns 1.
 */
CRASHLESS_API int crashless_get_api_version(void);

/**
 * @brief Allocates an opaque configuration block for context creation.
 * @param gpu_layers Number of layers to offload to GPU (-1 for maximum).
 * @param threads Number of CPU threads to utilize during evaluation.
 * @param out_config Pointer to a void* that receives the opaque handle.
 * @return CRASHLESS_SUCCESS on success, error code on failure.
 */
CRASHLESS_API int crashless_v1_create_config(int gpu_layers, int threads, void** out_config);

/**
 * @brief Frees a configuration block created by crashless_v1_create_config.
 * @param config Opaque configuration handle.
 */
CRASHLESS_API void crashless_v1_free_config(void* config);

/**
 * @brief Safely loads a GGUF model utilizing predictive allocation gating.
 *
 * Computes heuristic RAM requirements (File Size + Worst-case KV cache + 30%
 * margin). Actively refuses allocation if estimated usage exceeds physical RAM.
 *
 * @param model_path Absolute or process-relative filepath to the GGUF model.
 * @param config Opaque handle to the configuration object.
 * @param out_model_ctx Pointer to a void* receiving the session context handle.
 * @return CRASHLESS_SUCCESS or ERR_INSUFFICIENT_MEMORY_PREDICTED.
 */
CRASHLESS_API int crashless_v1_load_model_safe(const char* model_path, void* config, void** out_model_ctx);

/**
 * @brief Spawns a background worker thread for non-blocking text generation.
 *
 * Executes generation away from the caller thread and polls cancellation using
 * lock-free std::atomic<bool> semantics inside the native session. The optional
 * atomic_cancel_flag parameter may point to a native std::atomic<bool> for C/C++
 * callers; managed callers should use crashless_v1_cancel_generation.
 *
 * @param model_ctx Opaque handle to the active, isolated session context.
 * @param prompt Null-terminated UTF-8 input prompt sequence.
 * @param callback Function pointer for receiving ephemeral tokens.
 * @param atomic_cancel_flag Optional pointer to a native std::atomic<bool>.
 * @return CRASHLESS_SUCCESS immediately. Generation occurs asynchronously.
 */
CRASHLESS_API int crashless_v1_generate_async(void* model_ctx,
                                              const char* prompt,
                                              crashless_generation_callback callback,
                                              void* atomic_cancel_flag);

/**
 * @brief Requests cancellation of any active generation for this session.
 * @param model_ctx Opaque handle to the active session context.
 */
CRASHLESS_API void crashless_v1_cancel_generation(void* model_ctx);

/**
 * @brief Executes deterministic sequential cleanup of the session.
 * @param model_ctx Opaque handle to the active session context.
 */
CRASHLESS_API void crashless_v1_free_session_secure(void* model_ctx);

#ifdef __cplusplus
}
#endif

#endif // CRASHLESS_CORE_H
