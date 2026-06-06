using System.Text.Json;
using OrchestratorSetup.Models;

namespace OrchestratorSetup.Services;

/// <summary>
/// Writes the selected .agent.md profile template to the install location
/// and creates/updates the OrchestratorIDE settings.json so the installed
/// app knows which inference backend and model to use.
/// </summary>
public static class ProfileMerger
{
    // ── .agent.md ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies the selected coding profile template to
    /// <c>{appInstallPath}\.agent.md</c> so the installed app can find it.
    /// Also writes a copy alongside the settings.json for first-launch workspace init.
    /// </summary>
    public static void WriteAgentMd(InstallerState state)
    {
        var profile = CodingProfile.All.FirstOrDefault(p => p.Id == state.SelectedProfileId)
                      ?? CodingProfile.All[0];

        // Source: Resources/Profiles/ next to the setup exe
        var srcPath = FindProfileTemplate(profile.AgentMdFile);
        if (srcPath is null) return; // not bundled — skip silently

        // Destination: next to the installed app exe
        var destDir = state.AppInstallPath;
        Directory.CreateDirectory(destDir);
        File.Copy(srcPath, Path.Combine(destDir, ".agent.md"), overwrite: true);
    }

    private static string? FindProfileTemplate(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "Profiles", fileName),
            Path.Combine(AppContext.BaseDirectory, "Profiles",             fileName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ── settings.json ─────────────────────────────────────────────────────────

    /// <summary>
    /// Writes an <c>AppSettings</c>-compatible JSON file to
    /// <c>%APPDATA%\OrchestratorIDE\settings.json</c> so the installed app
    /// starts up already configured for the chosen backend and model.
    /// </summary>
    public static void WriteAppSettings(InstallerState state)
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrchestratorIDE");
        Directory.CreateDirectory(appDataDir);

        var settingsPath = Path.Combine(appDataDir, "settings.json");

        // Build a settings object that mirrors OrchestratorIDE.Core.AppSettings
        // We write raw JSON rather than taking a project reference to keep the
        // installer self-contained.
        var settings = new
        {
            backend              = state.UseExistingOllama ? "Ollama" : "LlamaCpp",
            ollamaHost           = "http://localhost:11434",
            llamaCppRuntimePath  = state.UseExistingOllama
                                       ? ""
                                       : state.LlamaRuntimeExtractPath,
            llamaCppModelPath    = state.UseExistingOllama
                                       ? ""
                                       : state.ModelFilePath,
            llamaCppPort         = 8080,
            llamaCppGpuLayers    = -1,        // -1 = offload all (auto)
            llamaCppContextSize  = 8192,
            llamaCppThreads      = 0,         // 0 = auto-detect
            defaultModel         = state.UseExistingOllama
                                       ? "qwen2.5-coder:14b"
                                       : Path.GetFileNameWithoutExtension(state.ModelFilePath),
            maxStepsOverride     = 0,
            autoVerify           = true,
            autoCheckpoint       = true,
            defaultWorkspace     = "",
            showActivityLog      = true,
            activityLogHeight    = 180.0,
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented        = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        File.WriteAllText(settingsPath, json);
    }

    // ── Desktop / Start Menu shortcuts (stub — Phase G) ───────────────────────

    /// <summary>
    /// Placeholder for Phase G shortcut creation.
    /// Creates a simple .lnk via WScript.Shell COM if available, otherwise skips.
    /// </summary>
    public static void CreateShortcuts(InstallerState state)
    {
        var exePath = Path.Combine(state.AppInstallPath, "OrchestratorIDE.exe");
        if (!File.Exists(exePath)) return; // nothing to point to yet

        if (state.CreateDesktopShortcut)
            TryCreateShortcut(exePath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                             "The Orc.lnk"));

        if (state.CreateStartMenuShortcut)
        {
            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "The Orc");
            Directory.CreateDirectory(startMenuDir);
            TryCreateShortcut(exePath, Path.Combine(startMenuDir, "The Orc.lnk"));
        }
    }

    private static void TryCreateShortcut(string targetPath, string shortcutPath)
    {
        try
        {
            // Use WScript.Shell COM object — available on all Windows versions
            var shell    = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            var shortcut = shell!.GetType().InvokeMember(
                "CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [shortcutPath]);

            shortcut!.GetType().InvokeMember("TargetPath",
                System.Reflection.BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcut.GetType().InvokeMember("Description",
                System.Reflection.BindingFlags.SetProperty, null, shortcut,
                ["The Orc — Local AI Coding Assistant"]);
            shortcut.GetType().InvokeMember("Save",
                System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        catch { /* shortcut creation is best-effort */ }
    }
}
