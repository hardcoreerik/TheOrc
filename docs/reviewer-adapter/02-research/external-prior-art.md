# Research — External Prior Art

**Date:** 2026-06-13
**Purpose:** Survey of published work the reviewer adapter strategy builds on.
Establishes that TheOrc is **not** in pioneering territory for the reviewer
machinery itself — the novel work is the self-review training loop on TheOrc's
own codebase.

---

## The headline finding

Code review with LLMs is a mature, well-studied problem. There are public
datasets, a known fine-tuning recipe on our exact base-model family, and several
prompt-engineering techniques with published accuracy numbers. We build on
shoulders for the reviewer; the novelty is the self-improving loop.

---

## 1. MelcotCR — fine-tuned Qwen2.5-14B matches DeepSeek-R1 671B

**Source:** "Fine-Tuning LLMs to Analyze Multiple Dimensions of Code Review: A
Maximum Entropy Regulated Long Chain-of-Thought Approach", arxiv 2509.21170
(Sept 2025).

**Why it matters:** same base-model *family* as ours (Qwen2.5-14B). Proves a
14B model can match a 671B reasoning model at code review with the right
fine-tuning — a 47× parameter advantage erased by task-specific training.

**Recipe:**
- 12,881 cleaned code-review instances (from 12.15M raw GitHub comments,
  aggressively filtered)
- Full-parameter fine-tune (not LoRA), 2 epochs, LR 1e-7, batch 64, 500 warmup
- Long chain-of-thought prompt: summarize → analyze logic flows → assess change
  impact → inspect concrete issues
- "Maximum Entropy" — 10 semantically distinct response variants per query to
  maximize diversity during training

**Takeaway for us:** 12.8K is the honest dataset scale for competent review
training. Our 50-capture Phase-2 gate is for *domain adaptation* (Stage 2), not
for teaching review competence from scratch (Stage 1, which uses this corpus or
similar).

---

## 2. Agentic Code Reasoning (Meta) — semi-formal reasoning, no training

**Source:** Ugare & Chandra, Meta, arxiv 2603.01896 (2026). Public templates:
`github.com/knot0-com/semi-formal-reasoning`.

**Why it matters:** the strongest *single-shot, no-training* technique. Forces
the model to fill a structured "certificate" (premises with file:line citations
→ execution traces → formal conclusion) before reaching a verdict.

**Results:** patch-equivalence accuracy 78% → 88% on curated cases, 93% on
real-world agent-generated patches. No fine-tuning required.

