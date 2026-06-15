// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Serialization;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.ToolCalls;
using OrchestratorIDE.Services.Swarm;

namespace OrchestratorIDE.Services.Models;

// ── Observation / test result records ────────────────────────────────────────

/// <summary>
/// A single recorded observation about a model, sourced from:
///   "built_in"           — baked into model-wiki-observations.json
///   "local_system_test"  — T06/T07-class FlaUI system tests
///   "local_probe"        — GOBLIN MIND category/format probe run
///   "user_run_test"      — user launched from "Run Model Capability Test…"
/// </summary>
public class ModelObservation
{
    [JsonPropertyName("ModelId")]   public string ModelId   { get; set; } = "";
    [JsonPropertyName("Source")]    public string Source    { get; set; } = "";
    [JsonPropertyName("TestId")]    public string TestId    { get; set; } = "";
    [JsonPropertyName("Date")]      public string Date      { get; set; } = "";
    [JsonPropertyName("Result")]    public string Result    { get; set; } = "";  // pass / fail / partial / not_tested / observed
    [JsonPropertyName("Summary")]   public string Summary   { get; set; } = "";
    [JsonPropertyName("Classification")] public string Classification { get; set; } = "";
    [JsonPropertyName("RecommendedUses")]    public List<string> RecommendedUses    { get; set; } = [];
    [JsonPropertyName("NotRecommendedUses")] public List<string> NotRecommendedUses { get; set; } = [];
    [JsonPropertyName("Confidence")] public string Confidence { get; set; } = "";

    /// <summary>Friendly label for the source.</summary>
    [JsonIgnore]
    public string SourceLabel => Source switch
    {
        "built_in"          => "Built-in profile",
        "local_system_test" => "Local system test",
        "local_probe"       => "Local GOBLIN MIND probe",
        "user_run_test"     => "User capability test",
        "local_observation" => "Local observation",
        _                   => Source,
    };

    /// <summary>CSS-style hex color for the result badge.</summary>
    [JsonIgnore]
    public string ResultColor => Result switch
    {
        "pass"      => "#4ACA4A",
        "partial"   => "#E8A030",
        "failed"    => "#E84040",
        "fail"      => "#E84040",
        "not_tested"=> "#888888",
        "observed"  => "#4A9AE8",
        _           => "#888888",
    };
}

/// <summary>
/// Result of a user-run model capability test (FileWriteSmall/Medium/Large).
/// Stored in %APPDATA%\OrchestratorIDE\model-wiki\results.jsonl.
/// </summary>
public class ModelCapabilityTestResult
{
    public string   ModelId          { get; set; } = "";
    public string   TestId           { get; set; } = "";  // FileWriteSmall / FileWriteMedium / FileWriteLarge
    public string   TestName         { get; set; } = "";
    public DateTime Timestamp        { get; set; }
    public string   Result           { get; set; } = "";  // pass / fail / partial
    public bool     FileWritten      { get; set; }
    public string   ExpectedFile     { get; set; } = "";
    public int      ActualFileSizeBytes { get; set; }
    public bool     ValidJson        { get; set; }
    public bool     Truncated        { get; set; }
    public int      OpenBraceCount   { get; set; }
    public int      CloseBraceCount  { get; set; }
    public string   Notes            { get; set; } = "";

    [JsonIgnore]
    public string ResultColor => Result switch
    {
        "pass"    => "#4ACA4A",
        "partial" => "#E8A030",
        "fail"    => "#E84040",
        _         => "#888888",
    };
}

// ── Merged wiki entry ─────────────────────────────────────────────────────────

/// <summary>
/// Merged view of a model from all available data sources:
///   ModelProfile        — built-in scores, capabilities, descriptions
///   ToolCallProfile     — GOBLIN MIND probe results (format + categories)
///   SwarmRunRecords     — historical swarm run metrics
///   ModelObservations   — system test + user-run observations
///   CapabilityResults   — FileWrite Small/Medium/Large test results
/// </summary>
public class ModelWikiEntry
{
    public string           ModelId          { get; set; } = "";
    public string           DisplayName      { get; set; } = "";
    public bool             IsInstalled      { get; set; }
    public ModelProfile     Profile          { get; set; } = null!;
    public ToolCallProfile? ProbeProfile     { get; set; }
    public List<SwarmRunRecord>           SwarmRuns         { get; set; } = [];
    public List<ModelObservation>         Observations      { get; set; } = [];
    public List<ModelCapabilityTestResult> CapabilityTests  { get; set; } = [];

