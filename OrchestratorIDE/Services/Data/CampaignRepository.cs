// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.Services.Data;

public sealed record CampaignRow(
    string CampaignId, string Name, string PackId, string PackVersion,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CampaignWorkUnitRow(
    string CampaignId, string WorkUnitId, string? TaskId, string Title,
    string ExecutionKind, string Status, int Attempt, string? ClaimedByNode,
    string? ErrorMsg);

public sealed class CampaignRepository(SqliteStore store, TimeSpan? retention = null) : RepositoryBase(store)
{
    private readonly TimeSpan _retention = retention ?? TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Create(CampaignDefinition campaign)
    {
        var now = DateTimeOffset.UtcNow;
        InTransaction((conn, tx) =>
        {
            using (var cmd = CreateCmd(conn, tx, """
                INSERT INTO campaigns
                    (campaign_id,name,pack_id,pack_version,status,definition_json,created_at,updated_at,retain_until)
                VALUES ($id,$name,$pack,$version,$status,$json,$created,$updated,$retain)
                """))
            {
                P(cmd.Parameters, "$id", campaign.CampaignId);
                P(cmd.Parameters, "$name", campaign.Name);
                P(cmd.Parameters, "$pack", campaign.PackId);
                P(cmd.Parameters, "$version", campaign.PackVersion);
                P(cmd.Parameters, "$status", campaign.Status);
                P(cmd.Parameters, "$json", JsonSerializer.Serialize(campaign, Json));
                P(cmd.Parameters, "$created", campaign.CreatedAt.ToString("o"));
                P(cmd.Parameters, "$updated", now.ToString("o"));
                P(cmd.Parameters, "$retain", now.Add(_retention).ToString("o"));
                cmd.ExecuteNonQuery();
            }

            foreach (var unit in campaign.WorkUnits)
            {
                using var cmd = CreateCmd(conn, tx, """
                    INSERT INTO campaign_work_units
                        (campaign_id,work_unit_id,title,execution_kind,status,attempt,updated_at)
                    VALUES ($campaign,$unit,$title,$kind,'pending',1,$updated)
                    """);
                P(cmd.Parameters, "$campaign", campaign.CampaignId);
                P(cmd.Parameters, "$unit", unit.WorkUnitId);
                P(cmd.Parameters, "$title", unit.Title);
                P(cmd.Parameters, "$kind", unit.ExecutionKind);
                P(cmd.Parameters, "$updated", now.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        });
    }

    public void SetStatus(string campaignId, string status) => Execute(
        "UPDATE campaigns SET status=$status,updated_at=$updated WHERE campaign_id=$id",
        ps => { P(ps, "$status", status); P(ps, "$updated", DateTimeOffset.UtcNow.ToString("o")); P(ps, "$id", campaignId); });

    public void BindTask(string campaignId, string workUnitId, string taskId) => Execute(
        "UPDATE campaign_work_units SET task_id=$task,updated_at=$updated WHERE campaign_id=$campaign AND work_unit_id=$unit",
        ps => { P(ps, "$task", taskId); P(ps, "$updated", DateTimeOffset.UtcNow.ToString("o")); P(ps, "$campaign", campaignId); P(ps, "$unit", workUnitId); });

    public void UpdateWorkUnit(string campaignId, string workUnitId, string status,
        int attempt, string? claimedByNode = null, string? resultJson = null, string? error = null) => Execute(
        """
        UPDATE campaign_work_units SET status=$status,attempt=$attempt,
            claimed_by_node=COALESCE($node,claimed_by_node),result_json=COALESCE($result,result_json),
            error_msg=$error,updated_at=$updated
        WHERE campaign_id=$campaign AND work_unit_id=$unit
        """,
        ps => { P(ps, "$status", status); P(ps, "$attempt", attempt); P(ps, "$node", claimedByNode); P(ps, "$result", resultJson); P(ps, "$error", error); P(ps, "$updated", DateTimeOffset.UtcNow.ToString("o")); P(ps, "$campaign", campaignId); P(ps, "$unit", workUnitId); });

    public void AddArtifact(string campaignId, string? workUnitId, ArtifactRef artifact,
        string storagePath, bool verified) => Execute(
        """
        INSERT INTO campaign_artifacts
            (campaign_id,work_unit_id,digest_sha256,name,size_bytes,media_type,kind,storage_path,verified,created_at)
        VALUES ($campaign,$unit,$digest,$name,$size,$media,$kind,$path,$verified,$created)
        ON CONFLICT(campaign_id,digest_sha256) DO UPDATE SET
            storage_path=excluded.storage_path,verified=excluded.verified
        """,
        ps => { P(ps, "$campaign", campaignId); P(ps, "$unit", workUnitId); P(ps, "$digest", artifact.DigestSha256); P(ps, "$name", artifact.Name); P(ps, "$size", artifact.SizeBytes); P(ps, "$media", artifact.MediaType); P(ps, "$kind", artifact.Kind); P(ps, "$path", storagePath); P(ps, "$verified", verified ? 1 : 0); P(ps, "$created", DateTimeOffset.UtcNow.ToString("o")); });

    public CampaignRow? Get(string campaignId) => Query(
        "SELECT campaign_id,name,pack_id,pack_version,status,created_at,updated_at FROM campaigns WHERE campaign_id=$id",
        MapCampaign, ps => P(ps, "$id", campaignId)).SingleOrDefault();

    public List<CampaignRow> Recent(int limit = 100) => Query(
        "SELECT campaign_id,name,pack_id,pack_version,status,created_at,updated_at FROM campaigns ORDER BY updated_at DESC LIMIT $limit",
        MapCampaign, ps => P(ps, "$limit", Math.Clamp(limit, 1, 500)));

    public List<CampaignWorkUnitRow> WorkUnits(string campaignId) => Query(
        "SELECT campaign_id,work_unit_id,task_id,title,execution_kind,status,attempt,claimed_by_node,error_msg FROM campaign_work_units WHERE campaign_id=$id ORDER BY work_unit_id",
        r => new CampaignWorkUnitRow(GetStr(r,"campaign_id")!, GetStr(r,"work_unit_id")!, GetStr(r,"task_id"), GetStr(r,"title") ?? "", GetStr(r,"execution_kind")!, GetStr(r,"status")!, GetInt(r,"attempt") ?? 1, GetStr(r,"claimed_by_node"), GetStr(r,"error_msg")),
        ps => P(ps, "$id", campaignId));

    public int SweepExpired() => Execute("DELETE FROM campaigns WHERE retain_until < $now",
        ps => P(ps, "$now", DateTimeOffset.UtcNow.ToString("o")));

    private static CampaignRow MapCampaign(SqliteDataReader r) => new(
        GetStr(r,"campaign_id")!, GetStr(r,"name")!, GetStr(r,"pack_id")!, GetStr(r,"pack_version")!, GetStr(r,"status")!,
        DateTimeOffset.Parse(GetStr(r,"created_at")!), DateTimeOffset.Parse(GetStr(r,"updated_at")!));
}
