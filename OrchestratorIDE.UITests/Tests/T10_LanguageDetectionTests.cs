using NUnit.Framework;
using OrchestratorIDE.Agents;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T10 — Unit tests for SwarmSession.DetectTargetLanguage.
///
/// Pure logic tests — no FlaUI, no app launch, no exclusive desktop access.
/// Guards the language-lock priority fix: the requested OUTPUT artifact must
/// outrank incidental tooling mentions (a goal asking for a .cs test class
/// that *invokes* .py scripts must lock to C#, not Python).
/// </summary>
[TestFixture]
public class T10_LanguageDetectionTests
{
    // ── Output artifact outranks tooling mentions ─────────────────────────────

    [Test]
    public void CsArtifact_InvokingPyScripts_LocksToCSharp()
    {
        // CAPTURE-010 regression: the old detector saw ".py" first and locked
        // the swarm to Python, making the boss swap NUnit for Python unittest.
        var goal = "Write an NUnit test class T09_TrainingPitTests.cs that verifies: " +
                   "review_captures.py --status exits 0, phase3_preflight.py exits 1 (BLOCKED), " +
                   "and that reviewed_v1.json is valid JSON. Use Process.Start to invoke the scripts.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo("C#"));
    }

    [Test]
    public void PyArtifact_Requested_LocksToPython()
    {
        var goal = "Write a Python script diff_jsonl.py that takes two JSONL files and " +
                   "reports lines only in A, only in B, and in both.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo("Python"));
    }

    [Test]
    public void Ps1Artifact_Requested_LocksToPowerShell()
    {
        var goal = "Write a PowerShell script health_check.ps1 that verifies the local " +
                   "TheOrc dev environment and outputs a colored status table.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo("PowerShell"));
    }

    [Test]
    public void ModifyingExistingPyScript_LocksToPython()
    {
        // Modification verbs count as requesting the artifact — the file being
        // edited IS the output.
        var goal = "Add a --dry-run flag to review_captures.py that shows what would be " +
                   "approved or rejected without writing to the manifest.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo("Python"));
    }

    [Test]
    public void XamlCsFilename_LocksToCSharp()
    {
        var goal = "Update ModelWikiWindow.xaml.cs to expose the TestedAt field as a " +
                   "display property for the model list.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo("C#"));
    }

    [Test]
    public void XamlMentions_WithoutKeywords_LockToCSharp()
    {
        // CAPTURE-013 shape: .xaml files mentioned, no "WPF"/"C#" keyword needed.
        var goal = "Add a dark mode toggle that switches the ResourceDictionary theme " +
                   "between Light.xaml and Dark.xaml and persists the choice.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo("C#"));
    }

    // ── Ambiguity and fallback behavior ───────────────────────────────────────

    [Test]
    public void MixedLanguageMentions_WithoutRequestedArtifact_YieldNoLock()
    {
        // No creation verb near either file and no language keywords — the
        // detector must not guess.
        var goal = "Compare how diff_jsonl.py and DatasetCapture.cs handle malformed input.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo(""));
    }

    [Test]
    public void KeywordFallback_StillDetectsPython()
    {
        // No filenames at all — pass 2 keyword scan must keep working.
        var goal = "Build a small REST API with Flask that returns model metadata.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo("Python"));
    }

    [Test]
    public void KeywordFallback_StillDetectsCSharpFromWpf()
    {
        var goal = "Add a collapsible sidebar panel to the WPF shell showing recent runs.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo("C#"));
    }

    [Test]
    public void NoLanguageSignal_YieldsNoLock()
    {
        Assert.That(SwarmSession.DetectTargetLanguage("Make the app better."), Is.EqualTo(""));
    }

    [Test]
    public void DocsAndDataFiles_AreNotLockSignals()
    {
        // .md / .json extensions carry no language lock.
        var goal = "Update INSTALLATION.md and regenerate config.json with current defaults.";

        Assert.That(SwarmSession.DetectTargetLanguage(goal), Is.EqualTo(""));
    }
}
