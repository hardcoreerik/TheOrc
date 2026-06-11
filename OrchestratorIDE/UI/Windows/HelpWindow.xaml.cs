using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using OrchestratorIDE.Research;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// In-app documentation browser. Guides are embedded into the assembly at build
/// time (docs/*.md), so help works on published single-file installs where no
/// docs/ folder ships next to the exe. A docs/ folder on disk (repo checkouts)
/// takes precedence so edits show without rebuilding.
///
/// Relative links between guides ([User Guide](USER_GUIDE.md)) navigate inside
/// this window; external links open the default browser as usual.
/// </summary>
public partial class HelpWindow : Window
{
    private sealed record Guide(string FileName, string Title, string Content);

    private static HelpWindow? _instance;

    private readonly List<Guide> _guides = [];
    private Guide? _current;
    private bool _suppressSelection;

    private const string RepoDocsUrl =
        "https://github.com/hardcoreerik/TheOrc/blob/master/docs/";

    // Display order + friendly titles for well-known guides; anything not listed
    // appears after these, alphabetically, titled from its first heading.
    private static readonly string[] PreferredOrder =
    [
        "QUICK_START.md", "INSTALLATION.md", "USER_GUIDE.md", "FAQ.md",
        "SINGLE_AGENT_GUIDE.md", "SWARM_GUIDE.md", "MODEL_GUIDE.md",
        "MODEL_WIKI_AND_LAB.md", "HARDWARE_GUIDE.md", "TRAINING_PIT_GUIDE.md",
        "DATASET_REVIEW_WORKFLOW.md", "TESTING_GUIDE.md", "TROUBLESHOOTING.md",
        "ROADMAP.md",
    ];

    private HelpWindow()
    {
        InitializeComponent();
        LoadGuides();
        PopulateList(filter: "");

        // Catch hyperlink navigation after MarkdownFlowDocument's own handler
        // (handledEventsToo) so orcdoc:// links route inside the window.
        DocViewer.AddHandler(Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(OnDocLinkNavigate), handledEventsToo: true);
    }

    /// <summary>Opens (or focuses) the single Help window, showing the given guide.</summary>
    public static void ShowGuide(Window owner, string fileName = "QUICK_START.md")
    {
        if (_instance is null || !_instance.IsLoaded)
        {
            _instance = new HelpWindow();
            if (owner.IsLoaded) _instance.Owner = owner;
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        _instance.Activate();
        _instance.SelectGuide(fileName);
    }

    // ── Guide loading ─────────────────────────────────────────────────────────

    private void LoadGuides()
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Embedded resources (always present in published builds)
        var asm = Assembly.GetExecutingAssembly();
        foreach (string res in asm.GetManifestResourceNames())
        {
            // Names look like "OrchestratorIDE.Resources.docs.QUICK_START.md"
            int marker = res.IndexOf(".docs.", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || !res.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;

            string fileName = res[(marker + ".docs.".Length)..];
            using var stream = asm.GetManifestResourceStream(res);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            byName[fileName] = reader.ReadToEnd();
        }

        // 2. docs/ on disk overrides embedded copies (repo checkouts stay fresh)
        foreach (string dir in CandidateDocsDirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string path in Directory.GetFiles(dir, "*.md"))
            {
                try { byName[Path.GetFileName(path)] = File.ReadAllText(path); }
                catch { /* unreadable file — keep embedded copy */ }
            }
            break; // first existing dir wins
        }

        // Hide contributor-process docs from the user-facing list
        string[] exclude = ["DOCUMENTATION_STANDARD.md", "SPONSOR_TEST_LAB.md"];
        foreach (string ex in exclude) byName.Remove(ex);

        foreach (var (file, content) in byName)
            _guides.Add(new Guide(file, TitleOf(file, content), content));

        _guides.Sort((a, b) =>
        {
            int ia = Array.IndexOf(PreferredOrder, a.FileName);
            int ib = Array.IndexOf(PreferredOrder, b.FileName);
            if (ia < 0 && ib < 0) return string.Compare(a.Title, b.Title, StringComparison.Ordinal);
            if (ia < 0) return 1;
            if (ib < 0) return -1;
            return ia.CompareTo(ib);
        });
    }

