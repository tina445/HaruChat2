#ifndef HC_LLM_BRIDGE_H
#define HC_LLM_BRIDGE_H

// This header is inside the package root, so xcross preserves the relative
// path while staging the plugin for SwiftPM.
#include "../../../Vendor/LlmCore.xcframework/ios-arm64/Headers/hc_llm.h"

static inline uint32_t hc_llm_bridge_abi_version(void) {
  return HC_LLM_ABI_VERSION;
}

#endif
