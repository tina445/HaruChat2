#include "hc_llm.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <deque>
#include <fstream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#if defined(HC_LLM_WITH_LLAMA_CPP)
#include "llama.h"
#endif

namespace {

constexpr uint64_t kMagic = UINT64_C(0x484352554e54494d); // "HCRUNTIME"
constexpr uint32_t kDefaultQueueCapacity = 32;
constexpr uint32_t kCapabilityPolling = 1U << 0U;
constexpr uint32_t kCapabilityCancellation = 1U << 1U;
constexpr uint32_t kCapabilityMockBackend = 1U << 2U;

bool has_trailing_field(uint32_t struct_size, size_t offset, size_t field_size) {
  return struct_size >= offset + field_size;
}

struct HandleHeader {
  uint64_t magic = kMagic;
  std::atomic<bool> alive{true};
};

bool valid_utf8(const uint8_t *bytes, uint32_t size) {
  if (size == 0) return true;
  if (bytes == nullptr) return false;
  uint32_t i = 0;
  while (i < size) {
    const uint8_t first = bytes[i];
    uint32_t continuation = 0;
    if ((first & 0x80U) == 0) {
      ++i;
      continue;
    }
    if (first >= 0xc2U && first <= 0xdfU) continuation = 1;
    else if ((first & 0xf0U) == 0xe0U) continuation = 2;
    else if (first >= 0xf0U && first <= 0xf4U) continuation = 3;
    else return false;
    if (i + continuation >= size) return false;
    for (uint32_t j = 1; j <= continuation; ++j) {
      if ((bytes[i + j] & 0xc0U) != 0x80U) return false;
    }
    // Reject overlong encodings, surrogate code points, and values above
    // U+10FFFF.  Accepting only continuation-byte shape is not UTF-8.
    if ((first == 0xe0U && bytes[i + 1] < 0xa0U) ||
        (first == 0xedU && bytes[i + 1] > 0x9fU) ||
        (first == 0xf0U && bytes[i + 1] < 0x90U) ||
        (first == 0xf4U && bytes[i + 1] > 0x8fU)) return false;
    i += continuation + 1;
  }
  return true;
}

std::vector<std::string> split_utf8(const std::string &value) {
  std::vector<std::string> result;
  for (size_t i = 0; i < value.size();) {
    size_t count = 1;
    const uint8_t first = static_cast<uint8_t>(value[i]);
    if ((first & 0xe0U) == 0xc0U) count = 2;
    else if ((first & 0xf0U) == 0xe0U) count = 3;
    else if ((first & 0xf8U) == 0xf0U) count = 4;
    if (i + count > value.size()) break; // guarded by valid_utf8 at API edge
    result.emplace_back(value.substr(i, count));
    i += count;
  }
  return result;
}

void copy_text(char *target, size_t target_size, const char *source) {
  if (target_size == 0) return;
  std::snprintf(target, target_size, "%s", source);
}

} // namespace

struct hc_llm_model;
struct hc_llm_context;
struct hc_llm_job;

struct hc_llm_runtime : HandleHeader {
  uint32_t queue_capacity = kDefaultQueueCapacity;
  std::mutex mutex;
  std::vector<hc_llm_model *> models;
  std::vector<hc_llm_context *> contexts;
  std::vector<hc_llm_job *> jobs;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  bool llama_backend_initialized = false;
#endif
};

struct hc_llm_model : HandleHeader {
  hc_llm_runtime *runtime = nullptr;
  std::string path;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  llama_model *native_model = nullptr;
#endif
};

struct hc_llm_context : HandleHeader {
  hc_llm_runtime *runtime = nullptr;
  hc_llm_model *model = nullptr;
  std::mutex mutex;
  hc_llm_job *active_job = nullptr;
  std::atomic<bool> abort_requested{false};
  uint64_t reset_count = 0;
  uint32_t batch_size = 0;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  llama_context *native_context = nullptr;
#endif
};

struct PendingEvent {
  hc_llm_event_type type = HC_LLM_EVENT_ERROR;
  bool terminal = false;
  uint64_t sequence = 0;
  std::string payload;
  hc_llm_generation_metrics metrics{};
};

struct hc_llm_job : HandleHeader {
  hc_llm_runtime *runtime = nullptr;
  hc_llm_context *context = nullptr;
  uint32_t queue_capacity = kDefaultQueueCapacity;
  std::mutex mutex;
  std::condition_variable queue_changed;
  std::deque<PendingEvent> events;
  bool terminal_pending = false;
  PendingEvent terminal_event{};
  std::thread worker;
  std::atomic<bool> cancel_requested{false};
  std::atomic<bool> worker_finished{false};
  std::string last_payload;
  float temperature = 0.0F;
  float top_p = 0.0F;
  uint32_t top_k = 0;
  uint32_t seed = 0;
};

