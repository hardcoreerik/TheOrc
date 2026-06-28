// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Core.Runtime;

public enum RuntimeWorkloadKind
{
    OrcChat,
    ToolCalling,
    StrictStructuredOutput,
    ContextFabricReader,
    ContextFabricReviewer,
    AgenticCoding,
    VisionReasoning,
}

public enum ModelAdmissionVerdict
{
    Rejected,
    Provisional,
    Admitted,
}

public enum RuntimeModelFamily
{
    Unknown,
    SmolLm,
    Qwen,
    Qwen3,
    QwenCoder,
    QwenVl,
    Gemma,
    Llama,
    Devstral,
    Codestral,
    Mistral,
    MistralNemo,
    Phi,
    DeepSeekCoder,
    DeepSeekR1,
    Dolphin,
    Hermes,
    Nemotron,
    Starcoder,
    Pixtral,
    Molmo,
}

public sealed record RuntimeModelFingerprint(
    RuntimeModelFamily Family,
    string FamilyLabel,
    double? ParametersB,
    bool HasVisionSignals,
    bool IsCoder,
    bool IsReasoningTuned,
    bool IsUncensoredStyle,
    string NormalizedName);

public sealed record ModelAdmissionDecision(
    RuntimeWorkloadKind Workload,
    ModelAdmissionVerdict Verdict,
    RuntimeModelFingerprint Fingerprint,
    string Summary,
    IReadOnlyList<string> Reasons);

