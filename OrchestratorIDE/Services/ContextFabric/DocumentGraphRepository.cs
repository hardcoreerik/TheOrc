// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class DocumentGraphRepository(SqliteStore store) : RepositoryBase(store)
{
    public void UpsertClaim(FabricClaimEntry claim, IReadOnlyList<FabricClaimCitationEntry> citations)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(citations);

        InTransaction((conn, tx) =>
        {
            UpsertClaimOn(conn, tx, claim);
            ExecuteOn(tx, "DELETE FROM fabric_claim_citations WHERE claim_id = $id",
                ps => P(ps, "$id", claim.ClaimId));

            foreach (var citation in citations.OrderBy(item => item.Ordinal))
                InsertCitationOn(conn, tx, claim.ClaimId, citation);
        });
    }

    public void ReplaceClaimsForDocument(
        string documentId,
        IReadOnlyList<FabricClaimEntry> claims,
        IReadOnlyDictionary<string, IReadOnlyList<FabricClaimCitationEntry>> citationsByClaimId)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("Document id is required.", nameof(documentId));
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(citationsByClaimId);
        if (claims.Any(claim => !string.Equals(claim.DocumentId, documentId, StringComparison.Ordinal)))
            throw new InvalidDataException($"Replacement claims must all belong to document '{documentId}'.");

        InTransaction((conn, tx) =>
        {
            ExecuteOn(tx, """
                DELETE FROM fabric_claim_citations
                WHERE claim_id IN (
                    SELECT claim_id
                    FROM fabric_claims
                    WHERE document_id = $document
                )
                """,
                ps => P(ps, "$document", documentId));
            ExecuteOn(tx, "DELETE FROM fabric_claims WHERE document_id = $document",
                ps => P(ps, "$document", documentId));

            foreach (var claim in claims)
            {
                UpsertClaimOn(conn, tx, claim);
                if (!citationsByClaimId.TryGetValue(claim.ClaimId, out var citations))
                    continue;

                foreach (var citation in citations.OrderBy(item => item.Ordinal))
                    InsertCitationOn(conn, tx, claim.ClaimId, citation);
            }
        });
    }

    public IReadOnlyList<FabricClaimEntry> ListClaims(string corpusId, string? verificationStatus = null, int limit = 200) => Query(
        """
        SELECT *
        FROM fabric_claims
        WHERE corpus_id = $corpus
          AND ($status IS NULL OR verification_status = $status)
        ORDER BY updated_at DESC, claim_id
        LIMIT $limit
        """,
        MapClaim,
        ps =>
        {
            P(ps, "$corpus", corpusId);
            P(ps, "$status", verificationStatus);
            P(ps, "$limit", Math.Clamp(limit, 1, 500));
        });

    public IReadOnlyList<FabricClaimCitationEntry> ListClaimCitations(string claimId) => Query(
        """
        SELECT *
        FROM fabric_claim_citations
        WHERE claim_id = $claim
        ORDER BY ordinal
        """,
        reader => new FabricClaimCitationEntry(
            reader.GetString(reader.GetOrdinal("claim_id")),
            reader.GetInt32(reader.GetOrdinal("ordinal")),
            reader.GetString(reader.GetOrdinal("segment_id")),
            reader.GetInt32(reader.GetOrdinal("char_start")),
            reader.GetInt32(reader.GetOrdinal("char_end")),
            reader.GetString(reader.GetOrdinal("quote_digest")),
            reader.GetString(reader.GetOrdinal("quote_text"))),
        ps => P(ps, "$claim", claimId));

    public IReadOnlyList<FabricClaimSearchHit> SearchClaims(string query, string? corpusId = null, int limit = 50)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (ftsQuery.Length == 0) return [];
        return Query(
            """
            SELECT c.claim_id, c.corpus_id, c.document_id, c.segment_id, d.display_name,
                   c.claim_type, c.claim_text, c.verification_status, c.confidence,
                   bm25(fabric_claim_fts) AS rank
            FROM fabric_claim_fts
            JOIN fabric_claims c ON c.rowid = fabric_claim_fts.rowid
            JOIN fabric_documents d ON d.document_id = c.document_id
            WHERE fabric_claim_fts MATCH $query
              AND ($corpus IS NULL OR c.corpus_id = $corpus)
            ORDER BY rank, c.claim_id
            LIMIT $limit
            """,
            reader => new FabricClaimSearchHit(
                reader.GetString(reader.GetOrdinal("claim_id")),
                reader.GetString(reader.GetOrdinal("corpus_id")),
                reader.GetString(reader.GetOrdinal("document_id")),
                reader.GetString(reader.GetOrdinal("segment_id")),
                reader.GetString(reader.GetOrdinal("display_name")),
                reader.GetString(reader.GetOrdinal("claim_type")),
                reader.GetString(reader.GetOrdinal("claim_text")),
                reader.GetString(reader.GetOrdinal("verification_status")),
                GetReal(reader, "confidence"),
                reader.GetDouble(reader.GetOrdinal("rank"))),
            ps =>
            {
                P(ps, "$query", ftsQuery);
                P(ps, "$corpus", corpusId);
                P(ps, "$limit", Math.Clamp(limit, 1, 200));
            });
    }

    public void UpsertEntity(FabricEntityEntry entity)
    {
        Execute("""
            INSERT INTO fabric_entities
                (entity_id, corpus_id, canonical_name, entity_type, verification_status,
                 confidence, created_at, updated_at)
            VALUES
                ($id, $corpus, $name, $type, $status, $confidence, $created, $updated)
            ON CONFLICT(entity_id) DO UPDATE SET
                corpus_id = excluded.corpus_id,
                canonical_name = excluded.canonical_name,
                entity_type = excluded.entity_type,
                verification_status = excluded.verification_status,
                confidence = excluded.confidence,
                updated_at = excluded.updated_at
            """,
            ps =>
            {
                P(ps, "$id", entity.EntityId);
                P(ps, "$corpus", entity.CorpusId);
                P(ps, "$name", entity.CanonicalName);
                P(ps, "$type", entity.EntityType);
                P(ps, "$status", entity.VerificationStatus);
                P(ps, "$confidence", entity.Confidence);
                P(ps, "$created", entity.CreatedAt.ToString("O"));
                P(ps, "$updated", entity.UpdatedAt.ToString("O"));
            });
    }

    public IReadOnlyList<FabricEntityEntry> ListEntities(string corpusId, int limit = 200) => Query(
        """
        SELECT *
        FROM fabric_entities
        WHERE corpus_id = $corpus
        ORDER BY canonical_name, entity_id
        LIMIT $limit
        """,
        reader => new FabricEntityEntry(
            reader.GetString(reader.GetOrdinal("entity_id")),
            reader.GetString(reader.GetOrdinal("corpus_id")),
            reader.GetString(reader.GetOrdinal("canonical_name")),
            GetStr(reader, "entity_type"),
            reader.GetString(reader.GetOrdinal("verification_status")),
            GetReal(reader, "confidence"),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")))),
        ps =>
        {
            P(ps, "$corpus", corpusId);
            P(ps, "$limit", Math.Clamp(limit, 1, 500));
        });

    public void UpsertRelation(FabricRelationEntry relation)
    {
        Execute("""
            INSERT INTO fabric_relations
                (relation_id, corpus_id, source_entity_id, target_entity_id, relation_type,
                 verification_status, confidence, evidence_count, created_at, updated_at)
            VALUES
                ($id, $corpus, $source, $target, $type, $status, $confidence, $count, $created, $updated)
            ON CONFLICT(relation_id) DO UPDATE SET
                corpus_id = excluded.corpus_id,
                source_entity_id = excluded.source_entity_id,
                target_entity_id = excluded.target_entity_id,
                relation_type = excluded.relation_type,
                verification_status = excluded.verification_status,
                confidence = excluded.confidence,
                evidence_count = excluded.evidence_count,
                updated_at = excluded.updated_at
            """,
            ps =>
            {
                P(ps, "$id", relation.RelationId);
                P(ps, "$corpus", relation.CorpusId);
                P(ps, "$source", relation.SourceEntityId);
                P(ps, "$target", relation.TargetEntityId);
                P(ps, "$type", relation.RelationType);
                P(ps, "$status", relation.VerificationStatus);
                P(ps, "$confidence", relation.Confidence);
                P(ps, "$count", relation.EvidenceCount);
                P(ps, "$created", relation.CreatedAt.ToString("O"));
                P(ps, "$updated", relation.UpdatedAt.ToString("O"));
            });
    }

    public IReadOnlyList<FabricRelationEntry> ListRelations(string corpusId, string? entityId = null, int limit = 200) => Query(
        """
        SELECT *
        FROM fabric_relations
        WHERE corpus_id = $corpus
          AND ($entity IS NULL OR source_entity_id = $entity OR target_entity_id = $entity)
        ORDER BY relation_type, relation_id
        LIMIT $limit
        """,
        reader => new FabricRelationEntry(
            reader.GetString(reader.GetOrdinal("relation_id")),
            reader.GetString(reader.GetOrdinal("corpus_id")),
            reader.GetString(reader.GetOrdinal("source_entity_id")),
            reader.GetString(reader.GetOrdinal("target_entity_id")),
            reader.GetString(reader.GetOrdinal("relation_type")),
            reader.GetString(reader.GetOrdinal("verification_status")),
            GetReal(reader, "confidence"),
            reader.GetInt32(reader.GetOrdinal("evidence_count")),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")))),
        ps =>
        {
            P(ps, "$corpus", corpusId);
            P(ps, "$entity", entityId);
            P(ps, "$limit", Math.Clamp(limit, 1, 500));
        });

    private static FabricClaimEntry MapClaim(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("claim_id")),
        reader.GetString(reader.GetOrdinal("corpus_id")),
        reader.GetString(reader.GetOrdinal("document_id")),
        reader.GetString(reader.GetOrdinal("segment_id")),
        reader.GetString(reader.GetOrdinal("claim_type")),
        reader.GetString(reader.GetOrdinal("claim_text")),
        reader.GetString(reader.GetOrdinal("verification_status")),
        GetReal(reader, "confidence"),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))));

    private static string BuildFtsQuery(string query) => string.Join(" AND ", query
        .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(term => $"\"{term.Replace("\"", "\"\"")}\""));

    private static void UpsertClaimOn(SqliteConnection conn, SqliteTransaction tx, FabricClaimEntry claim)
    {
        using var cmd = CreateCmd(conn, tx, """
            INSERT INTO fabric_claims
                (claim_id, corpus_id, document_id, segment_id, claim_type, claim_text,
                 verification_status, confidence, created_at, updated_at)
            VALUES
                ($id, $corpus, $document, $segment, $type, $text,
                 $status, $confidence, $created, $updated)
            ON CONFLICT(claim_id) DO UPDATE SET
                corpus_id = excluded.corpus_id,
                document_id = excluded.document_id,
                segment_id = excluded.segment_id,
                claim_type = excluded.claim_type,
                claim_text = excluded.claim_text,
                verification_status = excluded.verification_status,
                confidence = excluded.confidence,
                updated_at = excluded.updated_at
            """);
        P(cmd.Parameters, "$id", claim.ClaimId);
        P(cmd.Parameters, "$corpus", claim.CorpusId);
        P(cmd.Parameters, "$document", claim.DocumentId);
        P(cmd.Parameters, "$segment", claim.SegmentId);
        P(cmd.Parameters, "$type", claim.ClaimType);
        P(cmd.Parameters, "$text", claim.ClaimText);
        P(cmd.Parameters, "$status", claim.VerificationStatus);
        P(cmd.Parameters, "$confidence", claim.Confidence);
        P(cmd.Parameters, "$created", claim.CreatedAt.ToString("O"));
        P(cmd.Parameters, "$updated", claim.UpdatedAt.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void InsertCitationOn(
        SqliteConnection conn,
        SqliteTransaction tx,
        string claimId,
        FabricClaimCitationEntry citation)
    {
        using var cmd = CreateCmd(conn, tx, """
            INSERT INTO fabric_claim_citations
                (claim_id, ordinal, segment_id, char_start, char_end, quote_digest, quote_text)
            VALUES
                ($claim, $ordinal, $segment, $start, $end, $digest, $quote)
            """);
        P(cmd.Parameters, "$claim", claimId);
        P(cmd.Parameters, "$ordinal", citation.Ordinal);
        P(cmd.Parameters, "$segment", citation.SegmentId);
        P(cmd.Parameters, "$start", citation.CharStart);
        P(cmd.Parameters, "$end", citation.CharEnd);
        P(cmd.Parameters, "$digest", citation.QuoteDigest);
        P(cmd.Parameters, "$quote", citation.QuoteText);
        cmd.ExecuteNonQuery();
    }
}