namespace {

bool valid_runtime(const hc_llm_runtime *value) {
  return value != nullptr && value->magic == kMagic && value->alive.load();
}
bool valid_model(const hc_llm_model *value) {
  return value != nullptr && value->magic == kMagic && value->alive.load() &&
         valid_runtime(value->runtime);
}
bool valid_context(const hc_llm_context *value) {
  return value != nullptr && value->magic == kMagic && value->alive.load() &&
         valid_model(value->model);
}
bool valid_job(const hc_llm_job *value) {
  return value != nullptr && value->magic == kMagic && value->alive.load() &&
         value->runtime != nullptr && value->runtime->magic == kMagic && value->runtime->alive.load();
}

void join_job(hc_llm_job *job) {
  if (job != nullptr && job->worker.joinable()) job->worker.join();
}

void cancel_and_discard(hc_llm_job *job) {
  job->cancel_requested.store(true);
  if (job->context != nullptr) job->context->abort_requested.store(true);
  {
    std::lock_guard<std::mutex> lock(job->mutex);
    // Disposal has no remaining consumer. Freeing queued views lets the worker
    // publish its terminal event and makes join safe even under backpressure.
    job->events.clear();
  }
  job->queue_changed.notify_all();
}

void clear_active_job(hc_llm_job *job) {
  hc_llm_context *context = job->context;
  if (context == nullptr || context->magic != kMagic) return;
  std::lock_guard<std::mutex> lock(context->mutex);
  if (context->active_job == job) context->active_job = nullptr;
}

bool enqueue(hc_llm_job *job, PendingEvent event) {
  std::unique_lock<std::mutex> lock(job->mutex);
  job->queue_changed.wait(lock, [job] {
    return job->events.size() < job->queue_capacity || job->cancel_requested.load();
  });
  if (job->cancel_requested.load()) return false;
  job->events.push_back(std::move(event));
  lock.unlock();
  job->queue_changed.notify_all();
  return true;
}

void publish_terminal(hc_llm_job *job, PendingEvent event) {
  {
    std::lock_guard<std::mutex> lock(job->mutex);
    // Terminal delivery is an out-of-band, single slot signal. It cannot be
    // starved by a full bounded data queue, and poll exposes it only after all
    // earlier queued events, preserving event ordering without token loss.
    if (!job->terminal_pending) {
      job->terminal_event = std::move(event);
      job->terminal_pending = true;
    }
  }
  job->worker_finished.store(true);
  job->queue_changed.notify_all();
}

hc_llm_generation_metrics make_metrics(uint64_t token_count, uint64_t queue_depth,
                                       std::chrono::steady_clock::time_point started) {
  hc_llm_generation_metrics metrics{};
  metrics.struct_size = sizeof(metrics);
  metrics.abi_version = HC_LLM_ABI_VERSION;
  metrics.emitted_token_count = token_count;
  metrics.queue_depth = queue_depth;
  metrics.elapsed_milliseconds = static_cast<uint64_t>(
      std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started).count());
  return metrics;
}

