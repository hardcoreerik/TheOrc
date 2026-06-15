// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services.Hive;

/// <summary>Result of authenticating one HIVE request.</summary>
public sealed record HiveAuthResult(bool Ok, string NodeId, string? Reason = null)
{
    public static HiveAuthResult Authenticated(string nodeId) => new(true, nodeId);
    public static HiveAuthResult Failed(string reason)        => new(false, "", reason);
}

/// <summary>
/// Per-request HMAC-SHA256 authentication for all HIVE endpoints.
///
/// Every inbound request from an enrolled peer must carry four headers:
///   X-Hive-Node-Id  — sender NodeId (hex SHA-256 of its signing public key DER)
///   X-Hive-Nonce    — 16-byte random, lowercase hex (replay prevention)
///   X-Hive-Ts       — Unix milliseconds UTC; must be within ±30s of server clock
///   X-Hive-Sig      — HMAC-SHA256(shared_secret, canonical_msg), lowercase hex
///
/// Canonical message (UTF-8 bytes, newline-delimited):
///   METHOD\nPATH\nNONCE\nTS\nHEX(SHA-256(BODY))
///
/// Input validation caps on all string fields prevent oversized payloads reaching
/// business logic regardless of auth outcome.
///
/// Grace period: when GracePeriodActive=true, unsigned or invalid requests pass as
/// NodeId="anonymous" (no rejection). Set to false for hard enforcement.
/// </summary>
public sealed class HiveAuthMiddleware
{
    // ── Config ────────────────────────────────────────────────────────────────

    /// <summary>When true, missing/invalid auth headers are warned but not rejected.</summary>
    public bool GracePeriodActive { get; set; } = true;

    private readonly HivePeerStore _store;
    public   HiveAuthMiddleware()                     { _store = HivePeerStore.Default; }
    internal HiveAuthMiddleware(HivePeerStore store)  { _store = store; }

    private static readonly TimeSpan TsWindow        = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NonceTtl        = TimeSpan.FromMinutes(5);
    private const           int      MaxNoncesPerPeer = 1_000;

    // Per-peer nonce cache: nodeId → (nonce → expiry)
    private readonly Dictionary<string, Dictionary<string, DateTime>> _nonces = [];
    private readonly Lock    _nonceLock = new();
    private          DateTime _lastPrune = DateTime.UtcNow;

    // ── Inbound validation ────────────────────────────────────────────────────

    public HiveAuthResult Validate(HttpListenerRequest req, byte[] body)
        => ValidateCore(
            req.Headers["X-Hive-Node-Id"],
            req.Headers["X-Hive-Nonce"],
            req.Headers["X-Hive-Ts"],
            req.Headers["X-Hive-Sig"],
            req.HttpMethod,
            req.Url?.AbsolutePath ?? "/",
            body);

    /// <summary>
    /// Core validation logic. Exposed internally so tests can call it without needing
    /// an <see cref="HttpListenerRequest"/> (which is sealed and can't be constructed).
    /// </summary>
    internal HiveAuthResult ValidateCore(
        string? nodeId, string? nonce, string? tsStr, string? sig,
        string method, string path, byte[] body)
    {
        bool headersPresent = !string.IsNullOrEmpty(nodeId)
                           && !string.IsNullOrEmpty(nonce)
                           && !string.IsNullOrEmpty(tsStr)
                           && !string.IsNullOrEmpty(sig);

        if (!headersPresent)
            return GracePeriodActive
                ? HiveAuthResult.Authenticated("anonymous")
                : HiveAuthResult.Failed("missing HIVE auth headers");

        // 1. Enrolled and not revoked
        if (!_store.IsTrusted(nodeId!))
        {
            return GracePeriodActive
                ? HiveAuthResult.Authenticated("anonymous")
                : HiveAuthResult.Failed("unknown or revoked node");
        }

        // 2. Timestamp
        if (!long.TryParse(tsStr, out var tsMs))
            return Reject("invalid timestamp");
        var tsAge = DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime;
        if (tsAge < -TsWindow || tsAge > TsWindow)
            return Reject($"clock skew too large ({tsAge.TotalSeconds:F0}s)");

        // 3. Nonce uniqueness
        if (!RecordNonce(nodeId!, nonce!))
            return Reject("nonce already seen (replay)");

        // 4. HMAC
        var secret = _store.GetSharedSecret(nodeId!);
        if (secret is null) return Reject("no shared secret for peer");

        var canonical = BuildCanonical(method, path, nonce!, tsStr!, body);
        var expected  = ComputeHmac(secret, canonical);

        byte[] received;
        try   { received = Convert.FromHexString(sig!); }
        catch { return Reject("invalid signature encoding"); }

        if (!CryptographicOperations.FixedTimeEquals(expected, received))
            return Reject("HMAC mismatch");

        return HiveAuthResult.Authenticated(nodeId!);

        HiveAuthResult Reject(string reason) =>
            GracePeriodActive ? HiveAuthResult.Authenticated("anonymous")
                              : HiveAuthResult.Failed(reason);
    }

