#ifndef HC_LLM_H
#define HC_LLM_H

/*
 * HaruChat local-model C ABI.  Every pointer returned here is an opaque
 * handle; C++ types and native allocations never cross this boundary.
 */

#include <stdint.h>

#if defined(_WIN32) && defined(HC_LLM_BUILDING_LIBRARY)
#  define HC_LLM_API __declspec(dllexport)
#elif defined(_WIN32)
#  define HC_LLM_API __declspec(dllimport)
#else
#  define HC_LLM_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define HC_LLM_ABI_VERSION UINT32_C(0x00010000)

typedef struct hc_llm_runtime hc_llm_runtime;
typedef struct hc_llm_model hc_llm_model;
typedef struct hc_llm_context hc_llm_context;
typedef struct hc_llm_job hc_llm_job;

typedef enum hc_llm_status {
  HC_LLM_STATUS_OK = 0,
  HC_LLM_STATUS_INVALID_ARGUMENT = 1,
  HC_LLM_STATUS_INVALID_HANDLE = 2,
  HC_LLM_STATUS_ABI_MISMATCH = 3,
  HC_LLM_STATUS_IO_ERROR = 4,
  HC_LLM_STATUS_BUSY = 5,
  HC_LLM_STATUS_WOULD_BLOCK = 6,
  HC_LLM_STATUS_CANCELLED = 7,
  HC_LLM_STATUS_UNSUPPORTED = 8,
  HC_LLM_STATUS_INTERNAL_ERROR = 9
} hc_llm_status;

typedef enum hc_llm_event_type {
  HC_LLM_EVENT_TOKEN = 1,
  HC_LLM_EVENT_METRICS = 2,
  HC_LLM_EVENT_COMPLETED = 3,
  HC_LLM_EVENT_CANCELLED = 4,
  HC_LLM_EVENT_ERROR = 5
} hc_llm_event_type;

typedef struct hc_llm_runtime_options {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t event_queue_capacity;
  uint32_t reserved;
} hc_llm_runtime_options;

typedef struct hc_llm_model_load_options {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t reserved0;
  uint32_t reserved1;
} hc_llm_model_load_options;

typedef struct hc_llm_context_options {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t context_size;
  uint32_t reserved;
} hc_llm_context_options;

typedef struct hc_llm_generation_options {
  uint32_t struct_size;
  uint32_t abi_version;
  const uint8_t *prompt_utf8; /* borrowed for this call only */
  uint32_t prompt_bytes;
  const uint8_t *mock_response_utf8; /* bootstrap backend only; borrowed */
  uint32_t mock_response_bytes;
  uint32_t max_tokens;
  uint32_t token_delay_ms;
} hc_llm_generation_options;

typedef struct hc_llm_runtime_metadata {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t capability_flags;
  uint32_t reserved;
  char backend_name[32];
  char target_triple[64];
} hc_llm_runtime_metadata;

typedef struct hc_llm_model_metadata {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t training_context_tokens;
  uint32_t reserved;
  char description[128];
} hc_llm_model_metadata;

typedef struct hc_llm_generation_metrics {
  uint32_t struct_size;
  uint32_t abi_version;
  uint64_t emitted_token_count;
  uint64_t queue_depth;
  uint64_t elapsed_milliseconds;
} hc_llm_generation_metrics;

typedef struct hc_llm_event {
  uint32_t struct_size;
  uint32_t abi_version;
  hc_llm_event_type type;
  uint32_t is_terminal;
  uint64_t sequence;
  const uint8_t *payload_utf8; /* valid until next poll on this job or destroy */
  uint32_t payload_bytes;
  uint32_t reserved;
  hc_llm_generation_metrics metrics;
} hc_llm_event;

HC_LLM_API uint32_t hc_llm_get_abi_version(void);
HC_LLM_API const char *hc_llm_status_message(hc_llm_status status);

HC_LLM_API hc_llm_status hc_llm_runtime_create(
    const hc_llm_runtime_options *options, hc_llm_runtime **out_runtime);
HC_LLM_API hc_llm_status hc_llm_runtime_get_metadata(
    const hc_llm_runtime *runtime, hc_llm_runtime_metadata *out_metadata);
HC_LLM_API hc_llm_status hc_llm_runtime_destroy(hc_llm_runtime *runtime);

HC_LLM_API hc_llm_status hc_llm_model_load(
    hc_llm_runtime *runtime, const char *path_utf8,
    const hc_llm_model_load_options *options, hc_llm_model **out_model);
HC_LLM_API hc_llm_status hc_llm_model_get_path(
    const hc_llm_model *model, char *buffer, uint32_t buffer_bytes,
    uint32_t *out_required_bytes);
HC_LLM_API hc_llm_status hc_llm_model_get_metadata(
    const hc_llm_model *model, hc_llm_model_metadata *out_metadata);
HC_LLM_API hc_llm_status hc_llm_model_unload(hc_llm_model *model);

HC_LLM_API hc_llm_status hc_llm_context_create(
    hc_llm_model *model, const hc_llm_context_options *options,
    hc_llm_context **out_context);
HC_LLM_API hc_llm_status hc_llm_context_reset(hc_llm_context *context);
HC_LLM_API hc_llm_status hc_llm_context_destroy(hc_llm_context *context);

HC_LLM_API hc_llm_status hc_llm_job_start(
    hc_llm_context *context, const hc_llm_generation_options *options,
    hc_llm_job **out_job);
HC_LLM_API hc_llm_status hc_llm_job_cancel(hc_llm_job *job);
HC_LLM_API hc_llm_status hc_llm_job_poll(hc_llm_job *job, hc_llm_event *out_event);
HC_LLM_API hc_llm_status hc_llm_job_destroy(hc_llm_job *job);

#ifdef __cplusplus
}
#endif

#endif /* HC_LLM_H */
