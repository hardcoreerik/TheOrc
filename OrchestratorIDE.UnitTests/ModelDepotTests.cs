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
    public void ResolveRole_For_ContextFabric_Prefers_Compatible_Model()
    {
        var root = NewTempRoot();
        WriteFile(root, "DeepSeek-R1-Distill-Qwen-7B-Q4_K_M.gguf");
        WriteFile(root, "gemma-4-12B-it-qat-q4_0.gguf");
        var compatible = WriteFile(root, "Hermes-3-Llama-3.1-8B.Q5_K_M.gguf");

        var binding = ModelDepot.Scan(root).ResolveRole(
            RuntimeRole.Researcher,
            RuntimeWorkloadKind.ContextFabricReader);

        Assert.That(binding, Is.Not.Null);
        Assert.That(binding!.BaseModel.Path, Is.EqualTo(Path.GetFullPath(compatible)));
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
