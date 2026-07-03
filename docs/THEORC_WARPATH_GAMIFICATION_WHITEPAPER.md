# TheOrc Warpath — Gamification, Scoring, Badges, and Bragging Rights White Paper

> **Status:** Proposed product/design specification.  
> **Target project:** `hardcoreerik/TheOrc`  
> **Target implementation style:** Small, safe, event-driven, local-first, SQLite-backed, no cloud dependency.  
> **Audience:** TheOrc maintainer, AI coding agents, implementation reviewers, product/design contributors.  
> **Primary goal:** Add a meaningful gamification layer that rewards real engineering discipline: clean runs, safe approvals, useful reviews, strong datasets, local-first execution, HIVE/Warband reliability, Context Fabric evidence quality, and measurable model improvement.

---

## 0. Executive Summary

TheOrc already has the bones of a game system: a boss, goblin worker lanes, HIVE nodes, Warbands, Warchief leadership, approval gates, reviewer verdicts, Training Pit captures, ORC ACADEMY adapters, Context Fabric evidence runs, model capability probing, and long-running local infrastructure. The proposed **Warpath** system turns those real product behaviors into scores, badges, trophies, ranks, streaks, and shareable bragging-rights artifacts.

This must not become fake dopamine pasted over a coding tool. The Warpath is not about rewarding raw activity, token spam, shell command count, line count, or fastest approval clicking. It is about making the operator visibly better at the behaviors TheOrc already values:

- safe execution
- inspectable local automation
- clean swarm role discipline
- useful testing and review
- dataset hygiene
- source-grounded evidence
- local model capability discovery
- HIVE and Warband reliability
- successful rollback-ready improvements
- measurable model and workflow wins

The product fantasy is simple:

> **Run the Warband. Improve the Tribe. Prove it locally.**

The user should be able to say:

> **My local AI warband passed 100 clean gated runs, trained its own boss, ran across 3 machines, and processed a million-token source corpus without sending my code to the cloud.**

That is real bragging-rights energy. It is also aligned with TheOrc's product truth.

---

## 1. Repository-Grounded Product Context

This design is grounded in the current public project shape of TheOrc, not in a generic gamification template.

The live repository positions TheOrc as a local-first AI orchestration shell with an Avalonia desktop operator surface, local chat and swarm execution, native-runtime and Ollama-backed model paths, HIVE MIND for distributed local work, ORC ACADEMY for training a better boss model, and Context Fabric for source-grounded memory across corpora larger than a model context window.

The README also emphasizes that TheOrc is built around inspectability, approval gates, local ownership, and source reopening instead of magic-context marketing. The Warpath system must reinforce those values instead of diluting them.

The current roadmap establishes several shipped and partially shipped foundations relevant to Warpath:

- Swarm runtime has RESEARCHER, CODER, UIDEVELOPER, and TESTER lanes.
- TESTER is intentionally read-only.
- Swarm Board already has capability badges and per-configuration metrics history.
- Tool calls already flow through approval-aware handlers.
- Training Pit already captures, reviews, validates, sanitizes, and exports training data.
- ORC ACADEMY already produced a production boss adapter and has recorded cases where lower eval loss did not mean better behavior.
- HIVE MIND and Warbands already provide distributed local execution concepts.
- Reviewer Quality Gate already has Clean, Minor, and Blocker verdict concepts, but true blocking still needs hardening.
- Context Fabric has become a major product surface centered on source-grounded evidence and citation/reopening behavior.

The Warpath should therefore be implemented as a **thin event and scoring layer over existing product truth**, not as a separate fantasy system that invents its own reality.

---

## 2. Core Principle

### 2.1 The One Rule

> **Reward quality, safety, learning, and capability. Do not reward spam.**

> **Warpath rewards proof, not activity. It observes verified outcomes only and must not create incentives to train, promote, approve, override, or merge prematurely.**

Warpath scoring must prefer fewer, safer, cleaner, more useful actions over noisy activity. TheOrc is already an automation product. A bad scoring system would accidentally train users and agents to maximize junk output. That would actively harm the project.

### 2.2 Reward These Behaviors

Warpath should reward:

- valid structured plans
- correct role assignment
- no role-permission violations
- TESTER staying read-only
- useful researcher output
- tests that actually run
- reviewer findings that prevent bad output
- BLOCKER rework that later passes
- user approvals that happen through proper approval surfaces
- verified dataset admission
- valid candidate rejection under a frozen evaluation
- model probes that improve routing knowledge
- HIVE node recovery
- Warband task completion
- source-grounded answers with verified citations
- local-only successful runs
- adapters or candidates that beat baselines under declared evaluation
- rollbacks handled cleanly

### 2.3 Do Not Reward These Behaviors

Warpath must not reward:

- raw line count
- raw file count
- raw shell command count
- raw model call count
- raw token usage
- fastest approval clicking
- number of unreviewed captures
- number of generated synthetic examples without independent gates
- overriding BLOCKERs
- using a bigger model when a smaller one works
- bypassing deterministic safety policy
- noisy chat verbosity
- repeatedly probing the same model just to farm points

### 2.4 Negative Score Is Allowed, But Should Be Used Carefully

A scoring system that only adds points becomes fake. A scoring system that punishes experimentation becomes oppressive. The balance:

- Unsafe or quality-corrupting behavior can subtract points.
- Honest failed experiments should not be heavily punished.
- Rejected bad captures can be recorded as audit milestones, but positive score
  begins only when the resulting dataset passes admission.
- BLOCKER findings should be treated as useful if they prevent unsafe apply.
- BLOCKER overrides should be recorded, visible, and lightly penalized, but not treated as moral failure. Sometimes the operator knows more than the reviewer.

---

## 3. Naming and Product Surface

### 3.1 Recommended Names

| Concept | Recommended Name | Purpose |
|---|---|---|
| Overall gamification system | **The Warpath** | The full progression/scoring layer |
| Profile/stat page | **Tribe Ledger** | Persistent operator/project stats |
| Achievement wall | **Hall of Skulls** | Badges and trophies |
| Run scorecard | **Battle Report** | Per-run scoring summary |
| Project dashboard | **Campaign Map** | Per-workspace progress |
| Model capability collection | **Bestiary** | Model mastery and probe history |
| Training Pit achievements | **Forge Marks** | Dataset/training accomplishments |
| HIVE/Warband achievements | **Crown Deeds** | Distributed execution accomplishments |
| Reviewer achievements | **Trial Marks** | Gate/reviewer accomplishments |
| Safety score | **Honor Guard** | Approval and safety-discipline score |
| Rare trophies | **War Trophies** | High-value bragging rights |

### 3.2 Tone Guidance

The tone should be fun but still professional enough for a serious development tool.

Good tone:

- “The Gate Holds”
- “No Poison in the Pit”
- “Clean Bloodline”
- “Many Hands, One Axe”
- “Loss Is A Liar”
- “Local Legend”

