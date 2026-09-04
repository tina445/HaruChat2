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
  hc_llm_runtime_options reserved_runtime_options = runtime_options;
  reserved_runtime_options.reserved = 1;
  hc_llm_runtime *reserved_runtime = nullptr;
  assert(hc_llm_runtime_create(&reserved_runtime_options, &reserved_runtime) == HC_LLM_STATUS_INVALID_ARGUMENT);
  hc_llm_runtime *runtime = nullptr;
  assert(hc_llm_runtime_create(&runtime_options, &runtime) == HC_LLM_STATUS_OK);

  hc_llm_runtime_metadata runtime_metadata{};
  runtime_metadata.struct_size = sizeof(runtime_metadata);
  runtime_metadata.abi_version = HC_LLM_ABI_VERSION;
  assert(hc_llm_runtime_get_metadata(runtime, &runtime_metadata) == HC_LLM_STATUS_OK);
  assert(std::strcmp(runtime_metadata.backend_name, "bootstrap-mock") == 0);

  hc_llm_model *model = nullptr;
  assert(hc_llm_model_load(runtime, "/does/not/exist.gguf", nullptr, &model) == HC_LLM_STATUS_NOT_FOUND);
  const std::string fixture = make_gguf_fixture();
  hc_llm_model_load_options reserved_load_options{};
  reserved_load_options.struct_size = sizeof(reserved_load_options);
  reserved_load_options.abi_version = HC_LLM_ABI_VERSION;
  reserved_load_options.reserved0 = 1;
  assert(hc_llm_model_load(runtime, fixture.c_str(), &reserved_load_options, &model) == HC_LLM_STATUS_INVALID_ARGUMENT);
  const char invalid_path[] = {'G', static_cast<char>(0xc0), static_cast<char>(0x80), '\0'};
  assert(hc_llm_model_load(runtime, invalid_path, nullptr, &model) == HC_LLM_STATUS_INVALID_ARGUMENT);
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
  hc_llm_model_metadata legacy_model_metadata{};
  legacy_model_metadata.struct_size = HC_LLM_MODEL_METADATA_V1_SIZE;
  legacy_model_metadata.abi_version = HC_LLM_ABI_VERSION;
  std::memset(legacy_model_metadata.architecture, 0x5a, sizeof(legacy_model_metadata.architecture));
  assert(hc_llm_model_get_metadata(model, &legacy_model_metadata) == HC_LLM_STATUS_OK);
  assert(legacy_model_metadata.architecture[0] == static_cast<char>(0x5a));

  // Bootstrap token counting deterministically follows Unicode scalar values;
  // real backends use the loaded model vocabulary through the same ABI.
  const uint8_t mixed_text[] = u8"A가🙂";
  uint32_t token_count = 0;
  assert(hc_llm_model_count_tokens(model, mixed_text, sizeof(mixed_text) - 1, &token_count) == HC_LLM_STATUS_OK);
  assert(token_count == 3);
  assert(hc_llm_model_count_tokens(model, nullptr, 0, &token_count) == HC_LLM_STATUS_OK);
  assert(token_count == 0);
  const hc_llm_chat_message chat_message{ "user", mixed_text, static_cast<uint32_t>(sizeof(mixed_text) - 1) };
  uint32_t template_bytes = 0;
  assert(hc_llm_model_apply_chat_template(model, &chat_message, 1, 1, nullptr, 0, &template_bytes) == HC_LLM_STATUS_UNSUPPORTED);
  assert(hc_llm_model_count_tokens(model, mixed_text, sizeof(mixed_text) - 1, nullptr) == HC_LLM_STATUS_INVALID_ARGUMENT);
  const uint8_t invalid_token_text[] = {0xc0U, 0x80U};
  assert(hc_llm_model_count_tokens(model, invalid_token_text, sizeof(invalid_token_text), &token_count) ==
         HC_LLM_STATUS_INVALID_ARGUMENT);
  assert(hc_llm_model_count_tokens(nullptr, mixed_text, sizeof(mixed_text) - 1, &token_count) ==
         HC_LLM_STATUS_INVALID_HANDLE);

  hc_llm_context *context = nullptr;
  hc_llm_context_options legacy_context_options{};
  legacy_context_options.struct_size = HC_LLM_CONTEXT_OPTIONS_V1_SIZE;
  legacy_context_options.abi_version = HC_LLM_ABI_VERSION;
  assert(hc_llm_context_create(model, &legacy_context_options, &context) == HC_LLM_STATUS_OK);
  assert(hc_llm_context_destroy(context) == HC_LLM_STATUS_OK);

  hc_llm_context_options context_options{};
  context_options.struct_size = sizeof(context_options);
  context_options.abi_version = HC_LLM_ABI_VERSION;
  context_options.batch_size = 16;
  context_options.ubatch_size = 8;
  context_options.kv_cache_type_k = HC_LLM_KV_CACHE_TYPE_Q8_0;
  context_options.kv_cache_type_v = HC_LLM_KV_CACHE_TYPE_Q8_0;
  context_options.flash_attention = HC_LLM_FLASH_ATTENTION_AUTO;
  context_options.offload_kqv = 1;
  assert(hc_llm_context_create(model, &context_options, &context) == HC_LLM_STATUS_OK);
  hc_llm_context_options invalid_ubatch_options = context_options;
  invalid_ubatch_options.ubatch_size = 17;
  hc_llm_context *invalid_ubatch_context = nullptr;
  assert(hc_llm_context_create(model, &invalid_ubatch_options, &invalid_ubatch_context) == HC_LLM_STATUS_INVALID_ARGUMENT);
  hc_llm_context_options reserved_context_options = context_options;
  reserved_context_options.reserved1 = 1;
  hc_llm_context *reserved_context = nullptr;
  assert(hc_llm_context_create(model, &reserved_context_options, &reserved_context) == HC_LLM_STATUS_INVALID_ARGUMENT);
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

  hc_llm_generation_options legacy_generation{};
  legacy_generation.struct_size = HC_LLM_GENERATION_OPTIONS_V1_SIZE;
  legacy_generation.abi_version = HC_LLM_ABI_VERSION;
  const char legacy_response[] = "legacy";
  legacy_generation.mock_response_utf8 = reinterpret_cast<const uint8_t *>(legacy_response);
  legacy_generation.mock_response_bytes = sizeof(legacy_response) - 1;
  hc_llm_job *legacy_job = nullptr;
  assert(hc_llm_job_start(context, &legacy_generation, &legacy_job) == HC_LLM_STATUS_OK);
  terminals = 0;
  tokens.clear();
  drain_to_terminal(legacy_job, &terminals, &tokens);
  assert(tokens == legacy_response && terminals == 1);
  assert(hc_llm_job_destroy(legacy_job) == HC_LLM_STATUS_OK);

  // Strict UTF-8 rejects malformed, overlong, surrogate, and out-of-range input.
  const uint8_t invalid_utf8[][4] = {
      {0xc0U, 0x80U, 0, 0}, {0xedU, 0xa0U, 0x80U, 0}, {0xf4U, 0x90U, 0x80U, 0}};
  for (const auto &invalid : invalid_utf8) {
    hc_llm_generation_options invalid_options{};
    invalid_options.struct_size = sizeof(invalid_options);
    invalid_options.abi_version = HC_LLM_ABI_VERSION;
    invalid_options.prompt_utf8 = invalid;
    invalid_options.prompt_bytes = invalid[0] == 0xc0U ? 2U : 3U;
    hc_llm_job *invalid_job = nullptr;
    assert(hc_llm_job_start(context, &invalid_options, &invalid_job) == HC_LLM_STATUS_INVALID_ARGUMENT);
  }
  hc_llm_generation_options invalid_sampling{};
  invalid_sampling.struct_size = sizeof(invalid_sampling);
  invalid_sampling.abi_version = HC_LLM_ABI_VERSION;
  invalid_sampling.temperature = -0.1F;
  hc_llm_job *invalid_sampling_job = nullptr;
  assert(hc_llm_job_start(context, &invalid_sampling, &invalid_sampling_job) == HC_LLM_STATUS_INVALID_ARGUMENT);
  invalid_sampling.temperature = 0.7F;
  invalid_sampling.top_p = 1.1F;
  assert(hc_llm_job_start(context, &invalid_sampling, &invalid_sampling_job) == HC_LLM_STATUS_INVALID_ARGUMENT);

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
  assert(hc_llm_model_count_tokens(model, mixed_text, sizeof(mixed_text) - 1, &token_count) ==
         HC_LLM_STATUS_INVALID_HANDLE);

  hc_llm_model *reloaded = nullptr;
  assert(hc_llm_model_load(runtime, fixture.c_str(), nullptr, &reloaded) == HC_LLM_STATUS_OK);
  assert(hc_llm_model_unload(reloaded) == HC_LLM_STATUS_OK);
  assert(hc_llm_runtime_destroy(runtime) == HC_LLM_STATUS_OK);
  std::remove(fixture.c_str());
  return 0;
}
