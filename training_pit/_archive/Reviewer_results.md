From Grok

Final Independent Review — REVIEW_BUNDLE_B
ID: ex_20260611_015817
VERDICT: KEEP
REASON: 3 tasks with exact roles (RESEARCHER, CODER, UIDEVELOPER). Concrete files named (ModelFavoritesStore.cs, ModelWikiWindow.xaml). Tasks agree on deliverables and fully solve the star/favorite goal.
BEST TASK: CODER — clear service methods and JSON handling.
WORST TASK: RESEARCHER — solid but slightly generic.
ID: ex_20260611_024049
VERDICT: KEEP
REASON: 3 tasks, perfect role usage, concrete file (phase3_preflight.py). Handles both JSON and human output cleanly while solving the goal.
BEST TASK: CODER — precise argparse + output logic.
WORST TASK: TESTER — good but basic.
ID: ex_20260611_013148
VERDICT: KEEP
REASON: 3 tasks with exact roles. Concrete files and shared ShortcutBinding model. Solves Ctrl+F1 shortcut sheet goal properly.
BEST TASK: UIDEVELOPER — clear XAML requirements.
WORST TASK: CODER — assumes some statics that could be tighter.
ID: ex_20260611_025410
VERDICT: KEEP
REASON: 3 tasks, exact roles, concrete files (SwarmBoardPanel.xaml, SwarmSession.cs). Perfectly implements dry-run without mocks.
BEST TASK: CODER — clean flow interception.
WORST TASK: UIDEVELOPER — straightforward.
ID: ex_20260611_003032
VERDICT: KEEP
REASON: 3 tasks, correct roles, concrete service and bindings. Good timer + git integration for the branch status goal.
BEST TASK: CODER — thread-safety and timer details.
WORST TASK: RESEARCHER — standard but appropriate.
ID: ex_20260611_014846
VERDICT: KEEP
REASON: 3 tasks, exact roles, concrete ScrollViewer naming and toggle. Solves auto-scroll requirement cleanly.
BEST TASK: CODER — timer + hover logic.
WORST TASK: UIDEVELOPER — minimal but correct.
ID: ex_20260611_022139
VERDICT: KEEP
REASON: 3 tasks, correct roles, concrete CommitRecord and Expander. Handles git parsing and context menu well.
BEST TASK: CODER — robust git log parsing with error handling.
WORST TASK: UIDEVELOPER — standard ItemsControl usage.
ID: ex_20260611_025728
VERDICT: BORDERLINE
REASON: Only 2 tasks (CODER + UIDEVELOPER) — violates the 2-4 task requirement. No RESEARCHER step. CopyMetadata and button logic are otherwise reasonable but the plan is too thin.
BEST TASK: CODER — clear formatting and clipboard.
WORST TASK: UIDEVELOPER — missing RESEARCHER makes it incomplete.
ID: ex_20260611_002848
VERDICT: BORDERLINE
REASON: Only 2 tasks — violates 2-4 task rule. No TESTER or RESEARCHER. Script goal is solved but structure is too minimal for planner training.
BEST TASK: CODER — JSON grouping and ASCII chart logic.
WORST TASK: RESEARCHER — the only other task but still too short overall.
ID: ex_20260611_023613
VERDICT: KEEP
REASON: 3 tasks, exact roles, concrete env_doctor.ps1 file. Proper version checking and exit behavior.
BEST TASK: CODER — clear PowerShell structure.
WORST TASK: TESTER — syntax-only check is light.
ID: ex_20260611_025821
VERDICT: KEEP
REASON: 3 tasks, correct roles, concrete footer elements and size calculation. Solves the Model Wiki stats goal.
BEST TASK: CODER — aggregation and INotifyPropertyChanged handling.
WORST TASK: UIDEVELOPER — basic binding.
ID: ex_20260611_012053
VERDICT: KEEP
REASON: 3 tasks, exact roles, concrete ZoomIndicator. Clean zoom percentage and reset logic.
BEST TASK: CODER — property update and click handler.
WORST TASK: RESEARCHER — formula is straightforward.
Pass rate: 10/12 KEEP (83%)
Most common weakness: Several examples (especially 8 and 9) have only 2 tasks instead of the required 2-4 range, making plans feel incomplete/thin. Minor role omission or lack of depth in some RESEARCHER steps.
Trust assessment: I would trust a model trained primarily on the KEEP examples to plan reliably for TheOrc-style tasks — they strongly reinforce correct 4-role swarm structure, concrete naming, and safety-aligned flows. The BORDERLINE ones should be pulled or fixed to avoid teaching underspecified plans. Overall solid bundle after prior layers, but continue enforcing task count strictly.






