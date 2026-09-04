#include "hc_llm.h"

#include <cassert>
#include <cstring>
#include <vector>

int main(int argc, char **argv) {
  if (argc != 2) return 2;

  hc_llm_runtime *runtime = nullptr;
  assert(hc_llm_runtime_create(nullptr, &runtime) == HC_LLM_STATUS_OK);

  hc_llm_model_load_options options{};
  options.struct_size = sizeof(options);
  options.abi_version = HC_LLM_ABI_VERSION;
  options.load_flags = HC_LLM_MODEL_LOAD_FLAG_VOCAB_ONLY;
  hc_llm_model *model = nullptr;
  assert(hc_llm_model_load(runtime, argv[1], &options, &model) == HC_LLM_STATUS_OK);

  hc_llm_model_metadata metadata{};
  metadata.struct_size = sizeof(metadata);
  metadata.abi_version = HC_LLM_ABI_VERSION;
  assert(hc_llm_model_get_metadata(model, &metadata) == HC_LLM_STATUS_OK);
  assert(std::strstr(metadata.architecture, "gemma4") != nullptr);

  const char *system = "Be concise.";
  const char *user = "Hello";
  const hc_llm_chat_message messages[] = {
      {"system", reinterpret_cast<const uint8_t *>(system), static_cast<uint32_t>(std::strlen(system))},
      {"user", reinterpret_cast<const uint8_t *>(user), static_cast<uint32_t>(std::strlen(user))},
  };
  uint32_t required = 0;
  // The pinned llama.cpp version parses the GGUF template but deliberately
  // rejects this Gemma 4 Jinja form. The managed catalog must use the verified
  // architecture metadata to select its data-only fallback instead.
  assert(hc_llm_model_apply_chat_template(model, messages, 2, 1, nullptr, 0, &required) == HC_LLM_STATUS_UNSUPPORTED);

  hc_llm_context *context = nullptr;
  assert(hc_llm_context_create(model, nullptr, &context) == HC_LLM_STATUS_UNSUPPORTED);
  assert(hc_llm_model_unload(model) == HC_LLM_STATUS_OK);
  assert(hc_llm_runtime_destroy(runtime) == HC_LLM_STATUS_OK);
  return 0;
}