**Takeaway for us:** we adopted the certificate template as our canonical
reviewer prompt. The B-3 series confirmed it adds structure — but on
`qwen2.5-coder:14b` (smaller than Meta's test models), structure alone did not
yield correct findings. The technique works; the model under it needs the skill
prior that Stage 1 provides.

---

## 3. RARe — "When More Retrieval Hurts" (retrieval for code review)

**Source:** "When More Retrieval Hurts: Retrieval-Augmented Code Review
Generation", arxiv 2511.05302v2.

**Why it matters:** the counter-intuitive RAG finding — **top-1 retrieval is
best; more retrieval degrades.**

**Numbers (Llama-3.1-8B):** direct inference BLEU-4 5.84 → top-1 retrieval 12.32
(+111%) → top-3 11.76 (worse) → top-5 10.81 (worse still). Human eval: "valuable
reviews" 12-15% → 45-55% with top-1.

**Takeaway for us:** we implemented top-1-only RAG (the `-UseRagAnchor` flag).
But B-3b showed it backfires at our model size — the 14B coder copy-pastes the
anchor's findings rather than calibrating. RARe's lift was measured on
instruction-tuned models doing review-comment generation; our certificate-format
review on a coder model is a different regime. RAG-v2 (category-abstracted
anchors) is the proposed fix, deferred to post-SFT.

---

## 4. Adversarial Review — strongest multi-agent technique

**Source:** "Adversarial Review: Cooperative Code Review through Structured
Disagreement", OpenReview fOHvpLs6zp (2026).

**Why it matters:** the current top published result on code-review benchmarks,
and it maps directly onto TheOrc's multi-agent HIVE architecture.

**Protocol (3 agents):** Reviewer evaluates → Critic challenges the review with
evidence-based objections → Main agent decides edit-or-commit from the
structured disagreement.

**Results:** highest pass rate on LiveCodeBench, highest F1 on SWE-PRBench, beat
a six-agent baseline. Key insight: "cooperative oversight is useful when
disagreement is minimal, structured, and evidence-grounded" — structured
disagreement beats more agents.

**Takeaway for us:** we already have two of three agents (Codex + TheOrc). A
third Critic on a *different model family* (deepseek-coder-v2:16b) is the
untested lever — genuine disagreement requires a different prior, not a second
rollout of the same model. This is the B-4 experiment and the most promising
near-term move that doesn't require training.

---

## 5. Self-consistency / majority voting

**Sources:** Self-Consistency Decoding (Jan 2026); ICLR 2026 Test-Time Updates
majority-voting-for-code workshop (openreview hEnnYgRJdC).

**Why it matters:** cheap compute multiplier — run N times, vote, because wrong
answers are unique and right answers cluster. Code-gen: Qwen3-4B 37.7% → 48-53%
with voting.

**Takeaway for us:** implemented as `-SelfConsistencyN`. B-3c confirmed it
filters noise reliably but cannot add signal the model lacks. Most useful
*after* SFT, to clean residual hallucinations from a competent model.

---

## 6. CodeReviewer dataset + data-quality caveat

**Sources:** CodeReviewer (Microsoft, ~642K raw, 9 languages incl. C#); "Too
Noisy To Learn" arxiv 2502.02757 (cleaned 64% → 85% validity).

**Why it matters:** the public corpus option for Stage 1, with a known quality
problem and a known fix.

**Takeaway for us:** if MelcotCR isn't readily downloadable, the cleaned
CodeReviewer C#/C++/PowerShell subset is the fallback Stage 1 corpus. Filter to
our languages, use the cleaned-validity subset, map free-form severity to our
BLOCKER/MINOR taxonomy.

---

## 7. The open-source reviewer ecosystem (what not to reinvent)

**Source:** "15 Open Source AI Code Review Tools (2026)", DEV community roundup;
plus Alibaba `open-code-review`, LlamaPReview, PR-Agent, LAURA.

**Why it matters:** mature reviewers exist (PR-Agent, LlamaPReview at 4,000+
repos, Alibaba's hybrid deterministic+LLM tool). The reviewer *machinery* is
well-trodden.

**Takeaway for us:** none of them solve "an AI reviews its own codebase as a
self-improving training loop, gated by a stronger external reviewer until it
earns trust." That loop — not the reviewer itself — is TheOrc's novel
contribution.

---

## Synthesis — what's novel vs. borrowed

| Component | Source | Novel to TheOrc? |
|---|---|---|
| Semi-formal certificate prompt | Meta 2026 | Borrowed |
| Two-stage SFT + LoRA | Standard practice | Borrowed |
| Stage 1 corpus | MelcotCR / CodeReviewer | Borrowed |
| Top-1 RAG | RARe 2026 | Borrowed (and found wanting at our scale) |
| Self-consistency vote | ICLR 2026 | Borrowed |
| 3-agent disagreement | Adversarial Review 2026 | Borrowed (untested here) |
| **Self-review training loop on own codebase** | — | **Novel** |
| **Trust ladder: external reviewer → self, earned by agreement** | — | **Novel** |
| **Reviewer gate fused with distributed-swarm worktree merge** | — | **Novel** |

The publishable contribution, after the B-3 negative results, is sharper than
the original "first to stack these techniques": it is **"a reproducible
measurement that prompt-engineering stacks do not substitute for reviewer
fine-tuning on small coder models, plus an architecture for an AI that earns the
right to review itself."**

---

## Citations

- MelcotCR — arxiv 2509.21170
- Agentic Code Reasoning (Meta) — arxiv 2603.01896; `github.com/knot0-com/semi-formal-reasoning`
- When More Retrieval Hurts (RARe) — arxiv 2511.05302
- Adversarial Review — OpenReview fOHvpLs6zp
- Self-Consistency for code — OpenReview hEnnYgRJdC (ICLR 2026 TTU workshop)
- CodeReviewer — Microsoft; cleaned subset arxiv 2502.02757
- Qwen2.5-Coder Technical Report — confirms PR/review data in pretraining
- 15 Open Source AI Code Review Tools (2026) — DEV community
