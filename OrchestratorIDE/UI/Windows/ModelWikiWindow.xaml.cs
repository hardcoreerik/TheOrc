using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Models;
using OrchestratorIDE.Services.ToolCalls;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Code-behind for Model Wiki / Lab window.
///
/// Merges model data from all sources (ModelProfiles, installed list, GOBLIN MIND probes,
/// swarm metrics, built-in observations, user test results) into a browseable wiki.
///
/// Layout:
///   Left  — search box, filter chips, model ListBox
///   Right — per-model detail (sections A–F)
/// </summary>
public partial class ModelWikiWindow : Window
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly AppSettings           _settings;
    private List<ModelWikiListItem>        _allItems       = [];
    private List<ModelWikiListItem>        _filteredItems  = [];
    private readonly HashSet<string>       _activeFilters  = [];
    private ModelWikiListItem?             _selected;

    // ── Construction ──────────────────────────────────────────────────────────

    public ModelWikiWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Loaded += OnLoaded;
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetStatus("Loading model catalogue…");

        try
        {
            // Fetch installed model list from Ollama (background)
            var installedModels = await Task.Run(async () =>
            {
                try
                {
                    var ollama = new OllamaClient(_settings.OllamaHost ?? "http://192.168.1.15:11434");
                    return await ollama.GetInstalledModelsAsync();
                }
                catch
                {
                    return (IReadOnlyList<string>)[];
                }
            });

            // Build wiki entries and wrap in ViewModel
            var entries = await Task.Run(() =>
                ModelWikiService.BuildAll(installedModels));

            _allItems = entries.Select(e => new ModelWikiListItem(e)).ToList();
            ApplyFilters();

            SetStatus($"{_allItems.Count} models · {installedModels.Count} installed");
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading models: {ex.Message}");
        }
    }

    // ── Filter / search logic ─────────────────────────────────────────────────

    private void ApplyFilters()
    {
        var query = TbSearch.Text?.Trim().ToLowerInvariant() ?? "";

        _filteredItems = _allItems.Where(item =>
        {
            // Text search
            if (!string.IsNullOrEmpty(query))
            {
                if (!item.DisplayName.ToLowerInvariant().Contains(query) &&
                    !item.Entry.ModelId.ToLowerInvariant().Contains(query))
                    return false;
            }

            // Filter chips — each active filter is AND'd
            foreach (var filter in _activeFilters)
            {
                switch (filter)
                {
                    case "Installed":
                        if (!item.Entry.IsInstalled) return false;
                        break;
                    case "Boss":
                        if (item.Entry.Profile.BossScore < 6) return false;
                        break;
                    case "Coder":
                        if (item.Entry.Profile.CoderScore < 6) return false;
                        break;
                    case "Researcher":
                        if (item.Entry.Profile.ResearcherScore < 6) return false;
                        break;
                    case "Tester":
                        if (item.Entry.Profile.TesterScore < 6) return false;
                        break;
                    case "LongWrite":
                        // Positive filter: models that are good for long write_file
                        if (item.Entry.HasLongWriteWarning) return false;
                        if (item.Entry.Profile.CoderScore < 7) return false;
                        break;
                    case "Fast":
                        if (item.Entry.Profile.Speed != SpeedTier.Fast) return false;
                        break;
                    default:
                        // GOBLIN MIND category chips ("Cat:FileOps" …) — require a
                        // probe that passed the category; unprobed models drop out.
                        // Fails CLOSED: an unparseable Cat: tag matches nothing
                        // rather than silently broadening results.
                        if (filter.StartsWith("Cat:", StringComparison.Ordinal))
                        {
                            if (!Enum.TryParse<Services.ToolCalls.CategoryId>(filter[4..], out var cat))
                                return false;
                            var map = item.Entry.ProbeProfile?.CategoryProfile;
                            if (map is null || !map.CanHandle(cat)) return false;
                        }
                        break;
                }
            }

            return true;
        }).ToList();

        ModelList.ItemsSource = _filteredItems;

        // Re-select previously selected item if it's still visible
        if (_selected != null)
        {
            var match = _filteredItems.FirstOrDefault(i =>
                string.Equals(i.Entry.ModelId, _selected.Entry.ModelId,
                    StringComparison.OrdinalIgnoreCase));
            ModelList.SelectedItem = match;
        }
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

    private void TbSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TbSearchHint.Visibility = string.IsNullOrEmpty(TbSearch.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        BtnClearSearch.Visibility = string.IsNullOrEmpty(TbSearch.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilters();
    }

    private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
    {
        TbSearch.Clear();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        var tag = tb.Tag?.ToString() ?? "";
        if (tb.IsChecked == true)
            _activeFilters.Add(tag);
        else
            _activeFilters.Remove(tag);
        ApplyFilters();
    }

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is ModelWikiListItem item)
        {
            _selected = item;
            ShowDetail(item.Entry);
        }
        else
        {
            ShowEmpty();
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        _allItems.Clear();
        _filteredItems.Clear();
        _activeFilters.Clear();
        // Uncheck every chip in the panel (covers category chips with no x:Name)
        foreach (var chip in FilterPanel.Children.OfType<ToggleButton>())
            chip.IsChecked = false;
        TbSearch.Clear();
        ModelList.ItemsSource = null;
        ShowEmpty();
        OnLoaded(this, new RoutedEventArgs());
    }

    private void BtnProbeNow_Click(object sender, RoutedEventArgs e)
    {
        // Opens the GOBLIN MIND probe window. On close, the selected entry is
        // REBUILT from the stores (not just re-rendered) so fresh probe results
        // actually appear — the cached entry predates the probe run.
        var win = new Tests.ToolCallTestWindow(_settings) { Owner = this };
        win.Closed += (_, _) =>
        {
            if (_selected is null) return;

            var installed = _allItems
                .Where(i => i.Entry.IsInstalled)
                .Select(i => i.Entry.ModelId)
                .ToList();
            var fresh = ModelWikiService.BuildEntry(_selected.Entry.ModelId, installed);
            var freshItem = new ModelWikiListItem(fresh);

            int idx = _allItems.FindIndex(i =>
                string.Equals(i.Entry.ModelId, fresh.ModelId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) _allItems[idx] = freshItem;

            _selected = freshItem;
            ApplyFilters();
            ShowDetail(fresh);
        };
        win.Show();
    }

    private void BtnCompare_Click(object sender, RoutedEventArgs e)
    {
        if (_allItems.Count < 2)
        {
            SetStatus("Need at least two models in the catalogue to compare.");
            return;
        }
        var win = new ModelCompareWindow(
            _allItems.Select(i => i.Entry).ToList(),
            leftId: _selected?.Entry.ModelId) { Owner = this };
        win.Show();
    }

    private void BtnExportMatrix_Click(object sender, RoutedEventArgs e)
    {
        if (_allItems.Count == 0)
        {
            SetStatus("Nothing to export yet — wait for the catalogue to load.");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export capability matrix",
            FileName   = $"TheOrc-Model-Capability-Matrix-{DateTime.Now:yyyyMMdd}.md",
            Filter     = "Markdown (*.md)|*.md|All files (*.*)|*.*",
            DefaultExt = ".md",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var entries = _allItems.Select(i => i.Entry).ToList();
            System.IO.File.WriteAllText(dlg.FileName, ModelWikiExporter.ToMarkdown(entries));
            SetStatus($"Capability matrix exported: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}");
        }
    }

    private void BtnRunCapabilityTest_Click(object sender, RoutedEventArgs e)
    {
        var modelId = _selected?.Entry.ModelId ?? "";
        var dlg = new ModelCapabilityTestDialog(_settings, modelId) { Owner = this };
        dlg.ShowDialog();

        // Refresh the selected entry's detail pane after a test run
        if (_selected != null)
        {
            var all = ModelWikiService.BuildAll(
                _selected.Entry.IsInstalled
                    ? new[] { _selected.Entry.ModelId }
                    : Array.Empty<string>());
            var refreshed = all.FirstOrDefault(e =>
                string.Equals(e.ModelId, _selected.Entry.ModelId,
                    StringComparison.OrdinalIgnoreCase));
            if (refreshed != null) ShowDetail(refreshed);
        }
    }

    // ── Detail pane rendering ─────────────────────────────────────────────────

    private void ShowEmpty()
    {
        DetailEmpty.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowDetail(ModelWikiEntry entry)
    {
        DetailEmpty.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;

        // Header
        TbDetailName.Text = entry.DisplayName;
        TbDetailId.Text   = entry.ModelId;
        TbSpeed.Text      = entry.SpeedLabel;

        if (entry.IsInstalled)
        {
            BdInstalled.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x3A, 0x0A));
            TbInstalled.Text       = "Installed";
            TbInstalled.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0xCA, 0x4A));
        }
        else
        {
            BdInstalled.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1C));
            TbInstalled.Text       = "Not installed";
            TbInstalled.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x8A, 0x6A));
        }

        // Sections A–F
        RenderSummary(entry);
        RenderScores(entry);
        RenderObservations(entry);
        RenderTrends(entry);
        RenderToolReliability(entry);
        RenderRouting(entry);
        TbLoraGuidance.Text = ModelWikiService.GetLoraGuidance(entry);
    }

    // ── Section A: Summary ────────────────────────────────────────────────────

    private void RenderSummary(ModelWikiEntry entry)
    {
        GridSummary.Children.Clear();

        var p = entry.Profile;

        var rows = new (string Label, string Value, bool wrap)[]
        {
            ("Parameters",   p.ParamsBillions > 0 ? $"{p.ParamsBillions}B" : "Unknown", false),
            ("VRAM (min)",   entry.VramLabel,  false),
            ("Speed",        entry.SpeedLabel, false),
            ("Primary role", entry.PrimaryRole, false),
            ("Description",  string.IsNullOrWhiteSpace(p.Description) ? "—" : p.Description, true),
        };

        for (int i = 0; i < rows.Length; i++)
        {
            var (label, value, wrap) = rows[i];

            // Single-column rows for wrapped content
            if (wrap)
            {
                int lastRow = GridSummary.RowDefinitions.Count > i / 2 ? i / 2 : i / 2;
                var lbl = MakeLabel(label);
                var val = MakeValue(value);
                val.TextWrapping = TextWrapping.Wrap;

                // Place description as a wide row spanning all columns
                Grid.SetRow(lbl, i / 2); Grid.SetColumn(lbl, 0);
                Grid.SetRow(val, i / 2); Grid.SetColumn(val, 1);
                Grid.SetColumnSpan(val, 3);
                GridSummary.Children.Add(lbl);
                GridSummary.Children.Add(val);
            }
            else
            {
                int row = i / 2, baseCol = (i % 2) * 2;
                var lbl = MakeLabel(label);
                var val = MakeValue(value);
                Grid.SetRow(lbl, row); Grid.SetColumn(lbl, baseCol);
                Grid.SetRow(val, row); Grid.SetColumn(val, baseCol + 1);
                GridSummary.Children.Add(lbl);
                GridSummary.Children.Add(val);
            }
        }
    }

    // ── Section B: Role Scores ────────────────────────────────────────────────

    private void RenderScores(ModelWikiEntry entry)
    {
        PnlScores.Children.Clear();
        var p = entry.Profile;

        var roles = new (string Name, int Score)[]
        {
            ("Boss",       p.BossScore),
            ("Coder",      p.CoderScore),
            ("Researcher", p.ResearcherScore),
            ("Tester",     p.TesterScore),
        };

        foreach (var (name, score) in roles)
            PnlScores.Children.Add(BuildScoreRow(name, score));
    }

    private static UIElement BuildScoreRow(string role, int score)
    {
        var color = score >= 7 ? "#4ACA4A"
                  : score >= 5 ? "#E8A030"
                  :              "#E84040";

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text              = role,
            Foreground        = new SolidColorBrush(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4)),
            FontFamily        = new FontFamily("Segoe UI"),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);

        var barBg = new Border
        {
            Background        = new SolidColorBrush(Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)),
            CornerRadius      = new CornerRadius(2),
            Height            = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var barFill = new Border
        {
            Background          = ParseBrush(color),
            CornerRadius        = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width               = Math.Max(4, score * 20.0),
        };
        barBg.Child = barFill;
        Grid.SetColumn(barBg, 1);

        var scoreLabel = new TextBlock
        {
            Text              = $"{score}/10",
            Foreground        = ParseBrush(color),
            FontFamily        = new FontFamily("Consolas"),
            FontSize          = 11,
            Margin            = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(scoreLabel, 2);

        grid.Children.Add(label);
        grid.Children.Add(barBg);
        grid.Children.Add(scoreLabel);
        return grid;
    }

    // ── Section C: Observations ────────────────────────────────────────────────

    // ── Section C2: Result trends ──────────────────────────────────────────────

    /// <summary>
    /// Chronological outcome strip over every local result for this model:
    /// capability tests (squares) and swarm runs (tall bars), oldest → newest,
    /// green/amber/red by outcome, with a pass-rate trend line underneath.
    /// Plain WPF shapes — no charting packages.
    /// </summary>
    private void RenderTrends(ModelWikiEntry entry)
    {
        PnlTrends.Children.Clear();

        var events = entry.CapabilityTests
            .Select(t => (When: t.Timestamp, Kind: "test",
                          Ok: t.Result == "pass" ? 1.0 : t.Result == "partial" ? 0.5 : 0.0,
                          Tip: $"{t.Timestamp:yyyy-MM-dd HH:mm}  {t.TestName}: {t.Result}" +
                               (string.IsNullOrEmpty(t.Notes) ? "" : $"\n{t.Notes}")))
            .Concat(entry.SwarmRuns
                .Select(r => (When: r.StartedAt, Kind: "swarm",
                              Ok: r.SwarmSucceeded ? 1.0 : 0.0,
                              Tip: $"{r.StartedAt:yyyy-MM-dd HH:mm}  swarm run " +
                                   $"({(r.SwarmSucceeded ? "succeeded" : "failed")}, {r.FilesWritten} files)\n{r.Goal}")))
            .OrderBy(e => e.When)
            .ToList();

        if (events.Count == 0)
        {
            PnlTrends.Children.Add(MutedText(
                "No local results yet — run a capability test or a swarm with this model."));
            return;
        }

        var strip = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var ev in events.TakeLast(60))   // keep the strip readable
        {
            var color = ev.Ok >= 1.0 ? "#4ACA4A" : ev.Ok > 0 ? "#E8A030" : "#E84040";
            strip.Children.Add(new Border
            {
                Width  = ev.Kind == "swarm" ? 10 : 14,
                Height = ev.Kind == "swarm" ? 26 : 14,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, ev.Kind == "swarm" ? 0 : 6, 3, 0),
                Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(color)),
                ToolTip = ev.Tip,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }
        PnlTrends.Children.Add(strip);

        // Trend summary: recent half vs earlier half pass rate
        double Rate(IEnumerable<double> xs) { var l = xs.ToList(); return l.Count == 0 ? 0 : l.Average(); }
        string arrow;
        if (events.Count < 4)
        {
            arrow = "→ not enough data for a trend";   // codex: lone result must not read "declining"
        }
        else
        {
            var half  = events.Count / 2;
            var early = Rate(events.Take(half).Select(e => e.Ok));
            var late  = Rate(events.Skip(half).Select(e => e.Ok));
            arrow = late > early + 0.05 ? "↗ improving"
                  : late < early - 0.05 ? "↘ declining" : "→ steady";
        }

        PnlTrends.Children.Add(new TextBlock
        {
            Text = $"{events.Count} results  ·  pass rate {Rate(events.Select(e => e.Ok)):P0}  ·  trend {arrow}" +
                   $"   (■ capability test  ▌ swarm run, oldest → newest)",
            FontFamily = new FontFamily("Segoe UI"), FontSize = 11,
            Foreground = (Brush)FindResource("Br.Text.Muted"),
            Margin = new Thickness(0, 8, 0, 0),
        });
    }

    private void RenderObservations(ModelWikiEntry entry)
    {
        PnlObservations.Children.Clear();

        if (entry.Observations.Count == 0 && entry.CapabilityTests.Count == 0)
        {
            PnlObservations.Children.Add(MutedText("No local observations recorded yet."));
            return;
        }

        foreach (var obs in entry.Observations)
            PnlObservations.Children.Add(BuildObservationCard(obs));

        foreach (var result in entry.CapabilityTests.Take(10))
            PnlObservations.Children.Add(BuildTestResultCard(result));
    }

    private static UIElement BuildObservationCard(ModelObservation obs)
    {
        var panel = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var resultColor = obs.ResultColor;

        var resultBadge = new Border
        {
            Background      = ParseBrush(resultColor + "33"),
            BorderBrush     = ParseBrush(resultColor),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(2),
            Padding         = new Thickness(6, 2, 6, 2),
        };
        resultBadge.SetValue(DockPanel.DockProperty, Dock.Right);
        resultBadge.Child = new TextBlock
        {
            Text       = obs.Result.ToUpperInvariant(),
            Foreground = ParseBrush(resultColor),
            FontSize   = 10,
            FontFamily = new FontFamily("Segoe UI"),
        };

        var sourceLabel = new TextBlock
        {
            Text              = $"{obs.SourceLabel}  ·  {obs.TestId}  ·  {obs.Date}",
            Foreground        = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x8A, 0x6A)),
            FontSize          = 10,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(resultBadge);
        header.Children.Add(sourceLabel);
        panel.Children.Add(header);

        panel.Children.Add(new TextBlock
        {
            Text         = obs.Summary,
            Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4)),
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight   = 18,
        });

        if (obs.RecommendedUses.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text         = $"✓ {string.Join("  ·  ", obs.RecommendedUses)}",
                Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0xCA, 0x4A)),
                FontSize     = 10,
                FontFamily   = new FontFamily("Segoe UI"),
                Margin       = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        if (obs.NotRecommendedUses.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text         = $"✗ {string.Join("  ·  ", obs.NotRecommendedUses)}",
                Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x40, 0x40)),
                FontSize     = 10,
                FontFamily   = new FontFamily("Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
            });

        return WrapInCard(panel);
    }

    private static UIElement BuildTestResultCard(ModelCapabilityTestResult result)
    {
        var panel = new StackPanel();
        var resultColor = result.ResultColor;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var badge  = new Border
        {
            Background      = ParseBrush(resultColor + "33"),
            BorderBrush     = ParseBrush(resultColor),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(2),
            Padding         = new Thickness(6, 2, 6, 2),
        };
        badge.SetValue(DockPanel.DockProperty, Dock.Right);
        badge.Child = new TextBlock
        {
            Text       = result.Result.ToUpperInvariant(),
            Foreground = ParseBrush(resultColor),
            FontSize   = 10,
            FontFamily = new FontFamily("Segoe UI"),
        };

        var title = new TextBlock
        {
            Text              = $"User test  ·  {result.TestName}  ·  {result.Timestamp:yyyy-MM-dd HH:mm}",
            Foreground        = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x8A, 0x6A)),
            FontSize          = 10,
            FontFamily        = new FontFamily("Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(badge);
        header.Children.Add(title);
        panel.Children.Add(header);

        var detail = $"File written: {result.FileWritten}  ·  " +
                     $"Valid JSON: {result.ValidJson}  ·  " +
                     $"Truncated: {result.Truncated}  ·  " +
                     $"Size: {result.ActualFileSizeBytes:N0} bytes";
        panel.Children.Add(new TextBlock
        {
            Text         = detail,
            Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4)),
            FontFamily   = new FontFamily("Consolas"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(result.Notes))
            panel.Children.Add(new TextBlock
            {
                Text         = result.Notes,
                Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x8A, 0x6A)),
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 4, 0, 0),
            });

        return WrapInCard(panel);
    }

    // ── Section D: Tool-Call Reliability ─────────────────────────────────────

    private void RenderToolReliability(ModelWikiEntry entry)
    {
        PnlToolReliability.Children.Clear();

        var probe = entry.ProbeProfile;
        if (probe == null)
        {
            PnlToolReliability.Children.Add(MutedText(
                "No GOBLIN MIND probe results yet. " +
                "Run tool-call tests from Models → Run Tool Call Tests… to collect data."));
            return;
        }

        // One-line summary from the profile
        PnlToolReliability.Children.Add(new TextBlock
        {
            Text         = probe.Summary,
            Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4)),
            FontFamily   = new FontFamily("Consolas"),
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        // Format fingerprint
        if (probe.FormatProfile != null)
        {
            PnlToolReliability.Children.Add(new TextBlock
            {
                Text       = $"Preferred format: {probe.FormatProfile.PreferredFormat}  " +
                             $"({probe.FormatProfile.ShortSummary})",
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x76, 0xB9, 0x00)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
                Margin     = new Thickness(0, 8, 0, 0),
            });
        }

        // Category profile
        if (probe.CategoryProfile != null)
        {
            PnlToolReliability.Children.Add(new TextBlock
            {
                Text       = $"Category pass rate: {probe.CategoryProfile.ShortSummary}",
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
                Margin     = new Thickness(0, 4, 0, 0),
            });
        }
    }

    // ── Section E: Routing ────────────────────────────────────────────────────

    private void RenderRouting(ModelWikiEntry entry)
    {
        PnlRouting.Children.Clear();

        var rec = ModelWikiService.GetRoutingRecommendation(entry);

        var routingRows = new (string Role, string Value)[]
        {
            ("Boss role",       rec.Boss),
            ("Coder role",      rec.Coder),
            ("Researcher role", rec.Researcher),
            ("Tester role",     rec.Tester),
            ("Single-agent",    rec.SingleAgent),
            ("Swarm worker",    rec.SwarmWorker),
            ("Long write_file", rec.LongWriteFile),
        };

        var routingGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        routingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        routingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        routingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        routingGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        int rowCount = (routingRows.Length + 1) / 2;
        for (int i = 0; i < rowCount; i++)
            routingGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < routingRows.Length; i++)
        {
            var (role, val) = routingRows[i];
            int row = i / 2, baseCol = (i % 2) * 2;

            var lbl = MakeLabel(role);
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, baseCol);

            var color   = rec.ColorFor(val);
            var verdict = new TextBlock
            {
                Text       = val,
                Foreground = ParseBrush(color),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
                FontWeight = FontWeights.SemiBold,
                Margin     = new Thickness(0, 4, 0, 4),
            };
            Grid.SetRow(verdict, row); Grid.SetColumn(verdict, baseCol + 1);

            routingGrid.Children.Add(lbl);
            routingGrid.Children.Add(verdict);
        }

        PnlRouting.Children.Add(routingGrid);

        if (!string.IsNullOrWhiteSpace(rec.Summary))
        {
            PnlRouting.Children.Add(WrapInCard(new TextBlock
            {
                Text         = rec.Summary,
                Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4)),
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight   = 20,
            }));
        }
    }

    // ── Status bar ────────────────────────────────────────────────────────────

    private void SetStatus(string text) => TbStatus.Text = text;

    // ── Small UI helpers ──────────────────────────────────────────────────────

    private static TextBlock MakeLabel(string text) => new()
    {
        Text              = text,
        Foreground        = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x8A, 0x6A)),
        FontFamily        = new FontFamily("Segoe UI"),
        FontSize          = 11,
        Margin            = new Thickness(0, 4, 8, 4),
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static TextBlock MakeValue(string text) => new()
    {
        Text              = text,
        Foreground        = new SolidColorBrush(Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4)),
        FontFamily        = new FontFamily("Segoe UI"),
        FontSize          = 12,
        Margin            = new Thickness(0, 4, 0, 4),
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static TextBlock MutedText(string text) => new()
    {
        Text         = text,
        Foreground   = new SolidColorBrush(Color.FromArgb(0xFF, 0x7A, 0x8A, 0x6A)),
        FontFamily   = new FontFamily("Segoe UI"),
        FontSize     = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Wraps a UIElement in a dark-bordered card border.</summary>
    private static Border WrapInCard(UIElement child) => new()
    {
        Background      = new SolidColorBrush(Color.FromArgb(0xFF, 0x0D, 0x15, 0x0D)),
        BorderBrush     = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x2E, 0x1E)),
        BorderThickness = new Thickness(1),
        CornerRadius    = new CornerRadius(3),
        Padding         = new Thickness(10, 8, 10, 8),
        Margin          = new Thickness(0, 0, 0, 6),
        Child           = child,
    };

    private static SolidColorBrush ParseBrush(string hex)
    {
        try { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return new SolidColorBrush(Colors.Gray); }
    }
}