Avoid tone that implies unsafe behavior is cool:

- Do not glamorize bypassing approvals.
- Do not celebrate ignoring BLOCKERs.
- Do not use language that makes security review feel optional.

### 3.3 Product Promise

Warpath is not a game mode. It is a visible mastery system for TheOrc operators.

Suggested product copy:

> **The Warpath tracks how your local AI tribe improves: clean runs, safer gates, sharper goblins, stronger datasets, better model evidence, and bigger HIVE capability. It rewards proof, not noise.**

---

## 4. User Stories

### 4.1 New User

As a new user, I want to see simple early achievements so I understand the safe workflow.

Examples:

- Open first workspace.
- Run first read-only task.
- Approve first safe command.
- Complete first Swarm run.
- Open first Battle Report.

Acceptance criteria:

- The user learns correct workflow from badges.
- No badge encourages bypassing approval.
- No badge requires cloud services.

### 4.2 Power User

As a power user, I want bragging rights for disciplined local automation.

Examples:

- 100 local-only runs.
- 25 clean Reviewer Gate results in a row.
- All active models probed and current.
- HIVE node recovery works after worker loss.
- Training dataset passes sanitizer and preflight.

Acceptance criteria:

- Achievements map to real product events.
- Share exports do not leak private code.
- Streaks survive app restart.

### 4.3 Maintainer

As the maintainer, I want Warpath to expose quality trends and weak spots.

Examples:

- Which goblin lane causes most penalties?
- How often does TESTER provide meaningful verification?
- How often are BLOCKERs overridden?
- Which models produce the cleanest run scores?
- Which workspaces have the highest/lowest safety score?

Acceptance criteria:

- Scoring data is local.
- Metrics can be exported.
- A bad score helps diagnose the system instead of just shaming the user.

### 4.4 AI Coding Agent

As an AI coding agent implementing this feature, I need explicit rules, schema, triggers, and phased tasks so I do not invent unsafe behavior.

Acceptance criteria:

- Implementation instructions are deterministic.
- Event names are defined.
- Point values are defined.
- Data storage is defined.
- UI surface is defined.
- Non-goals are defined.

---

## 5. System Overview

### 5.1 Architecture Summary

Warpath should be implemented as an event-driven scoring layer.

Recommended core pieces:

```text
Existing TheOrc feature emits event
        │
        ▼
WarpathEventService records event
        │
        ▼
WarpathScoringService updates score projections
        │
        ▼
WarpathBadgeService evaluates badge unlocks
        │
        ▼
WarpathProfileRepository persists profile, badges, trophies, streaks
        │
        ▼
UI surfaces show Battle Report, Tribe Ledger, Hall of Skulls, Campaign Map
```

### 5.2 Implementation Rules

1. Warpath must not directly execute tools.
2. Warpath must not modify approval policy.
3. Warpath must not replace Reviewer Gate, ToolPolicyEngine, Training Pit validators, or Foundry/Arena policies.
4. Warpath only records and scores events that other systems already produce.
5. Warpath must be local-first.
6. Warpath must not upload score data anywhere by default.
7. Share cards must be explicit user-generated exports.
8. Share exports must avoid private paths, prompts, code snippets, secrets, or source content.
9. Warpath scoring must be reproducible from recorded events.
10. All badge unlocks must be auditable by event history.
11. Warpath may consume verified events from Foundry, Arena, Training Pit,
    Reviewer Gate, HIVE, Swarm, and Context Fabric, but Warpath events, scores,
    ranks, badges, streaks, and trophies must never become inputs to promotion,
    approval, evaluation, dataset admission, rollback, override, or merge decisions.

### 5.3 Recommended Storage

Use existing SQLite infrastructure if available. If SQLite integration is too expensive for the first pass, use local JSON files as an MVP, but design names so a SQLite migration is straightforward.

Recommended local paths:

```text
.orc/warpath/profile.json
.orc/warpath/events.jsonl
.orc/warpath/badges.json
.orc/warpath/trophies.md
.orc/warpath/share-card.json
.orc/warpath/share-card.md
```

Recommended later SQLite tables:

```sql
warpath_events
warpath_profile
warpath_badges
warpath_badge_unlocks
warpath_trophies
warpath_run_scores
warpath_streaks
warpath_exports
```

---

## 6. Data Model

### 6.1 Warpath Event

A Warpath event is an immutable record of something that happened.

```json
{
  "event_id": "wp_evt_20260703_183012_0001",
  "schema_version": "warpath-event-v1",
  "occurred_at": "2026-07-03T18:30:12-07:00",
  "workspace_id": "sha256-of-normalized-workspace-root-or-null",
  "run_id": "optional-swarm-or-chat-run-id",
  "event_type": "swarm.run.completed",
  "source_system": "SwarmSession",
  "actor": "system",
  "role": "CODER",
  "model": "qwen2.5-coder:14b",
  "node_id": "optional-hive-node-id",
  "payload": {
    "success": true,
    "files_changed": 3,
    "tests_passed": true,
    "review_verdict": "CLEAN"
  },
  "privacy": {
    "contains_user_content": false,
    "safe_for_share_card": true
  }
}
```

### 6.2 Required Event Fields

| Field | Required | Meaning |
|---|---:|---|
| `event_id` | yes | Stable unique event id |
| `schema_version` | yes | Must be `warpath-event-v1` for first release |
| `occurred_at` | yes | Local timestamp with timezone or UTC |
| `workspace_id` | no | Stable hash, not raw local path |
| `run_id` | no | Existing run/session id if available |
| `event_type` | yes | Namespaced event type |
| `source_system` | yes | System that emitted event |
| `actor` | yes | `system`, `user`, `agent`, `hive-node`, etc. |
| `role` | no | Swarm role if applicable |
| `model` | no | Model id if applicable |
| `node_id` | no | HIVE node id if applicable |
| `payload` | yes | Event-specific JSON |
| `privacy` | yes | Share/export safety hints |

### 6.3 Event Type Naming Convention

Use dotted namespaces.

Examples:

```text
app.workspace.opened
agent.plan.generated
agent.tool_call.proposed
approval.shell.approved
approval.file_write.approved
approval.blocked
swarm.run.started
swarm.run.completed
swarm.role.violation
swarm.tester.write_attempt
review.verdict.clean
review.verdict.minor
review.verdict.blocker
review.blocker.override
review.blocker.reworked_clean
training.capture.staged
training.capture.accepted
training.capture.rejected
training.preflight.passed
training.preflight.failed
academy.training.started
academy.training.completed
academy.adapter.evaluated
academy.adapter.promoted
academy.adapter.rejected
model.probe.started
model.probe.completed
model.capability.changed
hive.enabled
hive.node.paired
hive.node.offline
hive.node.recovered
hive.warchief.elected
warband.connected
warband.task.completed
fabric.corpus.attached
fabric.answer.cited
fabric.answer.verified
fabric.exhaustive.passed
foundry.baseline.reported
foundry.candidate.evaluated
foundry.candidate.promoted
foundry.candidate.quarantined
```

