namespace OrchestratorIDE.Core;

/// <summary>
/// Generates a personalised .agent.md for the user's workspace.
///
/// Structure of the output file:
///
///   ## My System          ← generated header block (hardware + user context)
///   ---
///   [existing profile template content if present]
///
/// The generator is called by:
///   1. The first-run wizard (FirstRunWindow) on initial launch
///   2. Settings panel → "Regenerate Agent File" button
/// </summary>
public static class AgentFileGenerator
{
    private const string AgentFileName = ".agent.md";

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build the personalised .agent.md and write it to <paramref name="workspaceRoot"/>.
    /// If an existing .agent.md is present, its content is preserved below the
    /// generated header block (anything after the first --- separator is kept).
    /// </summary>
    public static async Task GenerateAsync(
        AppSettings settings,
        string      workspaceRoot,
        string      userName,
        string      extraContext)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
            return;

        var path = Path.Combine(workspaceRoot, AgentFileName);

        // Read existing file so we can preserve the profile rules section.
        string profileBody = "";
        if (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path);
            profileBody  = StripSystemBlock(existing);
        }

        var header = BuildSystemBlock(settings, userName, extraContext);
        var merged = string.IsNullOrWhiteSpace(profileBody)
            ? header
            : header + "\n\n---\n\n" + profileBody.TrimStart();

        await File.WriteAllTextAsync(path, merged);
    }

    /// <summary>
    /// Preview what the generated file will look like (no disk write).
    /// </summary>
    public static string Preview(
        AppSettings settings,
        string      userName,
        string      extraContext)
        => BuildSystemBlock(settings, userName, extraContext);

    // ── Block builder ──────────────────────────────────────────────────────

    private static string BuildSystemBlock(
        AppSettings settings,
        string      userName,
        string      extraContext)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Agent Knowledge File — TheOrc");
        sb.AppendLine($"# Generated {DateTime.Now:yyyy-MM-dd HH:mm}  ·  Do not check into source control.");
        sb.AppendLine();
        sb.AppendLine("## My System");
        sb.AppendLine();

        // ── Operator ──────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(userName))
            sb.AppendLine($"**Operator:** {userName}");

        // ── Hardware ──────────────────────────────────────────────────────
        var gpuLine = BuildGpuLine(settings);
        if (!string.IsNullOrWhiteSpace(gpuLine))
            sb.AppendLine($"**GPU:** {gpuLine}");

        // ── Model / Backend ───────────────────────────────────────────────
        var modelName = Path.GetFileNameWithoutExtension(settings.LlamaCppModelPath);
        if (string.IsNullOrWhiteSpace(modelName))
            modelName = settings.DefaultModel;
        if (!string.IsNullOrWhiteSpace(modelName))
            sb.AppendLine($"**Model:** {modelName}");

        var backendLabel = settings.Backend == InferenceBackend.LlamaCpp
            ? "llama.cpp (local, no cloud)"
            : $"Ollama  ({settings.OllamaHost})";
        sb.AppendLine($"**Backend:** {backendLabel}");

        // ── Runtime ───────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(settings.DetectedRuntime))
            sb.AppendLine($"**Runtime variant:** {settings.DetectedRuntime}");

        sb.AppendLine();

        // ── Extra context ─────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(extraContext))
        {
            sb.AppendLine("## Additional Context");
            sb.AppendLine();
            sb.AppendLine(extraContext.Trim());
            sb.AppendLine();
        }

        // ── Standing instructions ─────────────────────────────────────────
        sb.AppendLine("## Standing Instructions");
        sb.AppendLine();
        sb.AppendLine("- When a working implementation already exists in the repo, adapt it. Never reimplement from scratch.");
        sb.AppendLine("- Before declaring a task done: build must be clean (0 errors). State what changed and why.");
        sb.AppendLine("- When the user says something isn't working — believe them. Fix it. Do not defend the existing code.");
        sb.AppendLine("- Commit with a clean styled message when a task completes.");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string BuildGpuLine(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DetectedGpuName)) return "";

        var parts = new List<string> { settings.DetectedGpuName };

        if (settings.DetectedVramGb > 0)
            parts.Add($"{settings.DetectedVramGb:0.#} GB VRAM");

        if (!string.IsNullOrWhiteSpace(settings.DetectedCudaVersion))
            parts.Add($"CUDA {settings.DetectedCudaVersion}");

        return string.Join(" · ", parts);
    }

    // ── Strip helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Remove the generated "My System" header block from an existing .agent.md
    /// so we don't double-up on regeneration. Returns the profile body only.
    ///
    /// The generated block always starts with "# Agent Knowledge File — TheOrc"
    /// and is separated from the profile rules by "---".  If no separator is
    /// found (hand-written file, or first write), we return the whole file
    /// unchanged so nothing is lost.
    /// </summary>
    private static string StripSystemBlock(string existing)
    {
        // If the file starts with our generated header, strip up to the first ---
        if (existing.TrimStart().StartsWith("# Agent Knowledge File — TheOrc",
            StringComparison.Ordinal))
        {
            var sepIdx = existing.IndexOf("\n---\n", StringComparison.Ordinal);
            if (sepIdx >= 0)
                return existing[(sepIdx + 5)..]; // skip "\n---\n"

            // Whole file is the generated block (no profile rules yet)
            return "";
        }

        // Hand-written or profile template — keep as-is
        return existing;
    }
}
