# ADR-0014: Use a bounded agent and private remote adapter

## Context

Post-MVP chat needs useful local tools and an optional remote provider without turning model output into authority or sending durable private memory outside the device.

## Decision

- Keep typed tool schemas, calls, results, registry, authorization and approval ports in the Unity-free runtime. Agent iterations and tool-result size are bounded; write tools require a Presentation approval port.
- Ship only local allowlisted time, random, lore and memory tools. No filesystem, arbitrary code, or network tools are registered.
- Add an OpenAI-compatible HTTP/SSE adapter behind `IModelAdapter`. It requires explicit remote opt-in and secure key lookup. It excludes Tool messages and canonical memory prompt sections from requests.

## Consequences

The local adapter remains tool-unsupported until its model/profile path can reliably emit typed tool calls. Remote providers are manually selected and do not receive long-term memory or tool results.

## Status

Accepted

## Date

2026-09-02
