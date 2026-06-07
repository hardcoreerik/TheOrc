namespace BenchmarkRunner.Models;

public class TestRun
{
    public string    Id           { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime  StartedAt    { get; set; } = DateTime.Now;
    public string    BenchmarkId  { get; set; } = "";
    public string    BenchmarkName { get; set; } = "";
    public string    BossModel        { get; set; } = "";
    public string    WorkerModel      { get; set; } = "";  // Coder + UIDev
    public string    ResearcherModel  { get; set; } = "";  // Researcher (may differ)
    public int       Slots        { get; set; } = 3;
    public string    TrustLevel   { get; set; } = "Standard";
    public string    Workspace    { get; set; } = "";
    public ResultCheck? ScanResult { get; set; }
    public int?      ManualScore  { get; set; }
    public string    Notes        { get; set; } = "";

    public int DisplayScore => ManualScore ?? ScanResult?.AutoScore ?? 0;

    public string Status => ScanResult is null ? "Pending Scan"
        : DisplayScore >= 80 ? "PASS"
        : DisplayScore >= 50 ? "PARTIAL"
        : "FAIL";
}

public class ResultCheck
{
    // Delegation (25 pts)
    public bool PlanJsonFound    { get; set; }
    public bool AgentFilesFound  { get; set; }
    public bool RolesDistinct    { get; set; }
    public int  AgentCount       { get; set; }

    // Project completeness (30 pts)
    public bool ReadmeFound      { get; set; }
    public bool TestPlanFound    { get; set; }
    public bool ImplNotesFound   { get; set; }
    public bool SampleDataFound  { get; set; }
    public bool SourceFilesFound { get; set; }

    // Reliability (25 pts)
    public bool FinalReportFound  { get; set; }
    public bool SwarmRunJsonFound { get; set; }
    public bool NoParseErrors     { get; set; }
    public bool TraceJsonlFound   { get; set; }
    public int  TraceLineCount    { get; set; }

    // Usability (20 pts)
    public bool RunInstructionsInReadme { get; set; }
    public bool TestInstructionsInPlan  { get; set; }

    public string RunDir         { get; set; } = "";
    public string OutputDir      { get; set; } = "";
    public List<string> FoundFiles { get; set; } = [];

    public int AutoScore
    {
        get
        {
            int score = 0;
            // Delegation — 25pts
            if (PlanJsonFound)    score += 5;
            if (AgentFilesFound)  score += 5;
            if (RolesDistinct)    score += 5;
            score += AgentCount > 0 ? 5 : 0;
            if (FinalReportFound) score += 5;
            // Completeness — 30pts
            if (ReadmeFound)      score += 5;
            if (TestPlanFound)    score += 5;
            if (ImplNotesFound)   score += 5;
            if (SampleDataFound)  score += 5;
            if (SourceFilesFound) score += 10;
            // Reliability — 25pts
            if (SwarmRunJsonFound) score += 5;
            if (FinalReportFound)  score += 5;
            if (NoParseErrors)     score += 10;
            score += 5; // no approval bypass (assume true — can't check automatically)
            // TraceJsonlFound is informational only — not scored
            // Usability — 20pts
            if (RunInstructionsInReadme) score += 10;
            if (TestInstructionsInPlan)  score += 10;
            return Math.Min(score, 100);
        }
    }
}
