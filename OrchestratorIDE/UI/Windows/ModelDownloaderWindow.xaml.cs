using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Model Downloader window — search HuggingFace + curated catalog,
/// view rich model cards, select a quantization variant, and download/register.
/// </summary>
public partial class ModelDownloaderWindow : Window
{
    private readonly AppSettings          _settings;
    private readonly ModelSearchService   _search;
    private readonly ModelDownloadService _downloader;
    private readonly HuggingFaceClient    _hf;

    private List<ModelSearchResult> _results = [];
    private ModelSearchResult?      _selected;
    private List<GgufVariant>       _variants = [];
    private GgufVariant?            _selectedVariant;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _downloadCts;

    private static readonly HashSet<string> _staleCuratedIds = new(StringComparer.OrdinalIgnoreCase);

    public ModelDownloaderWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings   = settings;
        _search     = new ModelSearchService();
        _downloader = new ModelDownloadService();
        _hf         = new HuggingFaceClient();

        Loaded += OnLoaded;
        Closed += (_, _) => OnClosed();
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Show detected GPU info
        TxtHardwareSummary.Text = BuildHardwareSummary();

        // Background stale check — just fires, UI hides stale rows when results come back
        _ = Task.Run(async () =>
        {
            var stale = await _search.VerifyCuratedReposAsync();
            foreach (var id in stale)
                _staleCuratedIds.Add(id);
        });

