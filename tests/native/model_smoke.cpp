#include "hc_llm.h"

#include <chrono>
#include <cstdlib>
#include <cstring>
#include <cstdio>
#include <thread>

namespace {

bool wait_for_terminal(hc_llm_job *job, hc_llm_event_type expected_terminal,
                       bool require_token) {
  bool saw_token = false;
  for (int i = 0; i < 120000; ++i) {
    hc_llm_event event{};
    event.struct_size = sizeof(event);
    event.abi_version = HC_LLM_ABI_VERSION;
    const hc_llm_status status = hc_llm_job_poll(job, &event);
    if (status == HC_LLM_STATUS_WOULD_BLOCK) {
      std::this_thread::sleep_for(std::chrono::milliseconds(1));
      continue;
    }
    if (status != HC_LLM_STATUS_OK) {
      std::fprintf(stderr, "model-smoke poll failed: %s\n", hc_llm_status_message(status));
      return false;
    }
    if (event.type == HC_LLM_EVENT_TOKEN && event.payload_bytes > 0) saw_token = true;
    if (event.is_terminal != 0) {
      if (event.type != expected_terminal || (require_token && !saw_token)) {
        std::fprintf(stderr, "model-smoke terminal mismatch: expected=%d actual=%d saw_token=%d payload=%.*s\n",
                     static_cast<int>(expected_terminal), static_cast<int>(event.type), saw_token ? 1 : 0,
                     static_cast<int>(event.payload_bytes),
                     event.payload_utf8 == nullptr ? "" : reinterpret_cast<const char *>(event.payload_utf8));
        return false;
      }
      return true;
    }
  }
  std::fprintf(stderr, "model-smoke timed out waiting for terminal event\n");
  return false;
}

hc_llm_generation_options make_options(uint32_t max_tokens) {
  hc_llm_generation_options options{};
  options.struct_size = sizeof(options);
  options.abi_version = HC_LLM_ABI_VERSION;
  // This prompt deliberately spans multiple tokens; model-smoke creates an
  // n_batch=1 context below to exercise chunked prompt evaluation.
  static const char prompt[] = "Reply briefly. Answer with one word only.";
  options.prompt_utf8 = reinterpret_cast<const uint8_t *>(prompt);
  options.prompt_bytes = static_cast<uint32_t>(std::strlen(prompt));
  options.max_tokens = max_tokens;
  options.temperature = 0.7F;
  options.top_p = 0.9F;
  options.top_k = 40;
  options.seed = 42;
  return options;
}

} // namespace

int main() {
  const char *path = std::getenv("HARUCHAT_TEST_MODEL_PATH");
  if (path == nullptr || path[0] == '\0') return 77;

  hc_llm_runtime *runtime = nullptr;
  if (hc_llm_runtime_create(nullptr, &runtime) != HC_LLM_STATUS_OK) return 1;
  hc_llm_model *model = nullptr;
  if (hc_llm_model_load(runtime, path, nullptr, &model) != HC_LLM_STATUS_OK) return 2;
  hc_llm_context_options context_options{};
  context_options.struct_size = sizeof(context_options);
  context_options.abi_version = HC_LLM_ABI_VERSION;
  context_options.batch_size = 1;
  hc_llm_context *context = nullptr;
  if (hc_llm_context_create(model, &context_options, &context) != HC_LLM_STATUS_OK) return 3;

  hc_llm_job *generation = nullptr;
  hc_llm_generation_options generation_options = make_options(1);
  if (hc_llm_job_start(context, &generation_options, &generation) != HC_LLM_STATUS_OK) return 4;
  if (!wait_for_terminal(generation, HC_LLM_EVENT_COMPLETED, true)) return 5;
  if (hc_llm_job_destroy(generation) != HC_LLM_STATUS_OK) return 6;
  if (hc_llm_context_reset(context) != HC_LLM_STATUS_OK) return 7;

  hc_llm_job *cancelled = nullptr;
  hc_llm_generation_options cancellation_options = make_options(512);
  if (hc_llm_job_start(context, &cancellation_options, &cancelled) != HC_LLM_STATUS_OK) return 8;
  if (hc_llm_job_cancel(cancelled) != HC_LLM_STATUS_OK) return 9;
  if (!wait_for_terminal(cancelled, HC_LLM_EVENT_CANCELLED, false)) return 10;
  if (hc_llm_job_destroy(cancelled) != HC_LLM_STATUS_OK) return 11;
  if (hc_llm_context_reset(context) != HC_LLM_STATUS_OK) return 12;
  if (hc_llm_context_destroy(context) != HC_LLM_STATUS_OK) return 13;
  if (hc_llm_model_unload(model) != HC_LLM_STATUS_OK) return 14;

  hc_llm_model *reloaded = nullptr;
  if (hc_llm_model_load(runtime, path, nullptr, &reloaded) != HC_LLM_STATUS_OK) return 15;
  hc_llm_context *reloaded_context = nullptr;
  if (hc_llm_context_create(reloaded, nullptr, &reloaded_context) != HC_LLM_STATUS_OK) return 16;
  if (hc_llm_context_destroy(reloaded_context) != HC_LLM_STATUS_OK) return 17;
  if (hc_llm_model_unload(reloaded) != HC_LLM_STATUS_OK) return 18;
  return hc_llm_runtime_destroy(runtime) == HC_LLM_STATUS_OK ? 0 : 19;
}
