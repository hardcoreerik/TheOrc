using System.IO;
using System.Text.Json;
using BenchmarkRunner.Models;

namespace BenchmarkRunner.Core;

public static class ResultScanner
{
    /// <summary>
    /// Scans workspace/.orc/swarm/runs/ for the most recent run and scores it.
    /// </summary>
    public static ResultCheck Scan(string workspaceRoot)
    {
        var result = new ResultCheck { NoParseErrors = true };

        var swarmDir = Path.Combine(workspaceRoot, ".orc", "swarm", "runs");
        if (!Directory.Exists(swarmDir))
            return result;

        // Find most recent run folder
        var runDirs = Directory.GetDirectories(swarmDir)
                               .OrderByDescending(d => Directory.GetCreationTime(d))
                               .ToList();
        if (runDirs.Count == 0) return result;

        var runDir = runDirs[0];
        result.RunDir = runDir;

        // ── Check top-level run files ─────────────────────────────────────────

        var planJson     = Path.Combine(runDir, "plan.json");
        var swarmRunJson = Path.Combine(runDir, "swarm_run.json");
        var finalReport  = Path.Combine(runDir, "final_report.md");

        result.PlanJsonFound     = File.Exists(planJson);
        result.SwarmRunJsonFound = File.Exists(swarmRunJson);
        result.FinalReportFound  = File.Exists(finalReport);

        var traceJsonl = Path.Combine(runDir, "trace.jsonl");
        result.TraceJsonlFound = File.Exists(traceJsonl);
        if (result.TraceJsonlFound)
        {
            try
            {
                var lineCount = File.ReadLines(traceJsonl).Count();
                result.TraceLineCount = lineCount;
            }
            catch { }
        }

        // Track all found files for display
        foreach (var f in Directory.GetFiles(runDir))
            result.FoundFiles.Add(Path.GetFileName(f));

        // ── Check agent files ─────────────────────────────────────────────────

        var agentsDir    = Path.Combine(runDir, "agents");
        var agentFiles   = Directory.Exists(agentsDir)
            ? Directory.GetFiles(agentsDir, "agent_*")
            : [];

        result.AgentFilesFound = agentFiles.Length > 0;
        result.AgentCount      = agentFiles.Count(f => f.EndsWith("_task.md"));
        result.RolesDistinct   = result.AgentCount >= 2;

        // ── Check output project files ────────────────────────────────────────

        var outputDir = Path.Combine(runDir, "output", "project");
        result.OutputDir = outputDir;

        if (Directory.Exists(outputDir))
        {
            var allFiles = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories)
                                    .Select(f => f.Replace(outputDir, "").TrimStart('\\', '/'))
                                    .ToList();

            result.ReadmeFound     = allFiles.Any(f => f.Equals("README.md", StringComparison.OrdinalIgnoreCase));
            result.TestPlanFound   = allFiles.Any(f => f.Equals("TEST_PLAN.md", StringComparison.OrdinalIgnoreCase));
            result.ImplNotesFound  = allFiles.Any(f => f.Equals("IMPLEMENTATION_NOTES.md", StringComparison.OrdinalIgnoreCase));
            result.SampleDataFound = allFiles.Any(f => f.StartsWith("sample_data/", StringComparison.OrdinalIgnoreCase)
                                                    || f.StartsWith("sample_data\\", StringComparison.OrdinalIgnoreCase));

            var sourceExtensions = new[] { ".py", ".js", ".ts", ".cs", ".go", ".rb", ".java", ".cpp", ".c", ".rs" };
            result.SourceFilesFound = allFiles.Any(f =>
                sourceExtensions.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

            // Check README for run instructions
            var readmePath = Path.Combine(outputDir, "README.md");
            if (result.ReadmeFound && File.Exists(readmePath))
            {
                var readmeText = File.ReadAllText(readmePath).ToLowerInvariant();
                result.RunInstructionsInReadme = readmeText.Contains("how to run") ||
                                                 readmeText.Contains("running") ||
                                                 readmeText.Contains("usage") ||
                                                 readmeText.Contains("install");
            }

            // Check TEST_PLAN for test instructions
            var testPlanPath = Path.Combine(outputDir, "TEST_PLAN.md");
            if (result.TestPlanFound && File.Exists(testPlanPath))
            {
                var testText = File.ReadAllText(testPlanPath).ToLowerInvariant();
                result.TestInstructionsInPlan = testText.Contains("test") && testText.Length > 200;
            }

            result.FoundFiles.AddRange(allFiles.Take(30));
        }

        // ── Validate plan.json parses ─────────────────────────────────────────

        if (result.PlanJsonFound)
        {
            try { JsonDocument.Parse(File.ReadAllText(planJson)); }
            catch { result.NoParseErrors = false; }
        }

        return result;
    }

    /// <summary>Returns a list of (label, found) pairs for display in the UI.</summary>
    public static IEnumerable<(string Label, bool Found, int Points)> CheckList(ResultCheck r) =>
    [
        ("plan.json",              r.PlanJsonFound,     5),
        ("swarm_run.json",         r.SwarmRunJsonFound, 5),
        ("final_report.md",        r.FinalReportFound,  5),
        ($"trace.jsonl ({r.TraceLineCount} events — info only)", r.TraceJsonlFound, 0),
        ("agent task files",       r.AgentFilesFound,   5),
        ($"≥2 distinct agents ({r.AgentCount} found)", r.RolesDistinct, 5),
        ("README.md",              r.ReadmeFound,       5),
        ("TEST_PLAN.md",           r.TestPlanFound,     5),
        ("IMPLEMENTATION_NOTES.md",r.ImplNotesFound,    5),
        ("sample_data/ folder",    r.SampleDataFound,   5),
        ("source code files",      r.SourceFilesFound,  10),
        ("run instructions in README", r.RunInstructionsInReadme, 10),
        ("test steps in TEST_PLAN",    r.TestInstructionsInPlan,  10),
        ("JSON files parse cleanly",   r.NoParseErrors,           10),
    ];
}
