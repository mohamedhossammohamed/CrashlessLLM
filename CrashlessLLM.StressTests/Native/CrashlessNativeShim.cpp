#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>

#if defined(_WIN32)
#include <sys/stat.h>
#define CRASHLESS_STRESS_API __declspec(dllexport)
#else
#include <sys/stat.h>
#define CRASHLESS_STRESS_API __attribute__((visibility("default")))
#endif

extern "C" {

typedef void (*crashless_generation_callback)(const char* token_utf8, bool is_done);

static constexpr int CRASHLESS_SUCCESS = 0;
static constexpr int ERR_INVALID_POINTER = -1;
static constexpr int ERR_INSUFFICIENT_MEMORY_PREDICTED = -100;
static constexpr int ERR_INTERNAL_EXCEPTION = -102;
static constexpr int ERR_THREAD_SPAWN_FAILED = -103;
static constexpr int ERR_GENERATION_IN_PROGRESS = -104;

struct crashless_load_diagnostics {
    uint64_t model_file_bytes = 0;
    uint64_t estimated_kv_cache_bytes = 0;
    uint64_t safety_margin_bytes = 0;
    uint64_t predicted_total_bytes = 0;
    uint64_t available_physical_bytes = 0;
    int32_t  native_error_code = 0;
};

struct ShimConfig {
    int gpu_layers = 0;
    int threads = 1;
};

struct ShimContext {
    std::atomic<bool> cancel_requested{false};
    std::atomic<bool> worker_active{false};
    std::mutex worker_mutex;
    std::thread worker;
};

static std::atomic<long long> g_active_sessions{0};
static std::atomic<long long> g_active_workers{0};
static std::atomic<long long> g_generated_callbacks{0};
static std::atomic<long long> g_completed_workers{0};
static std::atomic<unsigned long long> g_last_worker_thread_hash{0};

static bool contains(const std::string& text, const char* needle) {
    return text.find(needle) != std::string::npos;
}

static std::uint64_t file_size_bytes(const char* path) {
    if (path == nullptr || path[0] == '\0') {
        return 0;
    }

#if defined(_WIN32)
    struct __stat64 st {};
    if (_stat64(path, &st) == 0 && st.st_size >= 0) {
        return static_cast<std::uint64_t>(st.st_size);
    }
#else
    struct stat st {};
    if (stat(path, &st) == 0 && st.st_size >= 0) {
        return static_cast<std::uint64_t>(st.st_size);
    }
#endif

    return 0;
}

static std::uint64_t simulated_available_ram_bytes() {
    const char* value = std::getenv("CRASHLESS_STRESS_SIMULATED_AVAILABLE_RAM_BYTES");
    if (value == nullptr || value[0] == '\0') {
        return 512ULL * 1024ULL * 1024ULL;
    }

    char* end = nullptr;
    const unsigned long long parsed = std::strtoull(value, &end, 10);
    return parsed == 0 ? 512ULL * 1024ULL * 1024ULL : static_cast<std::uint64_t>(parsed);
}

static int parse_count_after_marker(const std::string& prompt, const char* marker, int fallback) {
    const std::size_t start = prompt.find(marker);
    if (start == std::string::npos) {
        return fallback;
    }

    const std::size_t value_start = start + std::strlen(marker);
    const int parsed = std::atoi(prompt.c_str() + value_start);
    return parsed > 0 ? parsed : fallback;
}

static bool should_cancel(const ShimContext* context) {
    return context == nullptr || context->cancel_requested.load(std::memory_order_acquire);
}

static void emit_token(ShimContext* context, crashless_generation_callback callback, const char* token) {
    if (should_cancel(context) || callback == nullptr) {
        return;
    }

    g_generated_callbacks.fetch_add(1, std::memory_order_relaxed);
    callback(token, false);
}

static void emit_done(crashless_generation_callback callback) {
    if (callback != nullptr) {
        callback("", true);
    }
}

static void run_utf8_split(ShimContext* context, crashless_generation_callback callback) {
    const char first[] = {static_cast<char>(0xF0), static_cast<char>(0x9F), '\0'};
    const char second[] = {static_cast<char>(0x92), static_cast<char>(0x80), '\0'};

    emit_token(context, callback, first);
    std::this_thread::sleep_for(std::chrono::milliseconds(10));
    emit_token(context, callback, second);
    emit_done(callback);
}

static void run_flood(ShimContext* context, crashless_generation_callback callback, int count) {
    for (int i = 0; i < count && !should_cancel(context); ++i) {
        emit_token(context, callback, "x");
    }

    emit_done(callback);
}

static void run_cancel_probe(ShimContext* context, crashless_generation_callback callback) {
    int iterations = 0;
    while (!should_cancel(context) && iterations < 1000000) {
        emit_token(context, callback, "c");
        ++iterations;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }

    emit_done(callback);
}

static void run_ui_delay(ShimContext* context, crashless_generation_callback callback) {
    std::this_thread::sleep_for(std::chrono::milliseconds(1000));
    emit_token(context, callback, "ui");
    emit_done(callback);
}

static void run_default(ShimContext* context, crashless_generation_callback callback) {
    emit_token(context, callback, "ok");
    emit_done(callback);
}

static void worker_entry(ShimContext* context, std::string prompt, crashless_generation_callback callback) noexcept {
    g_last_worker_thread_hash.store(
        static_cast<unsigned long long>(std::hash<std::thread::id>{}(std::this_thread::get_id())),
        std::memory_order_release);

    try {
        if (contains(prompt, "CRASHLESS_STRESS_UTF8_SPLIT")) {
            run_utf8_split(context, callback);
        } else if (contains(prompt, "CRASHLESS_STRESS_FLOOD:")) {
            run_flood(context, callback, parse_count_after_marker(prompt, "CRASHLESS_STRESS_FLOOD:", 100000));
        } else if (contains(prompt, "CRASHLESS_STRESS_CANCEL")) {
            run_cancel_probe(context, callback);
        } else if (contains(prompt, "CRASHLESS_STRESS_UI_DELAY")) {
            run_ui_delay(context, callback);
        } else {
            run_default(context, callback);
        }
    } catch (...) {
        emit_done(callback);
    }

    context->worker_active.store(false, std::memory_order_release);
    g_active_workers.fetch_sub(1, std::memory_order_acq_rel);
    g_completed_workers.fetch_add(1, std::memory_order_relaxed);
}

CRASHLESS_STRESS_API int crashless_get_api_version(void) {
    return 1;
}

CRASHLESS_STRESS_API int crashless_v1_create_config(int gpu_layers, int threads, void** out_config) {
    if (out_config == nullptr) {
        return ERR_INVALID_POINTER;
    }

    *out_config = nullptr;

    try {
        auto* config = new ShimConfig();
        config->gpu_layers = gpu_layers;
        config->threads = std::max(1, threads);
        *out_config = static_cast<void*>(config);
        return CRASHLESS_SUCCESS;
    } catch (...) {
        return ERR_INTERNAL_EXCEPTION;
    }
}

CRASHLESS_STRESS_API void crashless_v1_free_config(void* config) {
    delete static_cast<ShimConfig*>(config);
}

CRASHLESS_STRESS_API int crashless_v1_config_set_context_size(void* config, int n_ctx) {
    if (config == nullptr || n_ctx <= 0) {
        return ERR_INVALID_POINTER;
    }
    return CRASHLESS_SUCCESS;
}

CRASHLESS_STRESS_API int crashless_v1_config_set_memory_margin(void* config, double) {
    if (config == nullptr) {
        return ERR_INVALID_POINTER;
    }
    return CRASHLESS_SUCCESS;
}

static int load_model_safe_impl(const char* model_path, void* config, void** out_model_ctx, crashless_load_diagnostics* out_diag) {
    if (model_path == nullptr || config == nullptr || out_model_ctx == nullptr) {
        return ERR_INVALID_POINTER;
    }

    *out_model_ctx = nullptr;

    const std::string path(model_path);
    if (contains(path, "crashless_stress_oom")) {
        const std::uint64_t file_size = file_size_bytes(model_path);
        if (file_size > simulated_available_ram_bytes()) {
            if (out_diag != nullptr) {
                out_diag->model_file_bytes = file_size;
                out_diag->available_physical_bytes = simulated_available_ram_bytes();
                out_diag->native_error_code = ERR_INSUFFICIENT_MEMORY_PREDICTED;
            }
            return ERR_INSUFFICIENT_MEMORY_PREDICTED;
        }
    }

    try {
        auto* context = new ShimContext();
        *out_model_ctx = static_cast<void*>(context);
        g_active_sessions.fetch_add(1, std::memory_order_relaxed);
        if (out_diag != nullptr) {
            out_diag->model_file_bytes = file_size_bytes(model_path);
            out_diag->available_physical_bytes = simulated_available_ram_bytes();
            out_diag->native_error_code = CRASHLESS_SUCCESS;
        }
        return CRASHLESS_SUCCESS;
    } catch (...) {
        return ERR_INTERNAL_EXCEPTION;
    }
}

CRASHLESS_STRESS_API int crashless_v1_load_model_safe(const char* model_path, void* config, void** out_model_ctx) {
    return load_model_safe_impl(model_path, config, out_model_ctx, nullptr);
}

CRASHLESS_STRESS_API int crashless_v1_load_model_safe_ex(const char* model_path, void* config, void** out_model_ctx, crashless_load_diagnostics* out_diag) {
    return load_model_safe_impl(model_path, config, out_model_ctx, out_diag);
}

CRASHLESS_STRESS_API int crashless_v1_get_last_load_diagnostics(crashless_load_diagnostics* out_diag) {
    if (out_diag == nullptr) {
        return ERR_INVALID_POINTER;
    }
    *out_diag = crashless_load_diagnostics{};
    return CRASHLESS_SUCCESS;
}

CRASHLESS_STRESS_API int crashless_v1_generate_async(
    void* model_ctx,
    const char* prompt,
    crashless_generation_callback callback,
    void*) {
    if (model_ctx == nullptr || prompt == nullptr || callback == nullptr) {
        return ERR_INVALID_POINTER;
    }

    auto* context = static_cast<ShimContext*>(model_ctx);

    try {
        std::lock_guard<std::mutex> guard(context->worker_mutex);

        if (context->worker.joinable()) {
            if (context->worker_active.load(std::memory_order_acquire)) {
                return ERR_GENERATION_IN_PROGRESS;
            }

            context->worker.join();
        }

        context->cancel_requested.store(false, std::memory_order_release);
        context->worker_active.store(true, std::memory_order_release);
        g_active_workers.fetch_add(1, std::memory_order_acq_rel);

        try {
            context->worker = std::thread(worker_entry, context, std::string(prompt), callback);
        } catch (...) {
            context->worker_active.store(false, std::memory_order_release);
            g_active_workers.fetch_sub(1, std::memory_order_acq_rel);
            return ERR_THREAD_SPAWN_FAILED;
        }

        return CRASHLESS_SUCCESS;
    } catch (...) {
        return ERR_THREAD_SPAWN_FAILED;
    }
}

CRASHLESS_STRESS_API void crashless_v1_cancel_generation(void* model_ctx) {
    if (model_ctx == nullptr) {
        return;
    }

    auto* context = static_cast<ShimContext*>(model_ctx);
    context->cancel_requested.store(true, std::memory_order_release);
}

CRASHLESS_STRESS_API void crashless_v1_free_session_secure(void* model_ctx) {
    if (model_ctx == nullptr) {
        return;
    }

    auto* context = static_cast<ShimContext*>(model_ctx);
    context->cancel_requested.store(true, std::memory_order_release);

    try {
        std::thread worker_to_join;
        {
            std::lock_guard<std::mutex> guard(context->worker_mutex);
            if (context->worker.joinable()) {
                worker_to_join = std::move(context->worker);
            }
        }

        if (worker_to_join.joinable()) {
            worker_to_join.join();
        }
    } catch (...) {
    }

    delete context;
    g_active_sessions.fetch_sub(1, std::memory_order_acq_rel);
}

CRASHLESS_STRESS_API void crashless_stress_reset_metrics(void) {
    g_generated_callbacks.store(0, std::memory_order_release);
    g_completed_workers.store(0, std::memory_order_release);
    g_last_worker_thread_hash.store(0, std::memory_order_release);
}

CRASHLESS_STRESS_API long long crashless_stress_get_active_sessions(void) {
    return g_active_sessions.load(std::memory_order_acquire);
}

CRASHLESS_STRESS_API long long crashless_stress_get_active_workers(void) {
    return g_active_workers.load(std::memory_order_acquire);
}

CRASHLESS_STRESS_API long long crashless_stress_get_generated_callbacks(void) {
    return g_generated_callbacks.load(std::memory_order_acquire);
}

CRASHLESS_STRESS_API long long crashless_stress_get_completed_workers(void) {
    return g_completed_workers.load(std::memory_order_acquire);
}

CRASHLESS_STRESS_API unsigned long long crashless_stress_get_last_worker_thread_hash(void) {
    return g_last_worker_thread_hash.load(std::memory_order_acquire);
}

} // extern "C"
