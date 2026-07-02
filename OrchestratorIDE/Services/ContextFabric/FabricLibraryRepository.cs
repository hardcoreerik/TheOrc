// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricLibraryRepository(SqliteStore store) : RepositoryBase(store)
{
    private static readonly Regex SearchTerms = new(
        @"[\p{L}\p{N}_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public FabricCorpusEntry CreateCorpus(
        string corpusId,
        string name,
        string? description = null,
        string policyProfile = "default")
    {
        if (string.IsNullOrWhiteSpace(corpusId)) throw new ArgumentException("Corpus ID is required.", nameof(corpusId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Corpus name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(policyProfile)) throw new ArgumentException("Policy profile is required.", nameof(policyProfile));

        var now = DateTimeOffset.UtcNow;
        Execute(
            """
            INSERT INTO fabric_corpora
                (corpus_id, name, description, policy_profile, status, created_at, updated_at)
            VALUES ($id, $name, $description, $policy, 'ready', $created, $updated)
            """,
            ps =>
            {
                P(ps, "$id", corpusId);
                P(ps, "$name", name.Trim());
                P(ps, "$description", string.IsNullOrWhiteSpace(description) ? null : description.Trim());
                P(ps, "$policy", policyProfile.Trim());
                P(ps, "$created", now.ToString("O"));
                P(ps, "$updated", now.ToString("O"));
            });
        return GetCorpus(corpusId) ?? throw new InvalidOperationException("Corpus was not created.");
    }

    public FabricCorpusEntry? GetCorpus(string corpusId) => Query(
        "SELECT * FROM fabric_corpora WHERE corpus_id = $id",
        MapCorpus,
        ps => P(ps, "$id", corpusId)).SingleOrDefault();

    public IReadOnlyList<FabricCorpusEntry> ListCorpora() => Query(
        "SELECT * FROM fabric_corpora ORDER BY name, corpus_id",
        MapCorpus);

    public FabricDocumentEntry? GetDocument(string documentId) => Query(
        "SELECT * FROM fabric_documents WHERE document_id = $id",
        MapDocument,
        ps => P(ps, "$id", documentId)).SingleOrDefault();

    public IReadOnlyList<FabricDocumentEntry> ListDocuments(string corpusId) => Query(
        "SELECT * FROM fabric_documents WHERE corpus_id = $corpus ORDER BY display_name, document_id",
        MapDocument,
        ps => P(ps, "$corpus", corpusId));

    public IReadOnlyList<FabricSegmentEntry> GetSegments(string documentId) => Query(
        """
        SELECT s.*, t.normalized_text
        FROM fabric_segments s
        JOIN fabric_segment_text t ON t.segment_id = s.segment_id
        WHERE s.document_id = $document
        ORDER BY s.ordinal
        """,
        MapSegment,
        ps => P(ps, "$document", documentId));

    public FabricSegmentEntry? GetSegment(string segmentId) => Query(
        """
        SELECT s.*, t.normalized_text
        FROM fabric_segments s
        JOIN fabric_segment_text t ON t.segment_id = s.segment_id
        WHERE s.segment_id = $segment
        """,
        MapSegment,
        ps => P(ps, "$segment", segmentId)).SingleOrDefault();

    public IReadOnlyList<FabricSegmentEntry> GetSegmentsByIds(IEnumerable<string> segmentIds)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);

        var segments = new List<FabricSegmentEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segmentId in segmentIds)
        {
            if (string.IsNullOrWhiteSpace(segmentId) || !seen.Add(segmentId))
                continue;

            var segment = GetSegment(segmentId);
            if (segment is not null)
                segments.Add(segment);
        }

        return segments;
    }

    public void ReplaceDocument(FabricDocumentEntry document, IReadOnlyList<FabricSegmentDraft> segments)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0) throw new InvalidDataException("A document must contain at least one segment.");

        InTransaction((conn, tx) =>
        {
            var owningCorpusId = document.CorpusId;
            var isExistingDocument = false;
            using (var identity = CreateCmd(conn, tx, """
                SELECT corpus_id, source_digest, media_type, parser_id, parser_version
                FROM fabric_documents
                WHERE document_id = $id
                """))
            {
                P(identity.Parameters, "$id", document.DocumentId);
                using var reader = identity.ExecuteReader();
                if (reader.Read())
                {
                    isExistingDocument = true;
                    owningCorpusId = reader.GetString(0);
                    if (!owningCorpusId.Equals(document.CorpusId, StringComparison.Ordinal) ||
                     !reader.GetString(1).Equals(document.SourceDigest, StringComparison.Ordinal) ||
                     !reader.GetString(2).Equals(document.MediaType, StringComparison.Ordinal) ||
                     !reader.GetString(3).Equals(document.ParserId, StringComparison.Ordinal) ||
                     !reader.GetString(4).Equals(document.ParserVersion, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Document identity fields cannot change during replacement.");
                    }
                }
            }

            var effectiveDocument = document;
            if (!isExistingDocument)
            {
                ExecuteOn(tx,
                    "UPDATE fabric_corpora SET updated_at = updated_at WHERE corpus_id = $corpus",
                    ps => P(ps, "$corpus", document.CorpusId));

                using var ordinal = CreateCmd(conn, tx, """
                    SELECT COALESCE(MAX(version_ordinal), 0) + 1 AS next_version
                    FROM fabric_documents
                    WHERE corpus_id = $corpus
                      AND display_name = $name
                      AND media_type = $media
                      AND parser_id = $parser
                      AND parser_version = $version
                    """);
                P(ordinal.Parameters, "$corpus", document.CorpusId);
                P(ordinal.Parameters, "$name", document.DisplayName);
                P(ordinal.Parameters, "$media", document.MediaType);
                P(ordinal.Parameters, "$parser", document.ParserId);
                P(ordinal.Parameters, "$version", document.ParserVersion);
                effectiveDocument = document with { VersionOrdinal = Convert.ToInt32(ordinal.ExecuteScalar()) };
            }

            using (var cmd = CreateCmd(conn, tx, """
                INSERT INTO fabric_documents
                    (document_id, corpus_id, source_digest, normalized_digest, display_name,
                     media_type, parser_id, parser_version, status, warnings_json, created_at, updated_at,
                     version_ordinal, superseded_by_document_id, superseded_at)
                VALUES
                    ($id, $corpus, $source, $normalized, $name,
                     $media, $parser, $version, $status, $warnings, $created, $updated,
                     $versionOrdinal, $supersededBy, $supersededAt)
                ON CONFLICT(document_id) DO UPDATE SET
                    normalized_digest = excluded.normalized_digest,
                    display_name = excluded.display_name,
                    status = excluded.status,
                    warnings_json = excluded.warnings_json,
                    updated_at = excluded.updated_at
            """))
            {
                BindDocument(cmd.Parameters, effectiveDocument);
                cmd.ExecuteNonQuery();
            }

            if (!isExistingDocument)
            {
                ExecuteOn(tx,
                    """
                    UPDATE fabric_documents
                    SET status = 'superseded',
                        superseded_by_document_id = $newDocument,
                        superseded_at = $updated,
                        updated_at = $updated
                    WHERE corpus_id = $corpus
                      AND display_name = $name
                      AND media_type = $media
                      AND parser_id = $parser
                      AND parser_version = $version
                      AND document_id <> $newDocument
                      AND superseded_by_document_id IS NULL
                      AND status <> 'superseded'
                    """,
                    ps =>
                    {
                        P(ps, "$newDocument", effectiveDocument.DocumentId);
                        P(ps, "$updated", effectiveDocument.UpdatedAt.ToString("O"));
                        P(ps, "$corpus", effectiveDocument.CorpusId);
                        P(ps, "$name", effectiveDocument.DisplayName);
                        P(ps, "$media", effectiveDocument.MediaType);
                        P(ps, "$parser", effectiveDocument.ParserId);
                        P(ps, "$version", effectiveDocument.ParserVersion);
                    });
            }

            ExecuteOn(tx, "DELETE FROM fabric_segments WHERE document_id = $document",
                ps => P(ps, "$document", effectiveDocument.DocumentId));

            foreach (var segment in segments.OrderBy(item => item.Ordinal))
            {
                using (var cmd = CreateCmd(conn, tx, """
                    INSERT INTO fabric_segments
                        (segment_id, document_id, ordinal, heading_path, char_start, char_end,
                         token_count, text_digest, previous_segment_id, next_segment_id, chunker_version,
                         block_kind, page_number, source_locator, confidence)
                    VALUES
                        ($id, $document, $ordinal, $heading, $start, $end,
                         $tokens, $digest, $previous, $next, $version,
                         $blockKind, $pageNumber, $sourceLocator, $confidence)
                    """))
                {
                    P(cmd.Parameters, "$id", segment.SegmentId);
                    P(cmd.Parameters, "$document", effectiveDocument.DocumentId);
                    P(cmd.Parameters, "$ordinal", segment.Ordinal);
                    P(cmd.Parameters, "$heading", segment.HeadingPath);
                    P(cmd.Parameters, "$start", segment.CharStart);
                    P(cmd.Parameters, "$end", segment.CharEnd);
                    P(cmd.Parameters, "$tokens", segment.TokenCount);
                    P(cmd.Parameters, "$digest", segment.TextDigest);
                    P(cmd.Parameters, "$previous", segment.PreviousSegmentId);
                    P(cmd.Parameters, "$next", segment.NextSegmentId);
                    P(cmd.Parameters, "$version", segment.ChunkerVersion);
                    P(cmd.Parameters, "$blockKind", string.IsNullOrWhiteSpace(segment.BlockKind) ? "text" : segment.BlockKind);
                    P(cmd.Parameters, "$pageNumber", segment.PageNumber);
                    P(cmd.Parameters, "$sourceLocator", segment.SourceLocator);
                    P(cmd.Parameters, "$confidence", segment.Confidence);
                    cmd.ExecuteNonQuery();
                }

                using var textCmd = CreateCmd(conn, tx, """
                    INSERT INTO fabric_segment_text(segment_id, heading_path, normalized_text)
                    VALUES ($id, $heading, $text)
                    """);
                P(textCmd.Parameters, "$id", segment.SegmentId);
                P(textCmd.Parameters, "$heading", segment.HeadingPath);
                P(textCmd.Parameters, "$text", segment.Text);
                textCmd.ExecuteNonQuery();
            }

            ExecuteOn(tx,
                "UPDATE fabric_corpora SET updated_at = $updated WHERE corpus_id = $corpus",
                ps =>
                {
                    P(ps, "$updated", document.UpdatedAt.ToString("O"));
                    P(ps, "$corpus", owningCorpusId);
                });
        });
    }

    public IReadOnlyList<FabricSearchHit> Search(string query, string? corpusId = null, int limit = 50)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (ftsQuery.Length == 0) return [];
        limit = Math.Clamp(limit, 1, 500);
        return Query(
            """
            SELECT d.corpus_id, d.document_id, d.display_name,
                   s.segment_id, s.ordinal, s.heading_path, t.normalized_text,
                   s.block_kind, s.page_number, s.source_locator, s.confidence,
                   bm25(fabric_segment_fts) AS rank
            FROM fabric_segment_fts
            JOIN fabric_segment_text t ON t.rowid = fabric_segment_fts.rowid
            JOIN fabric_segments s ON s.segment_id = t.segment_id
            JOIN fabric_documents d ON d.document_id = s.document_id
            WHERE fabric_segment_fts MATCH $query
              AND ($corpus IS NULL OR d.corpus_id = $corpus)
              AND d.status <> 'superseded'
            ORDER BY rank, d.document_id, s.ordinal
            LIMIT $limit
            """,
            reader => new FabricSearchHit(
                reader.GetString(reader.GetOrdinal("corpus_id")),
                reader.GetString(reader.GetOrdinal("document_id")),
                reader.GetString(reader.GetOrdinal("display_name")),
                reader.GetString(reader.GetOrdinal("segment_id")),
                reader.GetInt32(reader.GetOrdinal("ordinal")),
                GetStr(reader, "heading_path"),
                reader.GetString(reader.GetOrdinal("normalized_text")),
                reader.GetDouble(reader.GetOrdinal("rank")),
                reader.GetString(reader.GetOrdinal("block_kind")),
                GetInt(reader, "page_number"),
                GetStr(reader, "source_locator"),
                GetReal(reader, "confidence")),
            ps =>
            {
                P(ps, "$query", ftsQuery);
                P(ps, "$corpus", corpusId);
                P(ps, "$limit", limit);
            });
    }

    public bool DeleteCorpus(string corpusId) => Execute(
        "DELETE FROM fabric_corpora WHERE corpus_id = $id",
        ps => P(ps, "$id", corpusId)) > 0;

    public IReadOnlySet<string> ListReferencedArtifactDigests()
    {
        var digests = Query(
            """
            SELECT source_digest AS digest FROM fabric_documents
            UNION
            SELECT normalized_digest AS digest FROM fabric_documents
            """,
            reader => reader.GetString(reader.GetOrdinal("digest")));
        return new HashSet<string>(digests, StringComparer.Ordinal);
    }

    private static string BuildFtsQuery(string query) => string.Join(" AND ", SearchTerms
        .Matches(query ?? "")
        .Select(match => $"\"{match.Value.Replace("\"", "\"\"")}\""));

    private static FabricCorpusEntry MapCorpus(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("corpus_id")),
        reader.GetString(reader.GetOrdinal("name")),
        GetStr(reader, "description"),
        reader.GetString(reader.GetOrdinal("policy_profile")),
        reader.GetString(reader.GetOrdinal("status")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))));

    private static FabricDocumentEntry MapDocument(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("document_id")),
        reader.GetString(reader.GetOrdinal("corpus_id")),
        reader.GetString(reader.GetOrdinal("source_digest")),
        reader.GetString(reader.GetOrdinal("normalized_digest")),
        reader.GetString(reader.GetOrdinal("display_name")),
        reader.GetString(reader.GetOrdinal("media_type")),
        reader.GetString(reader.GetOrdinal("parser_id")),
        reader.GetString(reader.GetOrdinal("parser_version")),
        reader.GetString(reader.GetOrdinal("status")),
        JsonSerializer.Deserialize<string[]>(reader.GetString(reader.GetOrdinal("warnings_json")), FabricJson.Options) ?? [],
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        reader.GetInt32(reader.GetOrdinal("version_ordinal")),
        GetStr(reader, "superseded_by_document_id"),
        GetStr(reader, "superseded_at") is { } supersededAt ? DateTimeOffset.Parse(supersededAt) : null);

    private static FabricSegmentEntry MapSegment(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("segment_id")),
        reader.GetString(reader.GetOrdinal("document_id")),
        reader.GetInt32(reader.GetOrdinal("ordinal")),
        GetStr(reader, "heading_path"),
        reader.GetInt32(reader.GetOrdinal("char_start")),
        reader.GetInt32(reader.GetOrdinal("char_end")),
        reader.GetInt32(reader.GetOrdinal("token_count")),
        reader.GetString(reader.GetOrdinal("text_digest")),
        reader.GetString(reader.GetOrdinal("normalized_text")),
        GetStr(reader, "previous_segment_id"),
        GetStr(reader, "next_segment_id"),
        reader.GetString(reader.GetOrdinal("chunker_version")),
        reader.GetString(reader.GetOrdinal("block_kind")),
        GetInt(reader, "page_number"),
        GetStr(reader, "source_locator"),
        GetReal(reader, "confidence"));

    private static void BindDocument(SqliteParameterCollection parameters, FabricDocumentEntry document)
    {
        P(parameters, "$id", document.DocumentId);
        P(parameters, "$corpus", document.CorpusId);
        P(parameters, "$source", document.SourceDigest);
        P(parameters, "$normalized", document.NormalizedDigest);
        P(parameters, "$name", document.DisplayName);
        P(parameters, "$media", document.MediaType);
        P(parameters, "$parser", document.ParserId);
        P(parameters, "$version", document.ParserVersion);
        P(parameters, "$status", document.Status);
        P(parameters, "$warnings", JsonSerializer.Serialize(document.Warnings, FabricJson.Options));
        P(parameters, "$created", document.CreatedAt.ToString("O"));
        P(parameters, "$updated", document.UpdatedAt.ToString("O"));
        P(parameters, "$versionOrdinal", document.VersionOrdinal);
        P(parameters, "$supersededBy", document.SupersededByDocumentId);
        P(parameters, "$supersededAt", document.SupersededAt?.ToString("O"));
    }
}
