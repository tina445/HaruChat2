#include "hc_llm.h"

int main(void) {
  hc_llm_runtime *runtime = 0;
  hc_llm_runtime_options options = {0};
  options.struct_size = sizeof(options);
  options.abi_version = HC_LLM_ABI_VERSION;
  options.event_queue_capacity = 2;
  if (hc_llm_get_abi_version() != HC_LLM_ABI_VERSION) return 1;
  if (hc_llm_runtime_create(&options, &runtime) != HC_LLM_STATUS_OK) return 2;
  return hc_llm_runtime_destroy(runtime) == HC_LLM_STATUS_OK ? 0 : 3;
}
