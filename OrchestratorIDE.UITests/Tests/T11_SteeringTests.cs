using NUnit.Framework;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Services.ToolCalls;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T11 — Steering test suite (v1.2 roadmap item).
///
/// Pure logic tests — no FlaUI, no app launch, no Ollama, no profile store on
/// disk. Verifies that GOBLIN MIND capability profiles steer task routing the
/// way SwarmSession promises: unprobed models are trusted, deficient primaries
/// yield to fallbacks, failures are reported precisely, and the capability map
/// injected into the boss prompt tells the truth.
/// </summary>
[TestFixture]
public class T11_SteeringTests
{
    // ── Map fabrication helpers ───────────────────────────────────────────────

    private static CategoryBoundaryMap MapWhere(params (CategoryId cat, CategoryResult result)[] scores)
        => new(
            scores.ToDictionary(
                s => s.cat.ToString(),
                s => new CategoryScore(s.result, s.result == CategoryResult.Pass ? 2 : s.result == CategoryResult.Partial ? 1 : 0, 2)),
            DateTime.UtcNow);

    /// <summary>A map that passes every category a role could require.</summary>
    private static CategoryBoundaryMap FullyCapable()
        => MapWhere([.. Enum.GetValues<CategoryId>().Select(c => (c, CategoryResult.Pass))]);

    private static Func<string, CategoryBoundaryMap?> Lookup(
        params (string model, CategoryBoundaryMap? map)[] entries)
        => id => entries.FirstOrDefault(e => e.model == id).map;

    // ── Unprobed models are assumed capable ───────────────────────────────────

    [Test]
    public void UnprobedPrimary_IsTrusted()
    {
        var d = SwarmSteering.SelectModel(
            SwarmWorkerRole.Coder, "coder-model", "boss-model",
            Lookup(("coder-model", null)));

        Assert.Multiple(() =>
        {
            Assert.That(d.Model, Is.EqualTo("coder-model"));
            Assert.That(d.UsedFallback, Is.False);
            Assert.That(d.PrimaryMissing, Is.Empty);
        });
    }

    // ── Capable primary keeps the task ────────────────────────────────────────

    [Test]
    public void CapablePrimary_KeepsTask()
    {
        var d = SwarmSteering.SelectModel(
            SwarmWorkerRole.Coder, "coder-model", "boss-model",
            Lookup(("coder-model", FullyCapable())));

        Assert.That(d.Model, Is.EqualTo("coder-model"));
        Assert.That(d.UsedFallback, Is.False);
    }

    [Test]
    public void PartialResult_StillCountsAsCapable()
    {
        // CanHandle treats Partial as usable — only Fail disqualifies.
        var map = MapWhere(
            (CategoryId.FileOps,  CategoryResult.Partial),
            (CategoryId.CodeExec, CategoryResult.Partial));

        var d = SwarmSteering.SelectModel(
            SwarmWorkerRole.Coder, "coder-model", "boss-model",
            Lookup(("coder-model", map)));

        Assert.That(d.UsedFallback, Is.False);
    }

    // ── Deficient primary falls back ──────────────────────────────────────────

    [Test]
    public void DeficientPrimary_FallsBack_AndReportsMissingCategories()
    {
        var map = MapWhere(
            (CategoryId.FileOps,  CategoryResult.Fail),
            (CategoryId.CodeExec, CategoryResult.Pass));

        var d = SwarmSteering.SelectModel(
            SwarmWorkerRole.Coder, "coder-model", "boss-model",
            Lookup(("coder-model", map), ("boss-model", FullyCapable())));

        Assert.Multiple(() =>
        {
            Assert.That(d.Model, Is.EqualTo("boss-model"));
            Assert.That(d.UsedFallback, Is.True);
            Assert.That(d.PrimaryMissing, Is.EqualTo(new[] { "FileOps" }));
            Assert.That(d.FallbackMissing, Is.Empty);
        });
    }

