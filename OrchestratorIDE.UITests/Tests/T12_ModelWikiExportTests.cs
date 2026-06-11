using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T12 — Model Wiki capability-matrix Markdown export (v1.4 roadmap item).
/// Pure logic tests — ModelWikiExporter.ToMarkdown is IO-free by design.
/// </summary>
[TestFixture]
public class T12_ModelWikiExportTests
{
    private static ModelWikiEntry Entry(string id, bool installed = true)
        => new()
        {
            ModelId     = id,
            DisplayName = id.ToUpperInvariant(),
            IsInstalled = installed,
            Profile     = ModelProfiles.Get(id),
        };

    [Test]
    public void Export_ContainsHeaderAndBothTables()
    {
        var md = ModelWikiExporter.ToMarkdown([Entry("qwen2.5-coder:14b")]);

        Assert.Multiple(() =>
        {
            Assert.That(md, Does.StartWith("# TheOrc — Model Capability Matrix"));
            Assert.That(md, Does.Contain("## Capability scores"));
            Assert.That(md, Does.Contain("## Routing recommendations"));
            Assert.That(md, Does.Contain("`qwen2.5-coder:14b`"));
        });
    }

    [Test]
    public void Export_MarksInstalledState()
    {
        var md = ModelWikiExporter.ToMarkdown(
            [Entry("model-a", installed: true), Entry("model-b", installed: false)]);

        var rowA = md.Split('\n').First(l => l.Contains("`model-a`"));
        var rowB = md.Split('\n').First(l => l.Contains("`model-b`") && l.Contains("|"));

        Assert.That(rowA, Does.Contain("✅"));
        Assert.That(rowB.Split('|')[2].Trim(), Is.EqualTo("—"));
    }

    [Test]
    public void Export_UnprobedModel_ShowsDashesNotCrash()
    {
        var md = ModelWikiExporter.ToMarkdown([Entry("never-probed-model")]);

        // No ProbeProfile set — dispatch/format/categories columns degrade to —
        var row = md.Split('\n').First(l => l.Contains("`never-probed-model`") &&
                                            l.Contains("| —"));
        Assert.That(row, Is.Not.Empty);
    }

    [Test]
    public void Export_PipeInDisplayName_IsEscaped()
    {
        var e = Entry("weird-model");
        e.DisplayName = "Weird | Model";

        var md = ModelWikiExporter.ToMarkdown([e]);

        Assert.That(md, Does.Contain("Weird \\| Model"));
    }

    [Test]
    public void Export_IsDeterministic_ForFixedTimestamp()
    {
        var when = new DateTime(2026, 6, 11, 8, 0, 0);
        var a = ModelWikiExporter.ToMarkdown([Entry("model-a")], when);
        var b = ModelWikiExporter.ToMarkdown([Entry("model-a")], when);

        Assert.That(a, Is.EqualTo(b));
    }
}
