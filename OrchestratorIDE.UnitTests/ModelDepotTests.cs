// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ModelDepotTests
{
    private readonly List<string> _tempRoots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for Windows file handles held briefly by test hosts.
            }
        }

        _tempRoots.Clear();
    }

    [Test]
    public void Scan_Finds_BaseGguf_And_Resolves_Role_Without_Adapter()
    {
        var root = NewTempRoot();
        var modelPath = WriteFile(root, "models", "boss-qwen.gguf");

        var depot = ModelDepot.Scan(root);
        var binding = depot.ResolveRole(RuntimeRole.Boss);

        Assert.Multiple(() =>
        {
            Assert.That(depot.Assets, Has.Count.EqualTo(1));
            Assert.That(depot.Assets[0].Kind, Is.EqualTo(RuntimeAssetKind.BaseModelGguf));
            Assert.That(depot.Assets[0].Path, Is.EqualTo(Path.GetFullPath(modelPath)));
            Assert.That(depot.Assets[0].SuggestedRoles, Does.Contain(RuntimeRole.Boss));
            Assert.That(binding, Is.Not.Null);
            Assert.That(binding!.BaseModel.Path, Is.EqualTo(Path.GetFullPath(modelPath)));
            Assert.That(binding.Adapter, Is.Null);
        });
    }

    [Test]
    public void Scan_Classifies_LoraGguf_And_Binds_Role_Adapter_To_Base()
    {
        var root = NewTempRoot();
        WriteFile(root, "models", "theorc-base.gguf");
        var adapterPath = WriteFile(root, "adapters", "reviewer-lora.gguf");

        var depot = ModelDepot.Scan(root);
        var binding = depot.ResolveRole(RuntimeRole.Reviewer);

        Assert.Multiple(() =>
        {
            Assert.That(depot.Assets.Count(a => a.Kind == RuntimeAssetKind.BaseModelGguf), Is.EqualTo(1));
            Assert.That(depot.Assets.Count(a => a.Kind == RuntimeAssetKind.LoraGguf), Is.EqualTo(1));
            Assert.That(binding, Is.Not.Null);
            Assert.That(binding!.Adapter, Is.Not.Null);
            Assert.That(binding.Adapter!.Kind, Is.EqualTo(RuntimeAssetKind.LoraGguf));
            Assert.That(binding.Adapter.Path, Is.EqualTo(Path.GetFullPath(adapterPath)));
        });
    }

    [Test]
    public void Scan_Recognizes_Peft_Adapter_Directory_And_Base_Hint()
    {
        var root = NewTempRoot();
        WriteFile(root, "models", "Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf");
        var adapterDir = Directory.CreateDirectory(Path.Combine(root, "researcher_adapter")).FullName;
        File.WriteAllText(Path.Combine(adapterDir, "adapter_config.json"),
            """{ "base_model_name_or_path": "Qwen/Qwen2.5-Coder-7B-Instruct" }""");
        File.WriteAllText(Path.Combine(adapterDir, "adapter_model.safetensors"), "fake peft weights");

        var depot = ModelDepot.Scan(root);
        var adapter = depot.Assets.Single(a => a.Kind == RuntimeAssetKind.PeftAdapterDirectory);
        var binding = depot.ResolveRole(RuntimeRole.Researcher);

        Assert.Multiple(() =>
        {
            Assert.That(adapter.Path, Is.EqualTo(Path.GetFullPath(adapterDir)));
            Assert.That(adapter.BaseModelHint, Is.EqualTo("Qwen/Qwen2.5-Coder-7B-Instruct"));
            Assert.That(adapter.SuggestedRoles, Does.Contain(RuntimeRole.Researcher));
            Assert.That(binding, Is.Not.Null);
            Assert.That(binding!.Adapter, Is.EqualTo(adapter));
        });
    }

    [Test]
    public void Scan_Marks_Incomplete_Peft_Directory_Unknown()
    {
        var root = NewTempRoot();
        var adapterDir = Directory.CreateDirectory(Path.Combine(root, "worker_adapter")).FullName;
        File.WriteAllText(Path.Combine(adapterDir, "adapter_config.json"),
            """{ "base_model_name_or_path": "local/base" }""");

        var depot = ModelDepot.Scan(root);

        Assert.Multiple(() =>
        {
            Assert.That(depot.Assets, Has.Count.EqualTo(1));
            Assert.That(depot.Assets[0].Kind, Is.EqualTo(RuntimeAssetKind.Unknown));
            Assert.That(depot.Assets[0].Path, Is.EqualTo(Path.GetFullPath(adapterDir)));
            Assert.That(depot.ResolveRole(RuntimeRole.Worker), Is.Null);
        });
    }

    [Test]
    public void Scan_Missing_Root_Returns_Empty_Depot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-missing-" + Guid.NewGuid().ToString("N"));

        var depot = ModelDepot.Scan(root);

        Assert.Multiple(() =>
        {
            Assert.That(depot.Root, Is.EqualTo(Path.GetFullPath(root)));
            Assert.That(depot.Assets, Is.Empty);
            Assert.That(depot.ResolveRole(RuntimeRole.Boss), Is.Null);
        });
    }

    [Test]
    public void ResolveRole_Prefers_HumanReadable_Model_Name_Over_Opaque_Hash_Name()
    {
        var root = NewTempRoot();
        var readable = WriteFile(root, "SmolLM2-360M-Instruct-Q4_K_M.gguf");
        WriteFile(root, "2f", "2fa3f013dcdd7b99f9b237717fa0b12d75bbb89984cc1274be1471a465bac9c2.gguf");

        var depot = ModelDepot.Scan(root);
        var binding = depot.ResolveRole(RuntimeRole.Researcher);

        Assert.That(binding, Is.Not.Null);
        Assert.That(binding!.BaseModel.Path, Is.EqualTo(Path.GetFullPath(readable)));
    }

    [Test]
    public void ResolveRole_For_ContextFabric_Prefers_Admitted_Model()
    {
        var root = NewTempRoot();
        WriteFile(root, "DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf");
        var admitted = WriteFile(root, "gemma-4-12B-it-qat-q4_0.gguf");
        WriteFile(root, "Hermes-3-Llama-3.1-8B.Q5_K_M.gguf");

        var binding = ModelDepot.Scan(root).ResolveRole(
            RuntimeRole.Researcher,
            RuntimeWorkloadKind.ContextFabricReader);

        Assert.That(binding, Is.Not.Null);
        Assert.That(binding!.BaseModel.Path, Is.EqualTo(Path.GetFullPath(admitted)));
    }

    [Test]
    public void AdmissionGate_ContextFabricReader_GrantsProvisionalForCompact3to7BModel()
    {
        // A 4B model must NOT be hard-rejected — it is admitted provisionally so the benchmark
        // run can determine actual fitness. Parameter count alone is not sufficient evidence.
        var root = NewTempRoot();
        WriteFile(root, "Qwen3.5-4B-Q8_0.gguf");
        var asset = ModelDepot.Scan(root).Assets.Single();

        var decision = ModelAdmissionGate.Evaluate(asset, RuntimeWorkloadKind.ContextFabricReader);

        Assert.That(decision.Verdict, Is.EqualTo(ModelAdmissionVerdict.Provisional));
    }

    [Test]
    public void AdmissionGate_ContextFabricReader_RejectsSubThreeBModel()
    {
        // The hard floor is 3B — models below this are never usable for CF citation work.
        var root = NewTempRoot();
        WriteFile(root, "smollm2-360m-instruct-q8_0.gguf");
        var asset = ModelDepot.Scan(root).Assets.Single();

        var decision = ModelAdmissionGate.Evaluate(asset, RuntimeWorkloadKind.ContextFabricReader);

        Assert.That(decision.Verdict, Is.EqualTo(ModelAdmissionVerdict.Rejected));
    }

    // ── ScanOllamaModels / ScanSources (docs/... found live 2026-07-30) ────────────────────
    // Ollama's own storage, verified by hand against a real install: manifests/<host>/<ns>/
    // <name>/<tag> is an OCI-style JSON manifest; blobs/sha256-<hex> are the actual layer
    // files, and the layer with mediaType "application/vnd.ollama.image.model" IS a real GGUF
    // (magic bytes "GGUF") -- just content-hashed with no extension. These tests build that
    // exact structure by hand rather than trusting a real Ollama install to be present in CI.

    [Test]
    public void ScanOllamaModels_ResolvesDefaultLibraryTag_ShorteningRegistryAndNamespace()
    {
        var ollamaRoot = NewTempRoot();
        var modelPath = WriteOllamaManifest(
            ollamaRoot, "registry.ollama.ai/library/qwen2.5-coder/14b", "aa11", modelLayerSize: 42);

        var assets = ModelDepot.ScanOllamaModels(ollamaRoot).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(assets, Has.Count.EqualTo(1));
            Assert.That(assets[0].DisplayName, Is.EqualTo("qwen2.5-coder:14b"));
            Assert.That(assets[0].Path, Is.EqualTo(Path.GetFullPath(modelPath)));
            Assert.That(assets[0].Kind, Is.EqualTo(RuntimeAssetKind.BaseModelGguf));
            Assert.That(assets[0].SizeBytes, Is.EqualTo(42));
        });
    }

    [Test]
    public void ScanOllamaModels_ResolvesHfCoTag_KeepingFullHostAndNamespace()
    {
        var ollamaRoot = NewTempRoot();
        WriteOllamaManifest(
            ollamaRoot, "hf.co/NousResearch/Hermes-3-Llama-3.2-3B-GGUF/Q4_K_M", "bb22", modelLayerSize: 7);

        var assets = ModelDepot.ScanOllamaModels(ollamaRoot).ToList();

        Assert.That(assets, Has.Count.EqualTo(1));
        Assert.That(assets[0].DisplayName, Is.EqualTo("hf.co/NousResearch/Hermes-3-Llama-3.2-3B-GGUF:Q4_K_M"));
    }

    [Test]
    public void ScanOllamaModels_ResolvesDefaultRegistryNonLibraryNamespace_StrippingOnlyTheHost()
    {
        // grok-review MINOR: the default registry host and the "library" namespace strip
        // independently -- a user's own pushed model under the default registry but a
        // non-"library" namespace (e.g. their own Ollama account) keeps its namespace segment,
        // matching Ollama's own short-name convention ("someuser/mymodel:tag", not the full
        // "registry.ollama.ai/someuser/mymodel:tag").
        var ollamaRoot = NewTempRoot();
        WriteOllamaManifest(
            ollamaRoot, "registry.ollama.ai/someuser/mymodel/latest", "ee55", modelLayerSize: 9);

        var assets = ModelDepot.ScanOllamaModels(ollamaRoot).ToList();

        Assert.That(assets, Has.Count.EqualTo(1));
        Assert.That(assets[0].DisplayName, Is.EqualTo("someuser/mymodel:latest"));
    }

    [Test]
    public void ScanOllamaModels_SkipsManifestsWithNoModelLayer()
    {
        var ollamaRoot = NewTempRoot();
        var manifestPath = Path.Combine(ollamaRoot, "manifests", "registry.ollama.ai", "library", "no-model-layer", "latest");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, """{"schemaVersion":2,"layers":[{"mediaType":"application/vnd.ollama.image.template","digest":"sha256:cc33","size":5}]}""");
        Directory.CreateDirectory(Path.Combine(ollamaRoot, "blobs"));

        Assert.That(ModelDepot.ScanOllamaModels(ollamaRoot), Is.Empty);
    }

    [Test]
    public void ScanOllamaModels_ReturnsEmpty_WhenManifestsOrBlobsFolderIsMissing()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "orc-no-ollama-" + Guid.NewGuid().ToString("N"));

        Assert.That(ModelDepot.ScanOllamaModels(missingRoot), Is.Empty);
    }

    [Test]
    public void ScanSources_MergesMultipleFolders_DedupingByResolvedPath()
    {
        var rootA = NewTempRoot();
        var rootB = NewTempRoot();
        var pathA = WriteFile(rootA, "boss-a.gguf");
        var pathB = WriteFile(rootB, "boss-b.gguf");

        var depot = ModelDepot.ScanSources([rootA, rootB, rootA]); // rootA repeated on purpose

        Assert.That(depot.Assets.Select(a => a.Path), Is.EquivalentTo(new[]
        {
            Path.GetFullPath(pathA), Path.GetFullPath(pathB),
        }));
    }

    [Test]
    public void ScanSources_IncludesOllamaModels_OnlyWhenRequested()
    {
        var folderRoot = NewTempRoot();
        WriteFile(folderRoot, "plain-model.gguf");
        var ollamaRoot = NewTempRoot();
        WriteOllamaManifest(ollamaRoot, "registry.ollama.ai/library/mistral-small/latest", "dd44", modelLayerSize: 3);
        // A definitely-nonexistent path, not "" -- "" falls through ResolveOllamaModelsRoot to
        // the REAL OLLAMA_MODELS env var (or the real ~/.ollama/models default), which on any
        // dev machine with Ollama actually installed picks up real models and makes this
        // assertion machine-dependent instead of isolated.
        var nonexistentOllamaRoot = Path.Combine(Path.GetTempPath(), "orc-no-such-ollama-" + Guid.NewGuid().ToString("N"));

        var withoutOllama = ModelDepot.ScanSources([folderRoot], includeOllamaModels: true, ollamaModelsRootOverride: nonexistentOllamaRoot);
        var withOllama = ModelDepot.ScanSources([folderRoot], includeOllamaModels: true, ollamaModelsRootOverride: ollamaRoot);

        Assert.Multiple(() =>
        {
            Assert.That(withoutOllama.Assets, Has.Count.EqualTo(1), "no Ollama root resolved -> just the folder's own model");
            Assert.That(withOllama.Assets.Select(a => a.DisplayName),
                Is.EquivalentTo(new[] { "plain-model.gguf", "mistral-small:latest" }));
        });
    }

    /// <summary>
    /// Writes a synthetic Ollama manifest + its model-layer blob, matching the real structure
    /// (a JSON manifest under manifests/&lt;relativeTagPath&gt;, referencing a layer whose
    /// content lives at blobs/sha256-&lt;hex&gt;) without needing gigabytes of real weights --
    /// asset LISTING never inspects blob content, only its existence and length.
    /// </summary>
    private static string WriteOllamaManifest(string ollamaRoot, string relativeTagPath, string digestHex, int modelLayerSize)
    {
        var blobPath = Path.Combine(ollamaRoot, "blobs", $"sha256-{digestHex}");
        Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
        File.WriteAllBytes(blobPath, new byte[modelLayerSize]);

        var manifestPath = Path.Combine(ollamaRoot, "manifests", relativeTagPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath,
            $$"""
            {"schemaVersion":2,"layers":[
              {"mediaType":"application/vnd.ollama.image.template","digest":"sha256:unused","size":1},
              {"mediaType":"application/vnd.ollama.image.model","digest":"sha256:{{digestHex}}","size":{{modelLayerSize}}}
            ]}
            """);
        return blobPath;
    }

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-model-depot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private static string WriteFile(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake model bytes");
        return path;
    }
}
