// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using OrchestratorIDE.Agents;

namespace OrchestratorIDE.Services.Swarm;

/// <summary>
/// Plan Quality Rubric — scores a boss decomposition plan on 6 dimensions (0–100 composite).
///
/// Dimensions and weights:
///   TaskCount         (0–20) — correct decomposition is the core capability
///   DescriptionDepth  (0–25) — empty descriptions are the primary failure mode
///   FilenamePresence  (0–15) — filenames prevent worker ambiguity
///   ApiContract       (0–15) — cross-task name consistency (Phase 1: fixed placeholder)
///   DomainAccuracy    (0–10) — tasks match the goal domain (Phase 1: fixed placeholder)
///   JsonValidity      (0–15) — parseable and schema-compliant
///
/// Phase 1 notes:
///   ApiContract and DomainAccuracy are fixed placeholders until Phase 2 wires
///   cross-task dependency analysis and embedding-based domain checking.
///
/// See: training_pit/EVAL_RUBRIC.md for full rubric specification.
/// </summary>
public static class EvalRubric
{
    /// <summary>Boss plans scoring at or above this threshold are staged as positive examples.</summary>
    public const int PositiveThreshold = 70;

    /// <summary>Boss plans scoring at or below this threshold are staged as negative examples.</summary>
    public const int NegativeThreshold = 39;

    // Matches filenames like: cleaner.py, index.html, README.md, sync_engine.py
    private static readonly Regex _filenameRe =
        new(@"[a-zA-Z0-9_\-]+\.[a-zA-Z0-9]{1,6}", RegexOptions.Compiled);

    /// <summary>
    /// Score a parsed boss plan.
    /// Call after ParseBossPlan() — pass the resulting List&lt;SwarmTask&gt;.
    /// </summary>
    public static RubricResult Score(List<SwarmTask> tasks, string userGoal)
    {
        bool jsonValid = tasks.Count > 0 && !IsFallbackPlan(tasks);

        return new RubricResult(
            TaskCount:        ScoreTaskCount(tasks.Count),
            DescriptionDepth: jsonValid ? ScoreDescriptionDepth(tasks) : 0,
            FilenamePresence: jsonValid ? ScoreFilenamePresence(tasks) : 0,
            ApiContract:      jsonValid ? 10 : 0,    // Phase 1 placeholder — manual scoring only
            DomainAccuracy:   jsonValid ? 10 : 0,    // Phase 1 placeholder — no embedding model yet
            JsonValidity:     jsonValid ? 15 : 0
        );
    }

    /// <summary>
    /// Detect the primary failure mode from a scored plan.
    /// Returns null when the plan is passing quality (positive or marginal).
    /// </summary>
    public static string? DetectFailureMode(List<SwarmTask> tasks, RubricResult score)
    {
        if (score.JsonValidity == 0)                                        return "json_invalid";
        if (IsFallbackPlan(tasks))                                          return "single_empty_task_collapse";
        if (tasks.Any(t => string.IsNullOrWhiteSpace(t.Description)))      return "single_empty_task_collapse";
        if (tasks.Count >= 5)                                               return "over_decomposition";
        return null;
    }

    // ── Private scoring helpers ───────────────────────────────────────────────

    private static bool IsFallbackPlan(List<SwarmTask> tasks) =>
        tasks.Count == 1 && tasks[0].Title == "Execute goal";

    private static int ScoreTaskCount(int count) => count switch
    {
        0 => 0,
        1 => 0,    // single task = classic collapse pattern
        2 => 12,
        3 => 18,
        4 => 20,
        _ => 10    // 5+ = over-decomposition
    };

    private static int ScoreDescriptionDepth(List<SwarmTask> tasks)
    {
        if (tasks.Any(t => string.IsNullOrWhiteSpace(t.Description)))   return 0;
        if (tasks.Any(t => t.Description.Trim().Length < 10))           return 2;

        // Count sentences by common terminators
        static int Sentences(string s) =>
            s.Split([". ", ".\n", "! ", "? "], StringSplitOptions.None).Length;

        // 25: all tasks ≥ 80 chars AND ≥ 2 sentences
        if (tasks.All(t => t.Description.Trim().Length >= 80 && Sentences(t.Description) >= 2))
            return 25;

        // 18: all tasks ≥ 40 chars
        if (tasks.All(t => t.Description.Trim().Length >= 40))
            return 18;

        // 10: at least 50% of tasks ≥ 40 chars
        int halfMed = tasks.Count(t => t.Description.Trim().Length >= 40);
        if (halfMed * 2 >= tasks.Count)
            return 10;

        return 5;
    }

    private static int ScoreFilenamePresence(List<SwarmTask> tasks)
    {
        if (tasks.Count == 0) return 0;
        int withFilename = tasks.Count(t => _filenameRe.IsMatch(t.Title ?? ""));
        double ratio = (double)withFilename / tasks.Count;
        return ratio switch
        {
            >= 1.0  => 15,
            >= 0.75 => 10,
            >= 0.5  => 6,
            _       => 0
        };
    }
}

/// <summary>
/// Immutable result of a Plan Quality Rubric scoring pass.
/// </summary>
public record RubricResult(
    int TaskCount,
    int DescriptionDepth,
    int FilenamePresence,
    int ApiContract,
    int DomainAccuracy,
    int JsonValidity)
{
    /// <summary>Composite score (0–100).</summary>
    public int Composite =>
        TaskCount + DescriptionDepth + FilenamePresence + ApiContract + DomainAccuracy + JsonValidity;

    /// <summary>Dataset example class derived from composite score.</summary>
    public string ExampleClass => Composite switch
    {
        >= EvalRubric.PositiveThreshold => "positive",
        >= 40                           => "marginal",
        _                               => "negative"
    };
}
