// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI;

/// <summary>
/// First-run personalisation wizard.
/// Shown once after install (when AppSettings.FirstRunComplete == false).
/// Also callable from Settings panel via "Regenerate Agent File".
///
/// On Save: writes a personalised .agent.md to the current workspace,
///          sets FirstRunComplete = true, and saves settings.
/// On Skip: sets FirstRunComplete = true (never shown again) without
///          writing a file — user can regenerate from Settings later.
/// </summary>
public partial class FirstRunWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string      _workspaceRoot;
    private readonly IReadOnlyList<string> _installedModels;

    // Trust level selected in this wizard (starts from saved setting)
    private TrustLevel _selectedTrust;

    public FirstRunWindow(AppSettings settings, string workspaceRoot,
                          IReadOnlyList<string>? installedModels = null)
    {
        InitializeComponent();
        _settings        = settings;
        _workspaceRoot   = workspaceRoot;
        _installedModels = installedModels ?? [];
        _selectedTrust   = settings.TrustLevel;

        Loaded += OnLoaded;
        TbName.TextChanged += (_, _) => RefreshPreview();
    }

    // ── Initialise ─────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pre-fill name if already set
        TbName.Text = _settings.AgentUserName;

        // Pre-fill extra context
        TbExtra.Text = _settings.AgentExtraContext;

        // Build the hardware summary display
        TbHardwareSummary.Text = BuildHardwareSummary();

        // Apply trust pill highlight for current saved level
        ApplyTrustPills(_selectedTrust);

        // Show swarm hint if a nemotron model is present
        bool hasNemotron = _installedModels.Any(
            m => m.Contains("nemotron", StringComparison.OrdinalIgnoreCase));
        BdrSwarmHint.Visibility = hasNemotron
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Initial preview
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
            TbName.Text.Trim(),
            TbExtra.Text.Trim());
    }

    private void TbExtra_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshPreview();

    // ── Trust level ─────────────────────────────────────────────────────────

    private void BtnTrustPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (Enum.TryParse<TrustLevel>(tag, out var level))
        {
            _selectedTrust = level;
            ApplyTrustPills(level);
        }
    }

    private void ApplyTrustPills(TrustLevel active)
    {
        // Map of pill button → its tier
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
            var color     = TrustLevelInfo.ActiveColor(level);
            btn.Background = isActive
                ? (Brush)new BrushConverter().ConvertFrom(color + "33")!  // tinted fill
                : Brushes.Transparent;
            btn.Foreground = isActive
                ? (Brush)new BrushConverter().ConvertFrom(color)!
                : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            btn.BorderBrush = isActive
                ? (Brush)new BrushConverter().ConvertFrom(color)!
                : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        }

        // Description line
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

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        BtnSave.IsEnabled = false;
        BtnSave.Content   = "Saving…";

        // Persist user choices to settings
        _settings.AgentUserName     = TbName.Text.Trim();
        _settings.AgentExtraContext = TbExtra.Text.Trim();
        _settings.TrustLevel        = _selectedTrust;
        _settings.FirstRunComplete  = true;
        _settings.Save();

        // Write the .agent.md into the workspace
        await AgentFileGenerator.GenerateAsync(
            _settings,
            _workspaceRoot,
            _settings.AgentUserName,
            _settings.AgentExtraContext);

        DialogResult = true;
        Close();
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
    {
        // Mark complete so we never show again, but don't write a file.
        // Still persist the trust level in case the user picked one.
        _settings.TrustLevel       = _selectedTrust;
        _settings.FirstRunComplete = true;
        _settings.Save();

        DialogResult = false;
        Close();
    }
}
