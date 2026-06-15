// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// When true, missing/invalid auth headers pass as NodeId="anonymous" instead of being rejected.
    /// Defaults to false (fail-closed). Only set true during explicit bootstrapping flows.
    /// </summary>
    public bool GracePeriodActive { get; set; } = false;

    private readonly HivePeerStore _store;

    /// <param name="persistenceKey">
    /// When set, the nonce cache is persisted to hive-nonces-{key}.json and reloaded on
    /// construction. This closes the replay window that otherwise opens across a process
    /// restart (empty in-memory cache → captured request replays within the ±30s window).
    /// Each validating instance must use a distinct key so their caches don't clobber.
    /// Null = in-memory only (tests, signing-only callers).
    /// </param>
    public   HiveAuthMiddleware(string? persistenceKey = null)
    {
        _store      = HivePeerStore.Default;
        _persistKey = persistenceKey;
        if (_persistKey is not null) LoadPersistedNonces();
    }
    internal HiveAuthMiddleware(HivePeerStore store)  { _store = store; }  // tests: no persistence

    private static readonly TimeSpan TsWindow        = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NonceTtl        = TimeSpan.FromMinutes(5);
    private const           int      MaxNoncesPerPeer = 1_000;
    private static readonly TimeSpan FlushThrottle   = TimeSpan.FromSeconds(5);

    // Per-peer nonce cache: nodeId → (nonce → expiry)
    private readonly Dictionary<string, Dictionary<string, DateTime>> _nonces = [];
    private readonly Lock    _nonceLock = new();
    private          DateTime _lastPrune = DateTime.UtcNow;

    // Persistence (null key = disabled)
    private readonly string?  _persistKey;
    private          DateTime _lastFlush = DateTime.MinValue;
    private          bool     _dirty;       // true once a nonce was recorded since last flush

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

        // Cap nonce length before it enters the nonce cache to prevent memory exhaustion.
        // Our own SignRequest produces 32-char hex nonces; anything larger is anomalous.
        if (nonce!.Length > 128)
            return Reject("nonce too long");

        // 1. Enrolled, not revoked, and secret available — one atomic lock acquisition
        //    eliminates the TOCTOU window between IsTrusted() and GetSharedSecret().
        var (trusted, secret) = _store.GetTrustedSecret(nodeId!);
        if (!trusted)
        {
            return GracePeriodActive
                ? HiveAuthResult.Authenticated("anonymous")
                : HiveAuthResult.Failed("unknown or revoked node");
        }
        if (secret is null) return Reject("no shared secret for peer");

        // 2. Timestamp
        if (!long.TryParse(tsStr, out var tsMs))
            return Reject("invalid timestamp");
        var tsAge = DateTime.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime;
        if (tsAge < -TsWindow || tsAge > TsWindow)
            return Reject($"clock skew too large ({tsAge.TotalSeconds:F0}s)");

        // 3. Nonce uniqueness
        if (!RecordNonce(nodeId!, nonce!))
            return Reject("nonce already seen (replay)");

        // 4. HMAC — secret retrieved atomically with trust check above
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
        // Sanitize CR/LF from path before embedding to prevent field-boundary injection.
        var safePath = path.Replace("\r", "%0D", StringComparison.Ordinal)
                           .Replace("\n", "%0A", StringComparison.Ordinal);
        return $"{method.ToUpperInvariant()}\n{safePath}\n{nonce}\n{tsMs}\n{bodyHash}";
    }

    private static byte[] ComputeHmac(byte[] key, string canonical)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
    }

    private bool RecordNonce(string nodeId, string nonce)
    {
        bool accepted;
        bool shouldFlush = false;
        lock (_nonceLock)
        {
            PruneExpiredNonces();

            if (!_nonces.TryGetValue(nodeId, out var peerNonces))
            {
                peerNonces        = [];
                _nonces[nodeId]   = peerNonces;
            }

            if (peerNonces.ContainsKey(nonce))
            {
                accepted = false;
            }
            else
            {
                // Evict oldest entry when over cap
                if (peerNonces.Count >= MaxNoncesPerPeer)
                {
                    var oldest = peerNonces.MinBy(kv => kv.Value).Key;
                    peerNonces.Remove(oldest);
                }

                peerNonces[nonce] = DateTime.UtcNow.Add(NonceTtl);
                accepted = true;
                _dirty   = true;

                // Throttle disk writes — at most once per FlushThrottle while under load.
                if (_persistKey is not null && DateTime.UtcNow - _lastFlush > FlushThrottle)
                {
                    _lastFlush  = DateTime.UtcNow;
                    shouldFlush = true;
                }
            }
        }

        if (shouldFlush) Flush();   // file write happens outside the lock
        return accepted;
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

    // ── Persistence ─────────────────────────────────────────────────────────────

    private static string NoncePath(string key) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheOrc", $"hive-nonces-{key}.json");

    /// <summary>
    /// Writes the live (non-expired) nonce cache to disk. Call on graceful shutdown for a
    /// zero replay window; throttled auto-flush bounds the loss on a hard kill to ~5s.
    /// Best-effort: a write failure degrades replay protection to in-memory only.
    /// </summary>
    public void Flush()
    {
        if (_persistKey is null) return;

        Dictionary<string, Dictionary<string, long>> snapshot;
        lock (_nonceLock)
        {
            // Nothing recorded since the last flush — skip so an idle/non-listening
            // instance can't clobber the active writer's file with a stale snapshot.
            if (!_dirty) return;
            _dirty = false;

            var cutoff = DateTime.UtcNow;
            snapshot = _nonces.ToDictionary(
                peer => peer.Key,
                peer => peer.Value
                            .Where(kv => kv.Value > cutoff)
                            .ToDictionary(
                                kv => kv.Key,
                                kv => new DateTimeOffset(kv.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()));
        }

        try
        {
            var path = NoncePath(_persistKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort — replay protection falls back to in-memory only */ }
    }

    private void LoadPersistedNonces()
    {
        try
        {
            var path = NoncePath(_persistKey!);
            if (!File.Exists(path)) return;

            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(
                File.ReadAllText(path));
            if (data is null) return;

            var now = DateTime.UtcNow;
            foreach (var (peer, nonces) in data)
            {
                var live = new Dictionary<string, DateTime>();
                foreach (var (nonce, expMs) in nonces)
                {
                    var exp = DateTimeOffset.FromUnixTimeMilliseconds(expMs).UtcDateTime;
                    if (exp > now) live[nonce] = exp;   // drop anything already expired
                }
                if (live.Count > 0) _nonces[peer] = live;
            }
        }
        catch { /* corrupt or unreadable — start with an empty cache */ }
    }
}