    [Test]
    public void MissingCategory_AbsentFromMap_DisqualifiesPrimary()
    {
        // A category with no entry at all (not probed for it) counts as missing.
        var map = MapWhere((CategoryId.FileOps, CategoryResult.Pass)); // no CodeExec entry

        var d = SwarmSteering.SelectModel(
            SwarmWorkerRole.Coder, "coder-model", "boss-model",
            Lookup(("coder-model", map)));

        Assert.That(d.UsedFallback, Is.True);
        Assert.That(d.PrimaryMissing, Is.EqualTo(new[] { "CodeExec" }));
    }

    [Test]
    public void DeficientFallback_IsStillUsed_ButReported()
    {
        // The swarm must run even when both options are weak: fall back anyway,
        // report both deficiency lists so the session can warn the operator.
        var weakPrimary  = MapWhere((CategoryId.FileOps, CategoryResult.Fail));
        var weakFallback = MapWhere((CategoryId.CodeExec, CategoryResult.Fail));

        var d = SwarmSteering.SelectModel(
            SwarmWorkerRole.Coder, "coder-model", "boss-model",
            Lookup(("coder-model", weakPrimary), ("boss-model", weakFallback)));

        Assert.Multiple(() =>
        {
            Assert.That(d.Model, Is.EqualTo("boss-model"));
            Assert.That(d.PrimaryMissing, Is.Not.Empty);
            Assert.That(d.FallbackMissing, Does.Contain("CodeExec"));
        });
    }

    // ── Role → required-category contract ─────────────────────────────────────

    [TestCase(SwarmWorkerRole.Coder,       new[] { CategoryId.FileOps, CategoryId.CodeExec })]
    [TestCase(SwarmWorkerRole.UIDeveloper, new[] { CategoryId.FileOps, CategoryId.CodeExec })]
    [TestCase(SwarmWorkerRole.Researcher,  new[] { CategoryId.Network, CategoryId.DataTransform })]
    [TestCase(SwarmWorkerRole.Tester,      new[] { CategoryId.CodeExec, CategoryId.SystemInspect })]
    public void RoleRequirements_MatchSpec(SwarmWorkerRole role, CategoryId[] expected)
        => Assert.That(SwarmSteering.RequiredCategories(role), Is.EqualTo(expected));

    [Test]
    public void TesterRole_DoesNotRequireFileOps()
    {
        // TESTER is a no-write lane: it must never be routed based on FileOps.
        Assert.That(SwarmSteering.RequiredCategories(SwarmWorkerRole.Tester),
                    Does.Not.Contain(CategoryId.FileOps));
    }

    // ── Capability summary injected into the boss prompt ─────────────────────

    [Test]
    public void Summary_UnprofiledModel_SaysAssumeCapable()
    {
        var text = SwarmSteering.BuildCapabilitySummary(
            "boss", "coder", "coder", Lookup(), id => id);

        Assert.That(text, Does.Contain("not yet profiled (assume capable)"));
        Assert.That(text, Does.Contain("## Goblin Capability Map"));
    }

    [Test]
    public void Summary_MarksPassAndFailDistinctly()
    {
        var coderMap = MapWhere(
            (CategoryId.FileOps,  CategoryResult.Pass),
            (CategoryId.CodeExec, CategoryResult.Fail));

        var text = SwarmSteering.BuildCapabilitySummary(
            "boss", "coder", "coder",
            Lookup(("coder", coderMap)), id => id);

        Assert.That(text, Does.Contain("FileOps ✅"));
        Assert.That(text, Does.Contain("CodeExec ⚠"));
    }

    [Test]
    public void Summary_OmitsResearcherLine_WhenSameModelAsCoder()
    {
        var text = SwarmSteering.BuildCapabilitySummary(
            "boss", "coder", "coder", Lookup(), id => id);

        Assert.That(text, Does.Not.Contain("Researcher"));
    }

    [Test]
    public void Summary_IncludesResearcherLine_WhenDistinctModel()
    {
        var text = SwarmSteering.BuildCapabilitySummary(
            "boss", "coder", "researcher", Lookup(), id => id);

        Assert.That(text, Does.Contain("**Researcher** (researcher)"));
    }
}
