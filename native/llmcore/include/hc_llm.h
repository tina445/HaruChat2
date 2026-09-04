#ifndef HC_LLM_H
#define HC_LLM_H

/*
 * HaruChat local-model C ABI.  Every pointer returned here is an opaque
 * handle; C++ types and native allocations never cross this boundary.
 */

#include <stdint.h>
#include <stddef.h>

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
  HC_LLM_STATUS_INTERNAL_ERROR = 9,
  /* Explicit load-stage outcomes; never overload these as generic I/O. */
  HC_LLM_STATUS_NOT_FOUND = 10,
  HC_LLM_STATUS_ACCESS_DENIED = 11,
  HC_LLM_STATUS_MODEL_LOAD_FAILED = 12,
  HC_LLM_STATUS_CONTEXT_INIT_FAILED = 13
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
  uint32_t reserved; /* Must be zero. */
} hc_llm_runtime_options;

typedef struct hc_llm_model_load_options {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t reserved0; /* Must be zero. */
  uint32_t reserved1; /* Must be zero. */
  /* Optional trailing fields. Older callers may omit them. */
  uint32_t load_flags;
  uint32_t reserved2; /* Must be zero when supplied. */
} hc_llm_model_load_options;

#define HC_LLM_MODEL_LOAD_OPTIONS_V1_SIZE ((uint32_t) offsetof(hc_llm_model_load_options, load_flags))
#define HC_LLM_MODEL_LOAD_FLAG_VOCAB_ONLY UINT32_C(0x00000001)

typedef struct hc_llm_context_options {
  uint32_t struct_size;
  uint32_t abi_version;
  uint32_t context_size;
  uint32_t reserved; /* Must be zero. */
  /* Optional trailing fields. Older callers may omit them. */
  uint32_t batch_size; /* 0 uses the backend default. */
  uint32_t reserved1; /* Must be zero when supplied. */
  /* Long-context controls.  Zero keeps the upstream llama.cpp default. */
  uint32_t ubatch_size; /* Physical micro-batch; must not exceed batch_size. */
  uint32_t kv_cache_type_k; /* hc_llm_kv_cache_type */
  uint32_t kv_cache_type_v; /* hc_llm_kv_cache_type */
  uint32_t flash_attention; /* hc_llm_flash_attention_mode */
  uint32_t offload_kqv; /* 0 keeps KV/KQV on CPU, 1 offloads when available. */
  uint32_t reserved2; /* Must be zero when supplied. */
} hc_llm_context_options;

typedef enum hc_llm_kv_cache_type {
  HC_LLM_KV_CACHE_TYPE_DEFAULT = 0,
  HC_LLM_KV_CACHE_TYPE_F16 = 1,
  HC_LLM_KV_CACHE_TYPE_Q8_0 = 2
} hc_llm_kv_cache_type;

typedef enum hc_llm_flash_attention_mode {
  HC_LLM_FLASH_ATTENTION_AUTO = 0,
  HC_LLM_FLASH_ATTENTION_DISABLED = 1,
  HC_LLM_FLASH_ATTENTION_ENABLED = 2
} hc_llm_flash_attention_mode;

typedef struct hc_llm_generation_options {
  uint32_t struct_size;
  uint32_t abi_version;
  const uint8_t *prompt_utf8; /* borrowed for this call only */
  uint32_t prompt_bytes;
  const uint8_t *mock_response_utf8; /* bootstrap backend only; borrowed */
  uint32_t mock_response_bytes;
  uint32_t max_tokens;
  uint32_t token_delay_ms;
  /* Optional trailing fields. Older callers may omit them. */
  float temperature; /* 0 selects deterministic greedy sampling. */
  float top_p;       /* (0, 1]; 0 uses the backend default. */
  uint32_t top_k;    /* 0 disables top-k filtering. */
  uint32_t seed;     /* Used for non-greedy sampling. */
} hc_llm_generation_options;

/* Minimum caller-provided sizes accepted by ABI v1 implementations. */
#define HC_LLM_CONTEXT_OPTIONS_V1_SIZE ((uint32_t) offsetof(hc_llm_context_options, batch_size))
#define HC_LLM_GENERATION_OPTIONS_V1_SIZE ((uint32_t) offsetof(hc_llm_generation_options, temperature))

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
  /* Optional trailing fields. Older callers may omit them. */
  char architecture[64];
} hc_llm_model_metadata;

#define HC_LLM_MODEL_METADATA_V1_SIZE ((uint32_t) offsetof(hc_llm_model_metadata, architecture))

typedef struct hc_llm_chat_message {
  const char *role_utf8;       /* borrowed, NUL-terminated UTF-8 role */
  const uint8_t *content_utf8; /* borrowed, explicitly-sized UTF-8 text */
  uint32_t content_bytes;
} hc_llm_chat_message;

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
/*
 * Counts tokens using the vocabulary owned by a loaded model. Input is a
 * borrowed, explicitly-sized UTF-8 byte sequence. The count includes the
 * model's prompt-start special token, matching hc_llm_job_start prompt
 * tokenization. Bootstrap builds deterministically count UTF-8 code points.
 */
HC_LLM_API hc_llm_status hc_llm_model_count_tokens(
    const hc_llm_model *model, const uint8_t *text_utf8, uint32_t text_bytes,
    uint32_t *out_token_count);
/* Applies the loaded GGUF's embedded tokenizer.chat_template. The returned
 * prompt is copied into caller storage; query out_required_bytes first. */
HC_LLM_API hc_llm_status hc_llm_model_apply_chat_template(
    const hc_llm_model *model, const hc_llm_chat_message *messages,
    uint32_t message_count, uint32_t add_assistant, uint8_t *buffer_utf8,
    uint32_t buffer_bytes, uint32_t *out_required_bytes);
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
