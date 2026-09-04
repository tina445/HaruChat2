#ifndef HC_LLM_BRIDGE_H
#define HC_LLM_BRIDGE_H

// This header is inside the package root, so xcross preserves the relative
// path while staging the plugin for SwiftPM.
#include "../../../Vendor/LlmCore.xcframework/ios-arm64/Headers/hc_llm.h"

static inline uint32_t hc_llm_bridge_abi_version(void) {
  return HC_LLM_ABI_VERSION;
}

// Keep Swift source compatible with a staged XCFramework header from before
// the optional long-context trailer was added. The ABI contract permits newer
// callers to provide trailing fields; older native artifacts ignore them.
hc_llm_status hc_llm_bridge_context_create_long(
    hc_llm_model *model, uint32_t context_size, hc_llm_context **out_context);

#endif
