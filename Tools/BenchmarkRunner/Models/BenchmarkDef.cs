// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace BenchmarkRunner.Models;

public record BenchmarkDef(
    string Id,
    string Name,
    string Description,
    int    ExpectedAgents,
    string[] ScoringTargets,
    string Prompt)
{
    public static readonly BenchmarkDef Bench01 = new(
        Id: "cleancsv",
        Name: "01 — CleanCSV",
        Description: "File I/O + transformations + export. Best first test — small, deterministic, easy to score.",
        ExpectedAgents: 2,
        ScoringTargets: ["sample_data/messy.csv", "README.md", "TEST_PLAN.md", "trim logic", "duplicate removal", "snake_case normalization", "export function"],
        Prompt: """
You are testing TheOrc Swarm Mode.

Create a small desktop utility called CleanCSV.

Goal:
The user should be able to load a CSV file, preview it, clean common issues, and export a cleaned copy.

Requirements:
- Load a CSV file.
- Show a preview of rows and columns.
- Provide cleaning actions:
  - trim whitespace
  - remove blank rows
  - remove duplicate rows
  - normalize column names to snake_case
- Export the cleaned CSV.
- Include a sample messy CSV file.
- Include a README.
- Include a manual test checklist.
- Include implementation notes.
- Keep the app small.
- Avoid cloud services.
- Avoid unnecessary dependencies.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
Do not use extra agents just to use them.
If using multiple agents, delegate clear roles such as data logic, GUI, testing, and documentation.

Automation Deliverable Requirements:
The final deliverable must create or update this structure inside the active workspace:

.orc/swarm/runs/[RUN_ID]/output/project/
  README.md
  TEST_PLAN.md
  IMPLEMENTATION_NOTES.md
  sample_data/
    messy.csv
  src/ or main app files

The final_report.md must explain:
- how many agents were used
- why those agents were chosen
- what each agent produced
- what files were created
- how to run the project
- how to test the project
- known risks or limitations

Safety / Control Requirements:
Do not bypass TheOrc's existing file-edit approval gates.
Do not bypass shell-command approval gates.
Do not silently install dependencies.
Do not use destructive commands.
""");

    public static readonly BenchmarkDef Bench02 = new(
        Id: "loganalyzer",
        Name: "02 — Log Analyzer",
        Description: "Deterministic parser. Verifiable by counting keywords in sample log.",
        ExpectedAgents: 2,
        ScoringTargets: ["sample_data/sample.log", "README.md", "TEST_PLAN.md", "ERROR", "WARN", "EXCEPTION", "export to markdown"],
        Prompt: """
You are testing TheOrc Swarm Mode.

Create a small local log analyzer tool.

Goal:
The user should be able to load a plain text log file and see a summary of warnings, errors, and repeated messages.

Requirements:
- Accept .txt and .log files.
- Count lines containing ERROR, WARN, WARNING, FAIL, EXCEPTION, and SUCCESS.
- Show the top 10 repeated lines.
- Show a simple summary panel.
- Allow exporting the summary to a Markdown report.
- Include sample log data for testing.
- Include a README.
- Include a manual test checklist.
- Include implementation notes.
- Keep the app small. Avoid cloud services. Avoid unnecessary dependencies.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
Do not use extra agents just to use them.
Delegate clear roles: parsing/coding, testing, documentation.

Automation Deliverable Requirements:
The final deliverable must create or update this structure inside the active workspace:

.orc/swarm/runs/[RUN_ID]/output/project/
  README.md
  TEST_PLAN.md
  IMPLEMENTATION_NOTES.md
  sample_data/
    sample.log
  src/ or main app files

The final_report.md must explain how many agents were used, what each produced, how to run and test.

Safety: Do not bypass approval gates. Do not install dependencies silently. No destructive commands.
""");

    public static readonly BenchmarkDef Bench03 = new(
        Id: "bugdex",
        Name: "03 — BugDex",
        Description: "GUI + data model + multi-agent delegation. Best all-round benchmark.",
        ExpectedAgents: 3,
        ScoringTargets: ["README.md", "TEST_PLAN.md", "IMPLEMENTATION_NOTES.md", "5 starter records", "JSON database", "add/edit/delete/search"],
        Prompt: """
You are testing TheOrc Swarm Mode.

Create a small, complete, local-first desktop app called BugDex.

Goal:
BugDex lets a user maintain a simple local database of software bug types and troubleshooting notes.

Requirements:
- Use a local JSON file as the database.
- Provide a simple GUI.
- Allow adding, editing, deleting, and searching bug records.
- Each bug record should include:
  - bug type name, category, severity, symptoms, likely causes, suggested fixes, notes
- Include 5 starter bug records.
- Include sample data, a README, a manual test checklist, and implementation notes.
- Keep the app small. Avoid cloud services and unnecessary dependencies.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
Do not use extra agents just to use them.
Delegate clear roles: planning, coding, testing/debugging, documentation.

Automation Deliverable Requirements:
The final deliverable must create or update this structure inside the active workspace:

.orc/swarm/runs/[RUN_ID]/output/project/
  README.md
  TEST_PLAN.md
  IMPLEMENTATION_NOTES.md
  sample_data/
  src/ or main app files

The final_report.md must explain how many agents were used, what each produced, how to run and test.

Safety: Do not bypass approval gates. Do not install dependencies silently. No destructive commands.
""");

    public static readonly BenchmarkDef Bench04 = new(
        Id: "guardscan",
        Name: "04 — GuardScan (Security)",
        Description: "Defensive security scanner. Use standard models (not security stack).",
        ExpectedAgents: 3,
        ScoringTargets: ["README.md", "TEST_PLAN.md", "fake sample files only", "eval(", "shell=True", "api_key =", "Markdown report"],
        Prompt: """
You are testing TheOrc Swarm Mode in a defensive security workspace.

Create a small local-only security review tool called GuardScan.

Goal:
GuardScan scans a folder of source files and reports potentially risky patterns.

Requirements:
- Scan .py, .js, .ts, .cs, .json, .env.example, and .txt files.
- Detect hardcoded secrets using simple pattern matching.
- Detect risky patterns: eval(, exec(, shell=True, Invoke-Expression, TODO SECURITY, password =, api_key =, token =
- Output a Markdown report.
- Include sample test files with FAKE secrets only — no real credentials.
- Include README.md, TEST_PLAN.md, IMPLEMENTATION_NOTES.md.
- Keep it local only. No real exploit code. No cloud services.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
Delegate: security rules planner, coding agent, testing/reporting agent.
Use only fake sample secrets — no real credentials, no real exploit patterns.

Automation Deliverable Requirements:
Output inside active workspace under .orc/swarm/runs/[RUN_ID]/output/project/

Safety: Do not bypass approval gates. Do not install dependencies silently. No destructive commands. Fake data only.
""");

    public static readonly BenchmarkDef Bench05 = new(
        Id: "mp3player",
        Name: "05 — MP3 Player (Advanced)",
        Description: "Compatibility-heavy. Do not start here. Run after completing 01 and 02.",
        ExpectedAgents: 3,
        ScoringTargets: ["README.md", "TEST_PLAN.md", "Windows/Linux/macOS notes", "play/pause/stop", "folder selection", "dependency notes"],
        Prompt: """
You are testing TheOrc Swarm Mode.

Create a small portable cross-platform MP3 player app.

Goal:
The app should allow a user to select a local folder of MP3 files, display the songs in a list, and play/pause/stop the selected file.

Requirements:
- Keep the app simple. Avoid paid or cloud services.
- Prefer cross-platform libraries.
- Include a README with setup instructions and Windows/Linux/macOS compatibility notes.
- Include a basic manual test checklist and implementation notes.
- Avoid unnecessary dependencies.

Swarm Instructions:
TheOrc should decide whether this task needs 1, 2, or 3 agents.
Do not use extra agents just to use them.
Delegate clear roles: research/library selection, coding, OS compatibility review, debugging.

Automation Deliverable Requirements:
Output inside active workspace under .orc/swarm/runs/[RUN_ID]/output/project/

Safety: Do not bypass approval gates. Do not install dependencies silently. No destructive commands.
""");

    // Declared last — static fields initialize in order, so all BenchXX must exist first
    public static readonly IReadOnlyList<BenchmarkDef> All = [Bench01, Bench02, Bench03, Bench04, Bench05];
}
