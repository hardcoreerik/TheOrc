# TheOrc — Model Wiki / Lab

> ## ⚠️ STATUS: Retired window — data layer retained, Avalonia rebuild planned
>
> The Model Wiki / Lab WPF windows (`ModelWikiWindow`, `ModelCompareWindow`) and the
> capability-test / tool-call-probe dialogs were **retired 2026-06-20** when WPF was deleted,
> and were not ported to Avalonia. The underlying data layer — `ModelWikiService`,
> `ModelProfiles`, GOBLIN MIND probe results, and local capability-test evidence — is fully
> retained and still drives runtime behavior. A from-scratch Avalonia rebuild of this surface
> is a planned feature (see [ROADMAP.md](ROADMAP.md)). **This guide describes the retired
> window's behavior as the design reference for that rebuild** — read it as "what this should
> do," not "what's on screen today."

> This guide covers the in-app model intelligence surface. For the scoring logic underneath it, see [MODEL_GUIDE.md](MODEL_GUIDE.md). For the architecture behind the merged data model, see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## What This Window Is For

Model Wiki / Lab is the place where TheOrc answers a practical question:

```text
What do we actually know about this model on this machine?
```

It merges static profile data with local runtime evidence so model choice becomes operational instead of aspirational.

---

## How To Open It

Use:

- `Models -> Model Wiki / Lab...`

The window is non-modal and single-instance. Reopening it focuses the existing window instead of spawning duplicates.

---

## Data Sources It Merges

`ModelWikiService` builds each entry from:

- `ModelProfiles`
- installed model discovery
- GOBLIN MIND probe results
- swarm run history
- built-in observations
- local capability test results

That data fusion is the main reason this window is more useful than a plain model list.

---

## What You See In Detail View

The right-hand detail view currently includes:

- identity and summary
- role scores
- observations
- trends strip
- tool-call reliability
- routing recommendation
- LoRA guidance

---

## Trends Strip

The trends strip is a chronological visual history built from:

- local capability tests
- swarm runs involving that model

It uses simple WPF shapes instead of a charting library and gives you a compact sense of whether results are improving, declining, or still too sparse to interpret.

---

## Comparison View

Use the Compare action when you need a side-by-side decision.

`ModelCompareWindow` currently compares:

- identity
- role scores
- GOBLIN MIND probe results
- routing recommendation

This is especially useful when two models both look "good enough" in isolation.

---

## Capability Testing

The capability test dialog records local evidence about how a model behaves on actual structured-write tasks. Those results are persisted and folded back into the wiki entry after the run.

Use local tests when:

- the built-in score looks promising but you do not trust it yet
- hardware or quantization may be changing behavior
- you care about long-write reliability more than short-call reliability

---

## Probe Data And Evolution

The wiki surfaces stored GOBLIN MIND probe data, but the actual probe and evolution workflow lives in the tool-call test window.

That probe stack currently supports:

- format fingerprinting
- category boundary mapping
- evolutionary fitness display
- promotion of winning schema variants

The Swarm Board and AgentLoop both consume the resulting stored profile data.

---

## Export

The capability matrix export writes a Markdown report built from the current merged entries. This is useful when you want to snapshot the current local model landscape for documentation or comparison outside the app.

---

## Recommended Use Pattern

When choosing or troubleshooting a model:

1. start in the wiki summary
2. inspect observations
3. inspect trends
4. run a capability test if evidence is thin
5. compare against a nearby alternative

That workflow makes model selection evidence-first instead of lore-first.
