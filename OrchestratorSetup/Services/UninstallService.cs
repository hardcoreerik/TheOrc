// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Win32;
using OrchestratorSetup.Models;

namespace OrchestratorSetup.Services;

/// <summary>
/// Writes the Add/Remove Programs uninstall key during installation and
/// performs file/shortcut/registry cleanup when the user chooses to uninstall.
///
/// Windows-only by design (Apps &amp; Features registration is a Windows concept, not just an
/// unimplemented cross-platform feature) -- every Registry call below is already wrapped in
/// try/catch, so on a non-Windows runtime this safely no-ops (silently skips registration/
/// cleanup) rather than throwing PlatformNotSupportedException. Real cross-platform uninstall
/// (.desktop cleanup, deleting a macOS .app bundle, etc.) is its own Phase 2+ IPlatformInstaller
/// implementation (INSTALLER_REVAMP_SPEC.md §7 Phase 4/5), not a guard added here.
/// </summary>
public static class UninstallService
{
    private const string RegistrySubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OrchestratorIDE";

    // ── Registration (called at install time) ─────────────────────────────────

    /// <summary>
    /// Copies the running setup exe into the install directory and writes the
    /// HKCU uninstall registry key so "Apps &amp; Features" can find it.
    /// </summary>
    public static void Register(InstallerState state)
    {
        // Place the setup exe in the install dir so uninstall still works
        // even if the user moves or deletes the original download.
        var uninstallExe = Path.Combine(state.AppInstallPath, "OrchestratorSetup.exe");
        try
        {
            var runningExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (!string.IsNullOrEmpty(runningExe) &&
                !string.Equals(runningExe, uninstallExe, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(runningExe, uninstallExe, overwrite: true);
            }
        }
        catch { /* non-fatal — uninstall may still work if the user runs it manually */ }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistrySubKey, writable: true);

            key.SetValue("DisplayName",    "TheOrc — AI Coding Assistant");
            key.SetValue("UninstallString",$"\"{uninstallExe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" --uninstall --quiet");
            key.SetValue("DisplayVersion", "1.4.0");
            key.SetValue("Publisher",      "hardcoreerik");
            key.SetValue("InstallLocation", state.AppInstallPath);
            key.SetValue("DisplayIcon",    $"{state.AppExePath},0");
            key.SetValue("URLInfoAbout",   "https://github.com/hardcoreerik/TheOrc");
            key.SetValue("NoModify",  1, RegistryValueKind.DWord);
            key.SetValue("NoRepair",  1, RegistryValueKind.DWord);

            // Estimated size in KB (best effort)
            try
            {
                var dir = new DirectoryInfo(state.AppInstallPath);
                if (dir.Exists)
                {
                    var sizeKb = (int)(dir.EnumerateFiles("*", SearchOption.AllDirectories)
                                          .Sum(f => f.Length) / 1024);
                    key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
                }
            }
            catch { /* non-fatal */ }
        }
        catch { /* non-fatal — registry write may fail in some UAC / policy configs */ }
    }

    // ── Read-back (called at uninstall time) ──────────────────────────────────

    public static string? ReadInstallPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey);
            return key?.GetValue("InstallLocation") as string;
        }
        catch { return null; }
    }

    // ── Uninstall ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes shortcuts, optionally user data (%APPDATA%\OrchestratorIDE),
    /// the registry key, and schedules the install directory for deletion after
    /// this process exits (cmd.exe ping delay).
    /// </summary>
    public static void Uninstall(string installPath, bool removeUserData,
                                 Action<string>? onLog = null)
    {
        void Log(string msg) => onLog?.Invoke(msg);

        // Desktop shortcut
        Log("Removing desktop shortcut…");
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "The Orc.lnk"));

        // Start Menu folder
        Log("Removing Start Menu entry…");
        TryDeleteDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "The Orc"));

        // User data
        if (removeUserData)
        {
            Log("Removing user data…");
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE");
            TryDeleteDir(appData);
        }

        // Registry key
        Log("Removing registry entry…");
        try { Registry.CurrentUser.DeleteSubKeyTree(RegistrySubKey, throwOnMissingSubKey: false); }
        catch { }

        // Install directory — deleted by a cmd.exe that fires after this process exits
        if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
        {
            Log("Scheduling install directory removal…");
            // Two-second delay so our process has time to exit before the delete runs.
            var script = $"/c ping 127.0.0.1 -n 3 >nul & rd /s /q \"{installPath}\"";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "cmd.exe",
                    Arguments       = script,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                    CreateNoWindow  = true,
                    UseShellExecute = false,
                });
            }
            catch { }
        }

        Log("Done.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