### 6.4 Warpath Profile

```json
{
  "schema_version": "warpath-profile-v1",
  "operator_name": "local-user-or-null",
  "rank": "Swarm Tamer",
  "total_score": 1840,
  "category_scores": {
    "swarm_discipline": 320,
    "quality_gate": 210,
    "forge_progress": 140,
    "hive_power": 100,
    "model_mastery": 260,
    "campaign_wins": 310,
    "safety_honor": 420,
    "fabric_evidence": 80,
    "foundry_proof": 0
  },
  "streaks": {
    "clean_gate": 4,
    "local_only": 12,
    "safe_approval": 22,
    "forge_purity": 2
  },
  "badges_unlocked": [
    "first_blood",
    "trial_passed",
    "beastmaster"
  ],
  "trophies_unlocked": [],
  "last_updated": "2026-07-03T18:30:12-07:00"
}
```

---

## 7. Score Categories

### 7.1 Category Summary

| Category | Recommended Max for Initial Display | Meaning |
|---|---:|---|
| Swarm Discipline | 1,500 | Role correctness, useful lane output, no permission violations |
| Quality Gate | 1,500 | Reviewer Gate outcomes and rework discipline |
| Forge Progress | 1,500 | Verified dataset admission and candidate evaluation outcomes |
| HIVE Power | 1,000 | Node pairing, Warband task completion, recovery, authenticated mesh behavior |
| Model Mastery | 1,000 | Model probes, capability freshness, correct model-role fit |
| Campaign Wins | 1,000 | Completed project runs and applied outputs |
| Safety Honor | 1,000 | Approval flow discipline, blocked risky operations, no bypasses |
| Fabric Evidence | 1,000 | Context Fabric citation precision, verified answers, exhaustive evidence tasks |
| Foundry Proof | 1,000 | Baselines, candidate evaluation, promotion, quarantine/rollback discipline |

Initial visible total: **10,500** soft cap. The profile can continue beyond the cap, but the category cap gives users a readable mastery map.

### 7.2 Why Include Fabric Evidence

Current TheOrc heavily emphasizes Context Fabric as a source-grounded memory layer. Warpath would be incomplete if it ignored evidence quality. Context Fabric achievements should reward verified citations, source reopening, exhaustive recall, correct abstention, and source-to-working-context leverage.

### 7.3 Why Include Foundry Proof

Foundry should not become “I trained a thing, give me points.” Foundry scoring must reward baseline reports, sealed evals, reproducible manifests, no safety regression, successful deployed-artifact verification, rollback readiness, and honest rejection when the candidate fails.

---

## 8. Rank Ladder

Ranks are profile-level titles. They should be fun, but not so goofy that they cheapen the product.

| Rank | Requirement |
|---|---|
| Mud Goblin | App launched and Warpath profile created |
| Camp Hand | First workspace opened |
| Tool Grunt | First approved tool call |
| Blooded Coder | First successful file write approved through diff flow |
| Swarm Tamer | First successful Swarm run |
| Pit Keeper | First dataset package passes declared admission gates |
| Gatebreaker | First BLOCKER resolved and rerun CLEAN |
| Warchief | First HIVE node paired or local node elected Warchief |
| Warband Captain | First headless Warband task completed |
| Forge Master | First candidate receives a valid baseline comparison decision |
| Iron Warchief | 50 clean gated runs |
| Mythic Warchief | Foundry candidate beats baseline under frozen evaluation |
| Local Legend | 100 successful local-only runs |

### 8.1 Rank Evaluation Rule

Ranks are not bought with points alone. Each rank has explicit event requirements. This prevents users from farming low-value actions to obtain high-value titles.

### 8.2 Rank Downgrade Rule

Do not downgrade rank automatically. Once earned, ranks remain. However, active profile panels may show warnings such as:

```text
Iron Warchief — current safety streak broken by recent BLOCKER override.
```

---

## 9. Run-Level Battle Report

Every completed meaningful run should produce a Battle Report.

### 9.1 Battle Report Example

```text
Battle Report — Swarm Run 2026-07-03 18:30

Run Score: 87 / 100
Verdict: CLEAN
Rank Progress: +42 Warpath XP

Positive:
+10 valid boss plan
+10 correct role assignments
+10 expected files named
+10 no role permission violations
+10 useful researcher output
+15 coder/UI produced expected files
+10 tester ran meaningful verification
+10 tests passed
+15 reviewer CLEAN
+10 all risky actions approved through proper gates
+10 dataset admission passed

Negative:
-3 stale model probe on UIDEVELOPER model
-10 tester verification was shallow

Badges unlocked:
- Trial Passed
- Hammer Goblin
```

### 9.2 Run Score Formula

| Component | Points |
|---|---:|
| Boss produced valid structured plan | +10 |
| Correct roles assigned | +10 |
| Expected files named | +10 |
| No role permission violations | +10 |
| Researcher output useful | +10 |
| Coder/UI produced expected files | +15 |
| Tester ran meaningful verification | +10 |
| Tests pass | +10 |
| Reviewer CLEAN | +15 |
| Reviewer MINOR | +7 |
| Reviewer BLOCKER found before apply | +5 |
| Rework resolves BLOCKER | +15 |
| Dataset admission passed | +10 |
| All risky actions approved properly | +10 |
| Context Fabric citations verified, when applicable | +10 |
| Local-only stack used successfully | +5 |

### 9.3 Run Penalties

| Problem | Points |
|---|---:|
| TESTER tries to write | -25 |
| Boss assigns wrong lane | -15 |
| Invented file path/API | -15 |
| Tool call malformed beyond repair | -10 |
| BLOCKER overridden | -20 |
| Risky action bypass attempted | -30 |
| Unreviewed synthetic data admitted | -50 |
| Train/eval leakage discovered | -100 and quarantine flag |
| Source citation cannot be reopened/verified | -15 |

### 9.4 Score Bounds

- Minimum run score: 0.
- Maximum displayed run score: 100.
- Bonus points beyond 100 may feed long-term Warpath Score, but the Battle Report should cap at 100 for readability.

---

## 10. Badge System

### 10.1 Badge Definition Schema

```json
{
  "badge_id": "trial_passed",
  "schema_version": "warpath-badge-v1",
  "name": "Trial Passed",
  "family": "reviewer_gate",
  "tier": "common",
  "description": "A run received a CLEAN Reviewer Gate verdict.",
  "unlock_rule": {
    "type": "event_count",
    "event_type": "review.verdict.clean",
    "count": 1
  },
  "score_award": 25,
  "share_safe": true
}
```

### 10.2 Badge Families

