// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.Tools;

public static class FabricTools
{
    public static void Register(ToolRegistry registry, string workspaceRoot, string? graphDbRoot = null)
    {
        string dbRoot = string.IsNullOrEmpty(graphDbRoot) ? workspaceRoot : graphDbRoot;
        string dbPath = Path.Combine(dbRoot, ".orc", "theorc.db");

        registry.Register(new ToolDefinition
        {
            Name = "library_list",
            Description = "List Context Fabric corpora and document counts. Read-only.",
            Parameters = new(),
            Required = [],
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                if (!File.Exists(dbPath))
                    return Task.FromResult("[library_list] Context Fabric database not available.");

                using var store = new SqliteStore(dbRoot);
                store.Initialize();
                var repo = new FabricLibraryRepository(store);
                var corpora = repo.ListCorpora();
                if (corpora.Count == 0) return Task.FromResult("[library_list]\n(no corpora)");

                var sb = new StringBuilder();
                sb.AppendLine($"[library_list] {corpora.Count} corpora");
                foreach (var corpus in corpora)
                {
                    var docs = repo.ListDocuments(corpus.CorpusId);
                    sb.AppendLine($"{corpus.CorpusId} | {corpus.Name} | status={corpus.Status} | docs={docs.Count}");
                }
                return Task.FromResult(sb.ToString().TrimEnd());
            }
        });

