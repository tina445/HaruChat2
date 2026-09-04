#include "hc_llm_bridge.h"

typedef struct hc_llm_bridge_context_options_v2 {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t context_size;
  uint32_t reserved;
  uint32_t batch_size;
  uint32_t reserved1;
  uint32_t ubatch_size;
  uint32_t kv_cache_type_k;
  uint32_t kv_cache_type_v;
  uint32_t flash_attention;
  uint32_t offload_kqv;
  uint32_t reserved2;
} hc_llm_bridge_context_options_v2;

hc_llm_status hc_llm_bridge_context_create_long(
    hc_llm_model *model, uint32_t context_size, hc_llm_context **out_context) {
  hc_llm_bridge_context_options_v2 options = {0};
  options.struct_size = (uint32_t) sizeof(options);
  options.abi_version = hc_llm_bridge_abi_version();
  options.context_size = context_size;
  options.batch_size = 256;
  options.ubatch_size = 64;
  options.kv_cache_type_k = 2; // HC_LLM_KV_CACHE_TYPE_Q8_0
  options.kv_cache_type_v = 2; // HC_LLM_KV_CACHE_TYPE_Q8_0
  options.flash_attention = 2; // HC_LLM_FLASH_ATTENTION_ENABLED
  options.offload_kqv = 1;
  return hc_llm_context_create(model, (const hc_llm_context_options *) &options, out_context);
}