| Family | Purpose |
|---|---|
| Swarm Badges | Role discipline and successful multi-lane work |
| Reviewer Gate Badges | Clean review, BLOCKER handling, rework |
| Forge Badges | Training Pit, ORC ACADEMY, dataset safety |
| HIVE/Warband Badges | Distributed local execution and node health |
| Model Mastery Badges | Capability probing and model-role fit |
| Context Fabric Badges | Source-grounded evidence and citation quality |
| Foundry Badges | Baselines, candidate eval, promotion/quarantine |
| Safety Badges | Approval discipline and blocked risky behavior |

### 10.3 Badge Rarity Tiers

| Tier | Meaning | Suggested Visual |
|---|---|---|
| Bone | Common first steps | gray/white |
| Iron | Uncommon competency | steel |
| Blood | Rare hard-won achievement | red |
| Warpaint | Epic system mastery | purple |
| Gold Crown | Legendary proof | gold |
| Black Anvil | Mythic evidence-backed milestone | black/neon green |

### 10.4 Starter Badge List

#### Swarm Badges

| Badge | Tier | Trigger |
|---|---|---|
| First Blood | Bone | First successful Swarm run |
| Boss Brain | Iron | Boss produces valid plan with correct roles and expected files |
| Many Hands, One Axe | Blood | Boss, Researcher, Coder, UI, and Tester all complete useful work in one run |
| Stay In Your Lane | Blood | 25 runs with no role-permission violations |
| No Tester With A Crayon | Gold Crown | 100 runs with TESTER never attempting write behavior |
| Hammer Goblin | Iron | Coder produces files that pass tests on first try |
| Pixel Shaman | Iron | UIDEVELOPER completes UI task with no layout/test issue |
| Truth Goblin | Blood | Tester catches a real issue before apply |
| Perfect Warpath | Gold Crown | Valid plan, useful lanes, tests pass, reviewer CLEAN |

#### Reviewer Gate Badges

| Badge | Tier | Trigger |
|---|---|---|
| Trial Passed | Bone | Reviewer verdict CLEAN |
| Scarred But Worthy | Bone | Reviewer verdict MINOR accepted |
| The Gate Holds | Iron | BLOCKER prevents apply |
| Back To The Pit | Iron | BLOCKER result sent back for rework |
| Redeemed In Battle | Blood | Previously BLOCKED run reruns CLEAN |
| No Cowardly Merge | Blood | 25 runs without overriding BLOCKER |
| Blood Oath Override | Iron, audit-flavored | User explicitly overrides BLOCKER |
| The Judge Nods | Blood | 10 CLEAN reviews in a row |
| Tribunal Standard | Gold Crown | 100 reviewed diffs with recorded verdicts |

Important: **Blood Oath Override must not award positive score.** It is a visible audit badge, not a reward. It should be shown differently from positive badges.

#### Forge Badges

Capture counts and training lifecycle badges are audit milestones only. They may
be displayed, but they award zero score. Positive Forge/Foundry score begins only
with verified evidence: baseline completion, dataset admission, valid candidate
rejection, deployed-artifact proof, rollback-ready promotion, or Arena-confirmed
improvement.

| Badge | Tier | Trigger |
|---|---|---|
| Ore Collector | Audit | 25 captures staged; 0 points |
| Ore Sorter | Audit | 25 captures reviewed; 0 points |
| Cursed Ore Rejected | Audit | 50 bad captures rejected; 0 points |
| No Poison In The Pit | Blood | Dataset passes its declared admission gates |
| Gold Tooth Goblin | Audit | 100 gold-quality examples approved; 0 points |
| Forge Lit | Audit | First training run started; 0 points |
| Blade Tempered | Audit | First training run completed; 0 points |
| Sharper Than Base | Gold Crown | Arena confirms candidate improvement over declared baseline |
| Loss Is A Liar | Gold Crown | Lower eval loss rejected because rubric/eval failed |
| Clean Bloodline | Gold Crown | No train/eval leakage detected in a full candidate package |
| Not Worth The Hammer | Blood | Training loses to deterministic baseline and is correctly rejected |

#### HIVE and Warband Badges

| Badge | Tier | Trigger |
|---|---|---|
| Campfire Lit | Bone | HIVE enabled |
| First Ally | Bone | First node paired |
| Crowned | Iron | Local machine elected Warchief |
| Crown Transfer | Blood | Warchief election succeeds after node loss |
| Three Fires Burning | Blood | 3 nodes online |
| Warband Deployed | Blood | First headless Warband connected |
| Cloud Raider | Blood | First cloud Warband completes a task |
| Fleet Commander | Gold Crown | 5+ nodes paired |
| Dead Node Recovery | Gold Crown | Task requeued after worker loss and completed |
| Signed And Sealed | Iron | Authenticated HIVE traffic confirmed |
| No Rogue Goblins | Gold Crown | 100 signed HIVE requests accepted, 0 unsigned accepted |

#### Model Mastery Badges

| Badge | Tier | Trigger |
|---|---|---|
| Beastmaster | Bone | First model probed |
| Know Your Goblin | Iron | All active models probed |
| Fresh Maps | Iron | All active models probed within 7 days |
| Right Tool, Right Goblin | Blood | Model selected matches role capability requirements |
| Tiny But Mean | Blood | Smaller model beats larger model on bounded task |
| JSON Whisperer | Iron | Model passes structured-output probe |
| Schema Crusher | Blood | Model passes complex schema probe |
| Hallucination Hunter | Blood | Model flagged for bad tool behavior and avoided |
| Local Legend | Gold Crown | Full successful run with local-only model stack |

#### Context Fabric Badges

| Badge | Tier | Trigger |
|---|---|---|
| Library Goblin | Bone | First corpus attached |
| Citation Fang | Iron | First answer with verified citation |
| Source Reopened | Iron | Citation opens original source successfully |
| No Bluffing | Blood | Answer abstains correctly when evidence is insufficient |
| Thread Finder | Blood | Multi-hop answer verified from multiple source ranges |
| Exhaustive Hunter | Gold Crown | Exhaustive evidence enumeration passes declared gate |
| Million Token Marauder | Gold Crown | Large deterministic corpus processed unattended with verified results |
| Fabric Warchief | Black Anvil | HIVE Context Fabric run passes worker-loss/recovery acceptance |

#### Foundry Badges

| Badge | Tier | Trigger |
|---|---|---|
| Baseline Before Blade | Iron | Baseline report completed before training |
| Arena Entered | Iron | Candidate enters declared evaluation |
| Arena Champion | Black Anvil | Candidate beats production baseline under frozen eval |
| Quarantine Keeper | Blood | Unsafe/corrupt candidate quarantined correctly |
| Rollback Ready | Blood | Promotion includes valid rollback target |
| No False Crown | Gold Crown | Candidate rejected because it failed to beat baseline |

---

