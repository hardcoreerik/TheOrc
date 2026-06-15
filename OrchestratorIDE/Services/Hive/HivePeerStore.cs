// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Services.Hive;

/// <summary>Role a node holds in the hive authority model.</summary>
public enum HiveNodeRole { Observer, Worker, Controller }

/// <summary>Policy governing when a peer may assert Warchief (controller) authority over this node.</summary>
public enum HiveAcceptControlPolicy
{
    Never,      // this node never accepts controller authority from outside
    Ask,        // show approval card each time (default for full-suite nodes)
    Allowlist,  // auto-accept from specific NodeIds in ControlAllowlist
    AnyPaired,  // auto-accept from any enrolled peer (lowest trust)
}

/// <summary>
/// One trusted peer record persisted in hive-peers.json.
/// </summary>
public sealed class HivePeer
{
    public string                 NodeId            { get; set; } = "";
    public string                 Name              { get; set; } = "";
    public string                 Fingerprint       { get; set; } = "";
    /// <summary>DPAPI-encrypted HKDF-derived shared secret, Base64.</summary>
    public string                 SharedSecretEnc   { get; set; } = "";
    /// <summary>Initiator's signing public key DER, Base64. Used to verify signed assertions.</summary>
    public string                 SigningPublicKeyDer { get; set; } = "";

    public HiveNodeRole           Role              { get; set; } = HiveNodeRole.Observer;
    /// <summary>Hard ceiling on role for this peer — mobile nodes are capped at Worker.</summary>
    public HiveNodeRole           MaxRole           { get; set; } = HiveNodeRole.Controller;
    /// <summary>Task lane constraints. Empty = all lanes permitted.</summary>
    public string[]               AllowedLanes      { get; set; } = [];

    public HiveAcceptControlPolicy AcceptControlFrom { get; set; } = HiveAcceptControlPolicy.Ask;
    /// <summary>NodeIds auto-approved for controller authority (Policy=Allowlist).</summary>
    public string[]               ControlAllowlist  { get; set; } = [];

    /// <summary>
    /// Enrollment order stamp. Used for deterministic leader election:
    /// lowest EnrollmentSeq among online peers becomes temporary Warchief.
    /// </summary>
    public int                    EnrollmentSeq     { get; set; }
    public DateTime               PairedAt          { get; set; }
    public bool                   IsMobile          { get; set; }  // Android/iOS client
    public bool                   Revoked           { get; set; }
    public DateTime?              RevokedAt         { get; set; }

    // ── Persisted liveness hint ────────────────────────────────────────────────

    /// <summary>
    /// Last known "ip:port" address for this peer. Persisted so the mesh can send
    /// the first outbound heartbeat after a restart without waiting for an inbound probe.
    /// May be stale (IP changed) — ReceiveHeartbeat updates it from the actual remote IP.
    /// </summary>
    public string        LastKnownAddress { get; set; } = "";

    // ── Runtime-only (not serialized) ─────────────────────────────────────────

    [JsonIgnore] public DateTime?     LastHeartbeat    { get; set; }
    [JsonIgnore] public HiveNodeRole  ActiveRole       { get; set; } = HiveNodeRole.Observer;
    [JsonIgnore] public int           VramFreeMb       { get; set; }
}

/// <summary>
/// Reads and writes hive-peers.json — the local trust store for enrolled HIVE peers.
/// Atomic writes (write-to-temp then replace). Thread-safe.
/// </summary>
public sealed class HivePeerStore
{
    private static readonly string PeersPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheOrc", "hive-peers.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented        = true,
        Converters           = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static readonly HivePeerStore Default = new();

    private readonly Lock           _lock          = new();
    private          List<HivePeer> _peers;
    private          int            _nextSeq;
    private readonly bool           _persistToDisk;

    private HivePeerStore()
    {
        _peers         = LoadFromDisk();
        _nextSeq       = _peers.Count > 0 ? _peers.Max(p => p.EnrollmentSeq) + 1 : 0;
        _persistToDisk = true;
    }

    private HivePeerStore(List<HivePeer> initial)
    {
        _peers         = initial;
        _nextSeq       = _peers.Count > 0 ? _peers.Max(p => p.EnrollmentSeq) + 1 : 0;
        _persistToDisk = false;
    }

    /// <summary>
    /// Creates an in-memory peer store with no disk I/O. For unit tests only.
    /// SetSharedSecret/GetSharedSecret still use DPAPI (fine on Windows test runners).
    /// </summary>
    internal static HivePeerStore CreateForTest(IEnumerable<HivePeer>? initial = null)
        => new HivePeerStore(initial?.ToList() ?? []);