        registry.Register(new ToolDefinition
        {
            Name = "library_search",
            Description = "Search Context Fabric segment text by BM25/FTS and return source-grounded hits. Read-only.",
            Parameters = new()
            {
                ["query"] = new("string", "Search query."),
                ["corpus_id"] = new("string", "Optional corpus id filter."),
                ["limit"] = new("number", "Maximum results. Default 10."),
            },
            Required = ["query"],
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                var query = GetString(args, "query");
                if (string.IsNullOrWhiteSpace(query))
                    return Task.FromResult("[ERROR] query is required.");
                if (!File.Exists(dbPath))
                    return Task.FromResult("[library_search] Context Fabric database not available.");

                using var store = new SqliteStore(dbRoot);
                store.Initialize();
                var repo = new FabricLibraryRepository(store);
                var graph = new DocumentGraphRepository(store);
                var search = new FabricSearchService(repo, graph);
                var hits = search.Search(query, GetString(args, "corpus_id"), GetInt(args, "limit") ?? 10);
                if (hits.Count == 0) return Task.FromResult("[library_search]\n(no matches)");

                var sb = new StringBuilder();
                sb.AppendLine($"[library_search] {hits.Count} hit(s)");
                foreach (var hit in hits)
                {
                    var heading = string.IsNullOrWhiteSpace(hit.HeadingPath) ? "-" : hit.HeadingPath;
                    sb.AppendLine($"{hit.CorpusId} | {hit.DisplayName} | seg={hit.SegmentId} | ordinal={hit.Ordinal} | heading={heading} | via={hit.RetrievalPath}");
                    if (!string.IsNullOrWhiteSpace(hit.ClaimId))
                        sb.AppendLine($"  claim={hit.ClaimId} [{hit.VerificationStatus}] {Trim(hit.ClaimText!)}");
                    sb.AppendLine($"  {Trim(hit.Text)}");
                }
                return Task.FromResult(sb.ToString().TrimEnd());
            }
        });

        registry.Register(new ToolDefinition
        {
            Name = "library_open",
            Description = "Open exact Context Fabric source segments by document_id or segment_id. Read-only.",
            Parameters = new()
            {
                ["document_id"] = new("string", "Document id to open."),
                ["segment_id"] = new("string", "Segment id to open directly."),
                ["start_ordinal"] = new("number", "When opening a document, first ordinal to include. Default 0."),
                ["count"] = new("number", "When opening a document, max segment count. Default 8."),
            },
            Required = [],
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                var documentId = GetString(args, "document_id");
                var segmentId = GetString(args, "segment_id");
                if (string.IsNullOrWhiteSpace(documentId) && string.IsNullOrWhiteSpace(segmentId))
                    return Task.FromResult("[ERROR] document_id or segment_id is required.");
                if (!File.Exists(dbPath))
                    return Task.FromResult("[library_open] Context Fabric database not available.");

                using var store = new SqliteStore(dbRoot);
                store.Initialize();
                var repo = new FabricLibraryRepository(store);

                if (!string.IsNullOrWhiteSpace(segmentId))
                {
                    var segment = repo.GetSegment(segmentId);
                    if (segment is null) return Task.FromResult($"[library_open] no segment '{segmentId}'");
                    return Task.FromResult(FormatSegment(segment));
                }

                var document = repo.GetDocument(documentId!);
                if (document is null) return Task.FromResult($"[library_open] no document '{documentId}'");
                var start = Math.Max(0, GetInt(args, "start_ordinal") ?? 0);
                var count = Math.Clamp(GetInt(args, "count") ?? 8, 1, 50);
                var segments = repo.GetSegments(documentId!)
                    .Where(segment => segment.Ordinal >= start)
                    .Take(count)
                    .ToList();
                if (segments.Count == 0) return Task.FromResult($"[library_open] no segments for '{documentId}' in requested range");

                var sb = new StringBuilder();
                sb.AppendLine($"[library_open] {document.DisplayName} ({document.DocumentId})");
                foreach (var segment in segments)
                {
                    sb.AppendLine(FormatSegment(segment));
                }
                return Task.FromResult(sb.ToString().TrimEnd());
            }
        });

        registry.Register(new ToolDefinition
        {
            Name = "library_graph",
            Description = "Inspect provisional Context Fabric claims, entities, and relations. Read-only.",
            Parameters = new()
            {
                ["corpus_id"] = new("string", "Corpus id to inspect."),
                ["query"] = new("string", "Optional claim-text search query."),
                ["entity_id"] = new("string", "Optional entity id filter for relations."),
                ["limit"] = new("number", "Maximum rows to return. Default 10."),
            },
            Required = ["corpus_id"],
            RequiresApproval = false,
            Handler = (args, ct) =>
            {
                var corpusId = GetString(args, "corpus_id");
                if (string.IsNullOrWhiteSpace(corpusId))
                    return Task.FromResult("[ERROR] corpus_id is required.");
                if (!File.Exists(dbPath))
                    return Task.FromResult("[library_graph] Context Fabric database not available.");

                using var store = new SqliteStore(dbRoot);
                store.Initialize();
                var graph = new DocumentGraphRepository(store);
                var limit = Math.Clamp(GetInt(args, "limit") ?? 10, 1, 50);
                var query = GetString(args, "query");
                var sb = new StringBuilder();
                sb.AppendLine($"[library_graph] corpus={corpusId}");

                if (!string.IsNullOrWhiteSpace(query))
                {
                    var claims = graph.SearchClaims(query, corpusId, limit);
                    if (claims.Count == 0) return Task.FromResult(sb.AppendLine("(no claim matches)").ToString().TrimEnd());
                    foreach (var claim in claims)
                    {
                        sb.AppendLine($"claim {claim.ClaimId} [{claim.VerificationStatus}] {claim.ClaimType} {claim.DisplayName} seg={claim.SegmentId}");
                        sb.AppendLine($"  {Trim(claim.ClaimText)}");
                    }
                    return Task.FromResult(sb.ToString().TrimEnd());
                }

                var entities = graph.ListEntities(corpusId, limit);
                var relations = graph.ListRelations(corpusId, GetString(args, "entity_id"), limit);
                sb.AppendLine($"entities={entities.Count} relations={relations.Count}");
                foreach (var entity in entities)
                    sb.AppendLine($"entity {entity.EntityId} [{entity.VerificationStatus}] {entity.CanonicalName} ({entity.EntityType ?? "-"})");
                foreach (var relation in relations)
                    sb.AppendLine($"relation {relation.RelationId} [{relation.VerificationStatus}] {relation.SourceEntityId} -{relation.RelationType}-> {relation.TargetEntityId} evidence={relation.EvidenceCount}");
                return Task.FromResult(sb.ToString().TrimEnd());
            }
        });
    }

    private static string FormatSegment(FabricSegmentEntry segment)
    {
        var heading = string.IsNullOrWhiteSpace(segment.HeadingPath) ? "-" : segment.HeadingPath;
        return $"seg={segment.SegmentId} ordinal={segment.Ordinal} heading={heading} range={segment.CharStart}-{segment.CharEnd}\n{segment.Text}";
    }

    private static string Trim(string text)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 180 ? normalized : normalized[..177] + "...";
    }

    private static string? GetString(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value is not null
            ? value.ToString()
            : null;

    private static int? GetInt(Dictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value is not null && int.TryParse(value.ToString(), out var parsed)
            ? parsed
            : null;
}