## 11. Trophy System

Badges are frequent. Trophies are rare.

### 11.1 Trophy Examples

| Trophy | Requirement |
|---|---|
| The Golden Axe | 25 CLEAN gated runs in a row |
| The Iron Crown | 5 HIVE nodes online and healthy |
| The Black Anvil | Adapter/candidate beats baseline and passes deployed-artifact eval |
| The Bone Ledger | 1,000 reviewed captures |
| The No-Cloud Banner | 100 successful local-only runs |
| The Perfect Warpath | Swarm run: valid plan, all lanes useful, tests pass, reviewer CLEAN |
| The Dragon Skull | Major multi-file feature completed with zero BLOCKERs |
| The Anti-Spam Totem | High success rate with low token/tool-call waste |
| The Arena Champion | Foundry candidate beats production baseline under frozen eval |
| The Goblin HR Award | 100 role-safe runs with no permission violations |

### 11.2 Trophy Definition Schema

```json
{
  "trophy_id": "the_black_anvil",
  "schema_version": "warpath-trophy-v1",
  "name": "The Black Anvil",
  "tier": "mythic",
  "description": "A candidate beat the current baseline under frozen evaluation and passed deployed-artifact verification.",
  "requirements": [
    { "event_type": "foundry.candidate.evaluated", "payload_match": { "beats_baseline": true } },
    { "event_type": "foundry.candidate.deployed_artifact_verified", "payload_match": { "passed": true } }
  ],
  "share_safe": true
}
```

---

## 12. Streaks

Streaks should be visible and motivating, but should not punish experimentation too harshly.

| Streak | Meaning | Break Condition |
|---|---|---|
| Clean Gate Streak | Consecutive CLEAN reviewer results | MINOR or BLOCKER |
| Local-Only Streak | Consecutive successful runs using local models only | Cloud/API model used |
| Safe Approval Streak | Consecutive risky actions handled through approval gates | Bypass attempt or blocked unsafe action |
| Tester Truth Streak | Consecutive runs with meaningful TESTER verification | Tester absent or shallow/no verification |
| Forge Purity Streak | Consecutive dataset preflight/sanitizer passes | Preflight/sanitizer failure |
| HIVE Uptime Streak | All paired nodes reachable during scheduled checks | Node offline beyond grace window |
| No Poison Streak | Bad captures rejected before training | Poison/contamination admitted |
| Citation Precision Streak | Context Fabric answers have verified citations | Citation fails verification |

### 12.1 Streak Reset Policy

- Reset streaks only on relevant failures.
- Do not reset Local-Only Streak for reading docs or using no model.
- Do not reset Clean Gate Streak for runs that do not invoke Reviewer Gate.
- Do not reset Forge Purity Streak for unrelated swarm runs.

---

## 13. Share Cards and Bragging Rights

### 13.1 Privacy Requirements

Share cards must never include:

- raw workspace paths
- source code snippets
- prompts containing private content
- secrets
- email addresses
- private IPs
- file names unless explicitly marked public/share-safe
- model outputs that contain user content

Share cards may include:

- rank
- score
- counts
- badge names
- trophy names
- local-only run count
- HIVE node count
- Warband count
- model names if user allows
- public repo name if user allows
- benchmark names if public

### 13.2 Markdown Share Card

```md
# TheOrc Warpath Card

**Operator:** Erik / hardcoreerik  
**Rank:** Iron Warchief  
**Warpath Score:** 8,740  
**Clean Gate Streak:** 12  
**Local-Only Runs:** 100  
**HIVE Nodes:** 3  
**Warbands:** 1  
**Best Goblin:** TESTER Lv. 14  

## Trophies

- The Golden Axe
- The No-Cloud Banner
- Truth Goblin
- No Poison In The Pit

Generated locally by TheOrc. No source code included.
```

### 13.3 JSON Share Card

```json
{
  "schema_version": "warpath-share-card-v1",
  "generated_at": "2026-07-03T18:30:12-07:00",
  "rank": "Iron Warchief",
  "warpath_score": 8740,
  "clean_gate_streak": 12,
  "local_only_runs": 100,
  "hive_nodes": 3,
  "warbands": 1,
  "badges": ["Trial Passed", "Truth Goblin", "No Poison In The Pit"],
  "trophies": ["The Golden Axe", "The No-Cloud Banner"],
  "privacy_statement": "No source code, prompts, file paths, secrets, or private content included."
}
```

### 13.4 GitHub Badge Export

Optional future export:

```md
![TheOrc Rank](https://img.shields.io/badge/TheOrc-Iron%20Warchief-39FF6A)
![Clean Runs](https://img.shields.io/badge/Clean%20Runs-62-blue)
![Local AI](https://img.shields.io/badge/Local%20AI-100%25-brightgreen)
```

Do not auto-publish these. Generate local markdown only.

---

## 14. UI Design

### 14.1 New Main Surfaces

| Surface | Description |
|---|---|
| Tribe Ledger | Main profile/stat page |
| Hall of Skulls | Badge/trophy collection page |
| Battle Report | Per-run scorecard shown after relevant runs |
| Campaign Map | Per-workspace progress and suggestions |
| Bestiary | Model mastery view; may integrate with model catalogue/capability data |

### 14.2 MVP UI

MVP should be simple:

- Add a Warpath/Tribe Ledger panel.
- Show total score, rank, category bars, current streaks.
- Show latest badges.
- Show recent Battle Reports.
- Add a button to export share card.

### 14.3 Battle Report UX

After a run completes:

- Do not interrupt the operator with a huge modal.
- Show a compact “Battle Report Ready” card.
- Allow click to expand.
- If badges unlocked, show a small toast.
- If penalties occurred, show direct explanation.

### 14.4 Badge Unlock UX

Badge unlock toast should contain:

```text
Badge Unlocked: Trial Passed
Reviewer Gate returned CLEAN.
+25 Warpath Score
```

For audit/flavored badges like Blood Oath Override:

```text
Audit Mark Recorded: Blood Oath Override
You overrode a BLOCKER finding with explicit acknowledgement.
Score impact: -20
```

### 14.5 Lite Mode

Warpath visuals should respect any Lite Mode or reduced-motion settings. Badges and cards can be visually fun without animation spam.

---

## 15. Integration Points

### 15.1 Swarm Runtime

Emit events for:

- run started
- run completed
- boss plan valid/invalid
- role assigned
- role violation
- tester write attempt
- files staged
- tests pass/fail
- worker output empty/useful
- model fallback used

### 15.2 Approval Flow

Emit events for:

- shell command proposed
- shell command approved
- shell command rejected
- file write proposed
- file write approved
- file write rejected
- unknown tool blocked
- policy block
- bypass attempt, if detectable

### 15.3 Reviewer Gate

Emit events for:

- review started
- review completed
- verdict CLEAN/MINOR/BLOCKER
- BLOCKER held apply
- BLOCKER override acknowledged
- rework requested
- rework later passes CLEAN