    // ── Outbound signing helper (used by HiveWorkerAgent, HiveMeshHeartbeat) ──

    public static void SignRequest(
        System.Net.Http.HttpRequestMessage req,
        byte[]  body,
        string  senderNodeId,
        byte[]  sharedSecret)
    {
        var nonce  = RandomNumberGenerator.GetHexString(32, lowercase: true);  // 16 bytes → 32 hex chars
        var tsMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var method = req.Method.Method;
        var path   = req.RequestUri?.AbsolutePath ?? "/";
        var canonical = BuildCanonical(method, path, nonce, tsMs, body);
        var sig    = Convert.ToHexString(ComputeHmac(sharedSecret, canonical)).ToLowerInvariant();

        req.Headers.TryAddWithoutValidation("X-Hive-Node-Id", senderNodeId);
        req.Headers.TryAddWithoutValidation("X-Hive-Nonce",   nonce);
        req.Headers.TryAddWithoutValidation("X-Hive-Ts",      tsMs);
        req.Headers.TryAddWithoutValidation("X-Hive-Sig",     sig);
    }

    // ── Input sanitisation ─────────────────────────────────────────────────────

    private static readonly Regex _alphanumDash = new(@"^[a-zA-Z0-9_\-]+$", RegexOptions.Compiled);
    private static readonly Regex _guidPattern   = new(
        @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    public static bool IsValidWorkerId(string? v)
        => v is { Length: > 0 and <= 64 } && _alphanumDash.IsMatch(v);

    public static bool IsValidGuid(string? v)
        => v is { Length: 36 } && _guidPattern.IsMatch(v);

    public static bool IsValidRole(string? v)
        => v is "Researcher" or "Coder" or "UIDeveloper" or "Tester";

    /// <summary>Truncates a free-text field to maxLen chars. Returns null for null input.</summary>
    public static string? Clamp(string? v, int maxLen)
        => v is null ? null : v.Length <= maxLen ? v : v[..maxLen];

    // ── Private ───────────────────────────────────────────────────────────────

    private static string BuildCanonical(string method, string path,
                                          string nonce, string tsMs, byte[] body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        return $"{method.ToUpperInvariant()}\n{path}\n{nonce}\n{tsMs}\n{bodyHash}";
    }

    private static byte[] ComputeHmac(byte[] key, string canonical)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
    }

    private bool RecordNonce(string nodeId, string nonce)
    {
        lock (_nonceLock)
        {
            PruneExpiredNonces();

            if (!_nonces.TryGetValue(nodeId, out var peerNonces))
            {
                peerNonces        = [];
                _nonces[nodeId]   = peerNonces;
            }

            if (peerNonces.ContainsKey(nonce)) return false;

            // Evict oldest entry when over cap
            if (peerNonces.Count >= MaxNoncesPerPeer)
            {
                var oldest = peerNonces.MinBy(kv => kv.Value).Key;
                peerNonces.Remove(oldest);
            }

            peerNonces[nonce] = DateTime.UtcNow.Add(NonceTtl);
            return true;
        }
    }

    private void PruneExpiredNonces()
    {
        if (DateTime.UtcNow - _lastPrune < TimeSpan.FromMinutes(1)) return;
        _lastPrune = DateTime.UtcNow;
        var now    = DateTime.UtcNow;
        foreach (var peerNonces in _nonces.Values)
        {
            var expired = peerNonces.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList();
            foreach (var k in expired) peerNonces.Remove(k);
        }
    }
}
