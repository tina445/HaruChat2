#include "hc_llm.h"

#include <cassert>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

namespace {

std::string make_gguf_fixture() {
  const std::string path = "/tmp/haruchat-llmcore-fixture.gguf";
  std::ofstream file(path, std::ios::binary | std::ios::trunc);
  file.write("GGUF", 4);
  file.write("fixture", 7);
  return path;
}

hc_llm_event next_event(hc_llm_job *job) {
  for (int attempts = 0; attempts < 5000; ++attempts) {
    hc_llm_event event{};
    event.struct_size = sizeof(event);
    event.abi_version = HC_LLM_ABI_VERSION;
    const hc_llm_status status = hc_llm_job_poll(job, &event);
    if (status == HC_LLM_STATUS_OK) return event;
    assert(status == HC_LLM_STATUS_WOULD_BLOCK);
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
  }
  assert(false && "timed out waiting for event");
  return {};
}

void drain_to_terminal(hc_llm_job *job, int *terminal_count, std::string *tokens) {
  while (true) {
    hc_llm_event event = next_event(job);
    if (event.type == HC_LLM_EVENT_TOKEN) {
      tokens->append(reinterpret_cast<const char *>(event.payload_utf8), event.payload_bytes);
    }
    if (event.is_terminal) {
      ++*terminal_count;
      return;
    }
  }
}

hc_llm_job *start(hc_llm_context *context, const char *response, uint32_t delay_ms = 0) {
  hc_llm_generation_options options{};
  options.struct_size = sizeof(options);
  options.abi_version = HC_LLM_ABI_VERSION;
  static const uint8_t prompt[] = "test";
  options.prompt_utf8 = prompt;
  options.prompt_bytes = sizeof(prompt) - 1;
  options.mock_response_utf8 = reinterpret_cast<const uint8_t *>(response);
  options.mock_response_bytes = static_cast<uint32_t>(std::strlen(response));
  options.token_delay_ms = delay_ms;
  hc_llm_job *job = nullptr;
  assert(hc_llm_job_start(context, &options, &job) == HC_LLM_STATUS_OK);
  return job;
}

} // namespace

int main() {
  assert(hc_llm_runtime_create(nullptr, nullptr) == HC_LLM_STATUS_INVALID_ARGUMENT);
  hc_llm_runtime_options bad_options{};
  bad_options.struct_size = sizeof(bad_options);
  bad_options.abi_version = 999;
  assert(hc_llm_runtime_create(&bad_options, nullptr) == HC_LLM_STATUS_INVALID_ARGUMENT);

  hc_llm_runtime_options runtime_options{};
  runtime_options.struct_size = sizeof(runtime_options);
  runtime_options.abi_version = HC_LLM_ABI_VERSION;
  runtime_options.event_queue_capacity = 1;
  hc_llm_runtime *runtime = nullptr;
  assert(hc_llm_runtime_create(&runtime_options, &runtime) == HC_LLM_STATUS_OK);

  hc_llm_runtime_metadata runtime_metadata{};
  runtime_metadata.struct_size = sizeof(runtime_metadata);
  runtime_metadata.abi_version = HC_LLM_ABI_VERSION;
  assert(hc_llm_runtime_get_metadata(runtime, &runtime_metadata) == HC_LLM_STATUS_OK);
  assert(std::strcmp(runtime_metadata.backend_name, "bootstrap-mock") == 0);

  hc_llm_model *model = nullptr;
  assert(hc_llm_model_load(runtime, "/does/not/exist.gguf", nullptr, &model) == HC_LLM_STATUS_IO_ERROR);
  const std::string fixture = make_gguf_fixture();
  assert(hc_llm_model_load(runtime, fixture.c_str(), nullptr, &model) == HC_LLM_STATUS_OK);
  uint32_t required = 0;
  assert(hc_llm_model_get_path(model, nullptr, 0, &required) == HC_LLM_STATUS_OK);
  std::vector<char> model_path(required);
  assert(hc_llm_model_get_path(model, model_path.data(), required, &required) == HC_LLM_STATUS_OK);
  assert(fixture == model_path.data());
  hc_llm_model_metadata model_metadata{};
  model_metadata.struct_size = sizeof(model_metadata);
  model_metadata.abi_version = HC_LLM_ABI_VERSION;
  assert(hc_llm_model_get_metadata(model, &model_metadata) == HC_LLM_STATUS_OK);
  assert(std::strlen(model_metadata.description) > 0);

  hc_llm_context *context = nullptr;
  assert(hc_llm_context_create(model, nullptr, &context) == HC_LLM_STATUS_OK);
  assert(hc_llm_model_unload(model) == HC_LLM_STATUS_BUSY);

  hc_llm_job *job = start(context, u8"가나다");
  hc_llm_job *duplicate = nullptr;
  hc_llm_generation_options duplicate_options{};
  duplicate_options.struct_size = sizeof(duplicate_options);
  duplicate_options.abi_version = HC_LLM_ABI_VERSION;
  assert(hc_llm_job_start(context, &duplicate_options, &duplicate) == HC_LLM_STATUS_BUSY);

  // Let the bounded queue apply backpressure before consuming it.
  std::this_thread::sleep_for(std::chrono::milliseconds(5));
  int terminals = 0;
  std::string tokens;
  drain_to_terminal(job, &terminals, &tokens);
  assert(terminals == 1);
  assert(tokens == u8"가나다");
  assert(hc_llm_context_reset(context) == HC_LLM_STATUS_OK);
  assert(hc_llm_job_destroy(job) == HC_LLM_STATUS_OK);

  hc_llm_job *cancelled = start(context, "this generation will be cancelled", 2);
  assert(hc_llm_job_cancel(cancelled) == HC_LLM_STATUS_OK);
  assert(hc_llm_job_cancel(cancelled) == HC_LLM_STATUS_OK);
  terminals = 0;
  tokens.clear();
  hc_llm_event terminal{};
  while (true) {
    hc_llm_event event = next_event(cancelled);
    if (event.is_terminal) {
      terminal = event;
      ++terminals;
      break;
    }
  }
  assert(terminal.type == HC_LLM_EVENT_CANCELLED && terminals == 1);
  assert(hc_llm_job_destroy(cancelled) == HC_LLM_STATUS_OK);

  hc_llm_job *second = start(context, "ok");
  terminals = 0;
  tokens.clear();
  drain_to_terminal(second, &terminals, &tokens);
  assert(tokens == "ok" && terminals == 1);
  assert(hc_llm_job_destroy(second) == HC_LLM_STATUS_OK);
  // An explicitly destroyed job must no longer remain in runtime ownership;
  // runtime teardown below is the regression check for double-free/UAF.
  assert(hc_llm_context_destroy(context) == HC_LLM_STATUS_OK);
  assert(hc_llm_context_reset(context) == HC_LLM_STATUS_INVALID_HANDLE);
  assert(hc_llm_model_unload(model) == HC_LLM_STATUS_OK);

  hc_llm_model *reloaded = nullptr;
  assert(hc_llm_model_load(runtime, fixture.c_str(), nullptr, &reloaded) == HC_LLM_STATUS_OK);
  assert(hc_llm_model_unload(reloaded) == HC_LLM_STATUS_OK);
  assert(hc_llm_runtime_destroy(runtime) == HC_LLM_STATUS_OK);
  std::remove(fixture.c_str());
  return 0;
}