### 15.4 Training Pit / ORC ACADEMY

Emit events for:

- capture staged
- capture accepted
- capture rejected
- sanitizer passed/failed
- preflight passed/failed
- training started
- training checkpoint
- training completed
- adapter evaluated
- adapter promoted/rejected
- train/eval leakage found
- candidate quarantined

### 15.5 Model Wiki / Capability Probing

Emit events for:

- probe started
- probe completed
- structured output passed/failed
- category capability changed
- model-role mismatch warning
- smaller model beats larger model on bounded eval

### 15.6 HIVE / Warbands

Emit events for:

- HIVE enabled
- node discovered
- node paired
- node authenticated
- node offline
- node recovered
- Warchief elected
- Warband connected
- Warband task completed
- worker loss detected
- task requeued
- stale completion rejected

### 15.7 Context Fabric

Emit events for:

- corpus attached
- source ingested
- citation produced
- citation verified
- citation failed verification
- source reopened
- answer abstained correctly
- exhaustive task passed
- distributed fabric task recovered from worker loss

### 15.8 Foundry / Arena

Emit events for:

- baseline report completed
- candidate trained
- candidate evaluated
- candidate beats baseline
- candidate fails baseline
- candidate promoted
- candidate quarantined
- rollback executed

---

## 16. AI Implementation Guidance for Smaller Models

This section is intentionally direct. It is written so a smaller coding model can follow it without inventing behavior.

### 16.1 Do This

1. Create a Warpath event model.
2. Create a local repository for storing events.
3. Create a scoring service that reads events and computes a profile.
4. Create a badge service that unlocks badges based on event history.
5. Create a simple UI panel that shows score, rank, categories, streaks, badges, and trophies.
6. Add event emissions at safe existing boundaries.
7. Add share-card export that excludes private content.
8. Add tests for scoring and badge unlock rules.

### 16.2 Do Not Do This

1. Do not execute tools from Warpath code.
2. Do not change approval behavior.
3. Do not make any model more trusted because of a badge.
4. Do not automatically upload score data.
5. Do not include raw code or private paths in share cards.
6. Do not reward BLOCKER overrides.
7. Do not reward raw lines written.
8. Do not add cloud services.
9. Do not make Warpath required for normal app operation.
10. Do not block user work if Warpath storage fails.

### 16.3 Failure Behavior

If Warpath fails:

- Log the error.
- Do not crash the app.
- Do not block the swarm.
- Do not block approvals.
- Do not block training.
- Continue the primary workflow.

Warpath is a scoring/visibility layer. It is not mission-critical execution infrastructure.

---

## 17. Phased Implementation Plan

### Phase W-0 — Documentation and Event Inventory

Status: proposed.

Deliverables:

- Accept this white paper.
- Identify existing code points that can emit events.
- Create a small event inventory table.
- Choose JSON or SQLite MVP storage.

Exit criteria:

- Maintainer approves event names and MVP scope.
- No code behavior changes yet.

### Phase W-1 — Event Logging Foundation

Deliverables:

- `WarpathEvent` model.
- `IWarpathEventSink` interface.
- `WarpathEventService` implementation.
- JSONL or SQLite event storage.
- Unit tests.

Acceptance criteria:

- Can record event.
- Can list events.
- Invalid event schema is rejected.
- Storage failure does not crash primary workflow.

### Phase W-2 — Profile and Score Projection

Deliverables:

- `WarpathProfile` model.
- `WarpathScoringService`.
- Category score calculation.
- Rank calculation.
- Streak calculation.
- Unit tests with fake events.

Acceptance criteria:

- Given a deterministic event list, service returns deterministic score.
- Penalties are applied correctly.
- Streaks reset only on relevant events.

### Phase W-3 — Badge and Trophy Engine

Deliverables:

- Badge definitions.
- Trophy definitions.
- `WarpathBadgeService`.
- Unlock persistence.
- Unit tests for first 25 badges.

Acceptance criteria:

- Badge unlocks once.
- Badge remains unlocked after restart.
- Audit mark badges can have negative/no score.
- Badge unlocks are traceable to event ids.

### Phase W-4 — UI MVP

Deliverables:

- Tribe Ledger panel.
- Recent badges list.
- Category bars.
- Streak list.
- Recent Battle Reports.
- Export share card button.

Acceptance criteria:

- UI loads with no events.
- UI updates after new events.
- UI does not require network.
- UI does not show private paths.

### Phase W-5 — Run Battle Reports

Deliverables:

- Battle Report model.
- Per-run score calculation.
- Compact completion card.
- Expanded report view.

Acceptance criteria:

- Swarm run produces report.
- Reviewer verdict affects report.
- Penalties are visible and explained.

### Phase W-6 — Integration Expansion

Deliverables:

- Training Pit events.
- HIVE/Warband events.
- Model probe events.
- Context Fabric events.
- Foundry/Arena events when those systems exist.

Acceptance criteria:

- Each integration emits only share-safe metadata by default.
- Existing workflows are not blocked by Warpath.

---

## 18. Test Plan

### 18.1 Unit Tests

Required tests:

- `WarpathEventService_RecordEvent_WritesEvent`
- `WarpathEventService_InvalidEvent_Rejects`
- `WarpathScoringService_CleanRun_AwardsExpectedPoints`
- `WarpathScoringService_TesterWriteAttempt_AppliesPenalty`
- `WarpathScoringService_BlockerOverride_DeductsPoints`
- `WarpathBadgeService_FirstSwarmRun_UnlocksFirstBlood`
- `WarpathBadgeService_CleanReview_UnlocksTrialPassed`
- `WarpathBadgeService_BadgeDoesNotUnlockTwice`
- `WarpathStreakService_CleanGateStreak_ResetsOnMinor`
- `WarpathShareCard_DoesNotIncludeWorkspacePath`

### 18.2 Integration Tests

Recommended tests:

- Swarm run completed event creates Battle Report.
- Reviewer CLEAN event unlocks Trial Passed.
- BLOCKER override records audit mark and penalty.
- Dataset admission passed updates Forge Progress.
- HIVE node paired unlocks First Ally.
- Model probe completed unlocks Beastmaster.

### 18.3 Privacy Tests

Required tests:

- Share card does not include raw workspace path.
- Share card does not include prompt text.
- Share card does not include file contents.
- Share card does not include private IP unless explicitly allowed and sanitized.
- Share card does not include email address.

### 18.4 Regression Tests

Warpath must not break:

- normal app launch
- workspace open
- swarm run
- approval flow
- Training Pit panel
- HIVE panel
- Context Fabric workflows

---

## 19. Security and Privacy

### 19.1 Local-First Storage

Warpath data stays local by default.

### 19.2 No Automatic Publishing

Do not automatically publish Warpath profile, score, badge, trophy, or share-card data.

