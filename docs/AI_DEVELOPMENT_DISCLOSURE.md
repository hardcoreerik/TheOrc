# AI Development Disclosure

TheOrc is human-directed and AI-assisted. This document says plainly what that means in
practice, so nobody has to guess from the commit history.

## Who does what

| Role | Who |
|---|---|
| Creator, maintainer, product direction | [Erik / hardcoreerik](https://github.com/hardcoreerik) |
| Tester, release authority | Erik |
| Implementation, architecture planning, code review | Claude Sonnet |
| Implementation support, adversarial review, verification | OpenAI Codex |
| Adversarial review, PROJECT_TRUTH audits, runtime critique | Grok Build |

Erik is not a professional software engineer. He directs the AI agents that write, review,
and test this codebase — deciding what gets built, what ships, and what gets rejected. The
agents write the code; Erik owns the outcome.

## What this means for trust

Because the code is AI-generated and the project has one human maintainer rather than a
team of reviewers, TheOrc holds itself to a few concrete rituals instead of asking readers
to just take the commit messages' word for it:

- **Claims require tests, docs, or a reproducible run.** A feature described in the docs as
  "passed" or "shipped" should have a test, a benchmark report, or a recorded run behind it —
  not just a commit message that says so. Where that evidence doesn't exist yet, the docs
  are supposed to say "framework passed, real-world exit still pending" rather than round up.
- **Multiple AI reviewers, not one.** Code that ships past the prototype stage typically goes
  through more than one AI's adversarial review pass (see the credits table above) before a
  human decides whether to merge it.
- **AI review is not a substitute for a security audit.** The HIVE cryptographic layer has
  been through multiple independent AI-assisted review passes (see
  [SECURITY.md](../SECURITY.md)), but that is explicitly **not** the same thing as a formal,
  independent, third-party cryptographic audit. If you have real crypto/security expertise
  and want to look, see SECURITY.md for how to report findings.
- **Generated code is subject to the repo license**, the same as any other contribution —
  see [LICENSING.md](../LICENSING.md).

## Where the docs sometimes get ahead of (or behind) the code

Because this project moves fast and is maintained by one person directing AI agents, docs
occasionally drift from what's actually in the code — in both directions. Some docs are
written as forward-looking design references and say so explicitly (e.g.
[HIVE_PAIRING_SPEC.md](HIVE_PAIRING_SPEC.md)'s historical-findings sections). Others describe
a UI surface that was later retired without being fully cleaned up in every doc that
referenced it (e.g. the Model Wiki window — see [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md)).

If you spot a doc that contradicts the actual running code, that's a bug worth reporting —
either via a GitHub issue or, for anything security-sensitive, via the process in
[SECURITY.md](../SECURITY.md).
