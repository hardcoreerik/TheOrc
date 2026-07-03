# The Training Pit — Training Data Safety and Privacy

> **Status:** Phase 1 training-data reference. These constraints govern examples
> and artifacts admitted to training, not ordinary source code or documentation.
> Run `training_pit/scripts/sanitize_dataset.py` on every JSONL file before training.

---

## What Must Never Appear in a Training Example

### Hard Blockers — Reject Immediately

These patterns cause `training_pit/scripts/sanitize_dataset.py` to flag the
training example as `REJECT`.
Never commit an example containing these to the dataset, even if the surrounding
context is training-valuable.

| Category | Examples | Why |
|---|---|---|
| API keys and tokens | `sk-...`, `ghp_...`, `ollama-...`, `Bearer ...` | Model learns to reproduce them |
| Passwords and secrets | `password=`, `passwd`, `secret=`, `credentials` | Same risk |
| Private IP addresses | `192.168.x.x`, `10.x.x.x`, `172.16-31.x.x` | Leaks local network topology |
| SSH private keys | `-----BEGIN RSA PRIVATE KEY-----`, `-----BEGIN EC PRIVATE KEY-----` | Obvious |
| Personal email addresses | User email, contact emails not in public docs | PII |
| Government IDs | SSN patterns (`\d{3}-\d{2}-\d{4}`), passport numbers | PII |
| Wallet addresses | Ethereum `0x...`, Bitcoin base58 strings | Financial PII |
| Database connection strings | `Server=;Database=;Password=` | Credential leak |

### Soft Flags — Review Before Use

These patterns cause `training_pit/scripts/sanitize_dataset.py` to flag the
training example as `REVIEW`.
A human annotator must review and redact before the example is used for training.

| Category | Examples | Action |
|---|---|---|
| Local file paths | `C:\Users\hardc\...`, `/home/username/...` | Replace with generic `<repo_root>` |
| Hostnames | `DESKTOP-ABC123`, non-public server names | Replace with `<hostname>` |
| Usernames in paths | Anything under `C:\Users\<name>` | Replace with `<username>` |
| Public IP addresses | Any `\d+\.\d+\.\d+\.\d+` outside RFC1918 | Review context; remove if identifiable |
| Internal URLs | `http://192.168...`, `http://internal-server/` | Replace with `http://<server>/` |

---

## What Not to Train On (Behavioral)

Beyond privacy, some content degrades model quality or teaches wrong behaviors.

### Do Not Train On

- **Hallucinated outputs** — if the model invented a file path, function name, or API that doesn't
  exist, do not include that example as training data. This bakes hallucinations in.
  Mark these examples `quality: "rejected"` in the metadata.

- **Apology loops** — "I'm sorry, I can't do that. I apologize for the confusion. Let me try again."
  Trains the model to be defensive and verbose instead of direct and patching.

- **Over-explanation** — examples where the assistant restates the goal, explains what it's about
  to do, then does it, then summarizes what it did. TheOrc should be dense and directive.

- **Bash-defaulting commands** — if an example has `ls`, `cat`, `grep` without the PowerShell
  equivalents, do not use it until corrected. Windows/PowerShell is the default environment.

- **Examples from production logs with unredacted user data** — terminal logs are valuable,
  but any log containing real file paths, real usernames, or real project names must be
  reviewed and redacted before inclusion.

- **Unresolved tool errors** — if a swarm run ended in an unhandled exception or a worker
  failed without recovery, the plan that led to that failure should only be included as a
  negative example (marked `quality: "rejected"`, `source: "eval_failure"`).

---

## Sanitizer Behavior

`training_pit/scripts/sanitize_dataset.py` applies the following checks to each
JSONL line:

1. **API key scan** — regex patterns for common API key prefixes (`sk-`, `ghp_`, etc.)
2. **Private IP scan** — RFC1918 address patterns
3. **SSH key scan** — PEM header patterns
4. **SSN scan** — US Social Security Number format
5. **Local path scan** — Windows `C:\Users\` and Linux `/home/` paths
6. **Long hex/base58 scan** — potential wallet addresses or tokens

For each match:
- **REJECT** patterns: the script prints the line number, the matched pattern, and exits
  non-zero. The file must not be used for training until the issue is resolved.
- **REVIEW** patterns: the script prints a warning with the line number and the matched
  text. A human must review before training.

Running the sanitizer:
```bash
python training_pit/scripts/sanitize_dataset.py training_pit/datasets/train_v1.jsonl
```

A clean file prints:
```
Sanitize complete: 0 rejects, 0 reviews. File is clean.
```

---

## Dataset Commit Policy

- **Never commit raw `training_pit/datasets/*.jsonl` files to git.** Dataset
  manifests, progress metadata, registry metadata, and deliberately curated
  examples under `training_pit/examples/` may be tracked after review.
- **Adapter weights (`adapters/local/*.bin`, `*.safetensors`, `*.gguf`) are not committed.**
  Only `adapters/registry.json` and metadata are tracked.
- **`swarm-metrics.json` is committed.** It contains only aggregate benchmark scores — no user data.
- **Example files in `examples/`** are manually curated and reviewed before commit.
  They must pass sanitize before being added.

---

## The One Rule

> **Never train a model on data you wouldn't be comfortable showing to the person whose
> system generated it.**

If a terminal log or swarm run contains information that would be surprising to see reflected
back in model output, don't train on it.

---

*Last updated: 2026-06-09 — Phase 1 safety baseline.*
