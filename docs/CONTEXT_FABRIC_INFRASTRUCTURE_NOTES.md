# Context Fabric CF-7 Infrastructure Notes

Operational reference: which models are known to work with the CF-7 native
runtime, and which machine/environment quirks affect whether a run *executes*
at all. None of this is about grading correctness — for that, see
[CONTEXT_FABRIC_GRADING_SPEC.md](CONTEXT_FABRIC_GRADING_SPEC.md). For the
investigative story behind any entry below, see
[CONTEXT_FABRIC_BUG_HISTORY.md](CONTEXT_FABRIC_BUG_HISTORY.md).

A crash or NO-GO caused by something on this page is an infrastructure
problem, not a model capability gap or a scoring-logic bug — recorded here so
it isn't mistaken for either.

## Known model compatibility

> **Provisional as of Phase 1 (2026-07-15).** This table reflects the data
> points already confirmed in `CONTEXT_FABRIC_BUG_HISTORY.md` as of this
> writing. It will be finalized once any gate-metric or exhaustive-heuristic
> changes from later phases are accounted for.

| Model | Architecture note | Status | Evidence |
|---|---|---|---|
| Gemma-4-12B (any variant) | Shared-KV-cache design (layers reuse other layers' K/V tensors) | **Incompatible** — native `NoKvSlot` crashes on Context Fabric's evidence-heavy prompts, upstream `llama.cpp` limitation, no LLamaSharp release includes the fix yet | §7a — [ggml-org/llama.cpp#21468](https://github.com/ggml-org/llama.cpp/issues/21468), [#23720](https://github.com/ggml-org/llama.cpp/issues/23720); fix [#23981](https://github.com/ggml-org/llama.cpp/pull/23981) merged 2026-06-02, not yet pinned by any LLamaSharp release |
| Meta-Llama-3.1-8B-Instruct | Plain transformer | **Confirmed working** — zero `NoKvSlot` across 30- and 100-question runs, 99.1% citation precision (113/114) | §7a |
| qwen2.5-coder-7b-instruct | Plain transformer | **Works, but weaker capability** — zero `NoKvSlot`, but real (non-infrastructure) citation-discipline gaps at scale: 25-31/100 pass rate, ~76-99% citation precision depending on run | §7a |
| Qwen3.5-9B (any quant, e.g. Q8_0/Q4_K_M) | Hybrid attention + Gated Delta Net (recurrent); thinking-mode model | **Runtime clean as of PR #56** (`SeqMax` fix) — zero `NoKvSlot`, zero OOM. **Reader-incompatible until PR #58** (thinking-suppression fix) — without it, the model's default reasoning mode consumes the reader's entire completion budget on every segment, so 0/128 segments ever get accepted and B3's pass count only reflects how many held-out questions are Unanswerable, not capability. As of PR #58: segment acceptance confirmed 123/128 on a smoke test; full-scale re-score not yet run — see the CF-7 remediation tracker. | §7b (SeqMax), §7c (thinking suppression) |

**General rule of thumb from the above:** infrastructure compatibility and
model capability are independent axes. A model can run with zero crashes and
still score poorly, or be structurally unable to run at all regardless of how
capable it might otherwise be (Gemma-4-12B), or run and score near-zero for a
reason that's neither (Qwen3.5-9B before PR #58 — clean runtime, but the
reader never actually read anything). Check this table before assuming a low
score reflects the model being "bad" — confirm there's no infrastructure
failure hiding in the raw JSON first: grep `verification.errors` for
`NoKvSlot`, and check `segmentResults[].metrics.rawOutputExcerpt` for
`<think>`-prefixed reader outputs with near-zero segment acceptance.

## Fleet/environment issues

These affect whether a run *executes* on a specific machine, independent of
model choice.

- **HARDCOREPC (RTX 3050, 6GB VRAM) native-library load regression, 2026-07-04.**
  After a clean rebuild (`rmdir` of `bin`/`obj`/`publish` followed by
  `dotnet publish -r win-x64 --self-contained true`), every model load on this
  machine fails immediately with `TypeInitializationException: The type
  initializer for 'LLama.Native.NativeApi' threw an exception. | Inner:
  RuntimeError: Failed to load the native library.` — before any inference is
  attempted (`segments 0/128, questions 0/N`). Confirmed **not**
  model-specific: reproduced identically with both `Qwen3.5-4B-Q8_0.gguf` and
  `qwen2.5-coder-7b-instruct-q5_k_m.gguf` (the latter had loaded and run
  successfully on this same machine earlier in the same session, before the
  clean rebuild). Native DLLs in `publish/runtimes/win-x64/native/*` are
  present at expected file sizes across all variants (avx/avx2/avx512/cuda12/
  noavx), so this isn't a missing- or truncated-file problem — the underlying
  first-chance exception is being swallowed by .NET's cached
  `TypeInitializationException` behavior (a static constructor's exception is
  saved and rethrown verbatim on every later access), so the *real* root
  cause is not yet visible from application logs alone. **Not yet resolved**
  — needs investigation with a debugger attached or
  `COMPlus_LegacyExceptionHandling`/first-chance-exception logging enabled,
  ideally comparing against NEWCOREPC and HARDCORELAPTOPMSI where the
  identical `dotnet publish -r win-x64 --self-contained true` recipe
  succeeded the same night. HARDCOREPC was left idle (no benchmark process
  running) pending this investigation.

- **Windows/OpenSSH process detachment.** A benchmark launched via
  `ssh host "start /b ... "` does **not** survive the SSH session closing —
  Windows' OpenSSH server tears down the whole console process tree when the
  channel closes, killing detached children too. Two working alternatives:
  keep the `ssh host "long-running command"` invocation itself running under
  the orchestrating side's own background-task mechanism (simplest, used for
  NEWCOREPC/HARDCOREPC runs), or register a Task Scheduler job
  (`schtasks /create ... /tr <path-to-a-.bat-wrapper>`) and trigger it with
  `schtasks /run` (works even if the orchestrating side disconnects, used for
  the HARDCORELAPTOPMSI run). When using `schtasks`, the `/tr` command runs
  via `CreateProcess`, not a shell — `>`/`2>&1` redirection syntax is
  silently ignored unless wrapped in a `.bat` file or `cmd /c "..."`.

## Re-running

See [CONTEXT_FABRIC_BENCHMARK_MANIFEST.md § Re-Running The Expanded 120-Question Gate](CONTEXT_FABRIC_BENCHMARK_MANIFEST.md#re-running-the-expanded-120-question-gate)
for the canonical recipe (`Tools/ContextFabricBench/Run-CF7GateExpanded.ps1`).
