// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ModelAdmissionGateTests
{
    [Test]
    public void ContextFabric_Rejects_Tiny_SmolLm_Model()
    {
        var decision = ModelAdmissionGate.Evaluate(
            Asset("SmolLM2-360M-Instruct-Q4_K_M.gguf"),
            RuntimeWorkloadKind.ContextFabricReader);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Verdict, Is.EqualTo(ModelAdmissionVerdict.Rejected));
            Assert.That(decision.Fingerprint.Family, Is.EqualTo(RuntimeModelFamily.SmolLm));
            Assert.That(decision.Fingerprint.ParametersB, Is.EqualTo(0.36).Within(0.001));
        });
    }

    [Test]
    public void ContextFabric_Admits_Qwen_14B_General_Model()
    {
        var decision = ModelAdmissionGate.Evaluate(
            Asset("Qwen2.5-14B-Instruct-Q4_K_M.gguf"),
            RuntimeWorkloadKind.ContextFabricReviewer);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Verdict, Is.EqualTo(ModelAdmissionVerdict.Admitted));
            Assert.That(decision.Fingerprint.Family, Is.EqualTo(RuntimeModelFamily.Qwen));
            Assert.That(decision.Fingerprint.ParametersB, Is.EqualTo(14).Within(0.001));
        });
    }

    [Test]
    public void AgenticCoding_Admits_Devstral()
    {
        var decision = ModelAdmissionGate.Evaluate(
            Asset("Devstral-Small-2505-Q4_K_M.gguf"),
            RuntimeWorkloadKind.AgenticCoding);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Verdict, Is.EqualTo(ModelAdmissionVerdict.Admitted));
            Assert.That(decision.Fingerprint.Family, Is.EqualTo(RuntimeModelFamily.Devstral));
            Assert.That(decision.Fingerprint.IsCoder, Is.True);
        });
    }

    [Test]
    public void VisionReasoning_Admits_Gemma3_Multimodal_Family()
    {
        var decision = ModelAdmissionGate.Evaluate(
            Asset("gemma-3-12b-it-q4_0.gguf"),
            RuntimeWorkloadKind.VisionReasoning);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Verdict, Is.EqualTo(ModelAdmissionVerdict.Admitted));
            Assert.That(decision.Fingerprint.Family, Is.EqualTo(RuntimeModelFamily.Gemma));
            Assert.That(decision.Fingerprint.HasVisionSignals, Is.True);
        });
    }

    [Test]
    public void StrictStructuredOutput_Rejects_Small_Dolphin_Chat_Model()
    {
        var decision = ModelAdmissionGate.Evaluate(
            Asset("dolphin-3.0-qwen2.5-3b-q4_k_m.gguf"),
            RuntimeWorkloadKind.StrictStructuredOutput);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Verdict, Is.EqualTo(ModelAdmissionVerdict.Rejected));
            Assert.That(decision.Fingerprint.Family, Is.EqualTo(RuntimeModelFamily.Dolphin));
            Assert.That(decision.Fingerprint.IsUncensoredStyle, Is.True);
        });
    }

    [Test]
    public void ToolCalling_Marks_Small_QwenCoder_As_Provisional()
    {
        var decision = ModelAdmissionGate.Evaluate(
            Asset("Qwen2.5-Coder-3B-Instruct-Q8_0.gguf"),
            RuntimeWorkloadKind.ToolCalling);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Verdict, Is.EqualTo(ModelAdmissionVerdict.Provisional));
            Assert.That(decision.Fingerprint.Family, Is.EqualTo(RuntimeModelFamily.QwenCoder));
            Assert.That(decision.Fingerprint.IsCoder, Is.True);
        });
    }

    private static RuntimeModelAsset Asset(string displayName) => new(
        Id: displayName.ToLowerInvariant(),
        Kind: RuntimeAssetKind.BaseModelGguf,
        Path: $@"F:\Models\{displayName}",
        DisplayName: displayName,
        SizeBytes: 1,
        LastModifiedUtc: DateTimeOffset.UtcNow,
        SuggestedRoles: []);
}
