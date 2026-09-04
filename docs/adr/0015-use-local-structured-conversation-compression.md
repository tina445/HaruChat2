# ADR-0015: local structured conversation compression 사용

## Context

96–128 Ki context 실험에서는 긴 대화가 output reserve를 침범하지 않아야 하며, 문자 수 추정은 한국어와 emoji에서 안전하지 않다.

## Decision

local adapter는 loaded GGUF tokenizer를 C ABI로 노출한다. prompt budget 70%에서 recent 8 completed turn을 제외한 process-local 원문 archive를 selected local model로 구조화 요약하고 55% 이하를 목표로 한다. summary는 memory와 별도 budget으로 prompt에 넣고, opt-in retention과 민감정보 필터를 통과한 경우만 session memory에 저장한다. 기본 8 Ki context에서는 2,048 output reserve와 tokenizer preflight 이후에도 필수 prompt가 넘으면 generation을 시작하지 않는다. 더 큰 context는 device gate를 통과한 explicit override다.

## Alternatives

문자 수 기반 eviction, remote summary, recursive summary-of-summary, KV cache shift를 검토했으나 tokenizer 정확성·privacy·품질 또는 lifecycle 위험 때문에 채택하지 않았다.

## Consequences

압축은 사용자 stream에 노출되지 않는 추가 local generation이며 실패 시 결정론적 eviction으로 계속한다. archive는 process-local이라 앱 재시작 후 재요약할 원문은 없다. 128 Ki 유지 여부는 M4 device gate가 결정한다.

## Status

Accepted

## Date

2026-09-02
