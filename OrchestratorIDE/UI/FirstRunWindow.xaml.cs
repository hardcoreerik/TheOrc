using System.Windows;
using System.Windows.Controls;
using OrchestratorIDE.Core;

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

    public FirstRunWindow(AppSettings settings, string workspaceRoot)
    {
        InitializeComponent();
        _settings      = settings;
        _workspaceRoot = workspaceRoot;

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

    // ── Buttons ─────────────────────────────────────────────────────────────

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        BtnSave.IsEnabled = false;
        BtnSave.Content   = "Saving…";

        // Persist user choices to settings
        _settings.AgentUserName     = TbName.Text.Trim();
        _settings.AgentExtraContext = TbExtra.Text.Trim();
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
        // Mark complete so we never show again, but don't write a file
        _settings.FirstRunComplete = true;
        _settings.Save();

        DialogResult = false;
        Close();
    }
}
