# Prefix KV Cache Research — Phase 5

Native Runtime Phase 5 (RUNTIME_PHASE0_SPEC.md): "research, non-blocking, never a promised
deliverable." This note closes the research with a concrete mechanism and a concrete blocker,
both confirmed against LLamaSharp 0.27.0's actual API (XML doc comments + the §7 spike's
firsthand `BatchedExecutor`/`SafeLLamaContextHandle` usage), not speculation.

## Verdict

**The mechanism exists and is cheap — but it's incompatible with AdapterManager's per-role
design as built.** `Conversation.Fork()` (wrapping `SafeLLamaContextHandle.MemorySequenceCopy`)
is exactly the "shared system-prompt prefix" primitive Phase 5 asked about: prefill the prefix
once into one sequence, then fork it into N child sequences that continue independently without
re-prefilling. It's genuinely free — no extra KV memory allocated, just token-to-sequence
reassignment. The blocker: LoRA adapters are set **per context**
(`SafeLLamaContextHandle.SetLoraAdapters`), not per sequence. All sequences forked within one
`BatchedExecutor`'s context share the same adapter at all times. TheOrc's warband wants Boss,
Worker, Researcher, and Reviewer each running a *different* LoRA — Fork() cannot give them a
shared prefix while running different adapters, because the adapter is a context-wide setting,
not a per-sequence one.

## What llama.cpp / LLamaSharp 0.27.0 actually expose

- `SafeLLamaContextHandle.MemorySequenceCopy(LLamaSeqId src, LLamaSeqId dest, LLamaPos p0, LLamaPos p1)`
  — official doc: *"Copy all tokens that belong to the specified sequence to another sequence.
  Note that this does not allocate extra memory — it simply assigns the tokens to the new
  sequence."* Confirmed via the package's shipped XML docs
  (`LLamaSharp.xml`), not inferred.
- `Conversation.Fork()` — official doc, `<summary>`: *"Create a copy of the current
  conversation"*; `<remarks>`: *"The copy shares internal state, so consumes very little extra
  memory."* (two separate XML tags on the same member, quoted together above as "official doc"
  — both are Fork()'s own documentation, neither is borrowed from `MemorySequenceCopy`.) This is
  the high-level wrapper over `MemorySequenceCopy` that `BatchedExecutor`'s `Conversation` API
  exposes directly — no manual native calls needed to use it.
- `SafeLLamaContextHandle.SetLoraAdapters(Span<(LoraAdapter, float)>)` — official doc: *"Set the
  LoRa adapters on the context"* (singular "the context" — confirms context scope, matching what
  the §7 spike's empirical run already demonstrated: one call affects the whole context, not a
  specific sequence within it).

## The LoRA safety question

**Confirmed unsafe for cross-adapter prefix sharing**, now via the API's own documented scope
(context-wide), not just the §7 spike's empirical inference. Forking a shared-prefix sequence
into multiple roles only works correctly if those roles run the *same* adapter (or no adapter)
for the lifetime of that forked group — exactly the case Fork() is good for, and exactly the
case TheOrc's actual warband design doesn't have (each role wants its own LoRA).

## Prior art

vLLM and TGI's prefix-caching / radix-attention designs generally serve one model deployment
with one active adapter set at a time per replica; per-request adapter switching within a shared
-prefix KV block isn't a documented pattern in either project's public docs, for the same
underlying reason — KV cache values are computed under whatever weights (base + adapter) were
active at write time, and nothing recomputes them when the adapter changes. This matches what
the §7 spike found independently for TheOrc's own stack.

## Feasibility for TheOrc, specifically

- **Same-role prefix sharing: a real, low-risk win, no AdapterManager redesign needed.**
  Multiple conversations *within the same role* (e.g. several Boss sessions sharing the
  BOSS_SYSTEM_PROMPT + tool-definitions prefix) already run on AdapterManager's one persistent
  per-role executor — `Fork()` on that role's base/prefix `Conversation` before branching into
  per-session turns would save the prefix re-prefill cost on every new session for that role.
  This is implementable today without touching the §7 verdict at all.
- **Cross-role prefix sharing (Boss+Worker+Researcher+Reviewer's common system-prompt header all
  sharing one cache): blocked**, for the context-scoped-adapter reason above. Not feasible with
  LLamaSharp 0.27.0's current API regardless of effort spent on TheOrc's side.

## If/when to revisit

Only becomes feasible if a future llama.cpp/LLamaSharp version exposes **per-sequence** LoRA
application instead of per-context (no such API exists in 0.27.0, and the C# wrapper of any such
future API would need to also become per-sequence, not just the native llama.cpp side). Until
then: same-role prefix forking is worth a small future implementation slice; cross-role prefix
forking stays research-only, exactly as Phase 5 was scoped from the start — this isn't a gap in
TheOrc's design, it's a real constraint in the underlying inference engine's current adapter
model.
