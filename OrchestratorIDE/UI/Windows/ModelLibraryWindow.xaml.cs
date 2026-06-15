// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UI.Windows;

public partial class ModelLibraryWindow : Window
{
    private readonly AppSettings _settings;

    public ModelLibraryWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Loaded   += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateLibrary();
    }

    private void PopulateLibrary()
    {
        ModelListPanel.Children.Clear();

        var roles = new[]
        {
            ("Worker · Coder",      _settings.LastWorkerModel,     "#4ACA4A", "#0D1A0D"),
            ("Boss · Orchestrator", _settings.LastSwarmModel,      "#FFB300", "#1A1400"),
            ("Researcher",          _settings.LastResearcherModel,  "#4A9FD9", "#0A1420"),
        };

        var count = 0;
        foreach (var (label, model, fg, bg) in roles)
        {
            if (string.IsNullOrEmpty(model)) continue;
            count++;
            ModelListPanel.Children.Add(BuildModelRow(label, model, fg, bg));
        }

        // Scan model storage directory for GGUF files
        var storageDir = _settings.ResolvedModelStoragePath;
        if (!string.IsNullOrEmpty(storageDir) && Directory.Exists(storageDir))
        {
            var ggufFiles = Directory.GetFiles(storageDir, "*.gguf");
            foreach (var file in ggufFiles)
            {
                var name = Path.GetFileName(file);
                var size = new FileInfo(file).Length;
                ModelListPanel.Children.Add(BuildFileRow(name, file, size));
                count++;
            }
        }

        if (count == 0)
        {
            ModelListPanel.Children.Add(new TextBlock
            {
                Text       = "No models found. Use Model → Download to get started.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 13,
                Margin     = new Thickness(0, 24, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            TxtLibStatus.Text = "No models installed";
        }
        else
        {
            TxtLibStatus.Text = $"{count} model{(count == 1 ? "" : "s")} found";
        }
    }

    private static Border BuildModelRow(string roleLabel, string model, string fgHex, string bgHex)
    {
        var fg = (Color)ColorConverter.ConvertFromString(fgHex);
        var bg = (Color)ColorConverter.ConvertFromString(bgHex);

        var row = new Border
        {
            Background      = new SolidColorBrush(bg),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Margin          = new Thickness(0, 0, 0, 6),
            Padding         = new Thickness(12, 10, 12, 10),
        };

        var sp = new StackPanel();

        var topRow = new DockPanel();
        var badge = new Border
        {
            Background   = new SolidColorBrush(Color.FromArgb(40, fg.R, fg.G, fg.B)),
            CornerRadius = new CornerRadius(3),
            Padding      = new Thickness(6, 2, 6, 2),
            Margin       = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text       = roleLabel.ToUpper(),
                Foreground = new SolidColorBrush(fg),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
            },
        };
        DockPanel.SetDock(badge, Dock.Left);
        topRow.Children.Add(badge);
        topRow.Children.Add(new TextBlock
        {
            Text       = model,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        sp.Children.Add(topRow);
        row.Child = sp;
        return row;
    }

    private static Border BuildFileRow(string name, string path, long sizeBytes)
    {
        var size = sizeBytes switch
        {
            < 1_073_741_824 => $"{sizeBytes / 1_048_576.0:F0} MB",
            _               => $"{sizeBytes / 1_073_741_824.0:F2} GB",
        };

        var row = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Margin          = new Thickness(0, 0, 0, 4),
            Padding         = new Thickness(12, 8, 12, 8),
        };

        var dp = new DockPanel();

        var sizeLabel = new TextBlock
        {
            Text       = size,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 11,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        DockPanel.SetDock(sizeLabel, Dock.Right);
        dp.Children.Add(sizeLabel);

        dp.Children.Add(new TextBlock
        {
            Text       = name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        row.Child = dp;
        return row;
    }

    private void BtnOpenDownloader_Click(object sender, RoutedEventArgs e)
    {
        var win = new ModelDownloaderWindow(_settings) { Owner = this };
        win.ShowDialog();
        PopulateLibrary(); // refresh after potential download
    }
}
