// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UI.Windows;

public partial class ModelLibraryWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<Window>? _createDownloaderWindow;

    public ModelLibraryWindow(AppSettings settings, Func<Window>? createDownloaderWindow = null)
    {
        _settings = settings;
        _createDownloaderWindow = createDownloaderWindow;
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        PopulateLibrary();
    }

    private void PopulateLibrary()
    {
        ItemsPanel.Children.Clear();

        var roles = new[]
        {
            ("Worker · Coder", _settings.LastWorkerModel, Color.FromRgb(0x4A, 0xCA, 0x4A), Color.FromRgb(0x0D, 0x1A, 0x0D)),
            ("Boss · Orchestrator", _settings.LastSwarmModel, Color.FromRgb(0xFF, 0xB3, 0x00), Color.FromRgb(0x1A, 0x14, 0x00)),
            ("Researcher", _settings.LastResearcherModel, Color.FromRgb(0x4A, 0x9F, 0xD9), Color.FromRgb(0x0A, 0x14, 0x20)),
        };

        var count = 0;
        foreach (var (label, model, fg, bg) in roles)
        {
            if (string.IsNullOrWhiteSpace(model)) continue;
            count++;
            ItemsPanel.Children.Add(BuildRoleRow(label, model, fg, bg));
        }

        var storageDir = _settings.ResolvedModelStoragePath;
        if (!string.IsNullOrWhiteSpace(storageDir) && Directory.Exists(storageDir))
        {
            foreach (var file in Directory.GetFiles(storageDir, "*.gguf"))
            {
                count++;
                ItemsPanel.Children.Add(BuildFileRow(file));
            }
        }

        if (count == 0)
        {
            ItemsPanel.Children.Add(new TextBlock
            {
                Text = "No models found. Use Model -> Download to get started.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 13,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 20, 0, 0),
            });
            TbStatus.Text = "No models installed";
        }
        else
        {
            TbStatus.Text = $"{count} model{(count == 1 ? "" : "s")} found";
        }
    }

    private static Border BuildRoleRow(string roleLabel, string model, Color fg, Color bg)
    {
        var top = new DockPanel();
        top.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, fg.R, fg.G, fg.B)),
            CornerRadius = new Avalonia.CornerRadius(3),
            Padding = new Avalonia.Thickness(6, 2),
            Margin = new Avalonia.Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = roleLabel.ToUpperInvariant(),
                Foreground = new SolidColorBrush(fg),
                FontSize = 9,
                FontWeight = FontWeight.Bold,
            }
        });
        top.Children.Add(new TextBlock
        {
            Text = model,
            Foreground = Brushes.White,
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        return new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(12, 10),
            Child = top,
        };
    }

    private static Border BuildFileRow(string path)
    {
        var info = new FileInfo(path);
        var size = info.Length < 1_073_741_824
            ? $"{info.Length / 1_048_576.0:F0} MB"
            : $"{info.Length / 1_073_741_824.0:F2} GB";

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        var sizeText = new TextBlock
        {
            Text = size,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetColumn(sizeText, 1);
        grid.Children.Add(sizeText);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(12, 8),
            Child = grid,
        };
    }

    private void BtnOpenDownloader_Click(object? sender, RoutedEventArgs e)
        => RunWindowTask(OpenDownloaderAsync());

    private async Task OpenDownloaderAsync()
    {
        var downloader = _createDownloaderWindow?.Invoke() ?? new ModelDownloaderWindow(_settings);
        await downloader.ShowDialog(this);
        await Dispatcher.UIThread.InvokeAsync(PopulateLibrary);
    }

    private void RunWindowTask(Task task)
    {
        task.ContinueWith(
            t =>
            {
                var message = t.Exception?.GetBaseException().Message ?? "unknown error";
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible)
                        return;
                    TbStatus.Text = $"Failed to open downloader: {message}";
                });
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