From DeepSeek
Individual Example Verdicts
Example 1 — ID: ex_20260611_013649
VERDICT: KEEP
REASON: All three tasks respect role boundaries and output file names; the TESTER role is absent, so rule 3 does not apply.
BEST TASK: UIDEVELOPER — adds the ContextMenu and binds the click event cleanly.
WORST TASK: RESEARCHER — the information requested (DataContext access, Clipboard API) is standard WPF knowledge and could have been part of the CODER task.

Example 2 — ID: ex_20260611_020910
VERDICT: KEEP
REASON: Three tasks with valid roles; the TESTER task correctly runs dotnet test and reports results, satisfying rule 3.
BEST TASK: CODER — specifies three concrete NUnit test cases with exact method names.
WORST TASK: RESEARCHER — again requests easily accessible knowledge (NUnit event testing) that could be part of the CODER task.

Example 3 — ID: ex_20260611_021147
VERDICT: KEEP
REASON: Valid three‑task plan; TESTER runs the tests and reports, staying within rule 3.
BEST TASK: CODER — correctly describes both debounce and throttle test cases.
WORST TASK: RESEARCHER — the same redundant pattern: analyzing DebounceDispatcher.cs could have been done by the CODER.

Example 4 — ID: ex_20260611_012459
VERDICT: PULL
REASON: The TESTER task violates rule 3. It instructs to "Check both files for correct C# syntax, valid XAML tags …" without specifying a tool to run, which is a manual verification, not an automated test execution.
BEST TASK: CODER — correctly creates the toast control and its static Show method.
WORST TASK: TESTER — performs a manual check, which is outside the allowed role.

Example 5 — ID: ex_20260611_025410
VERDICT: KEEP
REASON: Three tasks with valid roles; no TESTER role present.
BEST TASK: CODER — clearly implements the dry‑run check and early termination.
WORST TASK: RESEARCHER — asks for existing method names and binding details that the CODER could discover via reading the code.

Example 6 — ID: ex_20260611_024049
VERDICT: KEEP
REASON: Three tasks with valid roles; TESTER runs the script with and without the --json flag, which is a proper test execution.
BEST TASK: CODER — refactors the script to collect results into a list of dictionaries.
WORST TASK: RESEARCHER — requests basic knowledge of argparse and JSON that is standard Python.

Example 7 — ID: ex_20260611_015857
VERDICT: KEEP
REASON: Three tasks with valid roles; no TESTER role.
BEST TASK: CODER — correctly implements JSON read/write and event subscriptions.
WORST TASK: RESEARCHER — the requested System.Text.Json usage is routine knowledge.

Example 8 — ID: ex_20260610_063159
VERDICT: KEEP
REASON: Three tasks with valid roles; no TESTER role.
BEST TASK: CODER — correctly retrieves the verdict and exposes bound properties.
WORST TASK: RESEARCHER — asks for the location of the TesterVerdict enum and the existing event handler, which could be part of the CODER task.

Example 9 — ID: ex_20260611_024901
VERDICT: KEEP
REASON: Three tasks with valid roles; no TESTER role.
BEST TASK: CODER — writes a complete async service with Directory.EnumerateFiles.
WORST TASK: RESEARCHER — requests well‑known System.IO patterns and WPF Popup usage.

