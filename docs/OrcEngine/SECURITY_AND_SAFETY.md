# Security and Safety

## Security posture

OrcEngine processes untrusted model files and potentially untrusted prompts while using native memory and high-resource CPU/GPU operations. Local execution does not eliminate security boundaries.

This document extends, not replaces, the repository root `SECURITY.md`.

## Trust boundaries

| Input/resource | Trust | Primary threats |
|---|---|---|
| GGUF file and metadata | Untrusted | memory corruption, overflow, path/resource abuse, misleading identity/license. |
| Tokenizer data/template | Untrusted | pathological time/memory, injection into prompts, invalid byte behavior. |
| Prompt/tools | Untrusted content | resource exhaustion, tool-format confusion; tool approval stays in TheOrc. |
| Native API caller | Partially trusted | invalid handles, sizes, callback lifetime, races. |
| GPU driver/libraries | External trusted dependency | device loss, async errors, version incompatibility. |
| Oracle artifacts | Build/test input | poisoned expected data or provenance substitution. |

## Parser requirements

- checked addition/multiplication for offsets, counts, dimensions, and bytes;
- configured maximum file metadata, strings, arrays, tensor rank/count, dimensions, context, and allocations;
- validate complete structure before constructing executable tensor views;
- reject unsupported version/endian/dtype/architecture cleanly;
- never allocate directly from an unbounded file count;
- avoid recursive parsers;
- keep file mapping read-only;
- include fuzzing and sanitizers;
- retain minimal crash reproducers without distributing restricted model content.

## Native memory safety

- RAII ownership internally;
- opaque handles and stable error codes at C ABI;
- no exceptions across ABI;
- explicit buffer pointer/length contracts;
- reject integer narrowing and misalignment;
- define callback and returned-buffer lifetime;
- prevent model destruction with active contexts;
- use sanitizers and compiler hardening supported by platform;
- zero sensitive prompt/cache buffers when threat analysis justifies it, with performance measured.

## Resource safety

Before execution, compute bounded host/device/cache/workspace requirements. Admission must fail before partial work when the configured budget cannot be met. Context limits, generated-token limits, thread count, and cancellation are mandatory controls.

GPU free-memory queries can race with other processes. Treat them as snapshots, maintain OrcEngine’s own allocation ledger, handle allocation failure, and never overclaim isolation.

## Model authenticity

Record source revision and SHA-256. A displayed model name from metadata is not identity. Future downloads should verify expected hashes/signatures through TheOrc’s model-depot policy before loading.

## Prompt and tool safety

OrcEngine generates tokens; it must not execute tools. Tool registration, approval, sandboxing, and authorization remain in TheOrc. Grammar-constrained tool output improves syntax, not authorization or semantic safety.

## Denial of service

Mitigate:

- huge metadata arrays/strings;
- extreme context/model dimensions;
- tokenizer worst-case behavior;
- grammar state explosion later;
- cancellation starvation inside long operations;
- allocation churn and GPU fragmentation;
- excessive debug tensor capture;
- decompression bombs if compressed formats ever appear.

## Concurrency

The initial one-active-context policy reduces races. Any concurrency expansion needs data-race testing, lock-order/lifetime design, and context isolation proof. Backend library thread safety must be checked from primary documentation.

## Error and log safety

Logs may include file paths, model names, dimensions, error codes, and hashes. Do not log prompts, raw model tensor contents, secrets, or unrestricted metadata by default. Diagnostic captures are explicit local artifacts with retention controls.

## Supply chain

- pin source/dependency versions;
- record binary hashes and build provenance;
- minimize dependencies;
- review transitive native libraries;
- separate dev-only oracle dependencies from shipped runtime;
- scan licenses and known vulnerabilities before packaging;
- test dependency resolution on a clean system.

## Security release gate

Before any product integration: malformed-model suite, fuzz smoke, sanitizers, allocation caps, cancellation, dependency manifest, structured error paths, no tool execution, and responsible-disclosure instructions must be verified.

## Reporting

Follow the repository `SECURITY.md`: do not open public issues for suspected vulnerabilities; report privately to the listed maintainer contact.