void run_mock_generation(hc_llm_job *job, std::string response, uint32_t max_tokens,
                         uint32_t token_delay_ms) {
  const auto started = std::chrono::steady_clock::now();
  uint64_t sequence = 0;
  uint64_t emitted = 0;
  const std::vector<std::string> pieces = split_utf8(response);
  const uint32_t limit = max_tokens == 0 ? static_cast<uint32_t>(pieces.size())
                                          : std::min<uint32_t>(max_tokens, pieces.size());
  for (uint32_t i = 0; i < limit && !job->cancel_requested.load(); ++i) {
    PendingEvent event{};
    event.type = HC_LLM_EVENT_TOKEN;
    event.sequence = ++sequence;
    event.payload = pieces[i];
    {
      std::lock_guard<std::mutex> lock(job->mutex);
      event.metrics = make_metrics(++emitted, job->events.size(), started);
    }
    if (!enqueue(job, std::move(event))) break;
    if (token_delay_ms != 0) std::this_thread::sleep_for(std::chrono::milliseconds(token_delay_ms));
  }

  if (job->cancel_requested.load()) {
    PendingEvent terminal{};
    terminal.type = HC_LLM_EVENT_CANCELLED;
    terminal.terminal = true;
    terminal.sequence = ++sequence;
    {
      std::lock_guard<std::mutex> lock(job->mutex);
      terminal.metrics = make_metrics(emitted, job->events.size(), started);
    }
    publish_terminal(job, std::move(terminal));
  } else {
    PendingEvent metrics{};
    metrics.type = HC_LLM_EVENT_METRICS;
    metrics.sequence = ++sequence;
    {
      std::lock_guard<std::mutex> lock(job->mutex);
      metrics.metrics = make_metrics(emitted, job->events.size(), started);
    }
    if (!enqueue(job, std::move(metrics))) {
      PendingEvent terminal{};
      terminal.type = HC_LLM_EVENT_CANCELLED;
      terminal.terminal = true;
      terminal.sequence = ++sequence;
      {
        std::lock_guard<std::mutex> lock(job->mutex);
        terminal.metrics = make_metrics(emitted, job->events.size(), started);
      }
      publish_terminal(job, std::move(terminal));
      return;
    }

    PendingEvent terminal{};
    terminal.type = HC_LLM_EVENT_COMPLETED;
    terminal.terminal = true;
    terminal.sequence = ++sequence;
    {
      std::lock_guard<std::mutex> lock(job->mutex);
      terminal.metrics = make_metrics(emitted, job->events.size(), started);
    }
    publish_terminal(job, std::move(terminal));
  }
}

#if defined(HC_LLM_WITH_LLAMA_CPP)
bool llama_abort_requested(void *user_data) {
  const auto *context = static_cast<const hc_llm_context *>(user_data);
  return context == nullptr || context->abort_requested.load();
}