Example 10 — ID: ex_20260611_014020
VERDICT: KEEP
REASON: Three tasks, all with valid roles; no TESTER role. The plan correctly solves the given goal.
BEST TASK: CODER (first) — adds the EstimatedTokens property in SwarmTask.cs.
WORST TASK: CODER (second) — modifying SwarmBoardPanel.xaml.cs to ensure binding works is mostly redundant; the UI update could have been handled in one CODER task.

Example 11 — ID: ex_20260611_002825
VERDICT: KEEP
REASON: Three tasks with valid roles; TESTER simulates typing and verifies debounce behavior, which is a test execution (running a simulation).
BEST TASK: CODER — correctly implements the DispatcherTimer debounce pattern.
WORST TASK: RESEARCHER — again requests a standard pattern that is widely documented.

Example 12 — ID: ex_20260611_023613
VERDICT: PULL
REASON: The TESTER task violates rule 3. It instructs to "Run a syntax check … by attempting to parse the file content without executing the logic (e.g., using Get-Content and checking for parsing errors)." This is a manual verification, not an automated test run.
BEST TASK: CODER — writes a complete environment doctor script with exit‑code logic.
WORST TASK: TESTER — manual check that is not a test execution.

Summary Statistics
Pass Rate: 10 / 12 = 83.3%

Examples Pulled (IDs): ex_20260611_012459 , ex_20260611_023613

Most Common Weakness
Over‑use of RESEARCHER tasks for trivial or standard knowledge.
In 11 of the 12 examples, a RESEARCHER task is inserted to request information that is either standard in the given stack (e.g., argparse, System.Text.Json, Directory.EnumerateFiles) or could be discovered by the CODER/UIDEVELOPER by reading the existing code. This teaches the model to be inefficient, producing longer plans than necessary. The two pulled examples show a more serious weakness: misuse of TESTER for manual verification, which would corrupt the model's understanding of the TESTER role.

Overall Confidence in the Dataset
Confidence: 82%

The dataset is structurally clean—all examples follow the 2‑4 task rule, use only the four allowed roles, and stay within a single language stack. The 10 kept examples would be safe for training. However, the two pulled examples reveal that the automated filters (rubric, pre‑screen, LLM judge, and human review) missed clear violations of rule 3. If the same pattern appears in the remaining 151 examples, the trained model would sometimes try to use TESTER for manual checks. After removing those examples, the dataset becomes reliable, though the inefficiency from over‑using RESEARCHER tasks would remain unless addressed.





From Codex.

Findings
High: prescreen_captures.py provably missed real defect classes that the human reviewer had to catch later, so the deterministic gate is materially weaker than the commit history implies. The script only rejects low_rubric, single_task, tester_write, and extension-based wrong_stack, with create_existing limited to creat* phrasing in a narrow regex prescreen_captures.py (line 99). It has no check for invalid role strings and no check for overwriting an existing file when the plan says Write ... instead of Create .... Both failures happened in the audited data:
ex_20260611_015650 was correctly human-rejected for invented role UIDEDEVELOPER.
ex_20260611_030431 was correctly human-rejected for treating existing MainWindow.xaml.cs as a new file.
These are false negatives in the first-pass screen, not edge cases.

High: judge_captures.py was effectively non-discriminating on this batch. The saved triage outputs contain 139/139 low-risk verdicts, even though the human review still found 12 real defects. The failure is visible in the prompt design: the judge is told to assess only “fabrication and internal consistency” judge_captures.py (line 25), is given only GOAL and PLAN text with no repo-tree or existing-file context judge_captures.py (line 57), and is never instructed that any role outside the exact whitelist is automatically invalid judge_captures.py (line 19). That explains why it missed:
invalid role typo UIDEDEVELOPER in ex_20260611_015650
overwrite-existing MainWindow.xaml.cs in ex_20260611_030431
known stack substitutions like models.py / wiki_pane.py on a WPF goal in ex_20260610_214417
A hardened judge prompt needs exact-role validation, explicit existing-file context, and a few-shot set of known defect examples from this repo.

