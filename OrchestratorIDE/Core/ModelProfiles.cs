namespace OrchestratorIDE.Core;

public enum ToolSet { Minimal, Coding, Full }
public enum PromptStyle { General, Coding, Agent }

public record ModelProfile(
    string ModelId,
    string Name,
    int ContextTokens,
    bool NativeToolUse,
    string[] Strengths,
    ToolSet ToolSet,
    PromptStyle PromptStyle,
    int MaxSteps = 15,
    double Temperature = 0.1,
    int TimeoutSeconds = 90,
    bool AutoVerify = true,
    string Description = ""
)
{
    public int ContextK => ContextTokens / 1024;
}

public static class ModelProfiles
{
    private static readonly Dictionary<string, ModelProfile> _profiles = new()
    {
        // ── Coding specialists ───────────────────────────────────────────────
        ["qwen2.5-coder:32b"] = new(
            "qwen2.5-coder:32b", "Qwen2.5-Coder 32B", 32_768, true,
            ["coding", "architecture", "refactor", "tests"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 25, Temperature: 0.05, TimeoutSeconds: 180, AutoVerify: true,
            Description: "Highest coding quality. Best for complex multi-file tasks. Needs ~20 GB VRAM."
        ),
        ["qwen2.5-coder:14b"] = new(
            "qwen2.5-coder:14b", "Qwen2.5-Coder 14B", 32_768, true,
            ["coding", "debug", "refactor", "tests"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 20, Temperature: 0.05, TimeoutSeconds: 90, AutoVerify: true,
            Description: "Best speed/quality balance for code. Fits in 16 GB VRAM. ★ Recommended"
        ),
        ["qwen2.5-coder:7b"] = new(
            "qwen2.5-coder:7b", "Qwen2.5-Coder 7B", 32_768, true,
            ["coding", "fast", "debug"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 15, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: true,
            Description: "Fast coder. Great for quick edits and smaller tasks."
        ),

        // ── General purpose / instruct ───────────────────────────────────────
        ["qwen2.5:14b-instruct"] = new(
            "qwen2.5:14b-instruct", "Qwen2.5 14B Instruct", 32_768, true,
            ["general", "reasoning", "analysis", "writing"],
            ToolSet.Full, PromptStyle.General,
            MaxSteps: 18, Temperature: 0.1, TimeoutSeconds: 90, AutoVerify: false,
            Description: "Strong general model. Good for reasoning, planning, and writing."
        ),

        // ── Hermes / Heretic fine-tunes on NEWCOREPC ─────────────────────────
        ["hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M"] = new(
            "hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M",
            "Hermes 4 14B (Q5)", 32_768, true,
            ["agent", "reasoning", "tool-use", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 20, Temperature: 0.1, TimeoutSeconds: 120, AutoVerify: true,
            Description: "Nous Hermes 4 — strong agentic reasoning with excellent tool-use."
        ),
        ["hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M"] = new(
            "hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M",
            "GPT-OSS 20B Heretic (Q4)", 32_768, true,
            ["coding", "agent", "reasoning"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.1, TimeoutSeconds: 150, AutoVerify: true,
            Description: "20B heretic fine-tune. High capacity for complex coding tasks."
        ),
        ["hf.co/bartowski/p-e-w_phi-4-heretic-GGUF:Q4_K_M"] = new(
            "hf.co/bartowski/p-e-w_phi-4-heretic-GGUF:Q4_K_M",
            "Phi-4 Heretic (Q4)", 16_384, true,
            ["reasoning", "coding", "math", "structured"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 15, Temperature: 0.1, TimeoutSeconds: 90, AutoVerify: true,
            Description: "Phi-4 with heretic RLHF. Excellent at structured reasoning and math."
        ),
        ["hf.co/bartowski/p-e-w_Llama-3.1-8B-Instruct-heretic-GGUF:Q4_K_M"] = new(
            "hf.co/bartowski/p-e-w_Llama-3.1-8B-Instruct-heretic-GGUF:Q4_K_M",
            "Llama 3.1 8B Heretic (Q4)", 32_768, true,
            ["general", "chat", "fast", "coding"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 12, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: false,
            Description: "Fast 8B with heretic fine-tuning. Good balance of speed and quality."
        ),
        ["hf.co/cognitivecomputations/Dolphin3.0-Llama3.1-8B-GGUF:Q4_0"] = new(
            "hf.co/cognitivecomputations/Dolphin3.0-Llama3.1-8B-GGUF:Q4_0",
            "Dolphin 3.0 Llama 8B (Q4)", 32_768, true,
            ["uncensored", "coding", "agentic", "fast"],
            ToolSet.Coding, PromptStyle.Agent,
            MaxSteps: 12, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: false,
            Description: "Dolphin 3.0 — uncensored Llama 3.1 8B. Fast agentic tasks."
        ),

        // ── Compact / fast ───────────────────────────────────────────────────
        // ── NVIDIA Nemotron 3 ────────────────────────────────────────────────
        ["nemotron-3-nano:4b"] = new(
            "nemotron-3-nano:4b", "Nemotron 3 Nano 4B", 131_072, true,
            ["agent", "reasoning", "tool-use", "fast", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 18, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: true,
            Description: "NVIDIA Nemotron 3 Nano 4B — agentic-tuned, 128K context, 2.8 GB. Fast local agent."
        ),
        ["nemotron-3-nano:4b-q8_0"] = new(
            "nemotron-3-nano:4b-q8_0", "Nemotron 3 Nano 4B (Q8)", 131_072, true,
            ["agent", "reasoning", "tool-use", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 18, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: true,
            Description: "NVIDIA Nemotron 3 Nano 4B Q8 — higher precision, 4.2 GB. Better quality vs 4b base."
        ),

        ["gemma4:12b"] = new(
            "gemma4:12b", "Gemma 4 12B", 262_144, true,
            ["agent", "reasoning", "coding", "tool-use", "multimodal"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.1, TimeoutSeconds: 90, AutoVerify: true,
            Description: "Google Gemma 4 12B — native function calling, 256K context, ~6.7 GB VRAM. Encoder-free multimodal. Apache 2.0. ★ Recommended"
        ),
        ["gemma4:e4b"] = new(
            "gemma4:e4b", "Gemma 4 (E4B)", 32_768, true,
            ["general", "chat", "fast", "reasoning"],
            ToolSet.Coding, PromptStyle.General,
            MaxSteps: 15, Temperature: 0.2, TimeoutSeconds: 60, AutoVerify: false,
            Description: "Google Gemma 4 efficient 4B model. Very fast with 32k context."
        ),
        ["phi4-mini:latest"] = new(
            "phi4-mini:latest", "Phi-4 Mini", 16_384, true,
            ["reasoning", "math", "compact", "fast"],
            ToolSet.Minimal, PromptStyle.Coding,
            MaxSteps: 10, Temperature: 0.1, TimeoutSeconds: 45, AutoVerify: false,
            Description: "Microsoft Phi-4 Mini. Fastest option for simple tasks."
        ),
        ["llama3.1:8b"] = new(
            "llama3.1:8b", "Llama 3.1 8B", 32_768, true,
            ["general", "chat", "fast"],
            ToolSet.Minimal, PromptStyle.General,
            MaxSteps: 12, Temperature: 0.2, TimeoutSeconds: 60, AutoVerify: false,
            Description: "Standard Llama 3.1 8B. Very fast. Good for quick questions."
        ),

        // ── Security research stack ──────────────────────────────────────────
        ["deepseek-coder-v2:16b"] = new(
            "deepseek-coder-v2:16b", "DeepSeek-Coder V2 16B", 163_840, true,
            ["coding", "security", "algorithms", "debug", "exploit"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 20, Temperature: 0.1, TimeoutSeconds: 120, AutoVerify: true,
            Description: "DeepSeek Coder V2 16B — strong on code, security tooling, and algorithms. Fewer restrictions than mainstream models. 160k context."
        ),
        ["mistral-small"] = new(
            "mistral-small", "Mistral Small 3.1 24B", 131_072, true,
            ["agent", "security", "reasoning", "tool-use", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.1, TimeoutSeconds: 150, AutoVerify: true,
            Description: "Mistral Small 3.1 — native tool calling, 128k context, fewer RLHF restrictions than Llama/Qwen. Good security research model."
        ),
        ["mistral-small:latest"] = new(
            "mistral-small:latest", "Mistral Small 3.1 24B", 131_072, true,
            ["agent", "security", "reasoning", "tool-use", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.1, TimeoutSeconds: 150, AutoVerify: true,
            Description: "Mistral Small 3.1 — native tool calling, 128k context. Strong security research model."
        ),
        ["hf.co/bartowski/dolphin-2.9.2-qwen2-7b-GGUF:Q5_K_M"] = new(
            "hf.co/bartowski/dolphin-2.9.2-qwen2-7b-GGUF:Q5_K_M",
            "Dolphin 2.9.2 Qwen2 7B (Q5)", 32_768, true,
            ["uncensored", "security", "coding", "agentic", "tool-use"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 15, Temperature: 0.1, TimeoutSeconds: 75, AutoVerify: true,
            Description: "Dolphin 2.9.2 on Qwen2 base — explicitly uncensored fine-tune, good tool calling. Best fast option for security research."
        ),

        // ── Future/planned ───────────────────────────────────────────────────
        ["devstral:24b"] = new(
            "devstral:24b", "Devstral 24B", 131_072, true,
            ["agent", "coding", "planning", "multi-file"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 30, Temperature: 0.1, TimeoutSeconds: 180, AutoVerify: true,
            Description: "Mistral's agent-tuned model. 128k context. Best for long autonomous tasks."
        ),
    };

    // ── Security research model preference order ──────────────────────────────
    // Used by auto-switch when a pentest workspace is detected.
    // Priority: purpose-built tool-calling unrestricted models first,
    // then larger heretic merges, then fast uncensored fallbacks.
    public static readonly string[] SecurityPreference =
    [
        "hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M",   // purpose-built agentic, low restriction
        "mistral-small",                                              // native tools, 128k, less restricted
        "mistral-small:latest",
        "hf.co/bartowski/dolphin-2.9.2-qwen2-7b-GGUF:Q5_K_M",      // explicitly uncensored + tool calling
        "hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M",   // heretic merge, large capacity
        "deepseek-coder-v2:16b",                                     // strong coder, fewer restrictions
        "hf.co/cognitivecomputations/Dolphin3.0-Llama3.1-8B-GGUF:Q4_0",  // uncensored fallback
        "hf.co/bartowski/p-e-w_Llama-3.1-8B-Instruct-heretic-GGUF:Q4_K_M", // fast heretic fallback
    ];

    public static ModelProfile Get(string modelId)
    {
        // Exact match
        if (_profiles.TryGetValue(modelId, out var profile))
            return profile;

        // Fuzzy match strategy 1: base name (split on ':')
        var baseName = modelId.Split(':')[0];
        foreach (var (key, value) in _profiles)
            if (key.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                return value;

        // Fuzzy match strategy 2: last path segment (for hf.co/... style names)
        var lastSegment = modelId.Contains('/') ? modelId.Split('/').Last() : "";
        if (lastSegment.Length > 0)
        {
            var segBase = lastSegment.Split(':')[0];
            foreach (var (key, value) in _profiles)
            {
                var keyBase = key.Split('/').Last().Split(':')[0];
                if (keyBase.Equals(segBase, StringComparison.OrdinalIgnoreCase) ||
                    keyBase.Contains(segBase, StringComparison.OrdinalIgnoreCase) ||
                    segBase.Contains(keyBase, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
        }

        // Unknown model — return reasonable defaults based on size hints in name
        var isLarge = modelId.Contains("14b", StringComparison.OrdinalIgnoreCase)
                   || modelId.Contains("20b", StringComparison.OrdinalIgnoreCase)
                   || modelId.Contains("32b", StringComparison.OrdinalIgnoreCase);

        return new(modelId, modelId, isLarge ? 32_768 : 8_192, false,
            ["general"], isLarge ? ToolSet.Coding : ToolSet.Minimal,
            PromptStyle.General,
            MaxSteps: isLarge ? 15 : 10, AutoVerify: false,
            Description: "Unknown model — using inferred defaults.");
    }

    public static IReadOnlyDictionary<string, ModelProfile> All => _profiles;
}