void run_llama_generation(hc_llm_job *job, std::string prompt, uint32_t max_tokens) {
  const auto started = std::chrono::steady_clock::now();
  uint64_t sequence = 0;
  uint64_t emitted = 0;
  llama_context *context = job->context->native_context;
  const llama_vocab *vocab = llama_model_get_vocab(job->context->model->native_model);
  int32_t count = llama_tokenize(vocab, prompt.data(), static_cast<int32_t>(prompt.size()),
                                 nullptr, 0, true, true);
  if (count < 0) count = -count;
  if (count <= 0 || context == nullptr) {
    PendingEvent terminal{};
    terminal.type = HC_LLM_EVENT_ERROR;
    terminal.terminal = true;
    terminal.sequence = ++sequence;
    terminal.payload = "prompt tokenization failed";
    publish_terminal(job, std::move(terminal));
    return;
  }
  std::vector<llama_token> prompt_tokens(static_cast<size_t>(count));
  if (llama_tokenize(vocab, prompt.data(), static_cast<int32_t>(prompt.size()), prompt_tokens.data(), count,
                     true, true) < 0) {
    PendingEvent terminal{};
    terminal.type = HC_LLM_EVENT_ERROR;
    terminal.terminal = true;
    terminal.sequence = ++sequence;
    terminal.payload = "prompt tokenization failed";
    publish_terminal(job, std::move(terminal));
    return;
  }
  if (job->cancel_requested.load()) {
    PendingEvent terminal{};
    terminal.type = HC_LLM_EVENT_CANCELLED;
    terminal.terminal = true;
    terminal.sequence = ++sequence;
    publish_terminal(job, std::move(terminal));
    return;
  }
  // llama.cpp rejects a decode batch larger than n_batch.  Prompt length is
  // unconstrained by that throughput setting, so evaluate it sequentially.
  const uint32_t configured_batch = job->context->batch_size != 0
      ? job->context->batch_size : llama_n_batch(context);
  const uint32_t bounded_batch = std::min<uint32_t>(configured_batch,
      static_cast<uint32_t>(INT32_MAX));
  const int32_t prompt_batch = static_cast<int32_t>(std::max<uint32_t>(1U, bounded_batch));
  for (int32_t offset = 0; offset < count; offset += prompt_batch) {
    if (job->cancel_requested.load()) {
      PendingEvent terminal{};
      terminal.type = HC_LLM_EVENT_CANCELLED;
      terminal.terminal = true;
      terminal.sequence = ++sequence;
      publish_terminal(job, std::move(terminal));
      return;
    }
    const int32_t chunk = std::min(prompt_batch, count - offset);
    if (llama_decode(context, llama_batch_get_one(prompt_tokens.data() + offset, chunk)) != 0) {
      PendingEvent terminal{};
      terminal.type = job->cancel_requested.load() ? HC_LLM_EVENT_CANCELLED : HC_LLM_EVENT_ERROR;
      terminal.terminal = true;
      terminal.sequence = ++sequence;
      if (terminal.type == HC_LLM_EVENT_ERROR) terminal.payload = "prompt evaluation failed";
      publish_terminal(job, std::move(terminal));
      return;
    }
  }
  llama_sampler *sampler = llama_sampler_chain_init(llama_sampler_chain_default_params());
  if (sampler == nullptr) {
    PendingEvent terminal{};
    terminal.type = HC_LLM_EVENT_ERROR;
    terminal.terminal = true;
    terminal.sequence = ++sequence;
    terminal.payload = "sampler initialization failed";
    publish_terminal(job, std::move(terminal));
    return;
  }
  if (job->temperature <= 0.0F) {
    llama_sampler_chain_add(sampler, llama_sampler_init_greedy());
  } else {
    if (job->top_k != 0) llama_sampler_chain_add(sampler, llama_sampler_init_top_k(static_cast<int32_t>(job->top_k)));
    if (job->top_p > 0.0F && job->top_p < 1.0F) llama_sampler_chain_add(sampler, llama_sampler_init_top_p(job->top_p, 1));
    llama_sampler_chain_add(sampler, llama_sampler_init_temp(job->temperature));
    llama_sampler_chain_add(sampler, llama_sampler_init_dist(job->seed));
  }
  std::string pending_utf8;
  const uint32_t limit = max_tokens == 0 ? 64U : max_tokens;
  bool decode_failed = false;
  for (uint32_t i = 0; i < limit && !job->cancel_requested.load(); ++i) {
    const llama_token token = llama_sampler_sample(sampler, context, -1);
    if (llama_vocab_is_eog(vocab, token)) break;
    llama_sampler_accept(sampler, token);
    int32_t piece_size = llama_token_to_piece(vocab, token, nullptr, 0, 0, true);
    if (piece_size < 0) piece_size = -piece_size;
    if (piece_size > 0) {
      std::string piece(static_cast<size_t>(piece_size), '\0');
      const int32_t written = llama_token_to_piece(vocab, token, piece.data(), piece_size, 0, true);
      if (written > 0) {
        pending_utf8.append(piece.data(), static_cast<size_t>(written));
        if (valid_utf8(reinterpret_cast<const uint8_t *>(pending_utf8.data()),
                       static_cast<uint32_t>(pending_utf8.size()))) {
          PendingEvent event{};
          event.type = HC_LLM_EVENT_TOKEN;
          event.sequence = ++sequence;
          event.payload = std::move(pending_utf8);
          {
            std::lock_guard<std::mutex> lock(job->mutex);
            event.metrics = make_metrics(++emitted, job->events.size(), started);
          }
          if (!enqueue(job, std::move(event))) break;
        }
      }
    }
    llama_token decoded_token = token;
    if (llama_decode(context, llama_batch_get_one(&decoded_token, 1)) != 0) {
      if (job->cancel_requested.load()) break;
      decode_failed = true;
      break;
    }
  }
  llama_sampler_free(sampler);
  PendingEvent terminal{};
  terminal.terminal = true;
  terminal.sequence = ++sequence;
  if (job->cancel_requested.load()) terminal.type = HC_LLM_EVENT_CANCELLED;
  else if (decode_failed || !pending_utf8.empty()) {
    terminal.type = HC_LLM_EVENT_ERROR;
    terminal.payload = decode_failed ? "generation decode failed" : "incomplete UTF-8 token sequence";
  } else terminal.type = HC_LLM_EVENT_COMPLETED;
  {
    std::lock_guard<std::mutex> lock(job->mutex);
    terminal.metrics = make_metrics(emitted, job->events.size(), started);
  }
  publish_terminal(job, std::move(terminal));
}
#endif

} // namespace

