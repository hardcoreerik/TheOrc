using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace OrchestratorIDE.UI.Panels;

public partial class FileExplorerPanel : UserControl
{
    public event Action<string>? FileSelected;
    public event Action<string>? WorkspaceChanged;

    private string _currentRoot = "";

    private static readonly HashSet<string> SkipDirs =
    [
        "node_modules", ".venv", "venv", "__pycache__", ".git",
        "dist", "build", ".next", "target", "bin", "obj",
        ".pytest_cache", ".mypy_cache", ".ruff_cache"
    ];

    public FileExplorerPanel()
    {
        InitializeComponent();
    }

    public void LoadWorkspace(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return;
        _currentRoot = rootPath;
        TbWorkspaceLabel.Text = Path.GetFileName(rootPath);
        FileTree.Items.Clear();

        var root = BuildNode(rootPath, isRoot: true);
        if (root != null)
            FileTree.Items.Add(root);
    }

    // ── Public: open folder dialog (also callable from command palette) ──
    public void OpenFolderDialog()
    {
        var dlg = new OpenFolderDialog { Title = "Select workspace folder" };
        if (dlg.ShowDialog() == true)
        {
            var path = dlg.FolderName;
            LoadWorkspace(path);
            WorkspaceChanged?.Invoke(path);
        }
    }

    /// <summary>Public entry point — called by AgentPanel badge click or command palette.</summary>
    public void PromptOpenFolder() => OpenFolderDialog();

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        => OpenFolderDialog();

    private void BtnReveal_Click(object sender, RoutedEventArgs e)
        => RevealInExplorer(_currentRoot);

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileNode node && node.IsFile)
            FileSelected?.Invoke(node.FullPath);
    }

    // ── Shell helpers ─────────────────────────────────────────────────────

    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch { /* non-fatal */ }
    }

    // ── Tree building ─────────────────────────────────────────────────────

    private static FileNode? BuildNode(string path, bool isRoot = false, int depth = 0)
    {
        if (depth > 5) return null;

        var name = isRoot ? Path.GetFileName(path) : Path.GetFileName(path);
        var isDir = Directory.Exists(path);

        if (isDir && SkipDirs.Contains(name) && !isRoot) return null;
        if (!isRoot && name.StartsWith('.') &&
            name is not ".agent.md" and not ".gitignore" and not ".env.example") return null;

        var node = new FileNode
        {
            Name     = name,
            FullPath = path,
            IsFile   = !isDir,
            Icon     = isDir ? "📁" : GetFileIcon(name),
        };

        if (isDir)
        {
            try
            {
                var entries = Directory.GetFileSystemEntries(path)
                    .OrderBy(e => Directory.Exists(e) ? 0 : 1)
                    .ThenBy(e => Path.GetFileName(e).ToLower());

                foreach (var entry in entries)
                {
                    var child = BuildNode(entry, depth: depth + 1);
                    if (child != null)
                        node.Children.Add(child);
                }
            }
            catch { /* permission denied */ }
        }

        return node;
    }

    private static string GetFileIcon(string name)
    {
        return Path.GetExtension(name).ToLower() switch
        {
            ".cs"   => "🔷",
            ".py"   => "🐍",
            ".ts" or ".tsx" => "🔵",
            ".js" or ".jsx" => "🟡",
            ".json" => "📋",
            ".md"   => "📝",
            ".xml" or ".xaml" => "📄",
            ".toml" or ".yaml" or ".yml" => "⚙",
            ".sh" or ".ps1" or ".bat"    => "⚡",
            ".txt"  => "📃",
            ".png" or ".jpg" or ".svg"   => "🖼",
            _       => "📄"
        };
    }
}

public class FileNode
{
    public string Name     { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Icon     { get; set; } = "📄";
    public bool   IsFile   { get; set; }
    public ObservableCollection<FileNode> Children { get; } = [];
}
