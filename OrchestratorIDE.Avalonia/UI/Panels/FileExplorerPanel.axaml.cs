// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace OrchestratorIDE.UI.Panels;

public partial class FileExplorerPanel : UserControl
{
    public event Action<string>? FileSelected;
    public event Action<string>? WorkspaceChanged;

    private string _currentRoot = "";
    private readonly ObservableCollection<FileNode> _roots = [];

    private static readonly HashSet<string> SkipDirs =
    [
        "node_modules", ".venv", "venv", "__pycache__", ".git",
        "dist", "build", ".next", "target", "bin", "obj",
        ".pytest_cache", ".mypy_cache", ".ruff_cache"
    ];

    // macOS only: ~/Library stays out of the tree unless the user picks it as the workspace
    // root (Finder hides it too). It is huge and holds iCloud Drive ("Mobile Documents"),
    // whose not-yet-downloaded placeholders block enumeration until they materialise. Other
    // home folders load on demand like any folder: expanding a TCC-protected one (Desktop,
    // Documents, ...) shows macOS's normal privacy prompt on a background thread instead of
    // hanging the UI.
    private static readonly string? MacHomeLibrary = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library")
        : null;

    public FileExplorerPanel()
    {
        InitializeComponent();
        FileTree.ItemsSource = _roots;
        FileTree.AddHandler(TreeViewItem.ExpandedEvent, FileTree_ItemExpanded);
    }

    /// <summary>
    /// Shows <paramref name="rootPath"/> in the tree. Folders load lazily, one level at a time,
    /// on a background thread: the old eager walk (depth 5, on the UI thread, inside
    /// MainWindow's constructor) froze startup on big trees and hung outright on folders whose
    /// enumeration blocks (iCloud placeholders, privacy prompts, slow network shares).
    /// </summary>
    public void LoadWorkspace(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return;
        _currentRoot = rootPath;
        TbWorkspaceLabel.Text = Path.GetFileName(rootPath);
        _roots.Clear();

        var root = CreateNode(rootPath, isDir: true);
        _roots.Add(root);
        // Pre-load the first level so expanding the root is instant.
        _ = EnsureChildrenLoadedAsync(root);
    }

    private void FileTree_ItemExpanded(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: FileNode node })
            _ = EnsureChildrenLoadedAsync(node);
    }

    /// <summary>
    /// Replaces a folder's "Loading…" placeholder with its real children. Enumeration runs on
    /// the thread pool; the collection is updated back on the caller's (UI) context. Loads at
    /// most once per node.
    /// </summary>
    internal static async Task EnsureChildrenLoadedAsync(FileNode node)
    {
        if (node.IsFile || node.LoadState != FileNodeLoadState.NotLoaded) return;
        node.LoadState = FileNodeLoadState.Loading;

        var children = await Task.Run(() => ListChildren(node.FullPath));

        node.Children.Clear();
        foreach (var child in children) node.Children.Add(child);
        node.LoadState = FileNodeLoadState.Loaded;
    }

    public async void OpenFolderDialog()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Select workspace folder",
                AllowMultiple = false
            });

        if (folders.Count == 0) return;
        var path = folders[0].Path.LocalPath;
        LoadWorkspace(path);
        WorkspaceChanged?.Invoke(path);
    }

    public void PromptOpenFolder() => OpenFolderDialog();

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e) => OpenFolderDialog();
    private void BtnReveal_Click(object? sender, RoutedEventArgs e)     => RevealInExplorer(_currentRoot);

    private void FileTree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FileTree.SelectedItem is FileNode node && node.IsFile)
            FileSelected?.Invoke(node.FullPath);
    }

    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
#if WINDOWS
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { /* non-fatal */ }
#endif
    }

    /// <summary>
    /// One level of <paramref name="dirPath"/>: folders first, then files, by name, with the
    /// same filters the eager walk used. Child folders are not enumerated; each gets a
    /// placeholder child so the tree still shows its expand arrow. An unreadable folder
    /// yields no children, as before.
    /// </summary>
    internal static List<FileNode> ListChildren(string dirPath)
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(dirPath); }
        catch { return []; /* permission denied, vanished, or a cloud-storage error */ }

        var nodes = new List<FileNode>(entries.Length);
        foreach (var entry in entries)
        {
            var isDir = Directory.Exists(entry);
            if (IsHidden(entry, Path.GetFileName(entry), isDir)) continue;
            nodes.Add(CreateNode(entry, isDir));
        }

        return nodes
            .OrderBy(n => n.IsFile ? 1 : 0)
            .ThenBy(n => n.Name.ToLower())
            .ToList();
    }

    private static bool IsHidden(string path, string name, bool isDir) =>
        (isDir && SkipDirs.Contains(name)) ||
        (isDir && MacHomeLibrary is not null &&
            string.Equals(path, MacHomeLibrary, StringComparison.OrdinalIgnoreCase)) ||
        (name.StartsWith('.') &&
            name is not ".agent.md" and not ".gitignore" and not ".env.example");

    private static FileNode CreateNode(string path, bool isDir)
    {
        var name = Path.GetFileName(path);
        var node = new FileNode
        {
            Name     = name,
            FullPath = path,
            IsFile   = !isDir,
            Icon     = isDir ? "📁" : GetFileIcon(name),
        };
        if (isDir) node.Children.Add(FileNode.CreatePlaceholder());
        return node;
    }

    private static string GetFileIcon(string name) =>
        Path.GetExtension(name).ToLower() switch
        {
            ".cs"                             => "🔷",
            ".py"                             => "🐍",
            ".ts" or ".tsx"                   => "🔵",
            ".js" or ".jsx"                   => "🟡",
            ".json"                           => "📋",
            ".md"                             => "📝",
            ".xml" or ".xaml" or ".axaml"     => "📄",
            ".toml" or ".yaml" or ".yml"      => "⚙",
            ".sh"  or ".ps1"  or ".bat"       => "⚡",
            ".txt"                            => "📃",
            ".png" or ".jpg"  or ".svg"       => "🖼",
            _                                 => "📄"
        };
}

public class FileNode
{
    public string Name     { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Icon     { get; set; } = "📄";
    public bool   IsFile   { get; set; }
    /// <summary>The "Loading…" row a folder shows until it is first expanded.</summary>
    public bool   IsPlaceholder { get; init; }
    public ObservableCollection<FileNode> Children { get; } = [];

    internal FileNodeLoadState LoadState { get; set; }

    internal static FileNode CreatePlaceholder() => new()
    {
        Name          = "Loading…",
        Icon          = "⏳",
        IsPlaceholder = true,
        LoadState     = FileNodeLoadState.Loaded,
    };
}

internal enum FileNodeLoadState { NotLoaded, Loading, Loaded }
