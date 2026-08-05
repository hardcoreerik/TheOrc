// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UI.Windows;

public partial class ModelDepotWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ModelSearchService _search;
    private readonly ModelDownloadService _downloader;

    private List<ModelDepotCardEntry> _allCards = [];
    private ModelDepotCardEntry? _selected;
    private CancellationTokenSource? _downloadCts;
    private Process? _activeExternalProcess;
    private int _detectedVramGb;
    private bool _isClosed;
    private bool _transferRunning;

    private string? _roleFilter;
    private int? _vramFilter;
    private int? _qualityFilter;
    private bool _installedOnlyFilter;
    private bool _hiveOnlyFilter;
    private string? _tagFilter;
    private bool _hiveScanRunning;

    public ModelDepotWindow(AppSettings settings)
    {
        _settings = settings;
        _search = new ModelSearchService(settings: _settings);
        _downloader = new ModelDownloadService(settings: _settings);

        InitializeComponent();
        BuildRoleFilterChips();
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

        _ = LoadCatalogSafeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        CancelAndDispose(ref _downloadCts);
        TryStopActiveProcess();
    }

    // ── Loading + fusion ─────────────────────────────────────────────────────

    private async Task LoadCatalogSafeAsync()
    {
        try
        {
            await LoadCatalogCoreAsync();
        }
        catch (Exception ex)
        {
            if (_isClosed) return;
            SetStatus($"Failed to load catalog: {ex.Message}");
        }
    }

    private async Task LoadCatalogCoreAsync()
    {
        var searchResults = ModelSearchService.BrowseCurated();

        var localAssets = await Task.Run(() =>
        {
            try
            {
                var roots = _settings.ResolvedNativeRuntimeModelRoots;
                return ModelDepot.ScanSources(roots, includeOllamaModels: true).Assets.ToList();
            }
            catch
            {
                return new List<RuntimeModelAsset>();
            }
        });

        var installedTags = localAssets
            .Where(a => a.Kind == RuntimeAssetKind.BaseModelGguf)
            .Select(a => a.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var cards = ModelDepotBrowserService.BuildBrowseList(searchResults, localAssets, installedTags);

        if (_isClosed) return;
        _allCards = cards;

        await InvokeUiAsync(() =>
        {
            BuildTagFilterChips();
            ApplyFilters();
        });

        SetStatus($"{cards.Count} models in the depot ({cards.Count(c => c.InstallState == ModelDepotInstallState.InstalledLocally)} installed).");

        // Enrich with live quant variants in the background — must not block the initial render.
        try
        {
            await ModelDepotBrowserService.EnrichWithQuantVariantsAsync(cards, _search, _detectedVramGb);
            if (_isClosed) return;
            await InvokeUiAsync(() =>
            {
                if (_selected is not null)
                    RenderQuants(_selected);
            });
        }
        catch
        {
            // Best-effort; cards still render without variant badges.
        }
    }

    // ── Filters ──────────────────────────────────────────────────────────────

    private void BuildRoleFilterChips()
    {
        foreach (var role in new[] { "worker", "boss", "researcher" })
        {
            var tb = new ToggleButton
            {
                Content = role switch { "worker" => "Worker", "boss" => "Boss", _ => "Researcher" },
                Tag = role,
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
                Padding = new Avalonia.Thickness(8, 3),
                FontSize = 11,
            };
            tb.Click += RoleFilter_Click;
            WpRoleFilters.Children.Add(tb);
        }
    }

    private void RoleFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        var role = clicked.Tag?.ToString();

        foreach (var child in WpRoleFilters.Children.OfType<ToggleButton>())
            if (child != clicked) child.IsChecked = false;

        _roleFilter = clicked.IsChecked == true ? role : null;
        ApplyFilters();
    }

    private void VramFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        var vram = int.Parse(clicked.Tag!.ToString()!);

        foreach (var tb in new[] { TbVram4, TbVram8, TbVram12, TbVram24 })
            if (tb != clicked) tb.IsChecked = false;

        _vramFilter = clicked.IsChecked == true ? vram : null;
        ApplyFilters();
    }

    private void QualityFilter_Click(object? sender, RoutedEventArgs e)
    {
        _qualityFilter = TbQuality4.IsChecked == true ? 4 : null;
        ApplyFilters();
    }

    private void InstalledFilter_Click(object? sender, RoutedEventArgs e)
    {
        _installedOnlyFilter = TbInstalledOnly.IsChecked == true;
        ApplyFilters();
    }

    private void HiveFilter_Click(object? sender, RoutedEventArgs e)
    {
        _hiveOnlyFilter = TbHiveOnly.IsChecked == true;
        ApplyFilters();
    }

    private void BuildTagFilterChips()
    {
        while (WpTagFilters.Children.Count > 1)
            WpTagFilters.Children.RemoveAt(WpTagFilters.Children.Count - 1);

        foreach (var tag in ModelDepotBrowserService.DistinctTags(_allCards).Take(24))
        {
            var tb = new ToggleButton
            {
                Content = tag,
                Tag = tag,
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
                Padding = new Avalonia.Thickness(8, 3),
                FontSize = 11,
            };
            tb.Click += TagFilter_Click;
            WpTagFilters.Children.Add(tb);
        }
    }

    private void TagFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        var tag = clicked.Tag?.ToString();

        foreach (var child in WpTagFilters.Children.OfType<ToggleButton>())
            if (child != clicked) child.IsChecked = false;

        _tagFilter = clicked.IsChecked == true ? tag : null;
        ApplyFilters();
    }

    private void BtnClearFilters_Click(object? sender, RoutedEventArgs e)
    {
        _roleFilter = null;
        _vramFilter = null;
        _qualityFilter = null;
        _installedOnlyFilter = false;
        _hiveOnlyFilter = false;
        _tagFilter = null;

        foreach (var tb in WpRoleFilters.Children.OfType<ToggleButton>()) tb.IsChecked = false;
        foreach (var tb in WpTagFilters.Children.OfType<ToggleButton>()) tb.IsChecked = false;
        foreach (var tb in new[] { TbVram4, TbVram8, TbVram12, TbVram24, TbQuality4, TbInstalledOnly, TbHiveOnly }) tb.IsChecked = false;
        TxtSearch.Text = "";

        ApplyFilters();
    }

    private void TxtSearch_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) => ApplyFilters();

    private void ApplyFilters()
    {
        var filtered = ModelDepotBrowserService.Filter(
            _allCards,
            role: _roleFilter,
            maxVramGb: _vramFilter,
            installedOnly: _installedOnlyFilter ? true : null,
            minQualityStars: _qualityFilter,
            tag: _tagFilter,
            searchText: TxtSearch.Text,
            availableOnHiveOnly: _hiveOnlyFilter ? true : null).ToList();

        PopulateGrid(filtered);
        TxtResultCount.Text = $"{filtered.Count} model{(filtered.Count == 1 ? "" : "s")}";
    }

    // ── Live HF search widening ──────────────────────────────────────────────

    private void BtnLiveSearch_Click(object? sender, RoutedEventArgs e) => RunTrackedTask(RunLiveSearchSafeAsync());

    private async Task RunLiveSearchSafeAsync()
    {
        var query = TxtSearch.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            SetStatus("Enter a search term first, then click \"Search HF live\".");
            return;
        }

        SetStatus($"Searching Hugging Face for \"{query}\"...");
        try
        {
            var liveResults = await _search.SearchAsync(query, _detectedVramGb, s => PostUi(() => SetStatus(s)), CancellationToken.None);

            var localAssets = await Task.Run(() =>
            {
                try { return ModelDepot.ScanSources(_settings.ResolvedNativeRuntimeModelRoots, includeOllamaModels: true).Assets.ToList(); }
                catch { return new List<RuntimeModelAsset>(); }
            });
            var installedTags = localAssets
                .Where(a => a.Kind == RuntimeAssetKind.BaseModelGguf)
                .Select(a => a.DisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var merged = ModelDepotBrowserService.BuildBrowseList(liveResults, localAssets, installedTags);
            // Merge with the existing curated set rather than replacing it — a live search widens
            // the catalog, it doesn't hide models that were already browsable.
            var existingIds = _allCards.Select(c => c.Result.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _allCards = _allCards.Concat(merged.Where(c => !existingIds.Contains(c.Result.Id))).ToList();

            await InvokeUiAsync(() =>
            {
                BuildTagFilterChips();
                ApplyFilters();
            });
            SetStatus($"Live search added {merged.Count(c => !existingIds.Contains(c.Result.Id))} new result(s).");
        }
        catch (Exception ex)
        {
            SetStatus($"Live search failed: {ex.Message}");
        }
    }

    // ── HIVE network scan ────────────────────────────────────────────────────

    private void BtnScanHive_Click(object? sender, RoutedEventArgs e) => RunTrackedTask(ScanHiveSafeAsync());

    private async Task ScanHiveSafeAsync()
    {
        if (_hiveScanRunning) return;
        _hiveScanRunning = true;

        await InvokeUiAsync(() =>
        {
            BtnScanHive.IsEnabled = false;
            TxtHiveScanStatus.Text = "Scanning LAN for HIVE peers...";
        });

        try
        {
            // Two sources, merged by name: a UDP beacon scan (fast, no pairing needed, exactly
            // "search the network" -- HiveBeacon.ScanAsync) plus any already-known named hosts
            // (HiveHosts.Load, e.g. a paired peer on a different subnet the beacon can't reach),
            // each probed for its live Ollama model list.
            var peerModels = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var beaconHits = await HiveBeacon.ScanAsync(2500);
            foreach (var hit in beaconHits)
            {
                if (string.IsNullOrWhiteSpace(hit.Name)) continue;
                if (!peerModels.TryGetValue(hit.Name, out var list))
                    peerModels[hit.Name] = list = [];
                list.AddRange(hit.Models);
            }

            var namedHosts = HiveHosts.Load();
            HiveHosts.MergePairedPeers(namedHosts);
            HiveHosts.Dedupe(namedHosts);

            await Task.WhenAll(namedHosts
                .Where(h => !h.Name.Equals("This PC", StringComparison.OrdinalIgnoreCase))
                .Select(async host =>
                {
                    await HiveHosts.ProbeAsync(host);
                    if (host.Models.Count == 0) return;
                    lock (peerModels)
                    {
                        if (!peerModels.TryGetValue(host.Name, out var list))
                            peerModels[host.Name] = list = [];
                        list.AddRange(host.Models);
                    }
                }));

            var snapshot = peerModels
                .Select(kv => (PeerName: kv.Key, Models: (IReadOnlyList<string>)kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                .ToList();

            ModelDepotBrowserService.AttachHiveAvailability(_allCards, snapshot);

            await InvokeUiAsync(() =>
            {
                ApplyFilters();
                if (_selected is not null)
                    RenderDetail(_selected);
            });

            var withHive = _allCards.Count(c => c.IsAvailableOnHive);
            SetStatus(snapshot.Count == 0
                ? "No HIVE peers responded. Nothing else on the LAN is broadcasting or reachable."
                : $"Found {snapshot.Count} HIVE peer(s); {withHive} model(s) available somewhere on the HIVE.");
        }
        catch (Exception ex)
        {
            SetStatus($"HIVE scan failed: {ex.Message}");
        }
        finally
        {
            _hiveScanRunning = false;
            await InvokeUiAsync(() =>
            {
                BtnScanHive.IsEnabled = true;
                TxtHiveScanStatus.Text = "";
            });
        }
    }

    // ── Card grid ────────────────────────────────────────────────────────────

    private void PopulateGrid(List<ModelDepotCardEntry> cards)
    {
        CardGrid.Items.Clear();

        if (cards.Count == 0)
        {
            var msg = new TextBlock
            {
                Text = "No models match the current filters.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 12,
                Margin = new Avalonia.Thickness(4, 16, 0, 0),
            };
            CardGrid.Items.Add(msg);
            return;
        }

        foreach (var card in cards)
            CardGrid.Items.Add(BuildCard(card));
    }

    private Border BuildCard(ModelDepotCardEntry card)
    {
        var installed = card.InstallState == ModelDepotInstallState.InstalledLocally;
        var border = new Border
        {
            Width = 280,
            Margin = new Avalonia.Thickness(0, 0, 10, 10),
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
            BorderBrush = new SolidColorBrush(installed ? Color.FromRgb(0x2E, 0x5A, 0x2E) : Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(5),
            Padding = new Avalonia.Thickness(12, 10),
            Tag = card,
        };
        border.PointerPressed += (_, _) => SelectCard(card);

        var stack = new StackPanel { Spacing = 5 };

        var top = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(MakePill(card.SourceBadge.Contains("Verified") ? "VERIFIED" : "COMMUNITY",
            card.SourceBadge.Contains("Verified") ? Color.FromRgb(0x4A, 0xCA, 0x4A) : Color.FromRgb(0x72, 0xB7, 0xFF),
            card.SourceBadge.Contains("Verified") ? Color.FromRgb(0x1A, 0x3A, 0x1A) : Color.FromRgb(0x1A, 0x2A, 0x3A),
            new Avalonia.Thickness(0, 0, 6, 4)));
        if (installed)
            top.Children.Add(MakePill("INSTALLED", Color.FromRgb(0x7C, 0xE6, 0x7C), Color.FromRgb(0x12, 0x20, 0x12), new Avalonia.Thickness(0, 0, 6, 4)));
        stack.Children.Add(top);

        stack.Children.Add(new TextBlock
        {
            Text = card.DisplayName,
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (!string.IsNullOrWhiteSpace(card.Publisher))
            stack.Children.Add(new TextBlock
            {
                Text = card.Publisher,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
            });

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.ParameterDisplay)) meta.Add(card.ParameterDisplay);
        if (!string.IsNullOrWhiteSpace(card.VramDisplay)) meta.Add(card.VramDisplay);
        if (!string.IsNullOrWhiteSpace(card.StarsDisplay)) meta.Add(card.StarsDisplay);
        if (meta.Count > 0)
            stack.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", meta),
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });

        if (card.Tags.Length > 0)
        {
            var tagRow = new WrapPanel { Margin = new Avalonia.Thickness(0, 2, 0, 0) };
            foreach (var tag in card.Tags.Take(3))
                tagRow.Children.Add(MakePill(tag, Color.FromRgb(0xC0, 0xC0, 0xC0), Color.FromRgb(0x1A, 0x1A, 0x1A), new Avalonia.Thickness(0, 0, 4, 0)));
            stack.Children.Add(tagRow);
        }

        if (card.IsAvailableOnHive)
            stack.Children.Add(new TextBlock
            {
                Text = card.HiveAvailabilityDisplay,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xCA, 0x57)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 2, 0, 0),
            });

        border.Child = stack;
        return border;
    }

    private void SelectCard(ModelDepotCardEntry card)
    {
        _selected = card;

        foreach (var item in CardGrid.Items)
            if (item is Border b)
                b.BorderBrush = new SolidColorBrush(b.Tag == card
                    ? Color.FromRgb(0x76, 0xB9, 0x00)
                    : (card.InstallState == ModelDepotInstallState.InstalledLocally && b.Tag is ModelDepotCardEntry bc && bc.InstallState == ModelDepotInstallState.InstalledLocally
                        ? Color.FromRgb(0x2E, 0x5A, 0x2E)
                        : Color.FromRgb(0x1A, 0x2A, 0x1A)));

        RenderDetail(card);
    }

    // ── Detail pane ──────────────────────────────────────────────────────────

    private void RenderDetail(ModelDepotCardEntry card)
    {
        PanelNoSelection.IsVisible = false;
        PanelDetail.IsVisible = true;

        WpDetailBadges.Children.Clear();
        var verified = card.SourceBadge.Contains("Verified");
        WpDetailBadges.Children.Add(MakePill(verified ? "VERIFIED" : "COMMUNITY",
            verified ? Color.FromRgb(0x4A, 0xCA, 0x4A) : Color.FromRgb(0x72, 0xB7, 0xFF),
            verified ? Color.FromRgb(0x1A, 0x3A, 0x1A) : Color.FromRgb(0x1A, 0x2A, 0x3A),
            new Avalonia.Thickness(0, 0, 6, 0)));
        if (card.InstallState == ModelDepotInstallState.InstalledLocally)
            WpDetailBadges.Children.Add(MakePill("INSTALLED", Color.FromRgb(0x7C, 0xE6, 0x7C), Color.FromRgb(0x12, 0x20, 0x12), new Avalonia.Thickness(0, 0, 6, 0)));

        TxtDetailName.Text = card.DisplayName;
        TxtDetailPublisher.Text = string.IsNullOrWhiteSpace(card.Publisher)
            ? card.Result.HuggingFaceId
            : $"{card.Publisher}  ·  {card.Result.HuggingFaceId}";
        TxtDetailDesc.Text = string.IsNullOrWhiteSpace(card.Result.Description)
            ? card.Curated?.Description ?? "No description available."
            : card.Result.Description;

        WpDetailStats.Children.Clear();
        if (!string.IsNullOrWhiteSpace(card.VramDisplay)) WpDetailStats.Children.Add(MakeStatChip(card.VramDisplay, "#2A1A0A", "#FFCA57"));
        if (!string.IsNullOrWhiteSpace(card.ContextDisplay)) WpDetailStats.Children.Add(MakeStatChip(card.ContextDisplay, "#0A1A2A", "#72B7FF"));
        if (!string.IsNullOrWhiteSpace(card.StarsDisplay)) WpDetailStats.Children.Add(MakeStatChip(card.StarsDisplay, "#222108", "#FFD54D"));
        if (!string.IsNullOrWhiteSpace(card.ParameterDisplay)) WpDetailStats.Children.Add(MakeStatChip(card.ParameterDisplay, "#121212", "#9A9A9A"));
        if (!string.IsNullOrWhiteSpace(card.PrimaryRoleDisplay)) WpDetailStats.Children.Add(MakeStatChip(card.PrimaryRoleDisplay, "#121212", "#9A9A9A"));

        RenderQuants(card);

        // Local evidence (retired Model Wiki data), reused as-is.
        if (card.Evidence is { } evidence)
        {
            BdrEvidence.IsVisible = true;
            TxtEvidenceRole.Text = $"Primary role fit: {evidence.PrimaryRole} · {evidence.SpeedLabel} · min {evidence.VramLabel}";
            TxtEvidenceLongWrite.IsVisible = evidence.HasLongWriteWarning;
            TxtEvidenceLongWrite.Text = "⚠ Not recommended for long write_file payloads (evidence from local capability tests).";
            TxtEvidenceSwarm.Text = evidence.SwarmRuns.Count > 0
                ? $"{evidence.SwarmRuns.Count} recorded swarm run(s); {evidence.Observations.Count} observation(s); {evidence.CapabilityTests.Count} capability test(s)."
                : $"{evidence.Observations.Count} observation(s); {evidence.CapabilityTests.Count} capability test(s). No swarm runs recorded yet.";
        }
        else
        {
            BdrEvidence.IsVisible = false;
        }

        // Local install info.
        if (card.InstallState == ModelDepotInstallState.InstalledLocally)
        {
            BdrInstalled.IsVisible = true;
            var sizeText = card.LocalSizeBytes is { } sz ? FormatBytes(sz) : "unknown size";
            var headerText = card.LocalHeader is { } h ? $" · {h.Architecture}" : "";
            TxtInstalledInfo.Text = $"{card.LocalPath}\n{sizeText}{headerText}";
        }
        else
        {
            BdrInstalled.IsVisible = false;
        }

        if (card.IsAvailableOnHive)
        {
            BdrHiveAvailability.IsVisible = true;
            TxtHiveAvailability.Text = card.AvailableOnHivePeers.Count == 1
                ? $"Already installed on: {card.AvailableOnHivePeers[0]}"
                : $"Already installed on {card.AvailableOnHivePeers.Count} peer(s): {string.Join(", ", card.AvailableOnHivePeers)}";
        }
        else
        {
            BdrHiveAvailability.IsVisible = false;
        }

        BtnDownload.IsEnabled = card.InstallState != ModelDepotInstallState.InstalledLocally
            && (!string.IsNullOrWhiteSpace(card.Result.OllamaName) || card.Quants.Count > 0 || !string.IsNullOrWhiteSpace(card.Result.HuggingFaceId));
        BtnDownload.Content = card.InstallState == ModelDepotInstallState.InstalledLocally ? "Already installed" : "Download";

        if (card.Result.SwarmRoles?.Any(r => string.Equals(r, "boss", StringComparison.OrdinalIgnoreCase)) == true)
            RbRoleBoss.IsChecked = true;
        else if (card.Result.SwarmRoles?.Any(r => string.Equals(r, "researcher", StringComparison.OrdinalIgnoreCase)) == true)
            RbRoleResearcher.IsChecked = true;
        else
            RbRoleWorker.IsChecked = true;
    }

    private void RenderQuants(ModelDepotCardEntry card)
    {
        if (card.Quants.Count == 0)
        {
            BdrQuants.IsVisible = false;
            return;
        }

        BdrQuants.IsVisible = true;
        TxtQuants.Text = card.QuantsDisplay;
    }

    // ── Download ─────────────────────────────────────────────────────────────

    private void BtnDownload_Click(object? sender, RoutedEventArgs e) => RunTrackedTask(DownloadSelectedSafeAsync());

    private async Task DownloadSelectedSafeAsync()
    {
        if (_transferRunning || _selected is null) return;
        _transferRunning = true;
        var card = _selected;
        var role = GetSelectedRole();

        try
        {
            // Prefer an Ollama pull when the card has a known Ollama tag — matches the
            // existing downloader's precedent (simplest, most reliable path for curated models).
            if (!string.IsNullOrWhiteSpace(card.Result.OllamaName))
            {
                await OllamaPullAsync(card, role);
                return;
            }

            var storagePath = _settings.ResolvedModelStoragePath;
            if (string.IsNullOrWhiteSpace(storagePath))
            {
                SetStatus("Model storage path is not configured.");
                return;
            }
            if (string.IsNullOrWhiteSpace(card.Result.HuggingFaceId))
            {
                SetStatus("No download source available for this model.");
                return;
            }

            var variants = card.Quants.Count > 0
                ? card.Quants
                : await _search.GetVariantsAsync(card.Result, _detectedVramGb);
            var variant = variants.FirstOrDefault(v => v.IsRecommended) ?? variants.FirstOrDefault();
            if (variant is null || string.IsNullOrWhiteSpace(variant.DownloadUrl))
            {
                SetStatus("No downloadable GGUF file found for this model.");
                return;
            }

            var fileName = Path.GetFileName(variant.DownloadUrl.Split('?')[0]);
            var destPath = Path.Combine(storagePath, fileName);

            await InvokeUiAsync(() =>
            {
                PanelDownloadProgress.IsVisible = true;
                TxtDlFileName.Text = fileName;
                TxtDlStatus.Text = "Starting download...";
                TxtDlStats.Text = "";
                PbDownload.Value = 0;
                BtnDownload.IsEnabled = false;
            });

            CancelAndDispose(ref _downloadCts);
            _downloadCts = new CancellationTokenSource();
            var ct = _downloadCts.Token;

            var progress = new Progress<(long done, long total, double speed, int eta)>(p => PostUi(() =>
            {
                var pct = p.total > 0 ? (double)p.done / p.total * 100 : 0;
                PbDownload.Value = pct;
                TxtDlStats.Text = $"{FormatBytes(p.done)} / {FormatBytes(p.total)}  {FormatSpeed(p.speed)}  ETA {p.eta}s";
                TxtDlStatus.Text = $"Downloading {fileName}...";
            }));
            var retryStatus = new Progress<string>(msg => PostUi(() => TxtDlStatus.Text = msg));
            await _downloader.DownloadAsync(variant.DownloadUrl, destPath, progress, ct, onRetry: retryStatus);
            if (_isClosed) return;

            var sha256Verified = false;
            if (!string.IsNullOrWhiteSpace(variant.Sha256))
            {
                await InvokeUiAsync(() => TxtDlStatus.Text = "Verifying SHA-256...");
                if (!await _downloader.VerifySha256Async(destPath, variant.Sha256, ct))
                {
                    try { File.Delete(destPath); } catch { }
                    await InvokeUiAsync(() => TxtDlStatus.Text = "SHA-256 mismatch — downloaded file was corrupt, deleted. Try again.");
                    SetStatus($"Download of {card.DisplayName} failed SHA-256 verification.");
                    return;
                }
                sha256Verified = true;
            }

            if (_isClosed) return;
            await InvokeUiAsync(() =>
            {
                TxtDlStatus.Text = sha256Verified ? "Download complete (SHA-256 verified). Registering with Ollama..." : "Download complete. Registering with Ollama...";
                PbDownload.Value = 100;
            });

            var ollamaName = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
            var logProgress = new Progress<string>(msg => PostUi(() => TxtDlStatus.Text = msg));
            await _downloader.RegisterWithOllamaAsync(destPath, ollamaName, logProgress, ct);

            if (role != "library")
                ModelDownloadService.ApplyToSettings(_settings, ollamaName, role);

            MarkInstalledAndRefresh(card, destPath, new FileInfo(destPath).Length);
            SetStatus(sha256Verified
                ? $"Downloaded {card.DisplayName} (SHA-256 verified) and assigned it to {role}."
                : $"Downloaded {card.DisplayName} and assigned it to {role}.");
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
            await InvokeUiAsync(() => BtnDownload.IsEnabled = _selected is not null && _selected.InstallState != ModelDepotInstallState.InstalledLocally);
        }
    }

    private async Task OllamaPullAsync(ModelDepotCardEntry card, string role)
    {
        var ollamaName = card.Result.OllamaName!;

        await InvokeUiAsync(() =>
        {
            PanelDownloadProgress.IsVisible = true;
            TxtDlFileName.Text = ollamaName;
            TxtDlStatus.Text = $"Running: ollama pull {ollamaName}...";
            TxtDlStats.Text = "";
            PbDownload.Value = 0;
            BtnDownload.IsEnabled = false;
        });

        CancelAndDispose(ref _downloadCts);
        _downloadCts = new CancellationTokenSource();

        try
        {
            var psi = new ProcessStartInfo("ollama", $"pull \"{ollamaName}\"")
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
            proc.OutputDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) PostUi(() => TxtDlStatus.Text = args.Data); };
            proc.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) PostUi(() => TxtDlStatus.Text = args.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync(_downloadCts.Token);
            _activeExternalProcess = null;

            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ollama pull exited with code {proc.ExitCode}");

            if (role != "library")
                ModelDownloadService.ApplyToSettings(_settings, ollamaName, role);

            await InvokeUiAsync(() =>
            {
                PbDownload.Value = 100;
                TxtDlStatus.Text = $"Pulled {ollamaName}.";
            });

            MarkInstalledAndRefresh(card, localPath: null, sizeBytes: null);
            SetStatus($"Pulled {card.DisplayName} via Ollama and assigned it to {role}.");
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
            await InvokeUiAsync(() => BtnDownload.IsEnabled = _selected is not null && _selected.InstallState != ModelDepotInstallState.InstalledLocally);
        }
    }

    /// <summary>
    /// Updates the card's install-state badge in place after a successful download, without
    /// forcing a full catalog re-scan (the whole point of this is the badge updating "without a
    /// manual refresh," per the plan's own verification step).
    /// </summary>
    private void MarkInstalledAndRefresh(ModelDepotCardEntry oldCard, string? localPath, long? sizeBytes)
    {
        var idx = _allCards.IndexOf(oldCard);
        if (idx < 0) return;

        var refreshed = new ModelDepotCardEntry
        {
            Result = oldCard.Result,
            Curated = oldCard.Curated,
            InstallState = ModelDepotInstallState.InstalledLocally,
            LocalPath = localPath ?? oldCard.LocalPath,
            LocalSizeBytes = sizeBytes ?? oldCard.LocalSizeBytes,
            LocalHeader = oldCard.LocalHeader,
            Evidence = oldCard.Evidence,
            Quants = oldCard.Quants,
            AvailableOnHivePeers = oldCard.AvailableOnHivePeers,
        };
        _allCards[idx] = refreshed;
        _selected = refreshed;

        PostUi(() =>
        {
            ApplyFilters();
            RenderDetail(refreshed);
        });
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

    // ── Small helpers (mirrors ModelDownloaderWindow's own conventions) ────────

    private static Border MakePill(string text, Color fg, Color bg, Avalonia.Thickness? margin = null) => new()
    {
        Background = new SolidColorBrush(bg),
        CornerRadius = new Avalonia.CornerRadius(3),
        Padding = new Avalonia.Thickness(6, 2),
        Margin = margin ?? default,
        Child = new TextBlock { Text = text.ToUpperInvariant(), Foreground = new SolidColorBrush(fg), FontSize = 10, FontWeight = FontWeight.Bold },
    };

    private static Border MakeStatChip(string text, string bg, string fg) => new()
    {
        Background = new SolidColorBrush(Color.Parse(bg)),
        CornerRadius = new Avalonia.CornerRadius(3),
        Padding = new Avalonia.Thickness(6, 3),
        Margin = new Avalonia.Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.Parse(fg)), FontSize = 11 },
    };

    private void SetStatus(string msg) => PostUi(() => TxtWindowStatus.Text = msg);

    private static async Task<(string Summary, int VramGb)> ProbeHardwareAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits")
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
        catch { }
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
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
        cts = null;
    }

    private void PostUi(Action action)
    {
        if (_isClosed) return;
        Dispatcher.UIThread.Post(() => { if (!_isClosed) action(); });
    }

    private Task InvokeUiAsync(Action action)
    {
        if (_isClosed) return Task.CompletedTask;
        return Dispatcher.UIThread.InvokeAsync(() => { if (!_isClosed) action(); }).GetTask();
    }

    private void TryStopActiveProcess()
    {
        var proc = _activeExternalProcess;
        if (proc is null) return;
        try { if (!proc.HasExited) proc.Kill(true); }
        catch { }
        finally { _activeExternalProcess = null; }
    }

    private void RunTrackedTask(Task task)
    {
        task.ContinueWith(
            t =>
            {
                if (_isClosed) return;
                var message = t.Exception?.GetBaseException().Message ?? "unknown error";
                SetStatus(message);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
