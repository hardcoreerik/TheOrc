<!-- Copyright (C) 2025-present hardcoreerik / TheOrc contributors | SPDX-License-Identifier: AGPL-3.0-or-later -->
# Archived / disabled tooling — do not use

Files here are kept for reference only. They are **out of the active path** and
renamed with `.disabled` so they cannot be run via their original command.

## `generate_claude_gold.py.disabled`
Archived 2026-06-17 (red-team gap analysis finding, High severity).

**Why it is forbidden:** it generated bulk training data via the **Anthropic
API** and documented reading an **`ANTHROPIC_API_KEY` from a `.env` in the repo
root**. That violates two hard project rules:

1. Bulk data generation must use **Cerebras or Grok**, never paid Anthropic API.
2. Secrets must **never** live in the repo (no `.env`, no committed keys).

**Use instead:** `Tools/generate_cerebras_gold.py` (sanctioned path, same
gold-decomposition format, free tier). Grok is the secondary option.

The format logic is preserved here only as a historical reference; do not
re-enable it.
