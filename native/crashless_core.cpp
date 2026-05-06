/**
 * @file crashless_core.cpp
 * @brief Implementation of the CrashlessLLM unmanaged safety core.
 */

#include "crashless_core.h"
#include "llama.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cfloat>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
    #include <sys/stat.h>
    #define WIN32_LEAN_AND_MEAN
    #include <windows.h>
#elif defined(__APPLE__)
    #include <mach/mach_host.h>
    #include <mach/vm_statistics.h>
    #include <sys/stat.h>
    #include <sys/sysctl.h>
#elif defined(__linux__) || defined(__ANDROID__)
    #include <sys/stat.h>
    #include <unistd.h>
#else
    #include <sys/stat.h>
#endif

namespace {

// ============================================================================
// Internal Opaque Data Structures
// ============================================================================

struct CrashlessConfig {
    int gpu_layers = 0;
    int threads = 1;
    int n_ctx = 4096;            // Conservative default context size.
    double memory_margin = 0.30; // Configurable safety margin.
};

struct CrashlessModelCtx {
    llama_model* model = nullptr;
    llama_context* ctx = nullptr;
    llama_sampler* sampler = nullptr;
    CrashlessConfig config;

    std::atomic<bool> cancel_requested{false};
    std::atomic<bool> worker_active{false};
    std::mutex worker_mutex;
    std::thread worker;
};

// ============================================================================
// Global State Management
// ============================================================================

std::once_flag g_backend_init_flag;

void initialize_backend_once() {
    std::call_once(g_backend_init_flag, []() {
        llama_backend_init();
    });
}

// ============================================================================
// Cross-Platform Telemetry and Predictive Models
// ============================================================================

uint64_t get_file_size_bytes(const char* filepath) {
    if (filepath == nullptr || filepath[0] == '\0') {
        return 0;
    }

#if defined(_WIN32)
    struct __stat64 stat_buf;
    if (_stat64(filepath, &stat_buf) == 0 && stat_buf.st_size >= 0) {
        return static_cast<uint64_t>(stat_buf.st_size);
    }
#else
    struct stat stat_buf;
    if (stat(filepath, &stat_buf) == 0 && stat_buf.st_size >= 0) {
        return static_cast<uint64_t>(stat_buf.st_size);
    }
#endif

    return 0;
}

uint64_t get_available_physical_ram() {
#if defined(_WIN32)
    MEMORYSTATUSEX status;
    std::memset(&status, 0, sizeof(status));
    status.dwLength = sizeof(status);
    if (GlobalMemoryStatusEx(&status)) {
        return static_cast<uint64_t>(status.ullAvailPhys);
    }
    return 0;
#elif defined(__APPLE__)
    mach_msg_type_number_t count = HOST_VM_INFO64_COUNT;
    vm_statistics64_data_t vmstat;
    std::memset(&vmstat, 0, sizeof(vmstat));

    if (host_statistics64(mach_host_self(), HOST_VM_INFO64,
                          reinterpret_cast<host_info64_t>(&vmstat), &count) != KERN_SUCCESS) {
        return 0;
    }

    int mib[2] = {CTL_HW, HW_PAGESIZE};
    int page_size = 0;
    size_t length = sizeof(page_size);
    if (sysctl(mib, 2, &page_size, &length, nullptr, 0) != 0 || page_size <= 0) {
        return 0;
    }

    // More realistic available memory on macOS:
    // free_count + inactive_count + speculative_count gives memory the kernel
    // could reclaim under pressure. We exclude wired (kernel/unreclaimable).
    uint64_t available_pages = static_cast<uint64_t>(vmstat.free_count)
                             + static_cast<uint64_t>(vmstat.inactive_count)
                             + static_cast<uint64_t>(vmstat.speculative_count);
    return available_pages * static_cast<uint64_t>(page_size);
#elif defined(__linux__) || defined(__ANDROID__)
    const long pages = sysconf(_SC_AVPHYS_PAGES);
    const long page_size = sysconf(_SC_PAGE_SIZE);
    if (pages > 0 && page_size > 0) {
        return static_cast<uint64_t>(pages) * static_cast<uint64_t>(page_size);
    }
    return 0;
#else
    return 0;
#endif
}

bool add_u64(uint64_t left, uint64_t right, uint64_t& result) {
    if (left > std::numeric_limits<uint64_t>::max() - right) {
        return false;
    }
    result = left + right;
    return true;
}

bool multiply_u64(uint64_t left, uint64_t right, uint64_t& result) {
    if (left > 0 && right > std::numeric_limits<uint64_t>::max() / left) {
        return false;
    }
    result = left * right;
    return true;
}

bool apply_margin(uint64_t base, double margin_fraction, uint64_t& result) {
    if (margin_fraction <= 0.0 || base == 0) {
        result = base;
        return true;
    }
    if (!std::isfinite(margin_fraction) || margin_fraction > static_cast<double>(std::numeric_limits<uint64_t>::max())) {
        return false;
    }

    double margin = static_cast<double>(base) * margin_fraction;
    if (margin > static_cast<double>(std::numeric_limits<uint64_t>::max() - base)) {
        return false;
    }

    uint64_t margin_u64 = static_cast<uint64_t>(margin);
    return add_u64(base, margin_u64, result);
}

bool is_memory_sufficient(const char* filepath,
                          int n_ctx,
                          double margin_fraction,
                          crashless_load_diagnostics* out_diag) {
    const uint64_t file_size = get_file_size_bytes(filepath);

    // Populate what we know even if we will return false.
    crashless_load_diagnostics diag{};
    diag.model_file_bytes = file_size;
    diag.native_error_code = CRASHLESS_SUCCESS;

    if (file_size == 0 || n_ctx <= 0) {
        if (out_diag != nullptr) {
            *out_diag = diag;
        }
        return false;
    }

    // Conservative heuristic: model file + aggressive worst-case KV density per token.
    constexpr uint64_t worst_case_kv_bytes_per_token = 131072ULL;
    uint64_t estimated_kv_cache = 0;
    if (!multiply_u64(static_cast<uint64_t>(n_ctx), worst_case_kv_bytes_per_token, estimated_kv_cache)) {
        if (out_diag != nullptr) {
            *out_diag = diag;
        }
        return false;
    }
    diag.estimated_kv_cache_bytes = estimated_kv_cache;

    uint64_t predicted_base_usage = 0;
    if (!add_u64(file_size, estimated_kv_cache, predicted_base_usage)) {
        if (out_diag != nullptr) {
            *out_diag = diag;
        }
        return false;
    }

    uint64_t predicted_usage_with_margin = 0;
    if (!apply_margin(predicted_base_usage, margin_fraction, predicted_usage_with_margin)) {
        if (out_diag != nullptr) {
            *out_diag = diag;
        }
        return false;
    }
    diag.safety_margin_bytes = predicted_usage_with_margin - predicted_base_usage;
    diag.predicted_total_bytes = predicted_usage_with_margin;

    const uint64_t available_ram = get_available_physical_ram();
    diag.available_physical_bytes = available_ram;

    if (out_diag != nullptr) {
        *out_diag = diag;
    }

    // If RAM telemetry is unavailable, bypass gating to avoid soft-locking unsupported targets.
    if (available_ram == 0) {
        return true;
    }

    return predicted_usage_with_margin <= available_ram;
}

// ============================================================================
// Thread-safe diagnostics storage for last failed load
// ============================================================================

std::mutex g_last_diag_mutex;
crashless_load_diagnostics g_last_diag{};
bool g_last_diag_valid = false;

void store_last_diagnostics(const crashless_load_diagnostics& diag) {
    std::lock_guard<std::mutex> lock(g_last_diag_mutex);
    g_last_diag = diag;
    g_last_diag_valid = true;
}

// ============================================================================
// llama.cpp Batch and Token Helpers
// ============================================================================

bool crashless_batch_add(llama_batch& batch,
                         int32_t capacity,
                         llama_token token,
                         llama_pos pos,
                         bool logits) {
    if (batch.n_tokens >= capacity || batch.token == nullptr || batch.pos == nullptr ||
        batch.n_seq_id == nullptr || batch.seq_id == nullptr || batch.logits == nullptr) {
        return false;
    }

    const int32_t index = batch.n_tokens;
    batch.token[index] = token;
    batch.pos[index] = pos;
    batch.n_seq_id[index] = 1;
    batch.seq_id[index][0] = 0;
    batch.logits[index] = logits ? 1 : 0;
    batch.n_tokens++;
    return true;
}

void crashless_batch_clear(llama_batch& batch) {
    batch.n_tokens = 0;
}

std::string token_to_piece(llama_model* model, llama_token token) {
    std::array<char, 256> stack_buffer{};
    int32_t length = llama_token_to_piece(
        model, token, stack_buffer.data(), static_cast<int32_t>(stack_buffer.size()), 0, true);

    if (length < 0) {
        std::string heap_buffer;
        heap_buffer.resize(static_cast<size_t>(-length));
        length = llama_token_to_piece(
            model, token, heap_buffer.data(), static_cast<int32_t>(heap_buffer.size()), 0, true);
        if (length <= 0) {
            return std::string();
        }
        heap_buffer.resize(static_cast<size_t>(length));
        return heap_buffer;
    }

    if (length <= 0) {
        return std::string();
    }

    return std::string(stack_buffer.data(), static_cast<size_t>(length));
}

void invoke_callback(crashless_generation_callback callback, const char* token_utf8, bool is_done) noexcept {
    if (callback != nullptr) {
        callback(token_utf8 != nullptr ? token_utf8 : "", is_done);
    }
}

bool is_cancelled(const CrashlessModelCtx* ctx_container, const std::atomic<bool>* external_cancel_flag) {
    if (ctx_container->cancel_requested.load(std::memory_order_relaxed)) {
        return true;
    }

    return external_cancel_flag != nullptr && external_cancel_flag->load(std::memory_order_relaxed);
}

void generation_worker(CrashlessModelCtx* ctx_container,
                       std::string prompt,
                       crashless_generation_callback callback,
                       std::atomic<bool>* external_cancel_flag) noexcept {
    struct CompletionGuard {
        CrashlessModelCtx* ctx;
        ~CompletionGuard() {
            ctx->worker_active.store(false, std::memory_order_release);
        }
    } completion{ctx_container};

    try {
        if (prompt.size() > static_cast<size_t>(std::numeric_limits<int32_t>::max())) {
            invoke_callback(callback, "", true);
            return;
        }

        llama_sampler_reset(ctx_container->sampler);

        std::vector<llama_token> tokens(prompt.size() + 8U);
        int32_t n_tokens = llama_tokenize(
            ctx_container->model,
            prompt.c_str(),
            static_cast<int32_t>(prompt.size()),
            tokens.data(),
            static_cast<int32_t>(tokens.size()),
            true,
            true);

        if (n_tokens < 0) {
            tokens.resize(static_cast<size_t>(-n_tokens));
            n_tokens = llama_tokenize(
                ctx_container->model,
                prompt.c_str(),
                static_cast<int32_t>(prompt.size()),
                tokens.data(),
                static_cast<int32_t>(tokens.size()),
                true,
                true);
        }

        if (n_tokens <= 0 || n_tokens >= ctx_container->config.n_ctx) {
            invoke_callback(callback, "", true);
            return;
        }

        tokens.resize(static_cast<size_t>(n_tokens));

        const int32_t batch_capacity = std::max<int32_t>(n_tokens, 1);
        llama_batch batch = llama_batch_init(batch_capacity, 0, 1);
        if (batch.token == nullptr) {
            invoke_callback(callback, "", true);
            return;
        }

        for (int32_t i = 0; i < n_tokens; ++i) {
            if (!crashless_batch_add(batch, batch_capacity, tokens[static_cast<size_t>(i)], i, false)) {
                llama_batch_free(batch);
                invoke_callback(callback, "", true);
                return;
            }
        }
        batch.logits[batch.n_tokens - 1] = 1;

        if (llama_decode(ctx_container->ctx, batch) != 0) {
            llama_batch_free(batch);
            invoke_callback(callback, "", true);
            return;
        }

        int32_t n_cur = batch.n_tokens;
        int32_t n_decode = 0;
        const int32_t n_predict = std::min<int32_t>(512, ctx_container->config.n_ctx - n_cur);

        while (n_decode < n_predict && !is_cancelled(ctx_container, external_cancel_flag)) {
            const llama_token new_token_id = llama_sampler_sample(ctx_container->sampler, ctx_container->ctx, -1);
            llama_sampler_accept(ctx_container->sampler, new_token_id);

            if (llama_token_is_eog(ctx_container->model, new_token_id)) {
                break;
            }

            const std::string piece = token_to_piece(ctx_container->model, new_token_id);
            if (!piece.empty()) {
                // The std::string storage is null-terminated and remains valid for this synchronous call only.
                invoke_callback(callback, piece.c_str(), false);
            }

            crashless_batch_clear(batch);
            if (!crashless_batch_add(batch, batch_capacity, new_token_id, n_cur, true)) {
                break;
            }

            if (llama_decode(ctx_container->ctx, batch) != 0) {
                break;
            }

            n_cur += 1;
            n_decode += 1;
        }

        llama_batch_free(batch);
        invoke_callback(callback, "", true);
    } catch (...) {
        invoke_callback(callback, "", true);
    }
}

void free_model_container(CrashlessModelCtx* ctx_container) noexcept {
    if (ctx_container == nullptr) {
        return;
    }

    try {
        if (ctx_container->sampler != nullptr) {
            llama_sampler_free(ctx_container->sampler);
            ctx_container->sampler = nullptr;
        }
        if (ctx_container->ctx != nullptr) {
            llama_free(ctx_container->ctx);
            ctx_container->ctx = nullptr;
        }
        if (ctx_container->model != nullptr) {
            llama_model_free(ctx_container->model);
            ctx_container->model = nullptr;
        }
    } catch (...) {
        // Destruction must never escape the C-ABI boundary.
    }

    delete ctx_container;
}

} // namespace

