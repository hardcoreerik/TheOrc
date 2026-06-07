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

        // Read from embedded resource (production) or disk (dev mode)
        var content = ReadProfileTemplate(profile.AgentMdFile);
        if (content is null) return; // not bundled — skip silently

        // Write to the app install directory
        var destDir = state.AppInstallPath;
        Directory.CreateDirectory(destDir);
        File.WriteAllText(Path.Combine(destDir, ".agent.md"), content);
    }

    private static string? ReadProfileTemplate(string fileName)
    {
        // Dev mode: file next to exe
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "Profiles", fileName),
            Path.Combine(AppContext.BaseDirectory, "Profiles", fileName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return File.ReadAllText(c);

        // Production single-file: embedded resource
        // Resource suffix: "Profiles.{fileName}" e.g. "Profiles.general.agent.md"
        return EmbeddedResources.ReadText($"Profiles.{fileName}");
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

    // ── Desktop / Start Menu shortcuts ───────────────────────────────────────

    /// <summary>
    /// Creates .lnk shortcuts on the Desktop and/or Start Menu as requested.
    /// Uses WScript.Shell COM — available on every Windows version.
    /// Sets TargetPath, WorkingDirectory, IconLocation (embedded .ico), and Description.
    /// </summary>
    public static void CreateShortcuts(InstallerState state)
    {
        var exePath = Path.Combine(state.AppInstallPath, "OrchestratorIDE.exe");
        if (!File.Exists(exePath)) return; // installer will call this after copying the exe

        if (state.CreateDesktopShortcut)
        {
            var lnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "The Orc.lnk");
            TryCreateShortcut(exePath, lnk, state.AppInstallPath);
        }

        if (state.CreateStartMenuShortcut)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "The Orc");
            Directory.CreateDirectory(dir);
            TryCreateShortcut(exePath, Path.Combine(dir, "The Orc.lnk"),
                              state.AppInstallPath);
        }
    }

    /// <summary>
    /// Creates a Windows shortcut (.lnk) at <paramref name="shortcutPath"/> pointing
    /// at <paramref name="targetPath"/> with the icon embedded in the target exe.
    /// </summary>
    private static void TryCreateShortcut(string targetPath,
                                          string shortcutPath,
                                          string workingDirectory)
    {
        try
        {
            // WScript.Shell is a built-in COM server on every Windows version.
            var shell = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell")!);

            if (shell is null) return;

            var sc = shell.GetType().InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [shortcutPath]);

            if (sc is null) return;

            var t = sc.GetType();

            void Set(string prop, object value) =>
                t.InvokeMember(prop,
                    System.Reflection.BindingFlags.SetProperty,
                    null, sc, [value]);

            void Invoke(string method) =>
                t.InvokeMember(method,
                    System.Reflection.BindingFlags.InvokeMethod,
                    null, sc, null);

            // Target exe
            Set("TargetPath",       targetPath);

            // Launch from the install directory (not System32 or wherever the
            // user double-clicked the shortcut from)
            Set("WorkingDirectory", workingDirectory);

            // Use the embedded icon from the exe (resource index 0)
            // Format: "C:\path\to\app.exe,0"
            Set("IconLocation",     $"{targetPath},0");

            // Tooltip shown in Windows Explorer / Start Menu
            Set("Description",      "TheOrc — Free local AI coding assistant");

            // Persist the shortcut to disk
            Invoke("Save");
        }
        catch { /* shortcut creation is best-effort — never crash the installer */ }
    }
}
