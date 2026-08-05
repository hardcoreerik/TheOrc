// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Real curated-catalog data (CuratedModelCatalog.All is loaded from the actual
/// Resources/curated-models.json shipped with the app, not a fixture) + a faked local install
/// + a faked ModelWikiEntry, proving ModelDepotBrowserService.BuildBrowseList actually fuses
/// all three sources rather than just passing one through.
/// </summary>
[TestFixture]
public sealed class ModelDepotBrowserServiceTests
{
    // A real entry from the shipped curated catalog -- chosen because it has a real OllamaName,
    // the join key BuildBrowseList uses to match local install state and evidence.
    private const string RealCuratedOllamaTag = "qwen2.5-coder:1.5b";

    [Test]
    public void BuildBrowseList_IncludesFullCuratedCatalog()
    {
        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [], installedOllamaModels: []);

        Assert.That(cards.Count, Is.GreaterThanOrEqualTo(CuratedModelCatalog.All.Count),
            "every curated entry must produce at least one card");
        Assert.That(cards.All(c => c.InstallState == ModelDepotInstallState.NotInstalled), Is.True,
            "no local assets were passed in, so nothing should be marked installed");
    }

    [Test]
    public void BuildBrowseList_MarksCuratedEntryInstalled_WhenLocalAssetMatchesOllamaTag()
    {
        var localAsset = new RuntimeModelAsset(
            Id: "local-1", Kind: RuntimeAssetKind.BaseModelGguf, Path: "does-not-exist.gguf",
            DisplayName: RealCuratedOllamaTag, SizeBytes: 900_000_000,
            LastModifiedUtc: DateTimeOffset.UtcNow, SuggestedRoles: [RuntimeRole.Worker]);

        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [localAsset],
            installedOllamaModels: [RealCuratedOllamaTag]);

        var card = cards.SingleOrDefault(c => c.Result.OllamaName == RealCuratedOllamaTag);

        Assert.That(card, Is.Not.Null, "the curated entry for this real ollama tag must exist");
        Assert.Multiple(() =>
        {
            Assert.That(card!.InstallState, Is.EqualTo(ModelDepotInstallState.InstalledLocally));
            Assert.That(card.LocalSizeBytes, Is.EqualTo(900_000_000));
            Assert.That(card.InstallBadge, Does.Contain("Installed"));
            // ModelWikiService.BuildAll(installedOllamaModels) is real, not faked -- it should
            // have produced a real entry for a model we told it is installed, even with no
            // capability-test/probe history (an entry with only ModelProfiles defaults).
            Assert.That(card.HasEvidence, Is.True,
                "an installed, catalog-known model must get a wiki entry, even a mostly-empty one");
        });
    }

    [Test]
    public void BuildBrowseList_SurfacesLocalOnlyModel_NotInCuratedCatalog()
    {
        const string localOnlyTag = "totally-uncurated-local-model:9b";
        var localAsset = new RuntimeModelAsset(
            Id: "local-2", Kind: RuntimeAssetKind.BaseModelGguf, Path: "does-not-exist.gguf",
            DisplayName: localOnlyTag, SizeBytes: 5_000_000_000,
            LastModifiedUtc: DateTimeOffset.UtcNow, SuggestedRoles: []);

        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [localAsset],
            installedOllamaModels: [localOnlyTag]);

        var card = cards.SingleOrDefault(c => c.DisplayName == localOnlyTag);

        Assert.That(card, Is.Not.Null,
            "a local model with no curated entry must still get a card, not be silently hidden");
        Assert.Multiple(() =>
        {
            Assert.That(card!.InstallState, Is.EqualTo(ModelDepotInstallState.InstalledLocally));
            Assert.That(card.Curated, Is.Null, "no curated entry exists for this model");
            Assert.That(card.SourceBadge, Does.Contain("Community"),
                "an uncurated result must not claim to be Verified");
        });
    }

    [Test]
    public void BuildBrowseList_LocalOnlyModel_DerivesPublisherQuantAndVramFromTagAndSize()
    {
        // A real-shaped HF-cache-via-Ollama tag: "hf.co/<publisher>/<repo>:<quant>".
        const string tag = "hf.co/bartowski/Some-Model-GGUF:Q4_K_M";
        var localAsset = new RuntimeModelAsset(
            Id: "local-3", Kind: RuntimeAssetKind.BaseModelGguf, Path: "does-not-exist.gguf",
            DisplayName: tag, SizeBytes: 4_294_967_296L, // exactly 4 GiB
            LastModifiedUtc: DateTimeOffset.UtcNow, SuggestedRoles: []);

        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [localAsset],
            installedOllamaModels: [tag]);

        var card = cards.Single(c => c.DisplayName == tag);

        Assert.Multiple(() =>
        {
            Assert.That(card.Publisher, Is.EqualTo("bartowski"),
                "publisher must be derived from the hf.co/<publisher>/<repo> tag shape");
            Assert.That(card.Result.Description, Does.Contain("Local model"),
                "a local-only card must not render a blank/empty description");
            Assert.That(card.VramDisplay, Does.Not.Contain("Unknown"),
                "file size must produce a real VRAM estimate instead of the 'Unknown' fallback");
        });
    }

    [Test]
    public void BuildBrowseList_LocalOnlyModel_WithNoNamespaceTag_LeavesPublisherBlankNotUnknown()
    {
        const string tag = "gemma4:12b";
        var localAsset = new RuntimeModelAsset(
            Id: "local-4", Kind: RuntimeAssetKind.BaseModelGguf, Path: "does-not-exist.gguf",
            DisplayName: tag, SizeBytes: 8_000_000_000,
            LastModifiedUtc: DateTimeOffset.UtcNow, SuggestedRoles: []);

        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [localAsset],
            installedOllamaModels: [tag]);

        var card = cards.Single(c => c.DisplayName == tag);

        Assert.That(card.Publisher, Is.Empty,
            "a bare 'name:tag' pull genuinely carries no publisher -- an honest blank beats a fabricated guess");
    }

    [Test]
    public void AttachHiveAvailability_MatchesExactOllamaTag_AcrossMultiplePeers()
    {
        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [], installedOllamaModels: []);
        var target = cards.First(c => c.Result.OllamaName == RealCuratedOllamaTag);

        ModelDepotBrowserService.AttachHiveAvailability(cards,
        [
            ("HARDCOREPC", [RealCuratedOllamaTag, "some-other-model:7b"]),
            ("MSI-LAPTOP", ["totally-unrelated:1b"]),
            ("HARDCOREPI", [RealCuratedOllamaTag]),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(target.IsAvailableOnHive, Is.True);
            Assert.That(target.AvailableOnHivePeers, Is.EquivalentTo(new[] { "HARDCOREPC", "HARDCOREPI" }));
            Assert.That(target.HiveAvailabilityDisplay, Does.Contain("HARDCOREPC"));
        });

        var untouched = cards.First(c => c.Result.OllamaName != RealCuratedOllamaTag
            && !string.IsNullOrEmpty(c.Result.OllamaName));
        Assert.That(untouched.IsAvailableOnHive, Is.False,
            "a card whose tag no peer reported must not be marked available");
    }

    [Test]
    public void AttachHiveAvailability_MatchesSameBaseModelDifferentTag_ButNotUnrelatedModels()
    {
        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [], installedOllamaModels: []);
        var target = cards.First(c => c.Result.OllamaName == RealCuratedOllamaTag); // "qwen2.5-coder:1.5b"
        var baseName = RealCuratedOllamaTag.Split(':')[0];

        ModelDepotBrowserService.AttachHiveAvailability(cards,
        [
            // Same base model, different tag -- counts as available in spirit (loosest intended
            // fallback), NOT a substring/fuzzy match: "llama3.1:8b" below must not match at all.
            ("SAME-BASE-PEER", [$"{baseName}:32b"]),
            ("UNRELATED-PEER", ["llama3.1:8b"]),
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(target.IsAvailableOnHive, Is.True);
            Assert.That(target.AvailableOnHivePeers, Does.Contain("SAME-BASE-PEER"));
            Assert.That(target.AvailableOnHivePeers, Does.Not.Contain("UNRELATED-PEER"));
        });
    }

    [Test]
    public void Filter_InstalledOnly_ExcludesNotInstalledCards()
    {
        var localAsset = new RuntimeModelAsset(
            Id: "local-1", Kind: RuntimeAssetKind.BaseModelGguf, Path: "does-not-exist.gguf",
            DisplayName: RealCuratedOllamaTag, SizeBytes: 900_000_000,
            LastModifiedUtc: DateTimeOffset.UtcNow, SuggestedRoles: []);
        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [localAsset],
            installedOllamaModels: [RealCuratedOllamaTag]);

        var filtered = ModelDepotBrowserService.Filter(cards, installedOnly: true).ToList();

        Assert.That(filtered, Is.Not.Empty);
        Assert.That(filtered.All(c => c.InstallState == ModelDepotInstallState.InstalledLocally), Is.True);
        Assert.That(filtered.Count, Is.LessThan(cards.Count),
            "the full catalog has more than just this one installed entry, so filtering must narrow it");
    }

    [Test]
    public void Filter_SearchText_MatchesNameOrPublisherOrTag()
    {
        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [], installedOllamaModels: []);

        var byName = ModelDepotBrowserService.Filter(cards, searchText: "Qwen").ToList();

        Assert.That(byName, Is.Not.Empty, "the real curated catalog has multiple Qwen entries");
        Assert.That(byName.All(c =>
            c.DisplayName.Contains("Qwen", StringComparison.OrdinalIgnoreCase) ||
            c.Publisher.Contains("Qwen", StringComparison.OrdinalIgnoreCase) ||
            c.Tags.Any(t => t.Contains("Qwen", StringComparison.OrdinalIgnoreCase))), Is.True);
    }

    [Test]
    public void Filter_MaxVramGb_ExcludesModelsRequiringMoreVram()
    {
        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [], installedOllamaModels: []);

        var lowVram = ModelDepotBrowserService.Filter(cards, maxVramGb: 4).ToList();

        Assert.That(lowVram, Is.Not.Empty);
        Assert.That(lowVram.All(c => c.Result.VramMinGb == 0 || c.Result.VramMinGb <= 4), Is.True);
        Assert.That(lowVram.Count, Is.LessThan(cards.Count),
            "the real curated catalog includes models requiring more than 4 GB");
    }

    [Test]
    public async Task EnrichWithQuantVariantsAsync_SkipsCardsWithNoHuggingFaceId_WithoutThrowing()
    {
        // Deterministic, no live network call: a card with no HuggingFaceId (HasHuggingFace
        // false) must be skipped entirely, not attempted -- GetVariantsAsync itself already
        // short-circuits to an empty list for this case, but this proves the enrichment loop's
        // own HasHuggingFace filter does too, so it never even makes the call.
        var cardWithNoHf = new ModelDepotCardEntry
        {
            Result = new ModelSearchResult { Id = "no-hf-id", Name = "No HF Repo" },
        };
        using var searchService = new ModelSearchService();

        Assert.DoesNotThrowAsync(async () =>
            await ModelDepotBrowserService.EnrichWithQuantVariantsAsync(
                [cardWithNoHf], searchService, ct: CancellationToken.None));

        Assert.That(cardWithNoHf.Quants, Is.Empty);
    }

    [Test]
    public void DistinctTags_ReturnsSortedUniqueTags_FromRealCuratedCatalog()
    {
        var cards = ModelDepotBrowserService.BuildBrowseList(
            ModelSearchService.BrowseCurated(), localAssets: [], installedOllamaModels: []);

        var tags = ModelDepotBrowserService.DistinctTags(cards);

        Assert.That(tags, Is.Not.Empty);
        Assert.That(tags.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(tags.Count),
            "must not contain duplicates");
        Assert.That(tags, Is.Ordered.Using<string>(StringComparer.OrdinalIgnoreCase));
    }
}