    // ── Read ──────────────────────────────────────────────────────────────────

    public IReadOnlyList<HivePeer> All()
    {
        lock (_lock) return [.. _peers];
    }

    public HivePeer? Find(string nodeId)
    {
        lock (_lock) return _peers.FirstOrDefault(p => p.NodeId == nodeId);
    }

    public bool IsTrusted(string nodeId)
    {
        var p = Find(nodeId);
        return p is { Revoked: false };
    }

    /// <summary>Returns DPAPI-decrypted shared secret, or null if peer not found/revoked.</summary>
    public byte[]? GetSharedSecret(string nodeId)
    {
        var peer = Find(nodeId);
        if (peer is null || peer.Revoked || string.IsNullOrEmpty(peer.SharedSecretEnc))
            return null;
        try
        {
            var enc = Convert.FromBase64String(peer.SharedSecretEnc);
            return SecretProtection.Current.Unprotect(enc);
        }
        catch { return null; }
    }

    /// <summary>
    /// Atomically checks trust status and retrieves the shared secret under one lock,
    /// closing the TOCTOU window between <see cref="IsTrusted"/> and <see cref="GetSharedSecret"/>.
    /// Returns (false, null) for unknown/revoked peers or DPAPI failure.
    /// </summary>
    public (bool Trusted, byte[]? Secret) GetTrustedSecret(string nodeId)
    {
        lock (_lock)
        {
            var peer = _peers.FirstOrDefault(p => p.NodeId == nodeId);
            if (peer is null || peer.Revoked || string.IsNullOrEmpty(peer.SharedSecretEnc))
                return (false, null);
            try
            {
                var enc    = Convert.FromBase64String(peer.SharedSecretEnc);
                var secret = SecretProtection.Current.Unprotect(enc);
                return (true, secret);
            }
            catch { return (false, null); }
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public void AddOrUpdate(HivePeer peer)
    {
        lock (_lock)
        {
            var existing = _peers.FirstOrDefault(p => p.NodeId == peer.NodeId);
            if (existing is not null)
            {
                // Preserve enrollment sequence on update
                peer.EnrollmentSeq = existing.EnrollmentSeq;
                _peers.Remove(existing);
            }
            else
            {
                peer.EnrollmentSeq = _nextSeq++;
            }
            _peers.Add(peer);
            if (_persistToDisk) SaveToDisk(_peers);
        }
    }

    public void SetSharedSecret(string nodeId, byte[] secret)
    {
        lock (_lock)
        {
            var peer = _peers.FirstOrDefault(p => p.NodeId == nodeId);
            if (peer is null) return;
            var enc = SecretProtection.Current.Protect(secret);
            peer.SharedSecretEnc = Convert.ToBase64String(enc);
            if (_persistToDisk) SaveToDisk(_peers);
        }
    }

    public void Revoke(string nodeId)
    {
        lock (_lock)
        {
            var peer = _peers.FirstOrDefault(p => p.NodeId == nodeId);
            if (peer is null) return;
            peer.Revoked   = true;
            peer.RevokedAt = DateTime.UtcNow;
            if (_persistToDisk) SaveToDisk(_peers);
        }
    }

    /// <summary>
    /// Updates peer liveness. LastKnownAddress is persisted to disk when it changes
    /// so it survives restart. Other fields (LastHeartbeat, ActiveRole, VramFreeMb)
    /// are runtime-only.
    /// </summary>
    public void UpdateLiveness(string nodeId, string address, HiveNodeRole activeRole, int vramFreeMb)
    {
        bool addressChanged = false;
        lock (_lock)
        {
            var peer = _peers.FirstOrDefault(p => p.NodeId == nodeId);
            if (peer is null) return;

            addressChanged        = peer.LastKnownAddress != address;
            peer.LastKnownAddress = address;
            peer.LastHeartbeat    = DateTime.UtcNow;
            peer.ActiveRole       = activeRole;
            peer.VramFreeMb       = vramFreeMb;

            if (addressChanged && _persistToDisk) SaveToDisk(_peers);
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private static List<HivePeer> LoadFromDisk()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PeersPath)!);
        if (!File.Exists(PeersPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<HivePeer>>(
                File.ReadAllText(PeersPath), _json) ?? [];
        }
        catch { return []; }
    }

    private static void SaveToDisk(List<HivePeer> peers)
    {
        var tmp = PeersPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(peers, _json));
        File.Move(tmp, PeersPath, overwrite: true);
    }
}
