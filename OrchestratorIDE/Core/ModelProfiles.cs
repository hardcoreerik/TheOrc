namespace OrchestratorIDE.Core;

public enum ToolSet    { Minimal, Coding, Full }
public enum PromptStyle { General, Coding, Agent }
public enum SpeedTier  { Fast, Medium, Slow }

/// <summary>
/// Capability profile for a single model.
///
/// Role scores (0–10) are derived from published benchmarks:
///   BossScore       — MMLU + planning quality (decomposition, task routing)
///   CoderScore      — HumanEval / EvalPlus + file-writing reliability
///   ResearcherScore — Knowledge synthesis, factual accuracy, web retrieval
///   TesterScore     — Structured JSON output reliability (BFCL / constrained output)
///
/// MinVramGb is the practical VRAM floor to run the model without severe
/// performance degradation (assumes Q4 quantisation unless model is already quantised).
/// </summary>
public record ModelProfile(
    string ModelId,
    string Name,
    int ContextTokens,
    bool NativeToolUse,
    string[] Strengths,
    ToolSet ToolSet,
    PromptStyle PromptStyle,
    int MaxSteps       = 15,
    double Temperature = 0.1,
    int TimeoutSeconds = 90,
    bool AutoVerify    = true,
    string Description = "",

    // ── Hardware ──────────────────────────────────────────────────────────────
    int MinVramGb  = 4,       // minimum VRAM to run without severe slowdown
    int ParamsBillions = 0,   // approximate parameter count (0 = unknown)
    SpeedTier Speed = SpeedTier.Medium,

    // ── Per-role quality scores (0 = unusable, 10 = best-in-class) ───────────
    // Based on published benchmarks + observed swarm behaviour.
    // Boss: MMLU-Pro / IFEval planning quality
    // Coder: HumanEval / EvalPlus pass@1
    // Researcher: knowledge density, web synthesis quality
    // Tester: structured JSON output reliability (BFCL v3)
    int BossScore       = 5,
    int CoderScore      = 5,
    int ResearcherScore = 5,
    int TesterScore     = 5,

    // ── Boss prompt supplement ────────────────────────────────────────────────
    // Extra text appended to the BossDecomposeSystemPrompt for models that need
    // additional guidance to produce well-decomposed plans. Null = no supplement.
    // Use for models with known planning weaknesses (e.g. collapse to 1 empty task).
    string? BossPromptSupplement = null
)
{
    public int ContextK => ContextTokens / 1024;

    /// <summary>Composite quality score weighted for swarm orchestration.</summary>
    public double SwarmScore =>
        (BossScore * 0.30) + (CoderScore * 0.35) + (ResearcherScore * 0.20) + (TesterScore * 0.15);
}

public static class ModelProfiles
{
    // ── Score legend ─────────────────────────────────────────────────────────
    // BossScore:       MMLU-Pro + IFEval planning; 10 = GPT-4o class decomposition
    // CoderScore:      HumanEval/EvalPlus pass@1 normalised to 10; 10 ≈ 95%+ pass@1
    // ResearcherScore: knowledge density, web synthesis, factual citation quality
    // TesterScore:     BFCL v3 structured output + JSON schema adherence; 10 = 95%+ valid JSON
    // MinVramGb:       practical VRAM floor at default (Q4) quant; API models = 0

