// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OrchestratorSetup.ViewModels;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace OrchestratorSetup.Pages;

/// <summary>
/// INSTALLER_REVAMP_SPEC.md §5.4 — replaces OllamaCheckPage. Native runtime
/// (llama.cpp) is the only default path; this page's interactive choice is
/// scoped to the collapsed "Advanced" section (Ollama opt-in) plus the HIVE
/// MIND join checkbox carried over from the old page unchanged.
/// </summary>
public partial class RuntimeSetupPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;

    private enum OllamaChoice { None, InstallOllama, ExistingOllama }
    private OllamaChoice _ollamaChoice = OllamaChoice.None;

    public RuntimeSetupPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        // Native is the only default path -- nothing for the user to choose at this level.
        _vm.State.UseExistingOllama = false;
        _vm.State.InstallOllama     = false;

        TxtVariantSummary.Text =
            $"Downloads the llama.cpp runtime for the \"{FriendlyVariant(_vm.State.SelectedRuntimeVariant)}\" " +
            "variant selected on the previous page, plus GGUF model files directly from Hugging Face. " +
            "TheOrc starts and stops the runtime automatically — nothing to manage.";

        Loaded += async (_, _) => await DetectOllamaAsync();
    }

    private static string FriendlyVariant(string v) => v switch
    {
        "cuda12" => "CUDA 12.x (NVIDIA RTX)",
        "cuda11" => "CUDA 11.x (NVIDIA)",
        "vulkan" => "Vulkan (AMD / Intel Arc)",
        "avx2"   => "CPU with AVX2",
        "cpu"    => "CPU baseline",
        _        => v,
    };

    // ── Ollama detection (Advanced section) ──────────────────────────────────

    private async Task DetectOllamaAsync()
    {
        bool running = false;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync("http://localhost:11434/api/tags");
            running = resp.IsSuccessStatusCode;
        }
        catch { }

        bool installed = running || IsOllamaInstalled();

        _vm.State.OllamaDetected = installed;
        _vm.State.OllamaRunning  = running;

        UpdateStatusDisplay();
    }

    /// <summary>
    /// Returns true if ollama is found via PATH or a well-known install location.
    /// Windows-only checks (registry, ollama.exe path probing) are gated behind
    /// OperatingSystem.IsWindows() -- this is the Advanced/opt-in section, so a
    /// best-effort PATH check is enough off Windows for Phase 1.
    /// </summary>
    private static bool IsOllamaInstalled()
    {
        try
        {
            var whichCmd = OperatingSystem.IsWindows() ? "where" : "which";
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName               = whichCmd,
                Arguments              = "ollama",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            });
            if (p is not null)
            {
                string? line = p.StandardOutput.ReadLine()?.Trim();
                p.WaitForExit(3000);
                if (!string.IsNullOrEmpty(line) && File.Exists(line)) return true;
            }
        }
        catch { }

        if (!OperatingSystem.IsWindows()) return false;