extern "C" {

uint32_t hc_llm_get_abi_version(void) { return HC_LLM_ABI_VERSION; }

const char *hc_llm_status_message(hc_llm_status status) {
  switch (status) {
    case HC_LLM_STATUS_OK: return "ok";
    case HC_LLM_STATUS_INVALID_ARGUMENT: return "invalid argument";
    case HC_LLM_STATUS_INVALID_HANDLE: return "invalid handle";
    case HC_LLM_STATUS_ABI_MISMATCH: return "ABI version mismatch";
    case HC_LLM_STATUS_IO_ERROR: return "I/O error";
    case HC_LLM_STATUS_BUSY: return "resource is busy";
    case HC_LLM_STATUS_WOULD_BLOCK: return "no event is available";
    case HC_LLM_STATUS_CANCELLED: return "cancelled";
    case HC_LLM_STATUS_UNSUPPORTED: return "unsupported";
    case HC_LLM_STATUS_INTERNAL_ERROR: return "internal error";
  }
  return "unknown status";
}

hc_llm_status hc_llm_runtime_create(const hc_llm_runtime_options *options, hc_llm_runtime **out_runtime) {
  if (out_runtime == nullptr) return HC_LLM_STATUS_INVALID_ARGUMENT;
  *out_runtime = nullptr;
  uint32_t capacity = kDefaultQueueCapacity;
  if (options != nullptr) {
    if (options->abi_version != HC_LLM_ABI_VERSION) return HC_LLM_STATUS_ABI_MISMATCH;
    if (options->struct_size < sizeof(*options) || options->event_queue_capacity == 0 || options->reserved != 0)
      return HC_LLM_STATUS_INVALID_ARGUMENT;
    capacity = options->event_queue_capacity;
  }
  auto *runtime = new hc_llm_runtime();
  runtime->queue_capacity = capacity;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  llama_backend_init();
  runtime->llama_backend_initialized = true;
#endif
  *out_runtime = runtime;
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_runtime_get_metadata(const hc_llm_runtime *runtime, hc_llm_runtime_metadata *out_metadata) {
  if (!valid_runtime(runtime) || out_metadata == nullptr) return HC_LLM_STATUS_INVALID_ARGUMENT;
  if (out_metadata->abi_version != HC_LLM_ABI_VERSION) return HC_LLM_STATUS_ABI_MISMATCH;
  if (out_metadata->struct_size < sizeof(*out_metadata)) return HC_LLM_STATUS_INVALID_ARGUMENT;
  std::memset(out_metadata, 0, sizeof(*out_metadata));
  out_metadata->struct_size = sizeof(*out_metadata);
  out_metadata->abi_version = HC_LLM_ABI_VERSION;
  out_metadata->capability_flags = kCapabilityPolling | kCapabilityCancellation | kCapabilityMockBackend;
#if defined(HC_LLM_WITH_LLAMA_CPP)
#if defined(__APPLE__)
  copy_text(out_metadata->backend_name, sizeof(out_metadata->backend_name), "llama.cpp-metal");
#else
  copy_text(out_metadata->backend_name, sizeof(out_metadata->backend_name), "llama.cpp");
#endif
#else
  copy_text(out_metadata->backend_name, sizeof(out_metadata->backend_name), "bootstrap-mock");
#endif
#if defined(__ANDROID__)
  copy_text(out_metadata->target_triple, sizeof(out_metadata->target_triple), "android-arm64-v8a");
#elif defined(__APPLE__)
  copy_text(out_metadata->target_triple, sizeof(out_metadata->target_triple), "apple");
#else
  copy_text(out_metadata->target_triple, sizeof(out_metadata->target_triple), "linux-or-host");
#endif
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_runtime_destroy(hc_llm_runtime *runtime) {
  if (!valid_runtime(runtime)) return HC_LLM_STATUS_INVALID_HANDLE;
  std::vector<hc_llm_job *> jobs;
  std::vector<hc_llm_context *> contexts;
  std::vector<hc_llm_model *> models;
  {
    std::lock_guard<std::mutex> lock(runtime->mutex);
    jobs = runtime->jobs;
    contexts = runtime->contexts;
    models = runtime->models;
  }
  for (hc_llm_job *job : jobs) {
    if (job->alive.load()) cancel_and_discard(job);
  }
  for (hc_llm_job *job : jobs) join_job(job);
  for (hc_llm_context *context : contexts) context->alive.store(false);
#if defined(HC_LLM_WITH_LLAMA_CPP)
  for (hc_llm_context *context : contexts) {
    if (context->native_context != nullptr) llama_free(context->native_context);
  }
#endif
  for (hc_llm_model *model : models) {
    model->alive.store(false);
#if defined(HC_LLM_WITH_LLAMA_CPP)
    if (model->native_model != nullptr) llama_model_free(model->native_model);
#endif
  }
  runtime->alive.store(false);
  for (hc_llm_job *job : jobs) delete job;
  for (hc_llm_context *context : contexts) delete context;
  for (hc_llm_model *model : models) delete model;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  if (runtime->llama_backend_initialized) llama_backend_free();
#endif
  delete runtime;
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_model_load(hc_llm_runtime *runtime, const char *path_utf8,
                                const hc_llm_model_load_options *options, hc_llm_model **out_model) {
  if (!valid_runtime(runtime) || path_utf8 == nullptr || path_utf8[0] == '\0' || out_model == nullptr)
    return HC_LLM_STATUS_INVALID_ARGUMENT;
  *out_model = nullptr;
  if (options != nullptr) {
    if (options->abi_version != HC_LLM_ABI_VERSION) return HC_LLM_STATUS_ABI_MISMATCH;
    if (options->struct_size < sizeof(*options) || options->reserved0 != 0 || options->reserved1 != 0)
      return HC_LLM_STATUS_INVALID_ARGUMENT;
  }
  const size_t path_length = std::strlen(path_utf8);
  if (path_length > UINT32_MAX || !valid_utf8(reinterpret_cast<const uint8_t *>(path_utf8),
                                               static_cast<uint32_t>(path_length))) {
    return HC_LLM_STATUS_INVALID_ARGUMENT;
  }
  auto *model = new hc_llm_model();
  model->runtime = runtime;
  model->path = path_utf8;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  llama_model_params params = llama_model_default_params();
#if defined(__APPLE__)
  // Device probes must exercise the Metal backend instead of silently falling
  // back to CPU-only layers. Mobile sizing is a later profile/config concern.
  params.n_gpu_layers = 999;
#endif
  model->native_model = llama_model_load_from_file(path_utf8, params);
  if (model->native_model == nullptr) {
    delete model;
    return HC_LLM_STATUS_IO_ERROR;
  }
#else
  std::ifstream input(path_utf8, std::ios::binary);
  char magic[4]{};
  input.read(magic, sizeof(magic));
  if (!input || std::memcmp(magic, "GGUF", sizeof(magic)) != 0) {
    delete model;
    return HC_LLM_STATUS_IO_ERROR;
  }
#endif
  {
    std::lock_guard<std::mutex> lock(runtime->mutex);
    runtime->models.push_back(model);
  }
  *out_model = model;
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_model_get_path(const hc_llm_model *model, char *buffer, uint32_t buffer_bytes,
                                    uint32_t *out_required_bytes) {
  if (!valid_model(model) || out_required_bytes == nullptr || (buffer_bytes != 0 && buffer == nullptr))
    return HC_LLM_STATUS_INVALID_ARGUMENT;
  const uint64_t required64 = model->path.size() + 1U;
  if (required64 > UINT32_MAX) return HC_LLM_STATUS_INTERNAL_ERROR;
  const uint32_t required = static_cast<uint32_t>(required64);
  *out_required_bytes = required;
  if (buffer == nullptr || buffer_bytes == 0) return HC_LLM_STATUS_OK;
  if (buffer_bytes < required) return HC_LLM_STATUS_INVALID_ARGUMENT;
  std::memcpy(buffer, model->path.c_str(), required);
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_model_get_metadata(const hc_llm_model *model,
                                        hc_llm_model_metadata *out_metadata) {
  if (!valid_model(model) || out_metadata == nullptr) return HC_LLM_STATUS_INVALID_ARGUMENT;
  if (out_metadata->abi_version != HC_LLM_ABI_VERSION) return HC_LLM_STATUS_ABI_MISMATCH;
  if (out_metadata->struct_size < sizeof(*out_metadata)) return HC_LLM_STATUS_INVALID_ARGUMENT;
  std::memset(out_metadata, 0, sizeof(*out_metadata));
  out_metadata->struct_size = sizeof(*out_metadata);
  out_metadata->abi_version = HC_LLM_ABI_VERSION;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  const int32_t training_context = llama_model_n_ctx_train(model->native_model);
  out_metadata->training_context_tokens = training_context > 0 ? static_cast<uint32_t>(training_context) : 0U;
  llama_model_desc(model->native_model, out_metadata->description, sizeof(out_metadata->description));
#else
  copy_text(out_metadata->description, sizeof(out_metadata->description), "bootstrap GGUF fixture");
#endif
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_model_unload(hc_llm_model *model) {
  if (!valid_model(model)) return HC_LLM_STATUS_INVALID_HANDLE;
  hc_llm_runtime *runtime = model->runtime;
  std::lock_guard<std::mutex> lock(runtime->mutex);
  for (hc_llm_context *context : runtime->contexts) {
    if (context->model == model && context->alive.load()) return HC_LLM_STATUS_BUSY;
  }
  model->alive.store(false);
#if defined(HC_LLM_WITH_LLAMA_CPP)
  if (model->native_model != nullptr) {
    llama_model_free(model->native_model);
    model->native_model = nullptr;
  }
#endif
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_context_create(hc_llm_model *model, const hc_llm_context_options *options,
                                    hc_llm_context **out_context) {
  if (!valid_model(model) || out_context == nullptr) return HC_LLM_STATUS_INVALID_ARGUMENT;
  *out_context = nullptr;
  if (options != nullptr) {
    if (options->abi_version != HC_LLM_ABI_VERSION) return HC_LLM_STATUS_ABI_MISMATCH;
    if (options->struct_size < HC_LLM_CONTEXT_OPTIONS_V1_SIZE || options->reserved != 0 ||
        (has_trailing_field(options->struct_size, offsetof(hc_llm_context_options, reserved1),
                            sizeof(options->reserved1)) && options->reserved1 != 0)) {
      return HC_LLM_STATUS_INVALID_ARGUMENT;
    }
  }
  auto *context = new hc_llm_context();
  context->runtime = model->runtime;
  context->model = model;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  llama_context_params params = llama_context_default_params();
  params.abort_callback = llama_abort_requested;
  params.abort_callback_data = context;
  if (options != nullptr && options->context_size != 0) {
    params.n_ctx = options->context_size;
    params.n_batch = std::min<uint32_t>(params.n_batch, params.n_ctx);
  }
  if (options != nullptr && has_trailing_field(options->struct_size,
      offsetof(hc_llm_context_options, batch_size), sizeof(options->batch_size))) {
    if (options->batch_size != 0) {
      if (options->batch_size > params.n_ctx || options->batch_size > static_cast<uint32_t>(INT32_MAX)) {
        delete context;
        return HC_LLM_STATUS_INVALID_ARGUMENT;
      }
      context->batch_size = options->batch_size;
      params.n_batch = options->batch_size;
    }
  }
  context->native_context = llama_init_from_model(model->native_model, params);
  if (context->native_context == nullptr) {
    delete context;
    return HC_LLM_STATUS_IO_ERROR;
  }
#endif
  {
    std::lock_guard<std::mutex> lock(model->runtime->mutex);
    model->runtime->contexts.push_back(context);
  }
  *out_context = context;
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_context_reset(hc_llm_context *context) {
  if (!valid_context(context)) return HC_LLM_STATUS_INVALID_HANDLE;
  std::lock_guard<std::mutex> lock(context->mutex);
  if (context->active_job != nullptr) return HC_LLM_STATUS_BUSY;
#if defined(HC_LLM_WITH_LLAMA_CPP)
  llama_memory_clear(llama_get_memory(context->native_context), false);
#endif
  context->abort_requested.store(false);
  ++context->reset_count;
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_context_destroy(hc_llm_context *context) {
  if (!valid_context(context)) return HC_LLM_STATUS_INVALID_HANDLE;
  hc_llm_job *active = nullptr;
  {
    std::lock_guard<std::mutex> lock(context->mutex);
    active = context->active_job;
  }
  if (active != nullptr) {
    cancel_and_discard(active);
    join_job(active);
  }
  context->alive.store(false);
#if defined(HC_LLM_WITH_LLAMA_CPP)
  if (context->native_context != nullptr) {
    llama_free(context->native_context);
    context->native_context = nullptr;
  }
#endif
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_job_start(hc_llm_context *context, const hc_llm_generation_options *options,
                               hc_llm_job **out_job) {
  if (!valid_context(context) || options == nullptr || out_job == nullptr) return HC_LLM_STATUS_INVALID_ARGUMENT;
  *out_job = nullptr;
  if (options->abi_version != HC_LLM_ABI_VERSION) return HC_LLM_STATUS_ABI_MISMATCH;
  if (options->struct_size < HC_LLM_GENERATION_OPTIONS_V1_SIZE || !valid_utf8(options->prompt_utf8, options->prompt_bytes) ||
      !valid_utf8(options->mock_response_utf8, options->mock_response_bytes)) return HC_LLM_STATUS_INVALID_ARGUMENT;
  const bool has_temperature = has_trailing_field(options->struct_size,
      offsetof(hc_llm_generation_options, temperature), sizeof(options->temperature));
  const bool has_top_p = has_trailing_field(options->struct_size,
      offsetof(hc_llm_generation_options, top_p), sizeof(options->top_p));
  const bool has_top_k = has_trailing_field(options->struct_size,
      offsetof(hc_llm_generation_options, top_k), sizeof(options->top_k));
  const bool has_seed = has_trailing_field(options->struct_size,
      offsetof(hc_llm_generation_options, seed), sizeof(options->seed));
  if ((has_temperature && (!std::isfinite(options->temperature) || options->temperature < 0.0F)) ||
      (has_top_p && (!std::isfinite(options->top_p) || options->top_p < 0.0F || options->top_p > 1.0F))) {
    return HC_LLM_STATUS_INVALID_ARGUMENT;
  }
  std::string response = options->mock_response_bytes == 0
      ? "mock response" : std::string(reinterpret_cast<const char *>(options->mock_response_utf8), options->mock_response_bytes);
  std::string prompt = options->prompt_bytes == 0 ? "Hello" :
      std::string(reinterpret_cast<const char *>(options->prompt_utf8), options->prompt_bytes);
  auto *job = new hc_llm_job();
  job->runtime = context->runtime;
  job->context = context;
  job->queue_capacity = context->runtime->queue_capacity;
  if (has_temperature) job->temperature = options->temperature;
  if (has_top_p) job->top_p = options->top_p;
  if (has_top_k) job->top_k = options->top_k;
  if (has_seed) job->seed = options->seed;
  {
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->active_job != nullptr) {
      delete job;
      return HC_LLM_STATUS_BUSY;
    }
    context->abort_requested.store(false);
    context->active_job = job;
  }
  {
    std::lock_guard<std::mutex> lock(context->runtime->mutex);
    context->runtime->jobs.push_back(job);
  }
#if defined(HC_LLM_WITH_LLAMA_CPP)
  if (options->mock_response_bytes == 0) {
    job->worker = std::thread(run_llama_generation, job, std::move(prompt), options->max_tokens);
  } else {
    job->worker = std::thread(run_mock_generation, job, std::move(response), options->max_tokens,
                              options->token_delay_ms);
  }
#else
  job->worker = std::thread(run_mock_generation, job, std::move(response), options->max_tokens,
                            options->token_delay_ms);
#endif
  *out_job = job;
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_job_cancel(hc_llm_job *job) {
  if (!valid_job(job)) return HC_LLM_STATUS_INVALID_HANDLE;
  if (!job->worker_finished.load()) cancel_and_discard(job);
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_job_poll(hc_llm_job *job, hc_llm_event *out_event) {
  if (!valid_job(job) || out_event == nullptr) return HC_LLM_STATUS_INVALID_ARGUMENT;
  if (out_event->abi_version != HC_LLM_ABI_VERSION) return HC_LLM_STATUS_ABI_MISMATCH;
  if (out_event->struct_size < sizeof(*out_event)) return HC_LLM_STATUS_INVALID_ARGUMENT;
  std::unique_lock<std::mutex> lock(job->mutex);
  PendingEvent event{};
  if (!job->events.empty()) {
    event = std::move(job->events.front());
    job->events.pop_front();
  } else if (job->terminal_pending) {
    event = std::move(job->terminal_event);
    job->terminal_pending = false;
  } else {
    return HC_LLM_STATUS_WOULD_BLOCK;
  }
  job->last_payload = std::move(event.payload);
  std::memset(out_event, 0, sizeof(*out_event));
  out_event->struct_size = sizeof(*out_event);
  out_event->abi_version = HC_LLM_ABI_VERSION;
  out_event->type = event.type;
  out_event->is_terminal = event.terminal ? 1U : 0U;
  out_event->sequence = event.sequence;
  out_event->payload_utf8 = job->last_payload.empty() ? nullptr : reinterpret_cast<const uint8_t *>(job->last_payload.data());
  out_event->payload_bytes = static_cast<uint32_t>(job->last_payload.size());
  out_event->metrics = event.metrics;
  job->queue_changed.notify_all();
  if (event.terminal) {
    // Do not hold the queue mutex while taking the context mutex.
    lock.unlock();
    clear_active_job(job);
  }
  return HC_LLM_STATUS_OK;
}

hc_llm_status hc_llm_job_destroy(hc_llm_job *job) {
  if (!valid_job(job)) return HC_LLM_STATUS_INVALID_HANDLE;
  cancel_and_discard(job);
  join_job(job);
  clear_active_job(job);
  hc_llm_runtime *runtime = job->runtime;
  job->alive.store(false);
  {
    std::lock_guard<std::mutex> lock(runtime->mutex);
    auto &jobs = runtime->jobs;
    jobs.erase(std::remove(jobs.begin(), jobs.end(), job), jobs.end());
  }
  delete job;
  return HC_LLM_STATUS_OK;
}

} // extern "C"
