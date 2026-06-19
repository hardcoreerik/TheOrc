// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OrchestratorIDE.UI.Controls;

namespace OrchestratorIDE.UI.Windows;

public sealed record HelpGuide(string FileName, string Title, string Content);

public partial class HelpWindow : Window
{
    private static HelpWindow? _instance;

    private readonly List<HelpGuide> _guides = [];
    private HelpGuide? _current;
    private bool _suppressSelection;

    private const string RepoDocsUrl =
        "https://github.com/hardcoreerik/TheOrc/blob/master/docs/";

    private static readonly string[] PreferredOrder =
    [
        "QUICK_START.md", "INSTALLATION.md", "USER_GUIDE.md", "FAQ.md",
        "SINGLE_AGENT_GUIDE.md", "SWARM_GUIDE.md", "MODEL_GUIDE.md",
        "MODEL_WIKI_AND_LAB.md", "HARDWARE_GUIDE.md", "TRAINING_PIT_GUIDE.md",
        "DATASET_REVIEW_WORKFLOW.md", "REVIEWER_ADAPTER_GUIDE.md",
        "TESTING_GUIDE.md", "TROUBLESHOOTING.md", "ROADMAP.md",
    ];

    public HelpWindow()
    {
        InitializeComponent();
        DocView.LinkClicked = OnDocLinkClicked;
        Opened += (_, _) =>
        {
            if (_current is not null)
                Render(_current);
            else if (LstGuides.SelectedItem is HelpGuide selected)
                Render(selected);
        };
        LoadGuides();
        PopulateList("");
    }

    public static void ShowGuide(Window owner, string fileName = "QUICK_START.md")
    {
        if (_instance is null || !_instance.IsVisible)
        {
            _instance = new HelpWindow();
            _instance.Closed += (_, _) => _instance = null;
        }

        _instance.Show(owner);
        _instance.Activate();
        _instance.SelectGuide(fileName);
    }

    private void LoadGuides()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var asm = typeof(HelpWindow).Assembly;
        foreach (var resourceName in asm.GetManifestResourceNames())
        {
            var marker = resourceName.IndexOf(".Resources.docs.", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || !resourceName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = resourceName[(marker + ".Resources.docs.".Length)..];
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
                continue;

            using var reader = new StreamReader(stream);
            byName[fileName] = reader.ReadToEnd();
        }

        foreach (var dir in CandidateDocsDirs())
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var path in Directory.GetFiles(dir, "*.md"))
            {
                try { byName[Path.GetFileName(path)] = File.ReadAllText(path); }
                catch { }
            }

            break;
        }

        foreach (var excluded in new[] { "DOCUMENTATION_STANDARD.md", "SPONSOR_TEST_LAB.md" })
            byName.Remove(excluded);

        foreach (var pair in byName)
            _guides.Add(new HelpGuide(pair.Key, TitleOf(pair.Key, pair.Value), pair.Value));

        _guides.Sort((a, b) =>
        {
            var ia = Array.IndexOf(PreferredOrder, a.FileName);
            var ib = Array.IndexOf(PreferredOrder, b.FileName);
            if (ia < 0 && ib < 0)
                return string.Compare(a.Title, b.Title, StringComparison.Ordinal);
            if (ia < 0) return 1;
            if (ib < 0) return -1;
            return ia.CompareTo(ib);
        });
    }

    private static IEnumerable<string> CandidateDocsDirs()
    {
        var exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "docs");
        yield return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "docs"));
        yield return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "docs"));
    }

    private static string TitleOf(string fileName, string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                return trimmed[2..].Replace("TheOrc - ", "").Replace(" - TheOrc", "").Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
                break;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').ToLowerInvariant();
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(stem);
    }

    private void PopulateList(string filter)
    {
        _suppressSelection = true;
        LstGuides.ItemsSource = null;

        var items = _guides
            .Where(g =>
                filter.Length == 0 ||
                g.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                g.Content.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        LstGuides.ItemsSource = items;
        TbSearchHint.Text = filter.Length == 0
            ? $"{_guides.Count} guides"
            : $"{items.Count} of {_guides.Count} guides match";
        _suppressSelection = false;
    }

    private void TxtSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var filter = TxtSearch.Text?.Trim() ?? "";
        PopulateList(filter);

        var keep = (LstGuides.ItemsSource as IEnumerable<HelpGuide>)?
            .FirstOrDefault(g => ReferenceEquals(g, _current));
        if (keep is not null)
            LstGuides.SelectedItem = keep;
        else if (LstGuides.ItemCount > 0)
            LstGuides.SelectedIndex = 0;
    }

    private void LstGuides_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
            return;

        if (LstGuides.SelectedItem is HelpGuide guide)
            Render(guide);
    }

    private void SelectGuide(string fileName)
    {
        var basename = Path.GetFileName(fileName);
        var guide = _guides.FirstOrDefault(g =>
            g.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
            g.FileName.Equals(basename, StringComparison.OrdinalIgnoreCase));

        if (guide is not null)
            LstGuides.SelectedItem = guide;
        else if (_guides.Count > 0)
            LstGuides.SelectedIndex = 0;
    }

    private void Render(HelpGuide guide)
    {
        _current = guide;
        TbTitle.Text = guide.Title;
        TbSubtitle.Text = guide.FileName;
        DocView.Text = Regex.Replace(
            guide.Content,
            @"\]\((?!https?://|orcdoc://|#|mailto:)([\w./-]+?\.md)(#[\w-]*)?\)",
            m => $"](orcdoc://{m.Groups[1].Value.Replace('\\', '/')})");
    }

    private void OnDocLinkClicked(string url)
    {
        if (url.StartsWith("orcdoc://", StringComparison.OrdinalIgnoreCase))
        {
            var target = url["orcdoc://".Length..].Trim('/');
            SelectGuide(target);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnOpenGitHub_Click(object? sender, RoutedEventArgs e)
    {
        var url = RepoDocsUrl + (_current?.FileName ?? "");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