        TxtSearch.Focus();
    }

    private void OnClosed()
    {
        _searchCts?.Cancel();
        _downloadCts?.Cancel();
        _search.Dispose();
        _downloader.Dispose();
        _hf.Dispose();
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RunSearch();
    }

    private void BtnSearch_Click(object sender, RoutedEventArgs e) => RunSearch();

    private async void RunSearch()
    {
        var query = TxtSearch.Text.Trim();
        if (string.IsNullOrEmpty(query)) return;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        BtnSearch.IsEnabled  = false;
        TxtSearchStatus.Text = "Searching…";
        TxtResultCount.Text  = "";
        ResultsPanel.Children.Clear();

        try
        {
            var vram = DetectVramGb();
            _results = await _search.SearchAsync(query, vram,
                s => Dispatcher.InvokeAsync(() => TxtSearchStatus.Text = s),
                ct);

            // Filter out known-stale curated entries
            _results = _results
                .Where(r => !r.IsCurated || !_staleCuratedIds.Contains(r.Id))
                .ToList();

            PopulateResults(_results);
            TxtResultCount.Text  = $"{_results.Count} result{(_results.Count == 1 ? "" : "s")}";
            TxtSearchStatus.Text = "";
        }
        catch (OperationCanceledException)
        {
            TxtSearchStatus.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            TxtSearchStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BtnSearch.IsEnabled = true;
        }
    }

    // ── Result cards ──────────────────────────────────────────────────────────

    private void PopulateResults(List<ModelSearchResult> results)
    {
        ResultsPanel.Children.Clear();

        if (results.Count == 0)
        {
            ResultsPanel.Children.Add(new TextBlock
            {
                Text       = "No results found.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
                Margin     = new Thickness(8, 16, 8, 0),
            });
            return;
        }

        foreach (var r in results)
            ResultsPanel.Children.Add(BuildResultCard(r));
    }

    private Border BuildResultCard(ModelSearchResult r)
    {
        var card = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Margin          = new Thickness(0, 0, 0, 6),
            Padding         = new Thickness(10, 8, 10, 8),
            Cursor          = Cursors.Hand,
            Tag             = r,
        };

        card.MouseLeftButtonDown += (_, _) => SelectModel(r);

        var sp = new StackPanel();

        // Top row: name + badges
        var topRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };

        if (r.IsCurated)
        {
            topRow.Children.Add(MakePill("⬡", "#4ACA4A", "#1A3A1A"));
        }

        topRow.Children.Add(new TextBlock
        {
            Text       = r.Name,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (r.SwarmCapable)
            topRow.Children.Add(MakePill("🐝", "#4A9FD9", "#1A2A3A"));

        sp.Children.Add(topRow);

        // Publisher + VRAM + stars row
        var metaLine = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            TextWrapping = TextWrapping.NoWrap,
        };

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(r.Publisher)) parts.Add(r.Publisher);
        if (r.VramRecommendedGb > 0)            parts.Add($"{r.VramRecommendedGb} GB VRAM");
        if (r.QualityStars > 0)                 parts.Add(new string('★', r.QualityStars));

        metaLine.Text = string.Join("  ·  ", parts);
        sp.Children.Add(metaLine);

        if (!string.IsNullOrEmpty(r.Description))
        {
            var desc = r.Description;
            if (desc.Length > 100) desc = desc[..97] + "…";
            sp.Children.Add(new TextBlock
            {
                Text       = desc,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 3, 0, 0),
            });
        }

        card.Child = sp;
        return card;
    }

    private static Border MakePill(string text, string fg, string bg)
    {
        var fgColor = (Color)ColorConverter.ConvertFromString(fg);
        var bgColor = (Color)ColorConverter.ConvertFromString(bg);

        return new Border
        {
            Background      = new SolidColorBrush(bgColor),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(4, 1, 4, 1),
            Margin          = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text       = text,
                Foreground = new SolidColorBrush(fgColor),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
            },
        };
    }

    // ── Detail view ───────────────────────────────────────────────────────────

    private async void SelectModel(ModelSearchResult model)
    {
        _selected = model;
        _selectedVariant = null;
        BtnDownloadGguf.IsEnabled = false;

        // Highlight selected card
        foreach (Border card in ResultsPanel.Children.OfType<Border>())
        {
            bool isSelected = card.Tag == model;
            card.BorderBrush = new SolidColorBrush(isSelected
                ? Color.FromRgb(0x4A, 0xCA, 0x4A)
                : Color.FromRgb(0x1A, 0x2A, 0x1A));
        }

        // Show detail pane
        PanelNoSelection.Visibility = Visibility.Collapsed;
        PanelDetail.Visibility      = Visibility.Visible;

        // Fill in details
        TxtDetailName.Text      = model.Name;
        TxtDetailPublisher.Text = string.IsNullOrEmpty(model.Publisher)
            ? model.HuggingFaceId
            : $"{model.Publisher}  ·  {model.HuggingFaceId}";

        BdrCuratedBadge.Visibility = model.IsCurated    ? Visibility.Visible : Visibility.Collapsed;
        BdrSwarmBadge.Visibility   = model.SwarmCapable ? Visibility.Visible : Visibility.Collapsed;

        // Stats
        WpDetailStats.Children.Clear();
        if (model.VramRecommendedGb > 0)
            WpDetailStats.Children.Add(MakeStatChip($"{model.VramRecommendedGb} GB VRAM", "#2A1A0A", "#FFB300"));
        if (model.ContextK > 0)
            WpDetailStats.Children.Add(MakeStatChip($"{model.ContextK}K ctx", "#0A1A2A", "#4A9FD9"));
        if (model.QualityStars > 0)
            WpDetailStats.Children.Add(MakeStatChip(new string('★', model.QualityStars), "#1A1A00", "#FFD700"));
        if (model.CpuOk)
            WpDetailStats.Children.Add(MakeStatChip("CPU OK", "#1A1A1A", "#888888"));
        if (model.HfDownloads > 0)
            WpDetailStats.Children.Add(MakeStatChip($"↓ {FormatCount(model.HfDownloads)}", "#0D0D0D", "#666666"));

        TxtDetailDesc.Text = model.Description;

        BdrIntendedUse.Visibility = string.IsNullOrEmpty(model.IntendedUse) ? Visibility.Collapsed : Visibility.Visible;
        TxtIntendedUse.Text       = model.IntendedUse;

        BdrToolUse.Visibility = string.IsNullOrEmpty(model.ToolUseNotes) ? Visibility.Collapsed : Visibility.Visible;
        TxtToolUse.Text       = model.ToolUseNotes;

        // Swarm roles
        var hasRoles = model.SwarmRoles is { Length: > 0 };
        SpSwarmRoles.Visibility = hasRoles ? Visibility.Visible : Visibility.Collapsed;
        if (hasRoles)
        {
            // Remove old chips (keep the label TextBlock at index 0)
            while (SpSwarmRoles.Children.Count > 1)
                SpSwarmRoles.Children.RemoveAt(SpSwarmRoles.Children.Count - 1);

            foreach (var role in model.SwarmRoles)
            {
                var (fg, bg) = role.ToLowerInvariant() switch
                {
                    "worker"     => ("#4ACA4A", "#0D1A0D"),
                    "boss"       => ("#FFB300", "#1A1400"),
                    "researcher" => ("#4A9FD9", "#0A1420"),
                    _            => ("#888888", "#1A1A1A"),
                };
                SpSwarmRoles.Children.Add(MakePill(role, fg, bg));
            }
        }

        // Pre-select role radio based on model's recommended swarm roles
        if (model.SwarmRoles != null && System.Array.Exists(model.SwarmRoles, r => r == "Boss"))
            RbRoleBoss.IsChecked = true;
        else if (model.SwarmRoles != null && System.Array.Exists(model.SwarmRoles, r => r == "Researcher"))
            RbRoleResearcher.IsChecked = true;
        else
            RbRoleWorker.IsChecked = true;

        // Ollama pull button
        BtnOllamaPull.IsEnabled = !string.IsNullOrEmpty(model.OllamaName);

        // Load variants async
        await LoadVariantsAsync(model);
    }

    private async Task LoadVariantsAsync(ModelSearchResult model)
    {
        SpVariants.Children.Clear();
        TxtVariantLoadStatus.Text = "Loading…";
        _selectedVariant = null;
        BtnDownloadGguf.IsEnabled = false;

        if (string.IsNullOrEmpty(model.HuggingFaceId))
        {
            TxtVariantLoadStatus.Text = "No HuggingFace source";
            return;
        }

        try
        {
            var vram = DetectVramGb();
            _variants = await _search.GetVariantsAsync(model, vram);

            if (_variants.Count == 0)
            {
                TxtVariantLoadStatus.Text = "No GGUF files found";
                return;
            }

            TxtVariantLoadStatus.Text = $"{_variants.Count} variants";

            foreach (var v in _variants)
                SpVariants.Children.Add(BuildVariantRow(v));
        }
        catch (Exception ex)
        {
            TxtVariantLoadStatus.Text = $"Error: {ex.Message}";
        }
    }

    private Border BuildVariantRow(GgufVariant variant)
    {
        var row = new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding         = new Thickness(12, 8, 12, 8),
            Cursor          = Cursors.Hand,
            Tag             = variant,
        };

        if (variant.IsRecommended)
            row.Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x1A, 0x0D));

        row.MouseLeftButtonDown += (_, _) => SelectVariant(variant);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Quant label
        var quantLabel = new TextBlock
        {
            Text       = variant.QuantLabel,
            Foreground = variant.IsRecommended
                ? new SolidColorBrush(Color.FromRgb(0x4A, 0xCA, 0x4A))
                : Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth   = 90,
        };
        Grid.SetColumn(quantLabel, 0);
        grid.Children.Add(quantLabel);

        // Quality hint
        var hint = new TextBlock
        {
            Text       = variant.QualityHint,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(8, 0, 8, 0),
        };
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        // Size
        var sizeLabel = new TextBlock
        {
            Text       = variant.SizeDisplay,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(sizeLabel, 2);
        grid.Children.Add(sizeLabel);

        // VRAM badge
        var vramColor = variant.VramEstimateGb <= DetectVramGb()
            ? Color.FromRgb(0x1A, 0x3A, 0x1A) : Color.FromRgb(0x3A, 0x1A, 0x1A);
        var vramFg   = variant.VramEstimateGb <= DetectVramGb()
            ? Color.FromRgb(0x4A, 0xCA, 0x4A) : Color.FromRgb(0xF4, 0x47, 0x47);

        var vramBadge = new Border
        {
            Background   = new SolidColorBrush(vramColor),
            CornerRadius = new CornerRadius(3),
            Padding      = new Thickness(5, 2, 5, 2),
            Child = new TextBlock
            {
                Text       = $"~{variant.VramEstimateGb} GB",
                Foreground = new SolidColorBrush(vramFg),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 10,
            },
        };
        Grid.SetColumn(vramBadge, 3);
        grid.Children.Add(vramBadge);

        // Recommended badge
        if (variant.IsRecommended)
        {
            grid.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = GridLength.Auto });
            var recBadge = new Border
            {
                Background   = new SolidColorBrush(Color.FromRgb(0x1A, 0x3A, 0x1A)),
                CornerRadius = new CornerRadius(3),
                Padding      = new Thickness(4, 1, 4, 1),
                Margin       = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = "★",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xCA, 0x4A)),
                    FontSize   = 10,
                },
            };
            Grid.SetColumn(recBadge, 0);
            Grid.SetColumn(quantLabel, 1);
            Grid.SetColumn(hint, 2);
            Grid.SetColumn(sizeLabel, 3);
            Grid.SetColumn(vramBadge, 4);
            grid.Children.Add(recBadge);
        }

        row.Child = grid;
        return row;
    }

    private void SelectVariant(GgufVariant variant)
    {
        _selectedVariant = variant;
        BtnDownloadGguf.IsEnabled = !string.IsNullOrEmpty(variant.DownloadUrl);

        // Highlight selected variant
        foreach (Border row in SpVariants.Children.OfType<Border>())
        {
            bool isSel = row.Tag == variant;
            row.BorderBrush = new SolidColorBrush(isSel
                ? Color.FromRgb(0x4A, 0xCA, 0x4A)
                : Color.FromRgb(0x1A, 0x2A, 0x1A));
        }

        SetStatus($"Selected: {variant.QuantLabel} · {variant.SizeDisplay} · ~{variant.VramEstimateGb} GB VRAM");
    }

    // ── Download ──────────────────────────────────────────────────────────────

    private async void BtnDownloadGguf_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedVariant is null) return;
        if (string.IsNullOrEmpty(_selectedVariant.DownloadUrl))
        {
            SetStatus("No download URL available for this variant.");
            return;
        }

        var destDir = _settings.ResolvedModelStoragePath;

        var fileName = Path.GetFileName(_selectedVariant.DownloadUrl.Split('?')[0]);
        var destPath = Path.Combine(destDir, fileName);

        PanelDownloadProgress.Visibility = Visibility.Visible;
        TxtDlFileName.Text  = fileName;
        TxtDlStatus.Text    = "Starting download…";
        PbDownload.Value    = 0;

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        BtnDownloadGguf.IsEnabled = false;

        try
        {
            var progress = new Progress<(long done, long total, double speed, int eta)>(p =>
            {
                var pct = p.total > 0 ? (double)p.done / p.total * 100 : 0;
                PbDownload.Value   = pct;
                TxtDlStats.Text    = $"{FormatBytes(p.done)} / {FormatBytes(p.total)}  {FormatSpeed(p.speed)}  ETA {p.eta}s";
                TxtDlStatus.Text   = $"Downloading {fileName}…";
            });

            await _downloader.DownloadAsync(_selectedVariant.DownloadUrl, destPath, progress, ct);

            TxtDlStatus.Text  = "Download complete. Registering with Ollama…";
            PbDownload.Value  = 100;

            // Auto-register with Ollama if available
            var ollamaName  = _selected.OllamaName;
            if (string.IsNullOrEmpty(ollamaName))
                ollamaName = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

            var logProgress = new Progress<string>(msg => Dispatcher.InvokeAsync(() => TxtDlStatus.Text = msg));
            await _downloader.RegisterWithOllamaAsync(destPath, ollamaName, logProgress, ct);

            // Apply to settings
            var role = GetSelectedRole();
            if (role != "library")
            {
                var identifier = !string.IsNullOrEmpty(_selected.OllamaName)
                    ? _selected.OllamaName
                    : destPath;
                ModelDownloadService.ApplyToSettings(_settings, identifier, role);
            }

            TxtDlStatus.Text = $"✓ Model ready. Role: {role}";
            SetStatus($"✓ {_selected.Name} downloaded and registered as '{role}'.");
        }
        catch (OperationCanceledException)
        {
            TxtDlStatus.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            TxtDlStatus.Text = $"Error: {ex.Message}";
            SetStatus($"Download failed: {ex.Message}");
        }
        finally
        {
            BtnDownloadGguf.IsEnabled = _selectedVariant is not null;
        }
    }

    private async void BtnOllamaPull_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || string.IsNullOrEmpty(_selected.OllamaName)) return;

        BtnOllamaPull.IsEnabled = false;
        PanelDownloadProgress.Visibility = Visibility.Visible;
        TxtDlFileName.Text = _selected.OllamaName;
        TxtDlStatus.Text   = $"Running: ollama pull {_selected.OllamaName}…";
        PbDownload.Value   = 0;

        _downloadCts = new CancellationTokenSource();

        try
        {
            // Ollama pull is handled by RegisterWithOllamaAsync (it calls ollama create or pull)
            // For a clean pull, we just invoke ollama pull directly via Process
            var logProgress = new Progress<string>(msg => Dispatcher.InvokeAsync(() =>
            {
                TxtDlStatus.Text = msg;
            }));

            // Simple fire: run ollama pull {name}
            var psi = new System.Diagnostics.ProcessStartInfo("ollama",
                $"pull \"{_selected.OllamaName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    Dispatcher.InvokeAsync(() => TxtDlStatus.Text = args.Data);
            };
            proc.BeginOutputReadLine();
            await proc.WaitForExitAsync(_downloadCts.Token);

            var role = GetSelectedRole();
            if (role != "library")
                ModelDownloadService.ApplyToSettings(_settings, _selected.OllamaName, role);

            PbDownload.Value = 100;
            TxtDlStatus.Text = $"✓ {_selected.OllamaName} pulled and ready.";
            SetStatus($"✓ {_selected.Name} pulled from Ollama as '{role}'.");
        }
        catch (Exception ex)
        {
            TxtDlStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            BtnOllamaPull.IsEnabled = !string.IsNullOrEmpty(_selected?.OllamaName);
        }
    }

    private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        TxtDlStatus.Text = "Cancelling…";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetSelectedRole()
    {
        if (RbRoleWorker.IsChecked     == true) return "worker";
        if (RbRoleBoss.IsChecked       == true) return "boss";
        if (RbRoleResearcher.IsChecked == true) return "researcher";
        return "library";
    }

    private static Border MakeStatChip(string text, string bg, string fg)
    {
        var bgColor = (Color)ColorConverter.ConvertFromString(bg);
        var fgColor = (Color)ColorConverter.ConvertFromString(fg);

        return new Border
        {
            Background   = new SolidColorBrush(bgColor),
            CornerRadius = new CornerRadius(3),
            Padding      = new Thickness(6, 2, 6, 2),
            Margin       = new Thickness(0, 0, 6, 4),
            Child = new TextBlock
            {
                Text       = text,
                Foreground = new SolidColorBrush(fgColor),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 11,
            },
        };
    }

    private void SetStatus(string msg)
        => Dispatcher.InvokeAsync(() => TxtWindowStatus.Text = msg);

    private static string BuildHardwareSummary()
    {
        // Detect GPU via nvidia-smi (works without WMI package reference)
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "nvidia-smi",
                "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                var line = proc.StandardOutput.ReadLine() ?? "";
                proc.WaitForExit(3_000);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var parts = line.Split(',');
                    var gpuName = parts[0].Trim();
                    var gb = int.TryParse(
                        parts.ElementAtOrDefault(1)?.Trim(),
                        out var mb) ? mb / 1024 : 0;
                    return $"GPU: {gpuName}  ({gb} GB VRAM)";
                }
            }
        }
        catch { }
        return "GPU: install nvidia-smi for VRAM detection";
    }

    private int DetectVramGb()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "nvidia-smi",
                "--query-gpu=memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                var line = proc.StandardOutput.ReadLine() ?? "";
                proc.WaitForExit(3_000);
                if (int.TryParse(line.Trim(), out var mb))
                    return mb / 1024;
            }
        }
        catch { }
        return 0;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1_024               => $"{bytes} B",
        < 1_048_576           => $"{bytes / 1_024.0:F1} KB",
        < 1_073_741_824       => $"{bytes / 1_048_576.0:F1} MB",
        _                     => $"{bytes / 1_073_741_824.0:F2} GB",
    };

    private static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        < 1_024        => $"{bytesPerSec:F0} B/s",
        < 1_048_576    => $"{bytesPerSec / 1_024.0:F0} KB/s",
        _              => $"{bytesPerSec / 1_048_576.0:F1} MB/s",
    };

    private static string FormatCount(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000     => $"{n / 1_000.0:F0}K",
        _            => n.ToString(),
    };
}
