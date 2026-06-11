using System.Text;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Exports the Model Wiki capability matrix as Markdown (v1.4 roadmap item).
/// Pure string building over already-merged ModelWikiEntry data — no IO, no
/// store reads — so the output is unit-testable and the caller decides where
/// the file goes.
/// </summary>
public static class ModelWikiExporter
{
    /// <summary>
    /// Renders the full capability matrix for the given entries.
    /// Entries should already be merged by ModelWikiService.BuildAll.
    /// </summary>
    public static string ToMarkdown(IReadOnlyList<ModelWikiEntry> entries, DateTime? now = null)
    {
        var sb = new StringBuilder();
        var stamp = (now ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm");

        sb.AppendLine("# TheOrc — Model Capability Matrix");
        sb.AppendLine();
        sb.AppendLine($"> Generated {stamp} from the Model Wiki (built-in profiles + GOBLIN MIND");
        sb.AppendLine("> probes + local test observations on this machine). Scores are 0–10.");
        sb.AppendLine();

        // ── Role scores + probe results ───────────────────────────────────────
        sb.AppendLine("## Capability scores");
        sb.AppendLine();
        sb.AppendLine("| Model | Installed | Speed | VRAM | Boss | Coder | Researcher | Tester | Dispatch | Format | Categories |");
        sb.AppendLine("|---|:-:|---|---|:-:|:-:|:-:|:-:|---|---|---|");

        foreach (var e in entries)
        {
            var p     = e.Profile;
            var probe = e.ProbeProfile;
            string dispatch = probe?.RecommendedMode.ToString() ?? "—";
            string format   = probe?.FormatProfile?.PreferredFormat.ToString() ?? "—";
            string cats     = probe?.CategoryProfile?.ShortSummary ?? "—";

            sb.AppendLine(
                $"| {Escape(e.DisplayName)} (`{e.ModelId}`) " +
                $"| {(e.IsInstalled ? "✅" : "—")} " +
                $"| {e.SpeedLabel} | {e.VramLabel} " +
                $"| {p.BossScore} | {p.CoderScore} | {p.ResearcherScore} | {p.TesterScore} " +
                $"| {dispatch} | {format} | {Escape(cats)} |");
        }

        // ── Routing recommendations ───────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("## Routing recommendations");
        sb.AppendLine();
        sb.AppendLine("| Model | Boss | Coder | Researcher | Tester | Single-agent | Swarm worker | Long write_file |");
        sb.AppendLine("|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|");

        foreach (var e in entries)
        {
            var r = ModelWikiService.GetRoutingRecommendation(e);
            sb.AppendLine(
                $"| `{e.ModelId}` | {Mark(r.Boss)} | {Mark(r.Coder)} | {Mark(r.Researcher)} " +
                $"| {Mark(r.Tester)} | {Mark(r.SingleAgent)} | {Mark(r.SwarmWorker)} | {Mark(r.LongWriteFile)} |");
        }

        // ── Warnings worth surfacing in a doc ─────────────────────────────────
        var warned = entries.Where(e => e.HasLongWriteWarning).ToList();
        if (warned.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Known warnings");
            sb.AppendLine();
            foreach (var e in warned)
                sb.AppendLine($"- `{e.ModelId}` — unreliable for long `write_file` payloads " +
                              "(observed truncation or failed FileWriteLarge test)");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*Exported by TheOrc Model Wiki. Probe a model (GOBLIN MIND) to fill in " +
                      "Dispatch/Format/Categories; run capability tests to refine routing.*");
        return sb.ToString();
    }

    /// <summary>Yes/Limited/No → compact marks that survive a Markdown table.</summary>
    private static string Mark(string value) => value switch
    {
        "Yes"     => "✅",
        "Limited" => "⚠",
        "No"      => "❌",
        _         => "—",
    };

    private static string Escape(string s) => s.Replace("|", "\\|");
}