    private static IEnumerable<string> CandidateDocsDirs()
    {
        string exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "docs");
        // bin\Release\net10.0-windows\ → repo root
        yield return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "docs"));
        yield return Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "docs"));
    }

    private static string TitleOf(string fileName, string content)
    {
        foreach (string line in content.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("# "))
                return t[2..].Replace("TheOrc — ", "").Replace(" — TheOrc", "").Trim();
            if (t.Length > 0 && !t.StartsWith('#')) break; // body before any H1
        }
        // Fall back to prettified filename: "MODEL_WIKI_AND_LAB" → "Model Wiki And Lab"
        string stem = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').ToLowerInvariant();
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(stem);
    }

    // ── List + search ─────────────────────────────────────────────────────────

    private void PopulateList(string filter)
    {
        _suppressSelection = true;
        LstGuides.Items.Clear();

        foreach (var g in _guides)
        {
            bool match = filter.Length == 0
                || g.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || g.Content.Contains(filter, StringComparison.OrdinalIgnoreCase);
            if (match)
                LstGuides.Items.Add(new ListBoxItem { Content = g.Title, Tag = g });
        }

        TxtSearchHint.Text = filter.Length == 0
            ? $"{_guides.Count} guides — type to search"
            : $"{LstGuides.Items.Count} of {_guides.Count} guides match";
        _suppressSelection = false;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = TxtSearch.Text.Trim();
        PopulateList(filter);

        // Keep the current guide selected if it still matches; else select first hit
        var keep = LstGuides.Items.Cast<ListBoxItem>()
            .FirstOrDefault(i => ReferenceEquals(i.Tag, _current));
        if (keep is not null) { _suppressSelection = true; keep.IsSelected = true; _suppressSelection = false; }
        else if (filter.Length > 0 && LstGuides.Items.Count > 0)
            ((ListBoxItem)LstGuides.Items[0]).IsSelected = true;
    }

    private void LstGuides_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (LstGuides.SelectedItem is ListBoxItem { Tag: Guide g })
            Render(g);
    }

    private void SelectGuide(string fileName)
    {
        var item = LstGuides.Items.Cast<ListBoxItem>()
            .FirstOrDefault(i => i.Tag is Guide g &&
                g.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
        if (item is not null) item.IsSelected = true;
        else if (LstGuides.Items.Count > 0) ((ListBoxItem)LstGuides.Items[0]).IsSelected = true;
    }

    // ── Rendering + link routing ──────────────────────────────────────────────

    private void Render(Guide g)
    {
        _current = g;
        TxtSubtitle.Text = g.FileName;

        // Rewrite relative guide links to a private scheme BEFORE parsing so
        // MarkdownFlowDocument produces valid absolute URIs for them.
        // [User Guide](USER_GUIDE.md) → [User Guide](orcdoc://USER_GUIDE.md)
        string md = System.Text.RegularExpressions.Regex.Replace(
            g.Content,
            @"\]\((?!https?://|orcdoc://|#|mailto:)([\w./-]+?\.md)(#[\w-]*)?\)",
            m => $"](orcdoc://{Path.GetFileName(m.Groups[1].Value.Replace('\\', '/'))})");

        DocViewer.Document = MarkdownFlowDocument.Parse(md);
        DocViewer.Document.PagePadding = new Thickness(0);
    }

    private void OnDocLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri is { Scheme: "orcdoc" })
        {
            // Uri "orcdoc://USER_GUIDE.md" parses with Host=user_guide.md (lowercased);
            // recover the real casing from the original string.
            string target = e.Uri.OriginalString["orcdoc://".Length..].Trim('/');
            SelectGuide(target);
            e.Handled = true;
        }
        // http/https already opened by MarkdownFlowDocument's own handler.
    }

    // ── Header actions ────────────────────────────────────────────────────────

    private void BtnOpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        string url = RepoDocsUrl + (_current?.FileName ?? "");
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no browser available */ }
    }
}