### 19.3 Safe Workspace Identifier

Use a hash for workspace identity, not a raw path.

Bad:

```json
"workspace": "C:\\Users\\hardc\\source\\repos\\SecretProject"
```

Good:

```json
"workspace_id": "sha256:0af1..."
```

### 19.4 Event Payload Privacy

Events should store facts, not source content.

Good:

```json
{
  "event_type": "review.verdict.blocker",
  "payload": {
    "blocker_count": 2,
    "minor_count": 1
  }
}
```

Bad:

```json
{
  "event_type": "review.verdict.blocker",
  "payload": {
    "full_diff": "...private code..."
  }
}
```

### 19.5 Share Card Redaction

All share exports must include a privacy statement and should be generated from a share-safe projection, not raw events.

---

## 20. Anti-Gaming Controls

### 20.1 Cooldowns

Some badges/events should have cooldowns or uniqueness rules.

Examples:

- Model probe points only count once per model per version or per cooldown window.
- Repeated failed/identical runs do not farm run completion points.
- Same capture cannot count as reviewed multiple times.

### 20.2 Quality Gates

Award significant points only after quality evidence.

Training and capture lifecycle events remain visible audit history but award zero
points. Positive Forge/Foundry score is limited to verified outcomes:

- baseline report completed
- dataset admission passed
- candidate correctly rejected under the frozen evaluation
- deployed artifact passed its declared proof
- promotion includes a verified rollback target
- Arena confirmed improvement over the declared baseline

### 20.3 Penalty on Unsafe Shortcuts

Unsafe shortcuts must reduce score.

Examples:

- BLOCKER override: penalty.
- TESTER write attempt: penalty.
- train/eval leakage: major penalty and quarantine flag.

### 20.4 No Score for Noise

Do not score:

- repeated tool calls with no success
- verbose output
- model chatter
- huge diffs without tests
- synthetic data volume without review

---

## 21. Example Warpath Scenarios

### 21.1 Clean Swarm Run

Events:

```text
swarm.run.started
agent.plan.generated(valid=true)
swarm.role.assignment(valid=true)
approval.file_write.approved
swarm.tests.passed
review.verdict.clean
swarm.run.completed(success=true)
```

Result:

- Run Score: high.
- Badge: Trial Passed if first CLEAN.
- Possible badge: First Blood if first successful Swarm run.

### 21.2 BLOCKER Found and Reworked

Events:

```text
review.verdict.blocker
review.blocker.held_apply
swarm.rework.requested
review.verdict.clean
review.blocker.reworked_clean
```

Result:

- Award The Gate Holds.
- Award Redeemed In Battle.
- Positive score for catching and fixing issue.

### 21.3 BLOCKER Overridden

Events:

```text
review.verdict.blocker
review.blocker.override
```

Result:

- Record Blood Oath Override audit mark.
- Apply score penalty.
- Do not unlock “No Cowardly Merge.”

### 21.4 Training Loss Trap

Events:

```text
academy.training.completed
academy.adapter.evaluated(eval_loss_improved=true, rubric_regressed=true)
academy.adapter.rejected
```

Result:

- Unlock Loss Is A Liar.
- Award discipline points for rejecting bad candidate.

### 21.5 HIVE Worker Loss Recovery

Events:

```text
hive.node.offline
hive.task.requeued
hive.task.reclaimed_by_different_node
hive.stale_completion.rejected
hive.task.completed
```

Result:

- Unlock Dead Node Recovery.
- Increase HIVE Power.

### 21.6 Context Fabric Verified Answer

Events:

```text
fabric.corpus.attached
fabric.answer.cited
fabric.citation.verified
fabric.source.reopened
```

Result:

- Unlock Library Goblin.
- Unlock Citation Fang.
- Increase Fabric Evidence.

---

## 22. Development Backlog

### 22.1 MVP Backlog

1. Add `WarpathEvent` model.
2. Add `IWarpathEventSink`.
3. Add local JSONL event sink.
4. Add `WarpathScoringService`.
5. Add first 25 badge definitions.
6. Add `WarpathBadgeService`.
7. Add `WarpathProfile` projection.
8. Add Tribe Ledger panel.
9. Add share-card markdown export.
10. Emit events for Swarm run completed and Reviewer verdict.

### 22.2 Second Backlog

1. Add Training Pit events.
2. Add HIVE/Warband events.
3. Add Model probe events.
4. Add Battle Report view.
5. Add badge unlock toasts.
6. Add privacy tests.
7. Add SQLite migration.

### 22.3 Later Backlog

1. Context Fabric badge integration.
2. Foundry/Arena badge integration.
3. PNG share-card generation.
4. GitHub badge markdown export.
5. Campaign Map per workspace.
6. Bestiary/model mastery UI.
7. Trophy wall visuals.

---

## 23. Suggested File Layout

Actual project paths may vary. Do not force this layout if the repository already has a better convention.

```text
OrchestratorIDE/Services/Warpath/
  WarpathEvent.cs
  WarpathProfile.cs
  WarpathBadge.cs
  WarpathTrophy.cs
  IWarpathEventSink.cs
  WarpathEventService.cs
  WarpathScoringService.cs
  WarpathBadgeService.cs
  WarpathShareCardService.cs
  WarpathBattleReportService.cs
  WarpathRepository.cs

OrchestratorIDE.Avalonia/UI/Panels/Warpath/
  TribeLedgerPanel.axaml
  TribeLedgerPanel.axaml.cs
  HallOfSkullsPanel.axaml
  HallOfSkullsPanel.axaml.cs
  BattleReportView.axaml
  BattleReportView.axaml.cs

OrchestratorIDE.UnitTests/Warpath/
  WarpathEventServiceTests.cs
  WarpathScoringServiceTests.cs
  WarpathBadgeServiceTests.cs
  WarpathShareCardServiceTests.cs
```

If TheOrc has moved shared logic into a cross-platform runtime/shared project, place non-UI services there instead.

---

## 24. Open Questions

1. Should Warpath use SQLite immediately or start with JSONL?
2. Should Warpath be visible by default or opt-in under Settings?
3. Should operator name be user-provided, GitHub-derived, or omitted?
4. Should share cards include model names by default?
5. Should HIVE node names be share-safe by default?
6. Should Warpath support per-workspace profiles or one global profile plus workspace campaigns?
7. Should Warpath events be retained forever or compacted into projections after N days?
8. Should deleted/archived workspaces retain Campaign Map history?
9. Should badge definitions be code-only, JSON-driven, or hybrid?
10. Should community-shared badge packs ever be allowed? If yes, only after a safe plugin/config system exists.

Recommended defaults:

- Start global profile plus per-workspace campaign summaries.
- Use JSONL for MVP if SQLite migration cost is high; otherwise use SQLite immediately.
- Do not include model names or node names in share cards unless user enables advanced sharing.
- Keep badge definitions in code for first release to avoid dynamic badge security/quality problems.