#if WINDOWS
        // Default install locations (Windows-only paths)
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Ollama", "ollama.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Ollama", "ollama.exe"),
        };

        // Ollama installs PER-USER. When setup runs elevated, the env above resolves to the
        // ADMIN profile, hiding a normal-user Ollama -- scan every real user profile under
        // C:\Users (the actual 2026-06 bug on HARDCOREPC: per-user Ollama invisible to the
        // elevated installer).
        try
        {
            var usersRoot = Path.GetDirectoryName(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (usersRoot != null && Directory.Exists(usersRoot))
                foreach (var prof in Directory.GetDirectories(usersRoot))
                    candidates.Add(Path.Combine(prof, "AppData", "Local",
                        "Programs", "Ollama", "ollama.exe"));
        }
        catch { }

        if (candidates.Any(File.Exists)) return true;

        // Registry uninstall keys (HKLM + HKCU, 64/32-bit views).
        try
        {
            string[] roots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine,
                                         Microsoft.Win32.Registry.CurrentUser })
            foreach (var root in roots)
            {
                using var key = hive.OpenSubKey(root);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(sub);
                    var name = app?.GetValue("DisplayName") as string;
                    if (name != null && name.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch { }
#endif

        return false;
    }

    private void UpdateStatusDisplay()
    {
        if (_vm.State.OllamaRunning)
        {
            TxtOllamaIcon.Text   = "🟢";
            TxtOllamaStatus.Text = "Ollama is running on this machine";
            TxtOllamaDetail.Text = "Detected a running Ollama service at http://localhost:11434.";

            TxtOllamaOptionDetail.Text =
                "Ollama is already running — TheOrc connects to it directly. " +
                "You will need to have a compatible model available in Ollama.";

            CardOllama.IsEnabled = true;
            CardOllama.Opacity   = 1.0;
        }
        else if (_vm.State.OllamaDetected)
        {
            TxtOllamaIcon.Text   = "🟡";
            TxtOllamaStatus.Text = "Ollama is installed but not currently running";
            TxtOllamaDetail.Text = "Installed but its service is not active. " +
                                   "You can still choose it — just start Ollama before launching TheOrc.";

            TxtOllamaOptionDetail.Text =
                "Ollama is installed but not running right now. " +
                "Start Ollama before launching TheOrc, or it won't connect.";

            CardOllama.IsEnabled = true;
            CardOllama.Opacity   = 1.0;
        }
        else
        {
            TxtOllamaIcon.Text   = "⚪";
            TxtOllamaStatus.Text = "Ollama not detected";
            TxtOllamaDetail.Text = "No Ollama service found. Install it below, or stick with the native runtime above.";

            CardOllama.IsEnabled = false;
            CardOllama.Opacity   = 0.4;
        }
    }

    // ── Option selection (Advanced section only) ─────────────────────────────

    private void CardInstallOllama_PointerPressed(object? sender, PointerPressedEventArgs e)
        => SelectOllamaOption(OllamaChoice.InstallOllama);

    private void CardOllama_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CardOllama.IsEnabled)
            SelectOllamaOption(OllamaChoice.ExistingOllama);
    }

    private void SelectOllamaOption(OllamaChoice choice)
    {
        // Toggle off if clicking the already-selected card -- reverts to the native default.
        _ollamaChoice = _ollamaChoice == choice ? OllamaChoice.None : choice;

        _vm.State.InstallOllama     = _ollamaChoice == OllamaChoice.InstallOllama;
        _vm.State.UseExistingOllama = _ollamaChoice == OllamaChoice.ExistingOllama;

        ApplyCardStyle(CardInstallOllama, RadioInstallOllama, _ollamaChoice == OllamaChoice.InstallOllama);
        ApplyCardStyle(CardOllama,        RadioOllama,        _ollamaChoice == OllamaChoice.ExistingOllama);
    }

    private void ApplyCardStyle(Border card, Ellipse radio, bool selected)
    {
        if (!card.IsEnabled) return;

        card.Classes.Set("Selected", selected);
        radio.Fill   = selected ? this.FindResourceOrDefault("Accent")  : Brushes.Transparent;
        radio.Stroke = selected ? this.FindResourceOrDefault("Accent") : this.FindResourceOrDefault("TextMuted");
    }

    private void ChkJoinHive_Changed(object? sender, RoutedEventArgs e)
        => _vm.State.JoinHiveMind = ChkJoinHive.IsChecked == true;

    public bool CanLeave() => true;
}

internal static class ResourceExtensions
{
    /// <summary>
    /// Avalonia equivalent of WPF's FindResource(string) by-name lookup -- used where a
    /// brush needs to be picked dynamically (selected vs. unselected radio fill) rather
    /// than fixed at XAML-compile time via {StaticResource}.
    /// </summary>
    public static IBrush FindResourceOrDefault(this Avalonia.Controls.Control control, string key)
        => control.TryFindResource(key, out var res) && res is IBrush brush ? brush : Brushes.Gray;
}