    // ── Derived / computed ────────────────────────────────────────────────────

    /// <summary>Short VRAM tier label.</summary>
    public string VramLabel => Profile.MinVramGb switch
    {
        <= 4  => "4 GB",
        <= 6  => "6 GB",
        <= 8  => "8 GB",
        <= 12 => "12 GB",
        <= 16 => "16 GB",
        <= 24 => "24 GB",
        _     => $"{Profile.MinVramGb} GB",
    };

    /// <summary>Speed tier label with emoji.</summary>
    public string SpeedLabel => Profile.Speed switch
    {
        SpeedTier.Fast   => "⚡ Fast",
        SpeedTier.Medium => "⏱ Medium",
        SpeedTier.Slow   => "🐢 Slow",
        _                => "?",
    };

    /// <summary>Primary role recommendation based on highest score.</summary>
    public string PrimaryRole
    {
        get
        {
            int boss = Profile.BossScore, coder = Profile.CoderScore,
                res  = Profile.ResearcherScore, test = Profile.TesterScore;
            int max  = Math.Max(Math.Max(boss, coder), Math.Max(res, test));
            if (max == boss   && boss   >= 6) return "Boss";
            if (max == coder  && coder  >= 6) return "Coder";
            if (max == res    && res    >= 6) return "Researcher";
            if (max == test   && test   >= 6) return "Tester";
            return "General";
        }
    }

    /// <summary>
    /// Latest FileWriteLarge result, or null if never tested.
    /// </summary>
    public ModelCapabilityTestResult? LatestLargeWriteResult =>
        CapabilityTests
            .Where(r => r.TestId == "FileWriteLarge")
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefault();

    /// <summary>
    /// True if any observation or capability test explicitly flags this model
    /// as unreliable for long write_file payloads.
    /// </summary>
    public bool HasLongWriteWarning =>
        Observations.Any(o => o.Classification.Contains("not_recommended_for_long_write_file")) ||
        CapabilityTests.Any(r => r.TestId == "FileWriteLarge" && r.Result == "fail");
}

// ── List-pane ViewModel ───────────────────────────────────────────────────────

/// <summary>
/// Lightweight ViewModel for a row in the model list pane.
/// Wraps ModelWikiEntry with the display-formatted strings the DataTemplate needs.
/// </summary>
public class ModelWikiListItem
{
    public ModelWikiEntry Entry { get; }

    public ModelWikiListItem(ModelWikiEntry entry) => Entry = entry;

    /// <summary>Friendly display name shown in the list header row.</summary>
    public string DisplayName => Entry.DisplayName;

    /// <summary>✅ when installed, empty otherwise — shown as a right-aligned badge.</summary>
    public string InstalledBadge => Entry.IsInstalled ? "✅" : "";

    /// <summary>Brush color for the installed badge.</summary>
    public string InstalledColor => Entry.IsInstalled ? "#4ACA4A" : "Transparent";

    /// <summary>
    /// Second-row meta label: model ID · speed tier · VRAM.
    /// e.g.  "gemma4:12b · Medium · 8 GB"
    /// </summary>
    public string SubLabel
    {
        get
        {
            var speed = Entry.Profile.Speed switch
            {
                SpeedTier.Fast   => "Fast",
                SpeedTier.Medium => "Medium",
                SpeedTier.Slow   => "Slow",
                _                => "?"
            };
            return $"{Entry.ModelId} · {speed} · {Entry.VramLabel}";
        }
    }
}

// ── Routing recommendation ────────────────────────────────────────────────────

/// <summary>
/// Plain-English routing recommendation derived by ModelWikiService
/// for a specific ModelWikiEntry.
/// </summary>
public class RoutingRecommendation
{
    public string Boss          { get; set; } = "Unknown";  // "Yes" / "No" / "Limited"
    public string Coder         { get; set; } = "Unknown";
    public string Researcher    { get; set; } = "Unknown";
    public string Tester        { get; set; } = "Unknown";
    public string SingleAgent   { get; set; } = "Unknown";
    public string SwarmWorker   { get; set; } = "Unknown";
    public string LongWriteFile { get; set; } = "Unknown";
    public string Summary       { get; set; } = "";

    public string ColorFor(string val) => val switch
    {
        "Yes"     => "#4ACA4A",
        "Limited" => "#E8A030",
        "No"      => "#E84040",
        _         => "#888888",
    };
}
