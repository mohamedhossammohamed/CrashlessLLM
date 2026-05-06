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
#include <stdint.h>

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
// Load Diagnostics
// ============================================================================
/**
 * @brief Structured telemetry from the predictive allocation gate.
 *
 * Filled when load_model_safe_ex is called with a non-null diagnostics pointer,
 * or when querying the last failed load via crashless_v1_get_last_load_diagnostics.
 */
typedef struct {
    uint64_t model_file_bytes;
    uint64_t estimated_kv_cache_bytes;
    uint64_t safety_margin_bytes;
    uint64_t predicted_total_bytes;
    uint64_t available_physical_bytes;
    int32_t  native_error_code;
} crashless_load_diagnostics;

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
 * @brief Sets the context size on an opaque configuration block.
 * @param config Opaque configuration handle.
 * @param n_ctx Context size in tokens. Must be > 0.
 * @return CRASHLESS_SUCCESS on success, ERR_INVALID_POINTER if config is null.
 */
CRASHLESS_API int crashless_v1_config_set_context_size(void* config, int n_ctx);

/**
 * @brief Sets the memory safety margin on an opaque configuration block.
 * @param config Opaque configuration handle.
 * @param margin Fractional margin >= 0.0 (e.g. 0.30 for 30%).
 *               Values < 0 are clamped to 0.
 * @return CRASHLESS_SUCCESS on success, ERR_INVALID_POINTER if config is null.
 */
CRASHLESS_API int crashless_v1_config_set_memory_margin(void* config, double margin);

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
 * @brief Extended load with optional diagnostic telemetry.
 *
 * Identical behavior to crashless_v1_load_model_safe, but if out_diagnostics
 * is non-null it is filled with predictive-gate telemetry regardless of outcome.
 *
 * @param model_path Absolute or process-relative filepath to the GGUF model.
 * @param config Opaque handle to the configuration object.
 * @param out_model_ctx Pointer to a void* receiving the session context handle.
 * @param out_diagnostics Optional pointer to receive allocation-gate telemetry.
 *                        May be NULL.
 * @return CRASHLESS_SUCCESS or an error code.
 */
CRASHLESS_API int crashless_v1_load_model_safe_ex(const char* model_path,
                                                  void* config,
                                                  void** out_model_ctx,
                                                  crashless_load_diagnostics* out_diagnostics);

/**
 * @brief Retrieves diagnostics from the most recent failed load attempt.
 *
 * Useful when the managed caller used crashless_v1_load_model_safe (without
 * the _ex diagnostics pointer) and needs to explain the failure.
 *
 * @param out_diagnostics Pointer to receive the telemetry. Must not be NULL.
 * @return CRASHLESS_SUCCESS if valid diagnostics were written.
 */
CRASHLESS_API int crashless_v1_get_last_load_diagnostics(crashless_load_diagnostics* out_diagnostics);

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