Medium: night_harvest.ps1 is not safe enough for unattended future runs because it does not actually fail closed on native command errors. It pipes generate_goals.py, farm_batch.ps1, and judge_captures.py through Tee-Object but never checks native exit codes before continuing night_harvest.ps1 (line 60), night_harvest.ps1 (line 69), night_harvest.ps1 (line 86). It also deletes any pre-existing stop file at startup night_harvest.ps1 (line 29), which can erase a legitimate operator stop request. This did not corrupt the current exported dataset, but I would not trust it for unsupervised overnight farming yet.

Medium: generate_goals.py has provenance and tranche-integrity gaps that can degrade future batches without tripping a hard failure. It lint-checks goal text, but it never validates the generated domain against an allowed set generate_goals.py (line 59), writes the output PSV directly rather than atomically generate_goals.py (line 157), and only prints a warning when it undershoots the requested count generate_goals.py (line 160). That means a partial or malformed tranche can still be farmed if it contains even a few lines.

Low: phase3_preflight.py passes this dataset, but its duplicate/isolation checks are narrower than the audit requirement. Its duplicate logic only checks within-file duplicates and train/eval overlap phase3_preflight.py (line 446), and eval isolation only compares train against the 10 fixed prompts under training_pit/evals/ phase3_preflight.py (line 530). It does not independently guard against negative/train or negative/eval contamination. My independent scan found no cross-split duplicate example_ids and no duplicate goal texts across train_v1.jsonl, eval_v1.jsonl, and negative_v1.jsonl, so the current export is clean; this is a tooling blind spot, not a current dataset fault.

Audit result
I independently recounted the manifest and got 163 train / 20 eval / 25 negative approved entries, matching the claim. phase3_preflight.py returned READY, validate_dataset.py passed on all three JSONL files, and my independent cross-split scan found no duplicate example_ids or duplicate goal texts across the exported splits.
For decision quality, I sampled 15 approved train entries and 10 rejected entries using a fixed random sample. I did not find an approval in that sample that I would overturn. I also did not find an over-rejection in the 10 rejected samples; the notes were materially accurate. Representative accurate rejections included:
ex_20260611_015650 for invalid role UIDEDEVELOPER
ex_20260611_030431 for overwrite-existing MainWindow.xaml.cs
ex_20260611_022914 for TESTER-write
ex_20260610_214417 for Python hallucination on a WPF/C# goal
ex_20260610_151746 for replacing requested NUnit/C# with Python unittest
I also scanned all approved train entries for the known human-caught defect classes:
invalid role strings: none found
TESTER write-verbs: none found
CODER/UIDEVELOPER titles lacking filenames: none found
approved task counts outside 2–4: none found
Certification
Yes: based on the committed state at f59d385, I would certify the exported 163-example training dataset as training-ready.
I would not certify the current automation as unattended-run-safe without hardening. That is the important distinction here.
Example IDs I would pull from the exported train set: none from this audit.
Three highest-leverage hardening recommendations
Harden prescreen_captures.py before the next farming wave.
Add exact-role whitelist checks, overwrite-existing detection for write/update/modify phrasing, and stricter title/file-anchor checks. The current human-rejected examples already give you proven regression tests: ex_20260611_015650 and ex_20260611_030431.

Redesign the judge prompt around repo-grounded defect detection, not generic fabrication.
Feed it:
exact valid roles
goal file refs
existing-file basenames from the repo
a few-shot pack of real failures from tonight
The current prompt is too abstract and too permissive to be useful as a triage reducer.

Make NIGHT HARVEST fail closed.
Check native exit codes after goal generation, farming, prescreen, and judge; preserve operator stop intent; require atomic tranche writes; and refuse to continue on partial/undersized tranche generation. The current dataset survived because a human stayed in the loop, not because the unattended path is robust.


8:19 PM