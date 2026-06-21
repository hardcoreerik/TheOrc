// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Lets a node prove hive membership to a peer it has never directly paired with
/// (HIVE_MEMBERSHIP_SPEC.md §5). Issued only by a node holding
/// <see cref="HiveIdentity.CanIssueMembershipCerts"/> (a Controller, or the founder) at
/// pairing-approval time. Deliberately NOT a general-purpose PKI:
///
/// - No delegation chains — only a node the VERIFIER has itself directly paired with as a
///   Controller is trusted as an issuer. A Controller introduced transitively (vouched for by
///   someone else) does not count.
/// - Can only grant Observer or Worker — never Controller. Becoming a Controller remains a
///   deliberate, manual pairing-time grant, never a side effect of presenting a cert.
/// - No active revocation propagation — bounded by a short validity window instead. If the
///   issuer is later revoked, its already-issued certs simply expire rather than being
///   actively invalidated mesh-wide.
///
/// See HIVE_MEMBERSHIP_SPEC.md §5.2 for the full scope-discipline rationale.
/// </summary>
public sealed class HiveMembershipCert
{
    public string       HiveId        { get; set; } = "";
    public string       SubjectNodeId { get; set; } = "";
    public string       SubjectName   { get; set; } = "";
    public HiveNodeRole Role          { get; set; } = HiveNodeRole.Worker;
    public string       IssuerNodeId  { get; set; } = "";
    public DateTime     IssuedAt      { get; set; }
    public DateTime     ExpiresAt     { get; set; }
    /// <summary>Base64 ECDSA-P256 signature over <see cref="BuildSigningInput"/>.</summary>
    public string       Signature     { get; set; } = "";

    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(30);

    /// <summary>
    /// Canonical newline-joined signing input — mirrors the existing pairing-proof
    /// convention in HivePairingClient.PairCoreAsync rather than inventing a new
    /// serialization scheme. ISO8601 round-trip ("O") keeps both sides byte-identical
    /// regardless of culture/locale.
    /// </summary>
    public static string BuildSigningInput(string hiveId, string subjectNodeId, HiveNodeRole role,
        DateTime issuedAt, DateTime expiresAt, string issuerNodeId)
        => hiveId + "\n" + subjectNodeId + "\n" + role + "\n"
         + issuedAt.ToUniversalTime().ToString("O") + "\n"
         + expiresAt.ToUniversalTime().ToString("O") + "\n" + issuerNodeId;

    private string BuildSigningInput() =>
        BuildSigningInput(HiveId, SubjectNodeId, Role, IssuedAt, ExpiresAt, IssuerNodeId);

    /// <summary>
    /// Issues a new certificate signed by <paramref name="issuer"/>. Throws if
    /// <paramref name="issuer"/> is not currently authorized to issue
    /// (<see cref="HiveIdentity.CanIssueMembershipCerts"/>) or if asked to grant Controller —
    /// both are caller bugs, not runtime conditions to recover from, so they throw rather
    /// than silently returning null.
    /// </summary>
    public static HiveMembershipCert Issue(HiveIdentity issuer, string subjectNodeId, string subjectName,
        HiveNodeRole role, TimeSpan? validity = null)
    {
        if (!issuer.CanIssueMembershipCerts)
            throw new InvalidOperationException(
                "This node is not authorized to issue membership certificates -- " +
                "see HIVE_MEMBERSHIP_SPEC.md §5.2/§5.4.");
        if (role == HiveNodeRole.Controller)
            throw new ArgumentException(
                "Membership certs may only grant Observer or Worker, never Controller -- " +
                "see HIVE_MEMBERSHIP_SPEC.md §5.2.", nameof(role));

        var issuedAt  = DateTime.UtcNow;
        var expiresAt = issuedAt + (validity ?? DefaultValidity);
        var input     = BuildSigningInput(issuer.HiveId, subjectNodeId, role, issuedAt, expiresAt, issuer.NodeId);
        var sig       = Convert.ToBase64String(issuer.Sign(Encoding.UTF8.GetBytes(input)));

        return new HiveMembershipCert
        {
            HiveId        = issuer.HiveId,
            SubjectNodeId = subjectNodeId,
            SubjectName   = subjectName,
            Role          = role,
            IssuerNodeId  = issuer.NodeId,
            IssuedAt      = issuedAt,
            ExpiresAt     = expiresAt,
            Signature     = sig,
        };
    }

    /// <summary>
    /// Verifies signature, expiry, and the never-Controller rule. Does NOT verify that the
    /// verifier actually trusts <see cref="IssuerNodeId"/> as a Controller — that is the
    /// caller's responsibility (HivePeerStore.TryAcceptViaMembershipCert), since it depends
    /// on the verifier's own peer store, not anything in the certificate itself.
    /// </summary>
    public static bool Verify(HiveMembershipCert? cert, byte[] issuerSigningPublicKeyDer)
    {
        if (cert is null) return false;
        if (cert.Role == HiveNodeRole.Controller) return false;
        if (cert.ExpiresAt < DateTime.UtcNow) return false;
        if (string.IsNullOrEmpty(cert.HiveId) || string.IsNullOrEmpty(cert.SubjectNodeId)
            || string.IsNullOrEmpty(cert.IssuerNodeId) || string.IsNullOrEmpty(cert.Signature))
            return false;

        try
        {
            var input = cert.BuildSigningInput();
            return HiveIdentity.Verify(issuerSigningPublicKeyDer,
                Encoding.UTF8.GetBytes(input), Convert.FromBase64String(cert.Signature));
        }
        catch { return false; }
    }

    private static readonly JsonSerializerOptions _json = new()
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public string ToBase64Json() => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, _json)));

    /// <summary>Returns null rather than throwing on malformed input — callers treat an
    /// unparseable cert exactly like a missing one (fail closed, not an error to surface).</summary>
    public static HiveMembershipCert? FromBase64Json(string base64Json)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Json));
            return JsonSerializer.Deserialize<HiveMembershipCert>(json, _json);
        }
        catch { return null; }
    }
}
