// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UI.Windows;

public partial class ModelDownloaderWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ModelSearchService _search;
    private readonly ModelDownloadService _downloader;
    private readonly HuggingFaceClient _hf;

    private List<ModelSearchResult> _results = [];
    private ModelSearchResult? _selected;
    private List<GgufVariant> _variants = [];
    private GgufVariant? _selectedVariant;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _downloadCts;
    private Process? _activeExternalProcess;
    private int _detectedVramGb;
    private bool _isClosed;
    private bool _searchRunning;
    private bool _transferRunning;

    private static readonly HashSet<string> StaleCuratedIds = new(StringComparer.OrdinalIgnoreCase);

    public ModelDownloaderWindow(AppSettings settings)
    {
        _settings = settings;
        _search = new ModelSearchService();
        _downloader = new ModelDownloadService();
        _hf = new HuggingFaceClient();

        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        TxtHardwareSummary.Text = "GPU: probing hardware...";

        _ = Task.Run(ProbeHardwareAsync).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion)
                return;

            PostUi(() =>
            {
                _detectedVramGb = t.Result.VramGb;
                TxtHardwareSummary.Text = t.Result.Summary;
            });
        }, TaskScheduler.Default);

        _ = Task.Run(async () =>
        {
            try
            {
                var stale = await _search.VerifyCuratedReposAsync();
                lock (StaleCuratedIds)
                {
                    foreach (var id in stale)
                        StaleCuratedIds.Add(id);
                }

                PostUi(RefreshVisibleResultsFromStaleSet);
            }
            catch
            {
            }
        });

        TxtSearch.Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        CancelAndDispose(ref _searchCts);
        CancelAndDispose(ref _downloadCts);
        TryStopActiveProcess();
    }

    private void TxtSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunTrackedTask(RunSearchSafeAsync());
    }

    private void BtnSearch_Click(object? sender, RoutedEventArgs e)
        => RunTrackedTask(RunSearchSafeAsync());

    private async Task RunSearchSafeAsync()
    {
        if (_searchRunning)
            return;

        _searchRunning = true;
        try
        {
            await RunSearchCoreAsync();
        }
        catch (Exception ex)
        {
            if (_isClosed)
                return;

            await InvokeUiAsync(() => TxtSearchStatus.Text = $"Error: {ex.Message}");
            SetStatus($"Search failed: {ex.Message}");
        }
        finally
        {
            _searchRunning = false;
        }
    }

    private async Task RunSearchCoreAsync()
    {
        var query = TxtSearch.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(query))
        {
            TxtSearchStatus.Text = "Enter a query";
            return;
        }

        CancelAndDispose(ref _searchCts);
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        BtnSearch.IsEnabled = false;
        TxtSearchStatus.Text = "Searching...";
        TxtResultCount.Text = "";
        ResultsPanel.Children.Clear();

        try
        {
            var vram = _detectedVramGb;
            var results = await _search.SearchAsync(
                query,
                vram,
                s => PostUi(() => TxtSearchStatus.Text = s),
                ct);

            lock (StaleCuratedIds)
                _results = results.Where(r => !r.IsCurated || !StaleCuratedIds.Contains(r.Id)).ToList();

            await InvokeUiAsync(() =>
            {
                PopulateResults(_results);
                TxtResultCount.Text = $"{_results.Count} result{(_results.Count == 1 ? "" : "s")}";
                TxtSearchStatus.Text = "";
            });
            SetStatus(_results.Count == 0
                ? "No matching models found."
                : "Select a model to inspect variants.");
        }
        catch (OperationCanceledException)
        {
            await InvokeUiAsync(() => TxtSearchStatus.Text = "Cancelled");
        }
        catch (Exception ex)
        {
            await InvokeUiAsync(() => TxtSearchStatus.Text = $"Error: {ex.Message}");
            SetStatus($"Search failed: {ex.Message}");
        }
        finally
        {
            await InvokeUiAsync(() => BtnSearch.IsEnabled = true);
        }
    }

    private void PopulateResults(List<ModelSearchResult> results)
    {
        ResultsPanel.Children.Clear();

        if (results.Count == 0)
        {
            ResultsPanel.Children.Add(new TextBlock
            {
                Text = "No results found.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 12,
                Margin = new Thickness(8, 16, 8, 0),
            });
            return;
        }

        foreach (var result in results)
            ResultsPanel.Children.Add(BuildResultCard(result));
    }

    private Border BuildResultCard(ModelSearchResult result)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Tag = result,
        };

        card.PointerPressed += (_, _) => RunTrackedTask(SelectModelSafeAsync(result));

        var stack = new StackPanel { Spacing = 4 };
        var top = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        if (result.IsCurated)
            top.Children.Add(MakePill("CURATED", Color.FromRgb(0x4A, 0xCA, 0x4A), Color.FromRgb(0x1A, 0x3A, 0x1A), new Thickness(0, 0, 6, 0)));

        if (result.SwarmCapable)
            top.Children.Add(MakePill("SWARM", Color.FromRgb(0x72, 0xB7, 0xFF), Color.FromRgb(0x1A, 0x2A, 0x3A), new Thickness(0, 0, 6, 0)));

        top.Children.Add(new TextBlock
        {
            Text = result.Name,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(top);

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(result.Publisher)) meta.Add(result.Publisher);
        if (result.VramRecommendedGb > 0) meta.Add($"{result.VramRecommendedGb} GB VRAM");
        if (result.QualityStars > 0) meta.Add(new string('★', result.QualityStars));
        if (result.HfDownloads > 0) meta.Add(result.DownloadsDisplay);

        if (meta.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", meta),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var desc = string.IsNullOrWhiteSpace(result.Description)
            ? result.HuggingFaceId
            : result.Description;
        if (desc.Length > 120)
            desc = desc[..117] + "...";

        stack.Children.Add(new TextBlock
        {
            Text = desc,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        card.Child = stack;
        return card;
    }

    private async Task SelectModelAsync(ModelSearchResult model)
    {
        _selected = model;
        _selectedVariant = null;
        _variants = [];
        BtnDownloadGguf.IsEnabled = false;
        BtnOllamaPull.IsEnabled = !string.IsNullOrWhiteSpace(model.OllamaName);

        foreach (var card in ResultsPanel.Children.OfType<Border>())
        {
            var isSelected = card.Tag == model;
            card.BorderBrush = new SolidColorBrush(isSelected
                ? Color.FromRgb(0x76, 0xB9, 0x00)
                : Color.FromRgb(0x1A, 0x2A, 0x1A));
        }

        PanelNoSelection.IsVisible = false;
        PanelDetail.IsVisible = true;

        TxtDetailName.Text = model.Name;
        TxtDetailPublisher.Text = string.IsNullOrWhiteSpace(model.Publisher)
            ? model.HuggingFaceId
            : $"{model.Publisher}  ·  {model.HuggingFaceId}";
        TxtDetailDesc.Text = string.IsNullOrWhiteSpace(model.Description)
            ? "Loading model summary..."
            : model.Description;

        BdrCuratedBadge.IsVisible = model.IsCurated;
        BdrSwarmBadge.IsVisible = model.SwarmCapable;

        WpDetailStats.Children.Clear();
        if (model.VramRecommendedGb > 0)
            WpDetailStats.Children.Add(MakeStatChip($"{model.VramRecommendedGb} GB VRAM", "#2A1A0A", "#FFCA57"));
        if (model.ContextK > 0)
            WpDetailStats.Children.Add(MakeStatChip($"{model.ContextK}K ctx", "#0A1A2A", "#72B7FF"));
        if (model.QualityStars > 0)
            WpDetailStats.Children.Add(MakeStatChip(new string('★', model.QualityStars), "#222108", "#FFD54D"));
        if (model.CpuOk)
            WpDetailStats.Children.Add(MakeStatChip("CPU OK", "#1A1A1A", "#C0C0C0"));
        if (model.HfDownloads > 0)
            WpDetailStats.Children.Add(MakeStatChip($"Downloads {model.DownloadsDisplay}", "#121212", "#9A9A9A"));

        BdrIntendedUse.IsVisible = !string.IsNullOrWhiteSpace(model.IntendedUse);
        TxtIntendedUse.Text = model.IntendedUse;

        BdrToolUse.IsVisible = !string.IsNullOrWhiteSpace(model.ToolUseNotes);
        TxtToolUse.Text = model.ToolUseNotes;

        while (SpSwarmRoles.Children.Count > 1)
            SpSwarmRoles.Children.RemoveAt(SpSwarmRoles.Children.Count - 1);
        SpSwarmRoles.IsVisible = model.SwarmRoles is { Length: > 0 };
        if (model.SwarmRoles is { Length: > 0 })
        {
            foreach (var role in model.SwarmRoles)
            {
                var (fg, bg) = role.ToLowerInvariant() switch
                {
                    "worker" => (Color.FromRgb(0x7C, 0xE6, 0x7C), Color.FromRgb(0x12, 0x20, 0x12)),
                    "boss" => (Color.FromRgb(0xFF, 0xCA, 0x57), Color.FromRgb(0x22, 0x1A, 0x08)),
                    "researcher" => (Color.FromRgb(0x72, 0xB7, 0xFF), Color.FromRgb(0x10, 0x1A, 0x2A)),
                    _ => (Color.FromRgb(0xC0, 0xC0, 0xC0), Color.FromRgb(0x1A, 0x1A, 0x1A)),
                };
                SpSwarmRoles.Children.Add(MakePill(role, fg, bg, new Thickness(0, 0, 6, 0)));
            }
        }

        if (model.SwarmRoles?.Any(r => string.Equals(r, "boss", StringComparison.OrdinalIgnoreCase)) == true)
            RbRoleBoss.IsChecked = true;
        else if (model.SwarmRoles?.Any(r => string.Equals(r, "researcher", StringComparison.OrdinalIgnoreCase)) == true)
            RbRoleResearcher.IsChecked = true;
        else
            RbRoleWorker.IsChecked = true;

        SetStatus($"Loading variants for {model.Name}...");
        await Task.WhenAll(LoadDescriptionAsync(model), LoadVariantsAsync(model));
    }

    private async Task SelectModelSafeAsync(ModelSearchResult model)
    {
        try
        {
            await SelectModelAsync(model);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to load model details: {ex.Message}");
        }
    }

    private async Task LoadDescriptionAsync(ModelSearchResult model)
    {
        if (!string.IsNullOrWhiteSpace(model.Description) || string.IsNullOrWhiteSpace(model.HuggingFaceId))
            return;

        try
        {
            var summary = await _hf.GetReadmeSummaryAsync(model.HuggingFaceId);
            if (_selected != model)
                return;

            await InvokeUiAsync(() =>
            {
                TxtDetailDesc.Text = string.IsNullOrWhiteSpace(summary)
                    ? model.HuggingFaceId
                    : summary;
            });
        }
        catch
        {
            if (_selected == model)
                await InvokeUiAsync(() => TxtDetailDesc.Text = model.HuggingFaceId);
        }
    }

    private async Task LoadVariantsAsync(ModelSearchResult model)
    {
        SpVariants.Children.Clear();
        TxtVariantLoadStatus.Text = "Loading...";
        _selectedVariant = null;
        BtnDownloadGguf.IsEnabled = false;

        if (string.IsNullOrWhiteSpace(model.HuggingFaceId))
        {
            TxtVariantLoadStatus.Text = "No Hugging Face source";
            return;
        }

        try
        {
            var vram = _detectedVramGb;
            _variants = await _search.GetVariantsAsync(model, vram);

            if (_selected != model)
                return;

            if (_variants.Count == 0)
            {
                await InvokeUiAsync(() => TxtVariantLoadStatus.Text = "No GGUF files found");
                SetStatus("This repo did not expose downloadable GGUF files.");
                return;
            }

            await InvokeUiAsync(() =>
            {
                TxtVariantLoadStatus.Text = $"{_variants.Count} variants";
                foreach (var variant in _variants)
                    SpVariants.Children.Add(BuildVariantRow(variant));
            });

            var recommended = _variants.FirstOrDefault(v => v.IsRecommended);
            if (recommended is not null)
                await InvokeUiAsync(() => SelectVariant(recommended));

            SetStatus("Choose a quantization variant to download.");
        }
        catch (Exception ex)
        {
            await InvokeUiAsync(() => TxtVariantLoadStatus.Text = $"Error: {ex.Message}");
            SetStatus($"Variant lookup failed: {ex.Message}");
        }
    }

    private Border BuildVariantRow(GgufVariant variant)
    {
        var row = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8),
            Tag = variant,
            Background = variant.IsRecommended
                ? new SolidColorBrush(Color.FromRgb(0x0E, 0x15, 0x0E))
                : Brushes.Transparent,
        };
        row.PointerPressed += (_, _) => SelectVariant(variant);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 10,
        };

        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock
        {
            Text = variant.QuantLabel,
            Foreground = variant.IsRecommended
                ? new SolidColorBrush(Color.FromRgb(0x7C, 0xE6, 0x7C))
                : Brushes.White,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
        });
        left.Children.Add(new TextBlock
        {
            Text = variant.Filename,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var hint = new TextBlock
        {
            Text = variant.QualityHint,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(hint, 1);
        grid.Children.Add(hint);

        var size = new TextBlock
        {
            Text = variant.SizeDisplay,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)),
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(size, 2);
        grid.Children.Add(size);

        var fits = variant.VramEstimateGb <= Math.Max(_detectedVramGb, 1);
        var vramBadge = MakePill(
            $"~{variant.VramEstimateGb} GB",
            fits ? Color.FromRgb(0x7C, 0xE6, 0x7C) : Color.FromRgb(0xFF, 0x8A, 0x8A),
            fits ? Color.FromRgb(0x12, 0x20, 0x12) : Color.FromRgb(0x2A, 0x12, 0x12));
        Grid.SetColumn(vramBadge, 3);
        grid.Children.Add(vramBadge);

        row.Child = grid;
        return row;
    }

    private void SelectVariant(GgufVariant variant)
    {
        _selectedVariant = variant;
        BtnDownloadGguf.IsEnabled = !string.IsNullOrWhiteSpace(variant.DownloadUrl);

        foreach (var row in SpVariants.Children.OfType<Border>())
        {
            var isSelected = row.Tag == variant;
            row.BorderBrush = new SolidColorBrush(isSelected
                ? Color.FromRgb(0x76, 0xB9, 0x00)
                : Color.FromRgb(0x1A, 0x2A, 0x1A));
        }

        SetStatus($"Selected {variant.QuantLabel} · {variant.SizeDisplay} · ~{variant.VramEstimateGb} GB VRAM");
    }

    private void BtnDownloadGguf_Click(object? sender, RoutedEventArgs e)
        => RunTrackedTask(DownloadSelectedVariantSafeAsync());

    private async Task DownloadSelectedVariantSafeAsync()
    {
        if (_transferRunning)
            return;

        _transferRunning = true;
        if (_selected is null || _selectedVariant is null)
        {
            _transferRunning = false;
            return;
        }
        var selected = _selected;
        var selectedVariant = _selectedVariant;
        var role = GetSelectedRole();
        var storagePath = _settings.ResolvedModelStoragePath;
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            _transferRunning = false;
            SetStatus("Model storage path is not configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(selectedVariant.DownloadUrl))
        {
            _transferRunning = false;
            SetStatus("No download URL available for this variant.");
            return;
        }

        var fileName = Path.GetFileName(selectedVariant.DownloadUrl.Split('?')[0]);
        var destPath = Path.Combine(storagePath, fileName);

        PanelDownloadProgress.IsVisible = true;
        TxtDlFileName.Text = fileName;
        TxtDlStatus.Text = "Starting download...";
        TxtDlStats.Text = "";
        PbDownload.Value = 0;

        CancelAndDispose(ref _downloadCts);
        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;

        BtnDownloadGguf.IsEnabled = false;
        BtnOllamaPull.IsEnabled = false;

        try
        {
            var progress = new Progress<(long done, long total, double speed, int eta)>(p => PostUi(() =>
            {
                var pct = p.total > 0 ? (double)p.done / p.total * 100 : 0;
                PbDownload.Value = pct;
                TxtDlStats.Text = $"{FormatBytes(p.done)} / {FormatBytes(p.total)}  {FormatSpeed(p.speed)}  ETA {p.eta}s";
                TxtDlStatus.Text = $"Downloading {fileName}...";
            }));

            await _downloader.DownloadAsync(selectedVariant.DownloadUrl, destPath, progress, ct);

            if (_isClosed)
                return;
            await InvokeUiAsync(() =>
            {
                TxtDlStatus.Text = "Download complete. Registering with Ollama...";
                PbDownload.Value = 100;
            });

            var ollamaName = string.IsNullOrWhiteSpace(selected.OllamaName)
                ? Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant()
                : selected.OllamaName;
            var logProgress = new Progress<string>(msg => PostUi(() => TxtDlStatus.Text = msg));
            await _downloader.RegisterWithOllamaAsync(destPath, ollamaName, logProgress, ct);

            if (role != "library")
            {
                var identifier = !string.IsNullOrWhiteSpace(selected.OllamaName)
                    ? selected.OllamaName
                    : destPath;
                ModelDownloadService.ApplyToSettings(_settings, identifier, role);
            }

            await InvokeUiAsync(() => TxtDlStatus.Text = $"Model ready. Role: {role}");
            SetStatus($"Downloaded {selected.Name} and assigned it to {role}.");
        }
        catch (OperationCanceledException)
        {
            await InvokeUiAsync(() => TxtDlStatus.Text = "Download cancelled.");
            SetStatus("Download cancelled.");
        }
        catch (Exception ex)
        {
            await InvokeUiAsync(() => TxtDlStatus.Text = $"Error: {ex.Message}");
            SetStatus($"Download failed: {ex.Message}");
        }
        finally
        {
            _transferRunning = false;
            await InvokeUiAsync(() =>
            {
                BtnDownloadGguf.IsEnabled = _selectedVariant is not null;
                BtnOllamaPull.IsEnabled = !string.IsNullOrWhiteSpace(_selected?.OllamaName);
            });
        }
    }

    private void BtnOllamaPull_Click(object? sender, RoutedEventArgs e)
        => RunTrackedTask(OllamaPullSafeAsync());

    private async Task OllamaPullSafeAsync()
    {
        if (_transferRunning)
            return;

        _transferRunning = true;
        if (_selected is null || string.IsNullOrWhiteSpace(_selected.OllamaName))
        {
            _transferRunning = false;
            return;
        }
        var selected = _selected;
        var role = GetSelectedRole();

        PanelDownloadProgress.IsVisible = true;
        TxtDlFileName.Text = selected.OllamaName;
        TxtDlStatus.Text = $"Running: ollama pull {selected.OllamaName}...";
        TxtDlStats.Text = "";
        PbDownload.Value = 0;

        CancelAndDispose(ref _downloadCts);
        _downloadCts = new CancellationTokenSource();

        BtnDownloadGguf.IsEnabled = false;
        BtnOllamaPull.IsEnabled = false;

        try
        {
            var psi = new ProcessStartInfo("ollama", $"pull \"{selected.OllamaName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                throw new InvalidOperationException("Failed to start ollama pull.");

            _activeExternalProcess = proc;
            proc.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    PostUi(() => TxtDlStatus.Text = args.Data);
            };
            proc.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    PostUi(() => TxtDlStatus.Text = args.Data);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(_downloadCts.Token);
            _activeExternalProcess = null;

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ollama pull exited with code {proc.ExitCode}");

            if (role != "library")
                ModelDownloadService.ApplyToSettings(_settings, selected.OllamaName, role);

            await InvokeUiAsync(() =>
            {
                PbDownload.Value = 100;
                TxtDlStatus.Text = $"Pulled {selected.OllamaName}.";
            });
            SetStatus($"Pulled {selected.Name} via Ollama and assigned it to {role}.");
        }
        catch (OperationCanceledException)
        {
            await InvokeUiAsync(() => TxtDlStatus.Text = "Pull cancelled.");
            SetStatus("Ollama pull cancelled.");
        }
        catch (Exception ex)
        {
            await InvokeUiAsync(() => TxtDlStatus.Text = $"Error: {ex.Message}");
            SetStatus($"Ollama pull failed: {ex.Message}");
        }
        finally
        {
            _activeExternalProcess = null;
            _transferRunning = false;
            await InvokeUiAsync(() =>
            {
                BtnDownloadGguf.IsEnabled = _selectedVariant is not null;
                BtnOllamaPull.IsEnabled = !string.IsNullOrWhiteSpace(_selected?.OllamaName);
            });
        }
    }

    private void BtnCancelDownload_Click(object? sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        TryStopActiveProcess();
        TxtDlStatus.Text = "Cancelling...";
    }

    private string GetSelectedRole()
    {
        if (RbRoleWorker.IsChecked == true) return "worker";
        if (RbRoleBoss.IsChecked == true) return "boss";
        if (RbRoleResearcher.IsChecked == true) return "researcher";
        return "library";
    }

    private static Border MakePill(string text, Color fg, Color bg, Thickness? margin = null)
    {
        return new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2),
            Margin = margin ?? default,
            Child = new TextBlock
            {
                Text = text.ToUpperInvariant(),
                Foreground = new SolidColorBrush(fg),
                FontSize = 10,
                FontWeight = FontWeight.Bold,
            },
        };
    }

    private static Border MakeStatChip(string text, string bg, string fg)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse(bg)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.Parse(fg)),
                FontSize = 11,
            },
        };
    }

    private void SetStatus(string msg)
        => PostUi(() => TxtWindowStatus.Text = msg);

    private void RefreshVisibleResultsFromStaleSet()
    {
        if (_results.Count == 0)
            return;

        lock (StaleCuratedIds)
        {
            var filtered = _results.Where(r => !r.IsCurated || !StaleCuratedIds.Contains(r.Id)).ToList();
            if (filtered.Count == _results.Count)
                return;

            _results = filtered;
        }

        PopulateResults(_results);
        TxtResultCount.Text = $"{_results.Count} result{(_results.Count == 1 ? "" : "s")}";

        if (_selected is not null && !_results.Contains(_selected))
        {
            _selected = null;
            _selectedVariant = null;
            PanelDetail.IsVisible = false;
            PanelNoSelection.IsVisible = true;
            SetStatus("One or more stale curated entries were hidden after verification.");
        }
    }

    private static async Task<(string Summary, int VramGb)> ProbeHardwareAsync()
    {
        try
        {
            var psi = new ProcessStartInfo(
                "nvidia-smi",
                "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var line = await proc.StandardOutput.ReadLineAsync() ?? "";
                await proc.WaitForExitAsync();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var parts = line.Split(',');
                    var gpuName = parts[0].Trim();
                    var gb = int.TryParse(parts.ElementAtOrDefault(1)?.Trim(), out var mb) ? mb / 1024 : 0;
                    return ($"GPU: {gpuName}  ({gb} GB VRAM)", gb);
                }
            }
        }
        catch
        {
        }

        return ("GPU: install nvidia-smi for VRAM detection", 0);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1_024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1_024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1_073_741_824.0:F2} GB",
    };

    private static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        < 1_024 => $"{bytesPerSec:F0} B/s",
        < 1_048_576 => $"{bytesPerSec / 1_024.0:F0} KB/s",
        _ => $"{bytesPerSec / 1_048_576.0:F1} MB/s",
    };

    private static void CancelAndDispose(ref CancellationTokenSource? cts)
    {
        if (cts is null)
            return;

        try { cts.Cancel(); } catch { }
        cts.Dispose();
        cts = null;
    }

    private void PostUi(Action action)
    {
        if (_isClosed)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosed)
                return;
            action();
        });
    }

    private Task InvokeUiAsync(Action action)
    {
        if (_isClosed)
            return Task.CompletedTask;

        return Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_isClosed)
                return;
            action();
        }).GetTask();
    }

    private void TryStopActiveProcess()
    {
        var proc = _activeExternalProcess;
        if (proc is null)
            return;

        try
        {
            if (!proc.HasExited)
                proc.Kill(true);
        }
        catch
        {
        }
        finally
        {
            _activeExternalProcess = null;
        }
    }

    private void RunTrackedTask(Task task)
    {
        task.ContinueWith(
            t =>
            {
                if (_isClosed)
                    return;
                var message = t.Exception?.GetBaseException().Message ?? "unknown error";
                SetStatus(message);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
