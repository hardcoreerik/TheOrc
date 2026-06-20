// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Avalonia port of the WPF first-run personalisation wizard
/// (OrchestratorIDE/UI/FirstRunWindow.xaml.cs). Shown once after install (when
/// AppSettings.FirstRunComplete == false) and reused for the Settings panel's
/// "Regenerate Agent File" action — same dual-purpose split as the WPF original.
///
/// On Save: writes a personalised .agent.md to the current workspace,
///          sets FirstRunComplete = true, and saves settings.
/// On Skip: sets FirstRunComplete = true (never shown again) without
///          writing a file — user can regenerate from Settings later.
///
/// <b>TbPreview is a TextBlock, not a TextBox, by deliberate finding, not preference.</b>
/// A read-only TextBox here (wrapped, AcceptsReturn, fixed Height — matching the WPF
/// original) reproducibly hangs Avalonia 12.0.4's headless layout pass: OnLoaded
/// completes (confirmed via instrumented diagnostic logging during triage), but the
/// subsequent Dispatcher.UIThread.RunJobs() never returns — a genuine layout cycle, not
/// application logic (confirmed by swapping the assigned text for a placeholder, which
/// did not hang, then for the real generated text via TextBlock, which did not hang
/// either). Isolated with a standalone throwaway window reproducing the exact control
/// tree before landing on TextBox-vs-TextBlock as the actual variable. TextBlock is also
/// simply the correct control here regardless — this content is never edited/selected.
/// </summary>
public partial class FirstRunWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string _workspaceRoot;
    private readonly IReadOnlyList<string> _installedModels;

    // Trust level selected in this wizard (starts from saved setting)
    private TrustLevel _selectedTrust;

    public FirstRunWindow(AppSettings settings, string workspaceRoot,
                          IReadOnlyList<string>? installedModels = null)
    {
        InitializeComponent();
        _settings = settings;
        _workspaceRoot = workspaceRoot;
        _installedModels = installedModels ?? [];
        _selectedTrust = settings.TrustLevel;

        Loaded += OnLoaded;
        TbName.TextChanged += (_, _) => RefreshPreview();
    }

    // ── Initialise ─────────────────────────────────────────────────────────

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        TbName.Text = _settings.AgentUserName;
        TbExtra.Text = _settings.AgentExtraContext;
        TbHardwareSummary.Text = BuildHardwareSummary();
        ApplyTrustPills(_selectedTrust);

        bool hasNemotron = _installedModels.Any(
            m => m.Contains("nemotron", StringComparison.OrdinalIgnoreCase));
        BdrSwarmHint.IsVisible = hasNemotron;

        RefreshPreview();
    }

    private string BuildHardwareSummary()
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(_settings.DetectedGpuName))
        {
            var gpu = _settings.DetectedGpuName;
            if (_settings.DetectedVramGb > 0)
                gpu += $"  ·  {_settings.DetectedVramGb:0.#} GB VRAM";
            if (!string.IsNullOrWhiteSpace(_settings.DetectedCudaVersion))
                gpu += $"  ·  CUDA {_settings.DetectedCudaVersion}";
            lines.Add($"GPU        {gpu}");
        }
        else
        {
            lines.Add("GPU        (not detected — run installer for hardware scan)");
        }

        if (!string.IsNullOrWhiteSpace(_settings.DetectedRuntime))
            lines.Add($"Runtime    {_settings.DetectedRuntime}");

        var model = System.IO.Path.GetFileNameWithoutExtension(_settings.LlamaCppModelPath);
        if (string.IsNullOrWhiteSpace(model)) model = _settings.DefaultModel;
        if (!string.IsNullOrWhiteSpace(model))
            lines.Add($"Model      {model}");

        var backend = _settings.Backend == InferenceBackend.LlamaCpp
            ? "llama.cpp  (local)"
            : $"Ollama  ({_settings.OllamaHost})";
        lines.Add($"Backend    {backend}");

        if (!string.IsNullOrWhiteSpace(_workspaceRoot))
            lines.Add($"Workspace  {_workspaceRoot}");

        return string.Join("\n", lines);
    }

    // ── Live preview ────────────────────────────────────────────────────────

    private void RefreshPreview()
    {
        TbPreview.Text = AgentFileGenerator.Preview(
            _settings,
            TbName.Text?.Trim() ?? "",
            TbExtra.Text?.Trim() ?? "");
    }

    private void TbExtra_TextChanged(object? sender, TextChangedEventArgs e)
        => RefreshPreview();

    // ── Trust level ─────────────────────────────────────────────────────────

    private void BtnTrustPill_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string tag }) return;
        if (Enum.TryParse<TrustLevel>(tag, out var level))
        {
            _selectedTrust = level;
            ApplyTrustPills(level);
        }
    }

    private void ApplyTrustPills(TrustLevel active)
    {
        var pills = new (Button Btn, TrustLevel Level)[]
        {
            (BtnTrustPlan,     TrustLevel.Plan),
            (BtnTrustGuarded,  TrustLevel.Guarded),
            (BtnTrustStandard, TrustLevel.Standard),
            (BtnTrustFullAuto, TrustLevel.FullAuto),
        };

        foreach (var (btn, level) in pills)
        {
            bool isActive = level == active;
            var color = TrustLevelInfo.ActiveColor(level);
            btn.Background = isActive
                ? Brush.Parse(color + "33")  // tinted fill
                : Brushes.Transparent;
            btn.Foreground = isActive
                ? Brush.Parse(color)
                : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            btn.BorderBrush = isActive
                ? Brush.Parse(color)
                : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        }

        TbTrustDesc.Text = active switch
        {
            TrustLevel.Plan     => "📋  Plan — Read-only. The agent describes what it would do, but never writes files or runs commands.",
            TrustLevel.Guarded  => "🛡  Guarded — Every file write and shell command pauses and waits for your approval. Recommended for most users.",
            TrustLevel.Standard => "⚡  Standard — File writes are auto-approved; shell commands still require a click. Good for experienced users.",
            TrustLevel.FullAuto => "🔓  Full Auto — All tool calls run without prompts. Only use this in a sandboxed or throwaway environment.",
            _                   => ""
        };
    }

    // ── Buttons ─────────────────────────────────────────────────────────────

    private async void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        BtnSave.IsEnabled = false;
        BtnSave.Content = "Saving…";

        // A review pass caught two related problems with the original ordering (which mirrored
        // the WPF original's same bug, not introduced here): FirstRunComplete was set and saved
        // BEFORE awaiting GenerateAsync, so a failure mid-write left the wizard permanently
        // marked complete with no .agent.md ever produced and no way for the user to see why or
        // retry. And this is an `async void` event handler — an unhandled exception here is
        // unobservable, not just inconvenient. Fixed by moving the "mark complete" step to AFTER
        // a successful write, and catching failures instead of letting them vanish.
        try
        {
            _settings.AgentUserName = TbName.Text?.Trim() ?? "";
            _settings.AgentExtraContext = TbExtra.Text?.Trim() ?? "";
            _settings.TrustLevel = _selectedTrust;

            await AgentFileGenerator.GenerateAsync(
                _settings,
                _workspaceRoot,
                _settings.AgentUserName,
                _settings.AgentExtraContext);

            _settings.FirstRunComplete = true;
            _settings.Save();

            Close(true);
        }
        catch (Exception ex)
        {
            BtnSave.IsEnabled = true;
            BtnSave.Content = "✓  Save & Start";
            TbTrustDesc.Text = $"⚠ Could not save: {ex.Message}";
        }
    }

    private void BtnSkip_Click(object? sender, RoutedEventArgs e)
    {
        // Mark complete so we never show again, but don't write a file.
        // Still persist the trust level in case the user picked one.
        try
        {
            _settings.TrustLevel = _selectedTrust;
            _settings.FirstRunComplete = true;
            _settings.Save();
        }
        catch (Exception ex)
        {
            // Settings.Save() can throw (disk full, permissions, locked file) — a review pass
            // caught that this was unguarded in a UI event handler, where an unhandled exception
            // has no graceful recovery path. Skip is the low-stakes path (no file write to lose),
            // so close anyway rather than trapping the user in the wizard over a settings-save
            // failure; just don't let it propagate as an unhandled exception.
            System.Diagnostics.Debug.WriteLine($"FirstRunWindow: failed to save settings on skip: {ex.Message}");
        }

        Close(false);
    }
}
