using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UI.Windows;

public partial class WarmUpEditorWindow : Window
{
    private readonly AppSettings      _settings;
    private readonly ModelWarmUpService _warmUp;
    private string _currentRole = "worker";

    public WarmUpEditorWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _warmUp   = new ModelWarmUpService(settings);
        Loaded   += OnLoaded;
        Closed   += (_, _) => _warmUp.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ModelWarmUpService.EnsureScriptFiles();
        LoadScript("worker");
    }

    private void BtnTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        SaveCurrentScript();
        _currentRole = btn.Tag?.ToString() ?? "worker";
        LoadScript(_currentRole);
        UpdateTabHighlight();
    }

    private void LoadScript(string role)
    {
        TxtScript.Text = ModelWarmUpService.LoadScript(role);
        if (string.IsNullOrEmpty(TxtScript.Text))
            TxtScript.Text = ModelWarmUpService.DefaultScript(role);
        _currentRole = role;
        UpdateTabHighlight();
    }

    private void SaveCurrentScript()
    {
        if (!string.IsNullOrWhiteSpace(TxtScript.Text))
            ModelWarmUpService.SaveScript(_currentRole, TxtScript.Text);
    }

    private void UpdateTabHighlight()
    {
        var accent = (SolidColorBrush)FindResource("Br.Accent.Green");
        var muted  = (SolidColorBrush)FindResource("Br.Text.Muted");

        foreach (var btn in new[] { BtnTabWorker, BtnTabBoss, BtnTabResearcher })
        {
            bool active = btn.Tag?.ToString() == _currentRole;
            btn.Background = new SolidColorBrush(active
                ? Color.FromRgb(0x1A, 0x3A, 0x1A)
                : Color.FromRgb(0x0D, 0x0D, 0x0D));
            btn.Foreground = active ? accent : muted;
        }
    }

    private void BtnResetDefault_Click(object sender, RoutedEventArgs e)
    {
        TxtScript.Text = ModelWarmUpService.DefaultScript(_currentRole);
        TxtLog.Text    = "Script reset to default — click Save to persist.";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentScript();
        TxtLog.Text = $"✓ {_currentRole} script saved.";
    }

    private async void BtnRunWarmUp_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentScript();
        BtnRunWarmUp.IsEnabled = false;
        PbWarmUp.Value = 0;
        TxtLog.Text    = "Running…";

        try
        {
            await _warmUp.WarmUpAsync(
                _currentRole,
                msg => Dispatcher.InvokeAsync(() => TxtLog.Text = msg),
                pct => Dispatcher.InvokeAsync(() => PbWarmUp.Value = pct));
        }
        catch (Exception ex)
        {
            TxtLog.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BtnRunWarmUp.IsEnabled = true;
        }
    }
}