---

## 25. Acceptance Criteria for First Merge

The first merge should be small and safe.

Minimum acceptance criteria:

1. Warpath docs accepted.
2. Event model exists.
3. Event sink writes local event records.
4. Scoring service can compute profile from events.
5. Badge service unlocks at least 10 badges.
6. Basic Tribe Ledger panel displays rank and score.
7. Share-card markdown export exists.
8. Tests cover scoring, badge unlock, and privacy.
9. No primary workflow depends on Warpath.
10. No network upload exists.

---

## 26. Final Product Positioning

Warpath should make TheOrc feel more alive without making it less serious.

TheOrc is not just “AI writes code.” It is an operator-controlled local AI system that plans, executes, reviews, learns, routes, cites, and distributes work. Warpath makes that growth visible.

The correct flex is not:

> “I generated a lot of code.”

The correct flex is:

> “My local AI warband runs clean, stays in its lanes, passes review, rejects poisoned data, proves claims from source, and gets better on my hardware.”

That is the heart of TheOrc Warpath.

---

## 27. Appendix A — First 25 MVP Badges

| Badge ID | Name | Family | Tier | Trigger | Score |
|---|---|---|---|---|---:|
| `first_blood` | First Blood | Swarm | Bone | First successful Swarm run | 25 |
| `boss_brain` | Boss Brain | Swarm | Iron | Valid boss plan with correct roles | 25 |
| `stay_in_your_lane` | Stay In Your Lane | Swarm | Blood | 10 clean role-safe runs | 50 |
| `truth_goblin` | Truth Goblin | Swarm | Blood | Tester catches issue | 50 |
| `perfect_warpath` | Perfect Warpath | Swarm | Gold Crown | Valid plan + tests pass + reviewer CLEAN | 100 |
| `trial_passed` | Trial Passed | Reviewer | Bone | Reviewer CLEAN | 25 |
| `the_gate_holds` | The Gate Holds | Reviewer | Iron | BLOCKER prevents apply | 25 |
| `redeemed_in_battle` | Redeemed In Battle | Reviewer | Blood | BLOCKER fixed and rerun CLEAN | 75 |
| `scarred_but_worthy` | Scarred But Worthy | Reviewer | Bone | MINOR accepted | 15 |
| `no_cowardly_merge` | No Cowardly Merge | Reviewer | Blood | 10 runs with no BLOCKER override | 50 |
| `ore_collector` | Ore Collector | Forge | Audit | 25 captures staged | 0 |
| `ore_sorter` | Ore Sorter | Forge | Audit | 25 captures reviewed | 0 |
| `no_poison_in_the_pit` | No Poison In The Pit | Forge | Blood | Dataset admission gates pass | 75 |
| `forge_lit` | Forge Lit | Forge | Audit | First training run started | 0 |
| `loss_is_a_liar` | Loss Is A Liar | Forge | Gold Crown | Lower loss rejected because rubric failed | 100 |
| `campfire_lit` | Campfire Lit | HIVE | Bone | HIVE enabled | 20 |
| `first_ally` | First Ally | HIVE | Bone | First node paired | 30 |
| `crowned` | Crowned | HIVE | Iron | Machine elected Warchief | 50 |
| `warband_deployed` | Warband Deployed | HIVE | Blood | First headless Warband connected | 75 |
| `dead_node_recovery` | Dead Node Recovery | HIVE | Gold Crown | Requeued task completes after worker loss | 100 |
| `beastmaster` | Beastmaster | Model | Bone | First model probed | 20 |
| `know_your_goblin` | Know Your Goblin | Model | Iron | All active models probed | 50 |
| `json_whisperer` | JSON Whisperer | Model | Iron | Structured-output probe passes | 40 |
| `tiny_but_mean` | Tiny But Mean | Model | Blood | Smaller model beats larger model on bounded eval | 75 |
| `local_legend` | Local Legend | Model/Safety | Gold Crown | Full successful local-only project run | 100 |

---

## 28. Appendix B — Example Developer Prompt

Use this prompt for Codex/Grok/Qwen when starting implementation:

```text
Implement Phase W-1 of TheOrc Warpath exactly as specified in docs/THEORC_WARPATH_GAMIFICATION_WHITEPAPER.md.

Scope:
- Add a WarpathEvent model.
- Add IWarpathEventSink.
- Add a local JSONL-backed WarpathEventService.
- Add validation for required fields.
- Add unit tests.

Hard rules:
- Do not change approval behavior.
- Do not execute tools from Warpath code.
- Do not upload anything.
- Do not include workspace raw paths in event records; use a hash or null.
- Warpath failure must not crash or block primary workflows.
- Do not implement badges, UI, scoring, or SQLite yet unless explicitly requested.

Acceptance:
- dotnet build passes.
- Warpath event tests pass.
- Existing tests are not broken.
- Provide a short implementation report with files changed and how to test.
```

---

## 29. Appendix C — Example Battle Report Payload

```json
{
  "schema_version": "warpath-battle-report-v1",
  "run_id": "swarm_20260703_183012",
  "created_at": "2026-07-03T18:30:12-07:00",
  "score": 87,
  "verdict": "CLEAN",
  "positive_items": [
    { "label": "valid boss plan", "points": 10 },
    { "label": "correct role assignments", "points": 10 },
    { "label": "tests passed", "points": 10 },
    { "label": "reviewer CLEAN", "points": 15 }
  ],
  "negative_items": [
    { "label": "stale model probe", "points": -3 }
  ],
  "badges_unlocked": ["trial_passed"],
  "privacy": {
    "contains_user_content": false,
    "safe_for_share_card": true
  }
}
```

---

## 30. Appendix D — Glossary

| Term | Meaning |
|---|---|
| Warpath | Overall gamification and mastery system |
| Tribe Ledger | User/operator profile and stats page |
| Hall of Skulls | Badge and trophy display |
| Battle Report | Per-run scorecard |
| Campaign Map | Workspace/project progress view |
| Bestiary | Model capability/probe mastery view |
| Forge Marks | Training Pit/ORC ACADEMY achievements |
| Crown Deeds | HIVE/Warband achievements |
| Trial Marks | Reviewer Gate achievements |
| Honor Guard | Safety and approval-discipline score |
| War Trophy | Rare high-value achievement |
| Audit Mark | Visible record of risky/exception action, not necessarily positive |

---

## 31. Closing

Warpath is a natural fit for TheOrc because TheOrc is already a system of roles, gates, evidence, training loops, distributed workers, and local ownership. The implementation must stay honest: no fake claims, no cloud scoreboard, no unsafe incentives, no noise farming.

Build it as a local evidence-backed mastery layer. Make the user proud of clean engineering behavior. Make the goblins funny. Keep the gates serious.

That is the winning version.