public static class ModelAdmissionGate
{
    private static readonly Regex _tokenSplitter = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex _paramsPattern = new(@"(?<!\d)(?<value>\d+(?:[._]\d+)?)b(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex _paramsMillionPattern = new(@"(?<!\d)(?<value>\d+(?:[._]\d+)?)m(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static RuntimeModelFingerprint Fingerprint(RuntimeModelAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var normalized = Normalize(asset.DisplayName);
        var tokens = Tokenize(normalized);
        var family = DetectFamily(normalized, tokens);
        var parametersB = ParseParametersB(normalized, tokens);
        var hasVisionSignals =
            tokens.Contains("vl") ||
            tokens.Contains("vision") ||
            tokens.Contains("pixtral") ||
            tokens.Contains("molmo") ||
            tokens.Contains("multimodal") ||
            family is RuntimeModelFamily.QwenVl or RuntimeModelFamily.Pixtral or RuntimeModelFamily.Molmo ||
            (family == RuntimeModelFamily.Gemma && normalized.Contains("gemma-3", StringComparison.Ordinal));
        var isCoder =
            family is RuntimeModelFamily.QwenCoder or RuntimeModelFamily.Devstral or RuntimeModelFamily.Codestral or RuntimeModelFamily.DeepSeekCoder or RuntimeModelFamily.Starcoder ||
            tokens.Contains("coder") ||
            tokens.Contains("code");
        var isReasoningTuned =
            family is RuntimeModelFamily.Qwen3 or RuntimeModelFamily.DeepSeekR1 ||
            tokens.Contains("reasoning") ||
            tokens.Contains("r1");
        var isUncensoredStyle =
            family == RuntimeModelFamily.Dolphin ||
            tokens.Contains("uncensored") ||
            tokens.Contains("abliterated");

        return new RuntimeModelFingerprint(
            family,
            FamilyLabel(family),
            parametersB,
            hasVisionSignals,
            isCoder,
            isReasoningTuned,
            isUncensoredStyle,
            normalized);
    }

    public static ModelAdmissionDecision Evaluate(RuntimeModelAsset asset, RuntimeWorkloadKind workload)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (asset.Kind != RuntimeAssetKind.BaseModelGguf)
        {
            var rejectedFingerprint = Fingerprint(asset);
            return new ModelAdmissionDecision(
                workload,
                ModelAdmissionVerdict.Rejected,
                rejectedFingerprint,
                "Only base GGUF models are admissible for native workload selection.",
                [$"Asset kind '{asset.Kind}' is not a base GGUF model."]);
        }

        var fp = Fingerprint(asset);
        return workload switch
        {
            RuntimeWorkloadKind.OrcChat => EvaluateChat(fp),
            RuntimeWorkloadKind.ToolCalling => EvaluateToolCalling(fp),
            RuntimeWorkloadKind.StrictStructuredOutput => EvaluateStrictStructuredOutput(fp),
            RuntimeWorkloadKind.ContextFabricReader => EvaluateContextFabric(fp, RuntimeWorkloadKind.ContextFabricReader),
            RuntimeWorkloadKind.ContextFabricReviewer => EvaluateContextFabric(fp, RuntimeWorkloadKind.ContextFabricReviewer),
            RuntimeWorkloadKind.AgenticCoding => EvaluateAgenticCoding(fp),
            RuntimeWorkloadKind.VisionReasoning => EvaluateVisionReasoning(fp),
            _ => new ModelAdmissionDecision(workload, ModelAdmissionVerdict.Provisional, fp, "Unknown workload.", ["Workload has no gate policy yet."]),
        };
    }

    private static ModelAdmissionDecision EvaluateChat(RuntimeModelFingerprint fp)
    {
        if (fp.ParametersB is < 1)
            return Reject(RuntimeWorkloadKind.OrcChat, fp, "Model is too small for a reliable general chat default.");

        if (fp.IsUncensoredStyle && fp.ParametersB < 7)
            return Provisional(RuntimeWorkloadKind.OrcChat, fp, "Creative or uncensored chat is possible, but reliability will vary.", "Uncensored-style small models are better as optional personalities than safe defaults.");

        return Admit(RuntimeWorkloadKind.OrcChat, fp, "General chat is a valid native workload for this model.", "Chat is the least restrictive native workload.");
    }

    private static ModelAdmissionDecision EvaluateToolCalling(RuntimeModelFingerprint fp)
    {
        if (fp.ParametersB is null or < 3)
            return Reject(RuntimeWorkloadKind.ToolCalling, fp, "Model is too small for dependable tool orchestration.");

        if (fp.IsUncensoredStyle)
            return Provisional(RuntimeWorkloadKind.ToolCalling, fp, "Tool use may work, but strict orchestration should be proven locally first.", "Uncensored-style chat finetunes often trade away deterministic structure.");

        if (fp.Family is RuntimeModelFamily.Qwen or RuntimeModelFamily.Qwen3 or RuntimeModelFamily.QwenCoder or RuntimeModelFamily.Phi or RuntimeModelFamily.Llama or RuntimeModelFamily.Devstral or RuntimeModelFamily.Codestral or RuntimeModelFamily.Mistral or RuntimeModelFamily.MistralNemo)
            return (fp.ParametersB ?? 0) >= 7
                ? Admit(RuntimeWorkloadKind.ToolCalling, fp, "Model family is known to be strong at local tool use.", "Structured tool orchestration is a first-class target for this family.")
                : Provisional(RuntimeWorkloadKind.ToolCalling, fp, "Compact tool use may work, but multi-step loops should be probed before promotion.", "Sub-7B models often succeed at single tools and degrade on long loops.");

        return Provisional(RuntimeWorkloadKind.ToolCalling, fp, "Unknown family; admit only after local tool-call probes.", "No family-specific evidence is available yet.");
    }

    private static ModelAdmissionDecision EvaluateStrictStructuredOutput(RuntimeModelFingerprint fp)
    {
        if (fp.ParametersB is null or < 7)
            return Reject(RuntimeWorkloadKind.StrictStructuredOutput, fp, "Model is below the minimum size for strict JSON and schema-heavy native jobs.");

        if (fp.IsUncensoredStyle)
            return Reject(RuntimeWorkloadKind.StrictStructuredOutput, fp, "Uncensored-style chat finetunes are not safe defaults for strict structured output.");

        if (fp.Family is RuntimeModelFamily.SmolLm or RuntimeModelFamily.Nemotron)
            return Reject(RuntimeWorkloadKind.StrictStructuredOutput, fp, "This family is better treated as chat or light assistant material than schema-grade infrastructure.");

        if (fp.Family is RuntimeModelFamily.Qwen or RuntimeModelFamily.Qwen3 or RuntimeModelFamily.QwenCoder or RuntimeModelFamily.Gemma or RuntimeModelFamily.Llama or RuntimeModelFamily.Phi or RuntimeModelFamily.Devstral or RuntimeModelFamily.Mistral or RuntimeModelFamily.MistralNemo or RuntimeModelFamily.DeepSeekCoder or RuntimeModelFamily.DeepSeekR1)
            return (fp.ParametersB ?? 0) >= 12
                ? Admit(RuntimeWorkloadKind.StrictStructuredOutput, fp, "Model is a strong candidate for strict native JSON work.", "Family and size both clear the structured-output bar.")
                : Provisional(RuntimeWorkloadKind.StrictStructuredOutput, fp, "Model may work for strict JSON, but should pass local schema tests first.", "7B to 11B models are often usable but not uniformly trustworthy.");

        return Provisional(RuntimeWorkloadKind.StrictStructuredOutput, fp, "Unknown family; require local contract tests before promotion.", "No durable structured-output prior is stored for this family.");
    }

    private static ModelAdmissionDecision EvaluateContextFabric(RuntimeModelFingerprint fp, RuntimeWorkloadKind workload)
    {
        if (fp.ParametersB is null or < 7)
            return Reject(workload, fp, "Model is too small for Context Fabric evidence extraction and verification.");

        if (fp.IsUncensoredStyle)
            return Reject(workload, fp, "Context Fabric should not default to uncensored-style chat finetunes.");

        if (fp.Family == RuntimeModelFamily.Gemma &&
            fp.NormalizedName.Contains("gemma-4", StringComparison.Ordinal))
            return Reject(workload, fp, "Gemma 4 is not compatible with the current native chat-template path.", "The embedded template cannot be applied and ChatML fallback does not produce valid Gemma prompts.");

        if (fp.Family is RuntimeModelFamily.SmolLm or RuntimeModelFamily.Nemotron)
            return Reject(workload, fp, "This family should not be auto-admitted for high-trust evidence work.");

        if (fp.IsReasoningTuned)
            return Provisional(workload, fp, "Reasoning-tuned models require a clean structured-output benchmark pass for Context Fabric.", "Visible reasoning traces can consume the response budget or precede the required JSON object.");

        if (fp.Family is RuntimeModelFamily.Qwen3 or RuntimeModelFamily.Qwen or RuntimeModelFamily.Gemma or RuntimeModelFamily.Llama or RuntimeModelFamily.Phi or RuntimeModelFamily.MistralNemo or RuntimeModelFamily.Mistral or RuntimeModelFamily.Devstral or RuntimeModelFamily.DeepSeekR1)
            return (fp.ParametersB ?? 0) >= 12
                ? Admit(workload, fp, "Model is large enough and from a strong family for Context Fabric candidate status.", "Evidence extraction and citation-heavy summarization are reasonable targets here.")
                : Provisional(workload, fp, "Model may be usable for Context Fabric, but must pass CF-0/CF-1 benchmark gates first.", "7B to 11B models remain borderline for citation-safe evidence work.");

        return Provisional(workload, fp, "Unknown family; do not auto-promote without benchmark evidence.", "Context Fabric needs benchmark evidence, not just chat competence.");
    }

    private static ModelAdmissionDecision EvaluateAgenticCoding(RuntimeModelFingerprint fp)
    {
        if (fp.ParametersB is null or < 3)
            return Reject(RuntimeWorkloadKind.AgenticCoding, fp, "Model is too small for reliable agentic coding.");

        if (fp.Family is RuntimeModelFamily.Devstral or RuntimeModelFamily.Codestral or RuntimeModelFamily.QwenCoder or RuntimeModelFamily.DeepSeekCoder or RuntimeModelFamily.Starcoder)
            return (fp.ParametersB ?? 0) >= 7
                ? Admit(RuntimeWorkloadKind.AgenticCoding, fp, "Coder-focused family is a strong fit for Orc coding agents.", "Family is explicitly aimed at multi-file engineering work.")
                : Provisional(RuntimeWorkloadKind.AgenticCoding, fp, "Compact coder may help with short edits, but should not be a flagship coding default.", "Small coding models degrade first on long tool chains and repo exploration.");

        if (fp.Family is RuntimeModelFamily.Qwen3 or RuntimeModelFamily.Qwen or RuntimeModelFamily.Llama or RuntimeModelFamily.Gemma)
            return (fp.ParametersB ?? 0) >= 14
                ? Provisional(RuntimeWorkloadKind.AgenticCoding, fp, "General reasoning family may work for coding, but a coder-native family is preferred.", "Good generalists still trail code-specialized models on autonomous repo work.")
                : Reject(RuntimeWorkloadKind.AgenticCoding, fp, "Use a coder-native family instead of a small general model for agentic coding.");

        return Provisional(RuntimeWorkloadKind.AgenticCoding, fp, "Unknown family; probe on repo navigation and multi-file edits before promotion.", "Agentic coding needs stronger evidence than one-off completions.");
    }

    private static ModelAdmissionDecision EvaluateVisionReasoning(RuntimeModelFingerprint fp)
    {
        if (!fp.HasVisionSignals)
            return Reject(RuntimeWorkloadKind.VisionReasoning, fp, "Model has no clear multimodal or vision signals.");

        if (fp.Family is RuntimeModelFamily.QwenVl or RuntimeModelFamily.Gemma or RuntimeModelFamily.Pixtral or RuntimeModelFamily.Molmo)
            return (fp.ParametersB ?? 0) >= 7
                ? Admit(RuntimeWorkloadKind.VisionReasoning, fp, "Model is a strong candidate for future Orc image and document reasoning.", "Family is explicitly vision-capable.")
                : Provisional(RuntimeWorkloadKind.VisionReasoning, fp, "Vision may work, but larger multimodal variants are preferred.", "Smaller multimodal models often miss document detail and OCR-like structure.");

        return Provisional(RuntimeWorkloadKind.VisionReasoning, fp, "Model has some vision signals, but Orc should verify image tasks locally first.", "Vision support is inferred rather than proven.");
    }

    private static ModelAdmissionDecision Admit(RuntimeWorkloadKind workload, RuntimeModelFingerprint fp, string summary, params string[] reasons) =>
        new(workload, ModelAdmissionVerdict.Admitted, fp, summary, reasons);

    private static ModelAdmissionDecision Provisional(RuntimeWorkloadKind workload, RuntimeModelFingerprint fp, string summary, params string[] reasons) =>
        new(workload, ModelAdmissionVerdict.Provisional, fp, summary, reasons);

    private static ModelAdmissionDecision Reject(RuntimeWorkloadKind workload, RuntimeModelFingerprint fp, string summary, params string[] reasons) =>
        new(workload, ModelAdmissionVerdict.Rejected, fp, summary, reasons.Length == 0 ? ["No admission rule matched."] : reasons);

    private static RuntimeModelFamily DetectFamily(string normalized, HashSet<string> tokens)
    {
        if (tokens.Contains("smollm") || normalized.Contains("smollm", StringComparison.Ordinal))
            return RuntimeModelFamily.SmolLm;
        if (normalized.Contains("dolphin", StringComparison.Ordinal))
            return RuntimeModelFamily.Dolphin;
        if (normalized.Contains("hermes", StringComparison.Ordinal))
            return RuntimeModelFamily.Hermes;
        if (normalized.Contains("qwen2.5-vl", StringComparison.Ordinal) || normalized.Contains("qwen25-vl", StringComparison.Ordinal))
            return RuntimeModelFamily.QwenVl;
        if (normalized.Contains("qwen3", StringComparison.Ordinal))
            return RuntimeModelFamily.Qwen3;
        if (normalized.Contains("qwen", StringComparison.Ordinal) && (tokens.Contains("coder") || tokens.Contains("code")))
            return RuntimeModelFamily.QwenCoder;
        if (normalized.Contains("qwen", StringComparison.Ordinal))
            return RuntimeModelFamily.Qwen;
        if (normalized.Contains("gemma", StringComparison.Ordinal))
            return RuntimeModelFamily.Gemma;
        if (normalized.Contains("llama", StringComparison.Ordinal))
            return RuntimeModelFamily.Llama;
        if (normalized.Contains("devstral", StringComparison.Ordinal))
            return RuntimeModelFamily.Devstral;
        if (normalized.Contains("codestral", StringComparison.Ordinal))
            return RuntimeModelFamily.Codestral;
        if (normalized.Contains("mistral-nemo", StringComparison.Ordinal) || (normalized.Contains("nemo", StringComparison.Ordinal) && normalized.Contains("mistral", StringComparison.Ordinal)))
            return RuntimeModelFamily.MistralNemo;
        if (normalized.Contains("mistral", StringComparison.Ordinal))
            return RuntimeModelFamily.Mistral;
        if (normalized.Contains("phi", StringComparison.Ordinal))
            return RuntimeModelFamily.Phi;
        if (normalized.Contains("deepseek", StringComparison.Ordinal) && normalized.Contains("coder", StringComparison.Ordinal))
            return RuntimeModelFamily.DeepSeekCoder;
        if (normalized.Contains("deepseek", StringComparison.Ordinal) && (normalized.Contains("-r1", StringComparison.Ordinal) || tokens.Contains("r1")))
            return RuntimeModelFamily.DeepSeekR1;
        if (normalized.Contains("nemotron", StringComparison.Ordinal))
            return RuntimeModelFamily.Nemotron;
        if (normalized.Contains("starcoder", StringComparison.Ordinal))
            return RuntimeModelFamily.Starcoder;
        if (normalized.Contains("pixtral", StringComparison.Ordinal))
            return RuntimeModelFamily.Pixtral;
        if (normalized.Contains("molmo", StringComparison.Ordinal))
            return RuntimeModelFamily.Molmo;
        return RuntimeModelFamily.Unknown;
    }

    private static string FamilyLabel(RuntimeModelFamily family) => family switch
    {
        RuntimeModelFamily.SmolLm => "SmolLM",
        RuntimeModelFamily.Qwen => "Qwen",
        RuntimeModelFamily.Qwen3 => "Qwen3",
        RuntimeModelFamily.QwenCoder => "Qwen Coder",
        RuntimeModelFamily.QwenVl => "Qwen VL",
        RuntimeModelFamily.Gemma => "Gemma",
        RuntimeModelFamily.Llama => "Llama",
        RuntimeModelFamily.Devstral => "Devstral",
        RuntimeModelFamily.Codestral => "Codestral",
        RuntimeModelFamily.Mistral => "Mistral",
        RuntimeModelFamily.MistralNemo => "Mistral Nemo",
        RuntimeModelFamily.Phi => "Phi",
        RuntimeModelFamily.DeepSeekCoder => "DeepSeek Coder",
        RuntimeModelFamily.DeepSeekR1 => "DeepSeek R1",
        RuntimeModelFamily.Dolphin => "Dolphin",
        RuntimeModelFamily.Hermes => "Hermes",
        RuntimeModelFamily.Nemotron => "Nemotron",
        RuntimeModelFamily.Starcoder => "StarCoder",
        RuntimeModelFamily.Pixtral => "Pixtral",
        RuntimeModelFamily.Molmo => "Molmo",
        _ => "Unknown",
    };

    private static double? ParseParametersB(string normalized, HashSet<string> tokens)
    {
        var match = _paramsPattern.Match(normalized);
        if (match.Success &&
            double.TryParse(match.Groups["value"].Value.Replace('_', '.'), out var parsed))
            return parsed;

        match = _paramsMillionPattern.Match(normalized);
        if (match.Success &&
            double.TryParse(match.Groups["value"].Value.Replace('_', '.'), out parsed))
            return parsed / 1000d;

        if (normalized.Contains("devstral-small-2505", StringComparison.Ordinal))
            return 24;
        if (normalized.Contains("codestral", StringComparison.Ordinal))
            return 22;
        if (normalized.Contains("mistral-nemo", StringComparison.Ordinal))
            return 12;
        if (normalized.Contains("phi-4-mini", StringComparison.Ordinal) || normalized.Contains("phi4-mini", StringComparison.Ordinal))
            return 3.8;
        if (normalized.Contains("phi-4", StringComparison.Ordinal) || normalized.Contains("phi4", StringComparison.Ordinal))
            return 14;
        if (tokens.Contains("mini") && normalized.Contains("nemotron", StringComparison.Ordinal))
            return 4;

        return null;
    }

    private static string Normalize(string value) => value
        .Replace('\\', '/')
        .Replace('_', '-')
        .ToLowerInvariant();

    private static HashSet<string> Tokenize(string normalized) => _tokenSplitter
        .Split(normalized)
        .Where(token => !string.IsNullOrWhiteSpace(token))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
