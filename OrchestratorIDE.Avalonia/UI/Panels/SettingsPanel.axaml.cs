// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UI.Panels;

public partial class SettingsPanel : UserControl
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string RepoOwner  = "hardcoreerik";
    private const string RepoName   = "TheOrc";
    private const string RepoUrl    = $"https://github.com/{RepoOwner}/{RepoName}";
    private const string IssuesApi  = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/issues?state=open&per_page=20";
    private const string CommitsApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits?per_page=10";

    private static readonly string DefaultSourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "source");

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<AppSettings>? SettingsSaved;
    public event Func<Task>?          CheckUpdatesRequested;
    public event Func<Task>?          RegenerateAgentFileRequested;
    public event Action<string>?      OpenFolderAsWorkspaceRequested;
    public event Action<string>?      ScanAnalysisReady;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly OllamaClient _ollama;
    private AppSettings _current = new();
    private static readonly HttpClient _ghHttp = BuildGitHubClient();

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsPanel(OllamaClient ollama)
    {
        InitializeComponent();
        _ollama = ollama;
        TbInstallPath.Text = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location) ?? "(unknown)";
    }

    // ── Load / Read ───────────────────────────────────────────────────────────

    public void LoadSettings(AppSettings s)
    {
        _current = s;

        TbOllamaHost.Text             = s.OllamaHost;
        TbDefaultModel.Text           = s.DefaultModel;
        TbMaxSteps.Text               = s.MaxStepsOverride.ToString();
        TglAutoVerify.IsChecked       = s.AutoVerify;
        TglAutoCheckpoint.IsChecked   = s.AutoCheckpoint;
        TglRestoreLastModel.IsChecked = s.RestoreLastModel;
        TglAutoModelSwitch.IsChecked  = s.AutoModelSwitch;
        TglCheckUpdates.IsChecked     = s.CheckForUpdates;
        TbDefaultWorkspace.Text       = s.DefaultWorkspace;
        TbStatus.Text                 = "";

        TbSourceFolder.Text = string.IsNullOrEmpty(s.SourceFolderPath)
            ? DefaultSourceFolder
            : s.SourceFolderPath;

        var current = UpdateChecker.CurrentVersion();
        var known   = s.LastKnownLatestVersion;
        TbVersionInfo.Text = string.IsNullOrEmpty(known)
            ? $"v{current} installed"
            : $"v{current} installed  •  latest: v{known}";

        RefreshParallelStatus();
        SetComboToSlots(s.OllamaParallelSlots > 1
            ? s.OllamaParallelSlots
            : OllamaParallelHelper.DetectCurrentSlots());

        var recommended = OllamaParallelHelper.RecommendedSlots(s.DetectedVramGb);
        TbSlotHint.Text = s.DetectedVramGb > 0
            ? $"← {recommended} recommended ({s.DetectedVramGb:F0} GB VRAM)"
            : "(select based on available VRAM)";

        RefreshSourceButtons();
    }

    private void RefreshParallelStatus()
    {
        var slots = OllamaParallelHelper.DetectCurrentSlots();
        TbParallelStatus.Text       = OllamaParallelHelper.StatusText(slots);
        TbParallelStatus.Foreground = new SolidColorBrush(Color.Parse(OllamaParallelHelper.StatusColor(slots)));
        TbParallelExplain.Text      = OllamaParallelHelper.GetExplanation(slots);
    }

    private void RefreshSourceButtons()
    {
        var folder  = TbSourceFolder.Text.Trim();
        var hasRepo = Directory.Exists(Path.Combine(folder, ".git"));
        BtnGrabSource.Content                = hasRepo ? "↺  Pull Latest" : "⬇  Grab Source";
        BtnOpenSourceAsWorkspace.IsEnabled   = Directory.Exists(folder);
    }

    private void SetComboToSlots(int slots)
    {
        foreach (ComboBoxItem item in CbParallelSlots.Items)
        {
            if (item.Content?.ToString() == slots.ToString())
            {
                CbParallelSlots.SelectedItem = item;
                return;
            }
        }
        CbParallelSlots.SelectedIndex = 0;
    }

    private int SelectedSlots()
    {
        if (CbParallelSlots.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Content?.ToString(), out var n))
            return n;
        return 1;
    }

    private AppSettings ReadSettings()
    {
        var s = _current;
        s.OllamaHost          = TbOllamaHost.Text.Trim().TrimEnd('/');
        s.DefaultModel        = TbDefaultModel.Text.Trim();
        s.MaxStepsOverride    = int.TryParse(TbMaxSteps.Text, out var n) ? Math.Max(0, n) : 0;
        s.AutoVerify          = TglAutoVerify.IsChecked       == true;
        s.AutoCheckpoint      = TglAutoCheckpoint.IsChecked   == true;
        s.RestoreLastModel    = TglRestoreLastModel.IsChecked  == true;
        s.AutoModelSwitch     = TglAutoModelSwitch.IsChecked   == true;
        s.CheckForUpdates     = TglCheckUpdates.IsChecked      == true;
        s.DefaultWorkspace    = TbDefaultWorkspace.Text.Trim();
        s.OllamaParallelSlots = SelectedSlots();
        s.SourceFolderPath    = TbSourceFolder.Text.Trim();
        return s;
    }

    // ── Test connection ───────────────────────────────────────────────────────

    private async void BtnTestConn_Click(object? sender, RoutedEventArgs e)
    {
        BtnTestConn.IsEnabled = false;
        SetStatus("Testing…", "#CCA700");

        var host     = TbOllamaHost.Text.Trim().TrimEnd('/');
        var original = _ollama.Host;
        _ollama.Host = host;

        try
        {
            var models = await _ollama.GetInstalledModelsAsync();
            SetStatus(models.Count > 0
                ? $"✓  Connected — {models.Count} models found"
                : "⚠  Connected but no models returned",
                models.Count > 0 ? "#76B900" : "#CCA700");
        }
        catch (Exception ex)
        {
            _ollama.Host = original;
            SetStatus($"✗  {ex.Message}", "#F44747");
        }
        finally { BtnTestConn.IsEnabled = true; }
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();
        if (string.IsNullOrWhiteSpace(settings.OllamaHost))
        {
            SetStatus("✗  Ollama host cannot be empty", "#F44747");
            return;
        }
        settings.Save();
        SetStatus("✓  Saved", "#76B900");
        SettingsSaved?.Invoke(settings);
    }

    // ── Check for updates ─────────────────────────────────────────────────────

    private async void BtnCheckNow_Click(object? sender, RoutedEventArgs e)
    {
        BtnCheckNow.IsEnabled = false;
        SetStatus("Checking for updates…", "#CCA700");
        if (CheckUpdatesRequested != null)
            await CheckUpdatesRequested.Invoke();
        BtnCheckNow.IsEnabled = true;
        SetStatus("", "#76B900");
    }

    // ── Regenerate agent file ─────────────────────────────────────────────────

    private async void BtnRegenerateAgentFile_Click(object? sender, RoutedEventArgs e)
    {
        BtnRegenerateAgentFile.IsEnabled = false;
        if (RegenerateAgentFileRequested != null)
            await RegenerateAgentFileRequested.Invoke();
        BtnRegenerateAgentFile.IsEnabled = true;
    }

    // ── Multi-agent parallel ──────────────────────────────────────────────────

    private void BtnSetPermanent_Click(object? sender, RoutedEventArgs e)
    {
        var slots = SelectedSlots();
        try
        {
            OllamaParallelHelper.SetPermanently(slots);
            SetStatus($"✓  OLLAMA_NUM_PARALLEL={slots} written. Restart Ollama to apply.", "#76B900");
            RefreshParallelStatus();
        }
        catch (Exception ex) { SetStatus($"✗  {ex.Message}", "#F44747"); }
    }

    private async void BtnCopyRestartCmd_Click(object? sender, RoutedEventArgs e)
    {
        var cmd = OllamaParallelHelper.GetRestartCommand(SelectedSlots());
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is IClipboard cb)
            {
                await cb.SetTextAsync(cmd);
                SetStatus("✓  Restart command copied — paste into a PowerShell window.", "#76B900");
            }
            else
            {
                SetStatus($"Command: {cmd}", "#CCA700");
            }
        }
        catch { SetStatus($"Command: {cmd}", "#CCA700"); }
    }

    // ── Workspace browse ──────────────────────────────────────────────────────

    private async void BtnBrowseWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose default workspace folder", AllowMultiple = false });
        if (folders.Count > 0)
            TbDefaultWorkspace.Text = folders[0].Path.LocalPath;
    }

    // ── Install folder links ──────────────────────────────────────────────────

    private void BtnOpenInstallFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            OpenInExplorer(path);
        else
            SetStatus("✗  Install folder not found", "#F44747");
    }

    private void BtnOpenDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrchestratorIDE");
        Directory.CreateDirectory(path);
        OpenInExplorer(path);
    }

    // ── Source folder browse ──────────────────────────────────────────────────

    private async void BtnBrowseSourceFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose folder for TheOrc source code", AllowMultiple = false });
        if (folders.Count > 0)
        {
            TbSourceFolder.Text = folders[0].Path.LocalPath;
            RefreshSourceButtons();
        }
    }

    // ── Grab Source ───────────────────────────────────────────────────────────

    private async void BtnGrabSource_Click(object? sender, RoutedEventArgs e)
    {
        var folder = TbSourceFolder.Text.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            SetSelfStatus("✗  Set a source folder first.", "#F44747");
            return;
        }

        BtnGrabSource.IsEnabled = false;
        var isExisting = Directory.Exists(Path.Combine(folder, ".git"));

        if (isExisting)
        {
            SetSelfStatus("Pulling latest from GitHub/main…", "#CCA700");
            await RunGitAsync("pull", folder);
        }
        else
        {
            SetSelfStatus($"Cloning {RepoUrl} into {folder}…", "#CCA700");
            Directory.CreateDirectory(folder);
            await RunGitAsync($"clone {RepoUrl} .", folder);
        }

        BtnGrabSource.IsEnabled = true;
        RefreshSourceButtons();
    }

    private async Task RunGitAsync(string arguments, string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
                SetSelfStatus($"✓  Done: {(stdout + stderr).Trim().Split('\n').LastOrDefault() ?? "OK"}", "#76B900");
            else
                SetSelfStatus($"✗  git exited {proc.ExitCode}: {stderr.Trim().Split('\n').FirstOrDefault()}", "#F44747");
        }
        catch (Exception ex)
        {
            SetSelfStatus($"✗  {ex.Message} (is git installed?)", "#F44747");
        }
    }

    // ── Open source as workspace ──────────────────────────────────────────────

    private void BtnOpenSourceAsWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        var folder = TbSourceFolder.Text.Trim();
        if (!Directory.Exists(folder))
        {
            SetSelfStatus("✗  Source folder not found. Grab Source first.", "#F44747");
            return;
        }

        _current.SourceFolderPath = folder;
        _current.Save();

        OpenFolderAsWorkspaceRequested?.Invoke(folder);
        SetSelfStatus($"✓  Opened {Path.GetFileName(folder)} as workspace.", "#76B900");
    }

    // ── Scan GitHub for improvements ──────────────────────────────────────────

    private async void BtnScanImprovements_Click(object? sender, RoutedEventArgs e)
    {
        BtnScanImprovements.IsEnabled = false;
        SetSelfStatus("Fetching GitHub issues + commits…", "#CCA700");

        try
        {
            var (issues, commits) = await FetchGitHubDataAsync();

            if (issues == null && commits == null)
            {
                SetSelfStatus("✗  Could not reach GitHub API. Check your network.", "#F44747");
                return;
            }

            var prompt = BuildScanPrompt(issues, commits);
            SetSelfStatus($"✓  Fetched {issues?.Count ?? 0} issues, {commits?.Count ?? 0} commits — sending to agent…", "#76B900");
            ScanAnalysisReady?.Invoke(prompt);
        }
        catch (Exception ex)
        {
            SetSelfStatus($"✗  {ex.Message}", "#F44747");
        }
        finally
        {
            BtnScanImprovements.IsEnabled = true;
        }
    }

    private async Task<(List<GitHubIssue>? issues, List<GitHubCommit>? commits)>
        FetchGitHubDataAsync()
    {
        List<GitHubIssue>?  issues  = null;
        List<GitHubCommit>? commits = null;

        try
        {
            var issueJson  = await _ghHttp.GetStringAsync(IssuesApi);
            var issueArray = JsonNode.Parse(issueJson)?.AsArray();
            if (issueArray != null)
            {
                issues = issueArray
                    .Select(n => new GitHubIssue(
                        Number:  n?["number"]?.GetValue<int>() ?? 0,
                        Title:   n?["title"]?.GetValue<string>()   ?? "",
                        Body:    n?["body"]?.GetValue<string>()    ?? "",
                        Labels:  n?["labels"]?.AsArray()
                                   .Select(l => l?["name"]?.GetValue<string>() ?? "")
                                   .Where(s => s.Length > 0)
                                   .ToList() ?? [],
                        HtmlUrl: n?["html_url"]?.GetValue<string>() ?? ""))
                    .ToList();
            }
        }
        catch { /* non-fatal — partial data is fine */ }

        try
        {
            var commitJson  = await _ghHttp.GetStringAsync(CommitsApi);
            var commitArray = JsonNode.Parse(commitJson)?.AsArray();
            if (commitArray != null)
            {
                commits = commitArray
                    .Select(n => new GitHubCommit(
                        Sha:     (n?["sha"]?.GetValue<string>() ?? "")[..Math.Min(7, n?["sha"]?.GetValue<string>()?.Length ?? 0)],
                        Message: n?["commit"]?["message"]?.GetValue<string>()?.Split('\n')[0] ?? "",
                        Author:  n?["commit"]?["author"]?["name"]?.GetValue<string>() ?? ""))
                    .ToList();
            }
        }
        catch { /* non-fatal */ }

        return (issues, commits);
    }

    private static string BuildScanPrompt(
        List<GitHubIssue>?  issues,
        List<GitHubCommit>? commits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TheOrc Self-Improvement Scan");
        sb.AppendLine();
        sb.AppendLine($"You are TheOrc — the Orchestrator IDE — reviewing your own GitHub repository ({RepoUrl}).");
        sb.AppendLine("Analyze the open issues and recent commits below, then:");
        sb.AppendLine("1. **Prioritize** the top 3 bugs or regressions that should be fixed first.");
        sb.AppendLine("2. **Identify** the most impactful improvement or feature request.");
        sb.AppendLine("3. **Suggest** one specific code change (file, function, what to change and why).");
        sb.AppendLine("4. **Flag** any issue that is stale, duplicate, or out-of-scope.");
        sb.AppendLine();

        if (issues?.Count > 0)
        {
            sb.AppendLine($"## Open Issues ({issues.Count})");
            foreach (var iss in issues)
            {
                var labels = iss.Labels.Count > 0 ? $" [{string.Join(", ", iss.Labels)}]" : "";
                sb.AppendLine($"- #{iss.Number}{labels}: **{iss.Title}**");
                if (!string.IsNullOrWhiteSpace(iss.Body))
                {
                    var body = iss.Body.Replace("\r", "").Replace("\n", " ").Trim();
                    if (body.Length > 150) body = body[..150] + "…";
                    sb.AppendLine($"  > {body}");
                }
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Open Issues\n_(none fetched)_\n");
        }

        if (commits?.Count > 0)
        {
            sb.AppendLine($"## Recent Commits ({commits.Count})");
            foreach (var c in commits)
                sb.AppendLine($"- `{c.Sha}` {c.Message} — {c.Author}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("Respond with a prioritized action plan. Be specific and concise. Reference issue numbers.");
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private void SetStatus(string msg, string hex)
    {
        TbStatus.Text       = msg;
        TbStatus.Foreground = new SolidColorBrush(Color.Parse(hex));
    }

    private void SetSelfStatus(string msg, string hex)
    {
        TbSelfImproveStatus.Text       = msg;
        TbSelfImproveStatus.Foreground = new SolidColorBrush(Color.Parse(hex));
    }

    private static HttpClient BuildGitHubClient()
    {
        var v      = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TheOrc", $"{v.Major}.{v.Minor}.{v.Build}"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private record GitHubIssue(int Number, string Title, string Body, List<string> Labels, string HtmlUrl);
    private record GitHubCommit(string Sha, string Message, string Author);
}