    private static readonly Dictionary<string, ModelProfile> _profiles = new()
    {
        // ── Coding specialists ───────────────────────────────────────────────
        // HumanEval pass@1: 92.7%  |  BFCL: ~77%  |  MMLU: 81%
        ["qwen2.5-coder:32b"] = new(
            "qwen2.5-coder:32b", "Qwen2.5-Coder 32B", 32_768, true,
            ["coding", "architecture", "refactor", "tests"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 25, Temperature: 0.05, TimeoutSeconds: 180, AutoVerify: true,
            Description: "Highest local coding quality. Best for complex multi-file tasks.",
            MinVramGb: 20, ParamsBillions: 32, Speed: SpeedTier.Slow,
            BossScore: 7, CoderScore: 9, ResearcherScore: 7, TesterScore: 8
        ),
        // HumanEval: 86%  |  BFCL: ~73%  |  MMLU: 79%
        ["qwen2.5-coder:14b"] = new(
            "qwen2.5-coder:14b", "Qwen2.5-Coder 14B", 32_768, true,
            ["coding", "debug", "refactor", "tests"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 20, Temperature: 0.05, TimeoutSeconds: 90, AutoVerify: true,
            Description: "Best speed/quality balance for code. Fits in 10 GB VRAM. ★ Recommended",
            MinVramGb: 10, ParamsBillions: 14, Speed: SpeedTier.Medium,
            BossScore: 6, CoderScore: 8, ResearcherScore: 6, TesterScore: 7
        ),
        // HumanEval: 75%  |  BFCL: ~65%
        ["qwen2.5-coder:7b"] = new(
            "qwen2.5-coder:7b", "Qwen2.5-Coder 7B", 32_768, true,
            ["coding", "fast", "debug"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 15, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: true,
            Description: "Fast coder. Great for quick edits and smaller tasks.",
            MinVramGb: 5, ParamsBillions: 7, Speed: SpeedTier.Fast,
            BossScore: 5, CoderScore: 7, ResearcherScore: 5, TesterScore: 6
        ),

        // ── General purpose / instruct ───────────────────────────────────────
        // MMLU: 80%  |  BFCL: ~76%
        ["qwen2.5:14b-instruct"] = new(
            "qwen2.5:14b-instruct", "Qwen2.5 14B Instruct", 32_768, true,
            ["general", "reasoning", "analysis", "writing"],
            ToolSet.Full, PromptStyle.General,
            MaxSteps: 18, Temperature: 0.1, TimeoutSeconds: 90, AutoVerify: false,
            Description: "Strong general model. Good for reasoning, planning, and writing.",
            MinVramGb: 10, ParamsBillions: 14, Speed: SpeedTier.Medium,
            BossScore: 7, CoderScore: 6, ResearcherScore: 7, TesterScore: 7
        ),

        // ── Hermes / Heretic fine-tunes ──────────────────────────────────────
        // Hermes 4 BFCL: ~74%  |  agentic-tuned, strong tool-use
        ["hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M"] = new(
            "hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M",
            "Hermes 4 14B (Q5)", 32_768, true,
            ["agent", "reasoning", "tool-use", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 20, Temperature: 0.1, TimeoutSeconds: 120, AutoVerify: true,
            Description: "Nous Hermes 4 — strong agentic reasoning with excellent tool-use.",
            MinVramGb: 11, ParamsBillions: 14, Speed: SpeedTier.Medium,
            BossScore: 7, CoderScore: 7, ResearcherScore: 7, TesterScore: 8
        ),
        ["hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M"] = new(
            "hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M",
            "GPT-OSS 20B Heretic (Q4)", 32_768, true,
            ["coding", "agent", "reasoning"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.1, TimeoutSeconds: 150, AutoVerify: true,
            Description: "20B heretic fine-tune. High capacity for complex coding tasks.",
            MinVramGb: 13, ParamsBillions: 20, Speed: SpeedTier.Medium,
            BossScore: 7, CoderScore: 8, ResearcherScore: 7, TesterScore: 7
        ),
        ["hf.co/bartowski/p-e-w_phi-4-heretic-GGUF:Q4_K_M"] = new(
            "hf.co/bartowski/p-e-w_phi-4-heretic-GGUF:Q4_K_M",
            "Phi-4 Heretic (Q4)", 16_384, true,
            ["reasoning", "coding", "math", "structured"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 15, Temperature: 0.1, TimeoutSeconds: 90, AutoVerify: true,
            Description: "Phi-4 with heretic RLHF. Excellent at structured reasoning and math.",
            MinVramGb: 9, ParamsBillions: 14, Speed: SpeedTier.Medium,
            BossScore: 7, CoderScore: 7, ResearcherScore: 6, TesterScore: 8
        ),
        ["hf.co/bartowski/p-e-w_Llama-3.1-8B-Instruct-heretic-GGUF:Q4_K_M"] = new(
            "hf.co/bartowski/p-e-w_Llama-3.1-8B-Instruct-heretic-GGUF:Q4_K_M",
            "Llama 3.1 8B Heretic (Q4)", 32_768, true,
            ["general", "chat", "fast", "coding"],
            ToolSet.Coding, PromptStyle.Coding,
            MaxSteps: 12, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: false,
            Description: "Fast 8B with heretic fine-tuning. Good balance of speed and quality.",
            MinVramGb: 5, ParamsBillions: 8, Speed: SpeedTier.Fast,
            BossScore: 5, CoderScore: 6, ResearcherScore: 5, TesterScore: 6
        ),
        ["hf.co/cognitivecomputations/Dolphin3.0-Llama3.1-8B-GGUF:Q4_0"] = new(
            "hf.co/cognitivecomputations/Dolphin3.0-Llama3.1-8B-GGUF:Q4_0",
            "Dolphin 3.0 Llama 8B (Q4)", 32_768, true,
            ["uncensored", "coding", "agentic", "fast"],
            ToolSet.Coding, PromptStyle.Agent,
            MaxSteps: 12, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: false,
            Description: "Dolphin 3.0 — uncensored Llama 3.1 8B. Fast agentic tasks.",
            MinVramGb: 5, ParamsBillions: 8, Speed: SpeedTier.Fast,
            BossScore: 4, CoderScore: 6, ResearcherScore: 4, TesterScore: 5
        ),

        // ── NVIDIA Nemotron ──────────────────────────────────────────────────
        // BFCL: ~48%  |  HumanEval: ~52%  |  128K context is its strength
        ["nemotron-3-nano:4b"] = new(
            "nemotron-3-nano:4b", "Nemotron 3 Nano 4B", 131_072, true,
            ["agent", "reasoning", "tool-use", "fast", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 18, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: true,
            Description: "NVIDIA Nemotron 3 Nano 4B — agentic-tuned, 128K context, 2.8 GB. Fast local agent.",
            MinVramGb: 3, ParamsBillions: 4, Speed: SpeedTier.Fast,
            BossScore: 4, CoderScore: 4, ResearcherScore: 4, TesterScore: 3
        ),
        ["nemotron-3-nano:4b-q8_0"] = new(
            "nemotron-3-nano:4b-q8_0", "Nemotron 3 Nano 4B (Q8)", 131_072, true,
            ["agent", "reasoning", "tool-use", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 18, Temperature: 0.1, TimeoutSeconds: 60, AutoVerify: true,
            Description: "NVIDIA Nemotron 3 Nano 4B Q8 — higher precision, 4.2 GB. Better quality vs 4b base.",
            MinVramGb: 5, ParamsBillions: 4, Speed: SpeedTier.Fast,
            BossScore: 4, CoderScore: 5, ResearcherScore: 4, TesterScore: 4
        ),

        // ── Gemma 4 ──────────────────────────────────────────────────────────
        // BFCL: ~78%  |  256K context  |  native function calling
        // ⚠ BOSS WARNING (observed 2026-06-09): Gemma4:12b consistently outputs
        //   title:"Execute goal" description:"" when asked to decompose tasks.
        //   Root cause: ignores multi-task decomposition instructions, collapses to
        //   a single empty Coder task. BossScore set to 2 (effectively unusable as boss).
        //   Excellent as coder/researcher under a capable boss — CoderScore/ResearcherScore accurate.
        //   Requires custom Modelfile with baked-in few-shot planning examples to work as boss.
        ["gemma4:12b"] = new(
            "gemma4:12b", "Gemma 4 12B", 262_144, true,
            ["coding", "reasoning", "tool-use", "multimodal", "research"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.3, TimeoutSeconds: 90, AutoVerify: true,
            Description: "Google Gemma 4 12B — 256K context, ~7 GB VRAM. ★ Excellent coder/researcher. ⚠ Not recommended as Boss (under-plans). Use theorc-boss:gemma4 for the tuned boss variant.",
            MinVramGb: 7, ParamsBillions: 12, Speed: SpeedTier.Fast,
            BossScore: 2, CoderScore: 8, ResearcherScore: 8, TesterScore: 8,
            BossPromptSupplement: """

## CRITICAL PLANNING REQUIREMENT FOR THIS MODEL

You MUST produce EXACTLY 3–4 tasks. Never produce 1 task. Never leave "description" empty.

WRONG (do not do this):
{"tasks":[{"role":"Coder","priority":1,"title":"Execute goal","description":""}]}

CORRECT example for a CSV tool goal:
{"plan":"Build with Python/Tkinter: researcher gathers library docs, two coders split backend and UI.","tasks":[{"role":"RESEARCHER","priority":1,"title":"Research pandas and tkinter file dialog APIs","description":"Investigate: (1) pandas read_csv and DataFrame cleaning methods (dropna, drop_duplicates, str.strip, rename). (2) tkinter.filedialog.askopenfilename and asksaveasfilename usage. Return a 1-page summary with import statements and function signatures the coders will use."},{"role":"CODER","priority":2,"title":"Write csv_cleaner.py with pandas cleaning functions","description":"Create csv_cleaner.py. Implement: trim_whitespace(df), remove_blank_rows(df), remove_duplicates(df), normalize_headers(df) — each returns a cleaned DataFrame. Use the research findings. Include if __name__=='__main__' test block."},{"role":"CODER","priority":2,"title":"Write main.py with tkinter UI for file load, preview, clean, export","description":"Create main.py. Import csv_cleaner. Build a tkinter window with: File>Open button (filedialog), ttk.Treeview for CSV preview, checkboxes for each cleaning op, Clean button that calls csv_cleaner functions, File>Save As button. Use 900x600 geometry."},{"role":"UIDEVELOPER","priority":2,"title":"Write sample_data.csv and README.md","description":"Create sample_data.csv with 8 rows including: 2 blank rows, 2 duplicates, headers with spaces and mixed case, cells with leading/trailing whitespace. Create README.md with: setup (pip install pandas), how to run (python main.py), feature list, screenshot placeholder."}]}

Always follow that structure. Each task MUST have a non-empty description of at least 2 sentences.
"""
        ),
        // ── TheOrc custom boss model — gemma4:12b tuned for planning ────────────
        // Created via: ollama create theorc-boss:gemma4 -f theorc-boss-gemma4.Modelfile
        // Key differences from gemma4:12b base:
        //   • temperature 0.2 (vs 1.0 default) — prevents single-task collapse
        //   • num_ctx 16384 (vs 4096 default) — room for full plan + context
        //   • Few-shot MESSAGE pairs baked in — calibrates output format at model level
        //   • SYSTEM prompt aligned with BossDecomposeSystemPrompt
        // Benchmark expectation: BossScore 6 (rehabilitated from 2 → should produce
        // multi-task plans). Requires installation on the Ollama server first.
        ["theorc-boss:gemma4"] = new(
            "theorc-boss:gemma4", "TheOrc Boss — Gemma 4 12B (tuned)", 16_384, true,
            ["coding", "reasoning", "planning", "research", "boss"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.2, TimeoutSeconds: 90, AutoVerify: true,
            Description: "Gemma 4 12B tuned for boss/planning role. Baked-in temperature 0.2, 16K ctx, few-shot examples. Install via: ollama create theorc-boss:gemma4 -f theorc-boss-gemma4.Modelfile",
            MinVramGb: 7, ParamsBillions: 12, Speed: SpeedTier.Fast,
            BossScore: 6, CoderScore: 8, ResearcherScore: 8, TesterScore: 8
        ),

        ["gemma4:e4b"] = new(
            "gemma4:e4b", "Gemma 4 (E4B)", 32_768, true,
            ["general", "chat", "fast", "reasoning"],
            ToolSet.Coding, PromptStyle.General,
            MaxSteps: 15, Temperature: 0.2, TimeoutSeconds: 60, AutoVerify: false,
            Description: "Google Gemma 4 efficient 4B model. Very fast with 32k context.",
            MinVramGb: 3, ParamsBillions: 4, Speed: SpeedTier.Fast,
            BossScore: 5, CoderScore: 5, ResearcherScore: 5, TesterScore: 5
        ),
        ["phi4-mini:latest"] = new(
            "phi4-mini:latest", "Phi-4 Mini", 16_384, true,
            ["reasoning", "math", "compact", "fast"],
            ToolSet.Minimal, PromptStyle.Coding,
            MaxSteps: 10, Temperature: 0.1, TimeoutSeconds: 45, AutoVerify: false,
            Description: "Microsoft Phi-4 Mini. Fastest option for simple tasks.",
            MinVramGb: 3, ParamsBillions: 4, Speed: SpeedTier.Fast,
            BossScore: 4, CoderScore: 5, ResearcherScore: 4, TesterScore: 5
        ),
        ["llama3.1:8b"] = new(
            "llama3.1:8b", "Llama 3.1 8B", 32_768, true,
            ["general", "chat", "fast"],
            ToolSet.Minimal, PromptStyle.General,
            MaxSteps: 12, Temperature: 0.2, TimeoutSeconds: 60, AutoVerify: false,
            Description: "Standard Llama 3.1 8B. Very fast. Good for quick questions.",
            MinVramGb: 5, ParamsBillions: 8, Speed: SpeedTier.Fast,
            BossScore: 5, CoderScore: 5, ResearcherScore: 5, TesterScore: 5
        ),

        // ── Security / unrestricted stack ────────────────────────────────────
        // DeepSeek Coder V2: HumanEval ~82%  |  strong on algorithms
        ["deepseek-coder-v2:16b"] = new(
            "deepseek-coder-v2:16b", "DeepSeek-Coder V2 16B", 163_840, true,
            ["coding", "security", "algorithms", "debug", "exploit"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 20, Temperature: 0.1, TimeoutSeconds: 120, AutoVerify: true,
            Description: "DeepSeek Coder V2 16B — strong on code, security tooling. 160k context.",
            MinVramGb: 10, ParamsBillions: 16, Speed: SpeedTier.Medium,
            BossScore: 6, CoderScore: 8, ResearcherScore: 6, TesterScore: 7
        ),
        // Mistral Small 3.1: BFCL ~79%  |  128K context
        ["mistral-small"] = new(
            "mistral-small", "Mistral Small 3.1 24B", 131_072, true,
            ["agent", "security", "reasoning", "tool-use", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.1, TimeoutSeconds: 150, AutoVerify: true,
            Description: "Mistral Small 3.1 — native tool calling, 128k context, fewer restrictions.",
            MinVramGb: 15, ParamsBillions: 24, Speed: SpeedTier.Medium,
            BossScore: 7, CoderScore: 7, ResearcherScore: 7, TesterScore: 8
        ),
        ["mistral-small:latest"] = new(
            "mistral-small:latest", "Mistral Small 3.1 24B", 131_072, true,
            ["agent", "security", "reasoning", "tool-use", "coding"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 22, Temperature: 0.1, TimeoutSeconds: 150, AutoVerify: true,
            Description: "Mistral Small 3.1 — native tool calling, 128k context.",
            MinVramGb: 15, ParamsBillions: 24, Speed: SpeedTier.Medium,
            BossScore: 7, CoderScore: 7, ResearcherScore: 7, TesterScore: 8
        ),
        ["hf.co/bartowski/dolphin-2.9.2-qwen2-7b-GGUF:Q5_K_M"] = new(
            "hf.co/bartowski/dolphin-2.9.2-qwen2-7b-GGUF:Q5_K_M",
            "Dolphin 2.9.2 Qwen2 7B (Q5)", 32_768, true,
            ["uncensored", "security", "coding", "agentic", "tool-use"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 15, Temperature: 0.1, TimeoutSeconds: 75, AutoVerify: true,
            Description: "Dolphin 2.9.2 on Qwen2 base — explicitly uncensored, good tool calling.",
            MinVramGb: 6, ParamsBillions: 7, Speed: SpeedTier.Fast,
            BossScore: 5, CoderScore: 6, ResearcherScore: 5, TesterScore: 6
        ),

        // ── Large / agent-optimised ──────────────────────────────────────────
        // Devstral: SWE-bench 46.8%  |  128K context  |  purpose-built for agents
        ["devstral:24b"] = new(
            "devstral:24b", "Devstral 24B", 131_072, true,
            ["agent", "coding", "planning", "multi-file"],
            ToolSet.Full, PromptStyle.Agent,
            MaxSteps: 30, Temperature: 0.1, TimeoutSeconds: 180, AutoVerify: true,
            Description: "Mistral agent model. SWE-bench 46.8%. 128k context. Best for long autonomous tasks.",
            MinVramGb: 15, ParamsBillions: 24, Speed: SpeedTier.Medium,
            BossScore: 8, CoderScore: 8, ResearcherScore: 7, TesterScore: 8
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