// ============================================================================
// C-ABI Exported API Implementation
// ============================================================================

extern "C" {

int crashless_get_api_version(void) {
    return 1;
}

int crashless_v1_create_config(int gpu_layers, int threads, void** out_config) {
    if (out_config == nullptr) {
        return ERR_INVALID_POINTER;
    }

    *out_config = nullptr;

    try {
        auto* config = new (std::nothrow) CrashlessConfig();
        if (config == nullptr) {
            return ERR_INTERNAL_EXCEPTION;
        }

        config->gpu_layers = gpu_layers;
        config->threads = std::max(1, threads);
        *out_config = static_cast<void*>(config);
        return CRASHLESS_SUCCESS;
    } catch (...) {
        return ERR_INTERNAL_EXCEPTION;
    }
}

int crashless_v1_config_set_context_size(void* config, int n_ctx) {
    if (config == nullptr) {
        return ERR_INVALID_POINTER;
    }
    if (n_ctx <= 0) {
        return ERR_INVALID_POINTER;
    }

    try {
        static_cast<CrashlessConfig*>(config)->n_ctx = n_ctx;
        return CRASHLESS_SUCCESS;
    } catch (...) {
        return ERR_INTERNAL_EXCEPTION;
    }
}

int crashless_v1_config_set_memory_margin(void* config, double margin) {
    if (config == nullptr) {
        return ERR_INVALID_POINTER;
    }

    try {
        auto* cfg = static_cast<CrashlessConfig*>(config);
        if (!std::isfinite(margin) || margin < 0.0) {
            cfg->memory_margin = 0.0;
        } else {
            cfg->memory_margin = margin;
        }
        return CRASHLESS_SUCCESS;
    } catch (...) {
        return ERR_INTERNAL_EXCEPTION;
    }
}

void crashless_v1_free_config(void* config) {
    try {
        delete static_cast<CrashlessConfig*>(config);
    } catch (...) {
    }
}

int crashless_v1_load_model_safe_ex(const char* model_path,
                                    void* config_ptr,
                                    void** out_model_ctx,
                                    crashless_load_diagnostics* out_diagnostics) {
    if (out_model_ctx == nullptr) {
        return ERR_INVALID_POINTER;
    }

    *out_model_ctx = nullptr;

    if (model_path == nullptr || config_ptr == nullptr) {
        return ERR_INVALID_POINTER;
    }

    try {
        const auto* config = static_cast<const CrashlessConfig*>(config_ptr);
        crashless_load_diagnostics diag{};

        // Stage 1: Predictive Allocation Gating.
        if (!is_memory_sufficient(model_path, config->n_ctx, config->memory_margin, &diag)) {
            diag.native_error_code = ERR_INSUFFICIENT_MEMORY_PREDICTED;
            store_last_diagnostics(diag);
            if (out_diagnostics != nullptr) {
                *out_diagnostics = diag;
            }
            return ERR_INSUFFICIENT_MEMORY_PREDICTED;
        }

        // Stage 2: Thread-safe hardware initialization.
        initialize_backend_once();

        std::unique_ptr<CrashlessModelCtx> ctx_container(new (std::nothrow) CrashlessModelCtx());
        if (!ctx_container) {
            return ERR_INTERNAL_EXCEPTION;
        }
        ctx_container->config = *config;

        llama_model_params model_params = llama_model_default_params();
        model_params.n_gpu_layers = config->gpu_layers;

        // Stage 3: Model weight loading.
        ctx_container->model = llama_model_load_from_file(model_path, model_params);
        if (ctx_container->model == nullptr) {
            return ERR_MODEL_LOAD_FAILED;
        }

        // Stage 4: Execution context loading and KV cache allocation.
        llama_context_params ctx_params = llama_context_default_params();
        ctx_params.n_ctx = static_cast<uint32_t>(config->n_ctx);
        ctx_params.n_threads = config->threads;
        ctx_params.n_threads_batch = config->threads;

        ctx_container->ctx = llama_init_from_model(ctx_container->model, ctx_params);
        if (ctx_container->ctx == nullptr) {
            llama_model_free(ctx_container->model);
            ctx_container->model = nullptr;
            return ERR_MODEL_LOAD_FAILED;
        }

        // Stage 5: Deterministic greedy sampling chain.
        llama_sampler_chain_params sparams = llama_sampler_chain_default_params();
        ctx_container->sampler = llama_sampler_chain_init(sparams);
        if (ctx_container->sampler == nullptr) {
            llama_free(ctx_container->ctx);
            ctx_container->ctx = nullptr;
            llama_model_free(ctx_container->model);
            ctx_container->model = nullptr;
            return ERR_MODEL_LOAD_FAILED;
        }
        llama_sampler_chain_add(ctx_container->sampler, llama_sampler_init_greedy());

        if (out_diagnostics != nullptr) {
            *out_diagnostics = diag;
        }

        *out_model_ctx = static_cast<void*>(ctx_container.release());
        return CRASHLESS_SUCCESS;
    } catch (...) {
        return ERR_INTERNAL_EXCEPTION;
    }
}

int crashless_v1_load_model_safe(const char* model_path, void* config_ptr, void** out_model_ctx) {
    return crashless_v1_load_model_safe_ex(model_path, config_ptr, out_model_ctx, nullptr);
}

int crashless_v1_get_last_load_diagnostics(crashless_load_diagnostics* out_diagnostics) {
    if (out_diagnostics == nullptr) {
        return ERR_INVALID_POINTER;
    }

    std::lock_guard<std::mutex> lock(g_last_diag_mutex);
    if (!g_last_diag_valid) {
        *out_diagnostics = crashless_load_diagnostics{};
        return ERR_INVALID_POINTER; // Nothing stored yet.
    }

    *out_diagnostics = g_last_diag;
    return CRASHLESS_SUCCESS;
}

int crashless_v1_generate_async(void* model_ctx_ptr,
                                const char* prompt,
                                crashless_generation_callback callback,
                                void* atomic_cancel_flag) {
    if (model_ctx_ptr == nullptr || prompt == nullptr || callback == nullptr) {
        return ERR_INVALID_POINTER;
    }

    try {
        auto* ctx_container = static_cast<CrashlessModelCtx*>(model_ctx_ptr);
        auto* external_cancel_flag = static_cast<std::atomic<bool>*>(atomic_cancel_flag);
        std::string prompt_copy(prompt);

        std::lock_guard<std::mutex> guard(ctx_container->worker_mutex);

        if (ctx_container->worker.joinable()) {
            if (ctx_container->worker_active.load(std::memory_order_acquire)) {
                return ERR_GENERATION_IN_PROGRESS;
            }
            ctx_container->worker.join();
        }

        ctx_container->cancel_requested.store(false, std::memory_order_release);
        ctx_container->worker_active.store(true, std::memory_order_release);

        try {
            ctx_container->worker = std::thread(
                generation_worker,
                ctx_container,
                std::move(prompt_copy),
                callback,
                external_cancel_flag);
        } catch (...) {
            ctx_container->worker_active.store(false, std::memory_order_release);
            return ERR_THREAD_SPAWN_FAILED;
        }

        return CRASHLESS_SUCCESS;
    } catch (...) {
        return ERR_THREAD_SPAWN_FAILED;
    }
}

void crashless_v1_cancel_generation(void* model_ctx_ptr) {
    if (model_ctx_ptr == nullptr) {
        return;
    }

    try {
        auto* ctx_container = static_cast<CrashlessModelCtx*>(model_ctx_ptr);
        ctx_container->cancel_requested.store(true, std::memory_order_release);
    } catch (...) {
    }
}

void crashless_v1_free_session_secure(void* model_ctx_ptr) {
    if (model_ctx_ptr == nullptr) {
        return;
    }

    auto* ctx_container = static_cast<CrashlessModelCtx*>(model_ctx_ptr);
    ctx_container->cancel_requested.store(true, std::memory_order_release);

    try {
        // Attempt to join the worker. If this is called from the worker thread itself,
        // std::thread::join() would deadlock (or invoke undefined behavior). We detect
        // this condition and defer cleanup to a short-lived detached helper thread so
        // that the current call returns promptly and the model context is eventually
        // freed without leaking indefinitely.
        std::thread worker_to_join;
        bool self_destruct = false;
        {
            std::lock_guard<std::mutex> guard(ctx_container->worker_mutex);
            if (ctx_container->worker.joinable()) {
                if (ctx_container->worker.get_id() == std::this_thread::get_id()) {
                    self_destruct = true;
                } else {
                    worker_to_join = std::move(ctx_container->worker);
                }
            }
        }

        if (self_destruct) {
            // Launch a detached thread that waits briefly for the current worker
            // to finish its callback scope, then performs cleanup.
            std::thread deferred_cleanup([ctx_container]() {
                // Poll until the worker marks itself inactive. The generation_worker
                // CompletionGuard sets worker_active=false when it exits.
                constexpr int max_wait_ms = 5000;
                constexpr int poll_interval_ms = 10;
                int waited_ms = 0;
                while (ctx_container->worker_active.load(std::memory_order_acquire) && waited_ms < max_wait_ms) {
                    std::this_thread::sleep_for(std::chrono::milliseconds(poll_interval_ms));
                    waited_ms += poll_interval_ms;
                }

                // After the worker is done (or we timed out), we can safely join and free.
                std::thread local_worker;
                {
                    std::lock_guard<std::mutex> guard(ctx_container->worker_mutex);
                    if (ctx_container->worker.joinable()) {
                        local_worker = std::move(ctx_container->worker);
                    }
                }
                if (local_worker.joinable()) {
                    local_worker.join();
                }
                free_model_container(ctx_container);
            });
            deferred_cleanup.detach();
            return;
        }

        if (worker_to_join.joinable()) {
            worker_to_join.join();
        }

        free_model_container(ctx_container);
    } catch (...) {
        // Swallowing exceptions on deletion prevents application crash during teardown.
    }
}

} // extern "C"
