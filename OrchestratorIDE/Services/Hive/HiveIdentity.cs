// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Local-only record of how this node came to belong to its current HiveId
/// (HIVE_MEMBERSHIP_SPEC.md §4.2). Not transmitted on the wire, carries no authority by
/// itself — distinct from <see cref="HiveNodeRole"/>, which is the per-peer authority tier
/// (Observer/Worker/Controller) that other nodes grant and enforce.
/// </summary>
public enum HiveRole { Unset, Founder, Member }

/// <summary>
/// Per-node cryptographic identity: P-256 ECDSA signing key + P-256 ECDH exchange key.
/// Persisted to %AppData%\TheOrc\hive-identity.json encrypted at rest with DPAPI.
///
/// NodeId = hex(SHA-256(signing public key DER)) — stable for the lifetime of the install.
/// Fingerprint = 8-word human-readable phrase derived from NodeId for visual verification.
///
/// All crypto is BCL-native (System.Security.Cryptography). No external packages.
/// </summary>
public sealed class HiveIdentity : IDisposable
{
    private static readonly string IdentityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TheOrc", "hive-identity.json");

    public string            NodeId              { get; }
    public string            Fingerprint         { get; }
    public byte[]            SigningPublicKeyDer  { get; }
    public byte[]            ExchangePublicKeyDer { get; }

    /// <summary>
    /// Hive-wide identifier (HIVE_MEMBERSHIP_SPEC.md §4) — "" until this node has either
    /// founded or joined a hive. Unlike NodeId, this survives Warchief elections; it
    /// identifies the mesh, not any one machine. Mutable post-construction (set via
    /// <see cref="SetHive"/>) because, unlike the signing/exchange keys, a node legitimately
    /// transitions Unset → Founder/Member during its lifetime without regenerating identity.
    /// </summary>
    public string            HiveId              { get; private set; } = "";

    /// <summary>
    /// Local-only record of how this node came to have <see cref="HiveId"/>. Not transmitted
    /// on the wire and carries no authority — authority comes from HivePeer.Role == Controller
    /// (see HIVE_MEMBERSHIP_SPEC.md §4.2). Diagnostic/UX value only (the repair wizard uses
    /// this to explain *why* a node is in a given state).
    /// </summary>
    public HiveRole           HiveRole            { get; private set; } = HiveRole.Unset;

    private readonly ECDsa             _signingKey;
    private readonly ECDiffieHellman   _exchangeKey;
    // CreateEphemeral() identities must never touch the real hive-identity.json on disk --
    // without this flag, SetHive() on a test identity would silently overwrite this
    // machine's actual persisted identity via DPAPI (caught before any test was written).
    private readonly bool              _isEphemeral;
    // Guards the check-then-mutate-then-persist sequence in SetHive -- without this, two
    // concurrent pairing flows (e.g. an inbound ApprovePairing racing an outbound
    // CompletePairing on the same node) could both observe HiveId as empty and each set a
    // different value, defeating the "refuse to bridge two hives" guarantee (grok review
    // BLOCKER, 2026-06-21).
    private readonly Lock              _hiveLock = new();

    private HiveIdentity(ECDsa signing, ECDiffieHellman exchange, string hiveId = "",
                         HiveRole hiveRole = HiveRole.Unset, bool isEphemeral = false)
    {
        _signingKey  = signing;
        _exchangeKey = exchange;
        _isEphemeral = isEphemeral;

        SigningPublicKeyDer  = signing.ExportSubjectPublicKeyInfo();
        ExchangePublicKeyDer = exchange.PublicKey.ExportSubjectPublicKeyInfo();

        var hashBytes = SHA256.HashData(SigningPublicKeyDer);
        NodeId        = Convert.ToHexString(hashBytes).ToLowerInvariant();
        Fingerprint   = DeriveFingerprint(hashBytes);

        HiveId   = hiveId;
        HiveRole = hiveRole;
    }

    /// <summary>
    /// Transitions this node from Unset to Founder (mints a fresh HiveId, no peers required)
    /// or Member (adopts a HiveId received during pairing). Persists immediately (unless this
    /// is a <see cref="CreateEphemeral"/> test identity, which never touches disk). Refuses to
    /// silently overwrite an already-set HiveId with a different one — callers must not call
    /// this when HiveId is already set and differs (HIVE_MEMBERSHIP_SPEC.md §4.3 mismatch case
    /// is a refused-pairing decision made by the caller, not by this method).
    /// </summary>
    public void SetHive(string hiveId, HiveRole role)
    {
        if (string.IsNullOrWhiteSpace(hiveId))
            throw new ArgumentException("hiveId must not be empty", nameof(hiveId));

        lock (_hiveLock)
        {
            if (!string.IsNullOrEmpty(HiveId) && HiveId != hiveId)
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing HiveId '{HiveId}' with '{hiveId}' — " +
                    "this would silently bridge two separate hives. Caller must resolve the " +
                    "conflict explicitly (HIVE_MEMBERSHIP_SPEC.md §4.3).");

            // Persist the NEW values before touching in-memory state. If Persist throws
            // (disk/DPAPI failure), HiveId/HiveRole are untouched -- the caller's pairing
            // attempt fails cleanly instead of leaving memory and disk disagreeing about
            // which hive this node belongs to (grok review BLOCKER, 2026-06-21).
            if (!_isEphemeral) Persist(hiveId, role);

            HiveId   = hiveId;
            HiveRole = role;
        }
    }

    private void Persist(string? hiveId = null, HiveRole? role = null) =>
        DpapiSave(IdentityPath, JsonSerializer.Serialize(new StoredIdentity(
            Convert.ToBase64String(_signingKey.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(_exchangeKey.ExportPkcs8PrivateKey()),
            hiveId ?? HiveId,
            role   ?? HiveRole)));

    // ── Singleton load-or-generate ─────────────────────────────────────────────

    private static HiveIdentity? _instance;
    private static readonly Lock _lock = new();

    /// <summary>
    /// Creates a fresh ephemeral identity (new P-256 keys, no DPAPI, no disk).
    /// Intended for unit tests only — not a singleton, caller owns disposal.
    /// </summary>
    internal static HiveIdentity CreateEphemeral()
    {
        var signing  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var exchange = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new HiveIdentity(signing, exchange, isEphemeral: true);
    }

    /// <summary>
    /// Returns the singleton identity for this node.
    /// Generates and persists a new identity on first call; loads from disk thereafter.
    /// Thread-safe. Silent on success, logs on regenerate.
    /// </summary>
    public static HiveIdentity Load()
    {
        lock (_lock)
        {
            if (_instance is not null) return _instance;

            Directory.CreateDirectory(Path.GetDirectoryName(IdentityPath)!);

            if (File.Exists(IdentityPath))
            {
                try
                {
                    var json   = DpapiLoad(IdentityPath);
                    var stored = JsonSerializer.Deserialize<StoredIdentity>(json);
                    if (stored is not null)
                    {
                        var signing  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                        signing.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.SigningPriv), out _);
                        var exchange = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                        exchange.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.ExchangePriv), out _);
                        _instance = new HiveIdentity(signing, exchange, stored.HiveId, stored.HiveRole);
                        return _instance;
                    }
                }
                catch { /* corrupt or wrong user — regenerate */ }
            }

            var newSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var newExch = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _instance   = new HiveIdentity(newSign, newExch);
            _instance.Persist(hiveId: "", role: HiveRole.Unset);

            return _instance;
        }
    }

    // ── Signing ───────────────────────────────────────────────────────────────

    public byte[] Sign(ReadOnlySpan<byte> data)
        => _signingKey.SignData(data.ToArray(), HashAlgorithmName.SHA256);

    public static bool Verify(byte[] signingPublicKeyDer, ReadOnlySpan<byte> data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            ecdsa.ImportSubjectPublicKeyInfo(signingPublicKeyDer, out _);
            return ecdsa.VerifyData(data.ToArray(), signature, HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    // ── ECDH shared secret derivation ─────────────────────────────────────────

    /// <summary>
    /// Derives a 32-byte shared secret using X25519-equivalent ECDH over P-256.
    /// Key derivation: HKDF-SHA256(rawSecret, salt=XOR(nodeId_a, nodeId_b), info="hive-session-v1").
    /// </summary>
    public byte[] DeriveSharedSecret(byte[] peerExchangePublicKeyDer, byte[] salt)
    {
        using var peerKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        peerKey.ImportSubjectPublicKeyInfo(peerExchangePublicKeyDer, out _);

        var rawSecret = _exchangeKey.DeriveKeyMaterial(peerKey.PublicKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, rawSecret, 32,
                              salt, "hive-session-v1"u8.ToArray());
    }

    // ── Fingerprint ───────────────────────────────────────────────────────────

    // 256 unique words — one per byte value (0-255). Exactly 256 entries.
    private static readonly string[] _words =
    [
        "acorn","amber","anchor","anvil","apex","arch","arctic","arrow",
        "ash","atlas","axle","azure","badge","bark","basalt","beacon",
        "bear","birch","blade","blaze","bluff","bough","brace","brand",
        "brine","brook","buckle","butte","cairn","canopy","cape","castle",
        "cedar","chalk","chord","cinder","cleft","cliff","cloak","cloud",
        "coil","colt","compass","coral","crag","crane","crest","crisp",
        "crown","culm","dagger","dale","dawn","delta","depth","dew",
        "dike","dome","draft","drift","dune","dusk","dust","eagle",
        "echo","eddy","elder","ember","ermine","escarp","estuary","fable",
        "falcon","fallow","fang","faro","fathom","fawn","feldspar","fern",
        "fjord","flange","flare","flax","fleet","flint","floe","flume",
        "forge","fossil","fox","frond","frost","gale","garnet","geyser",
        "glade","gleam","glen","glyph","gorge","granite","grove","gulf",
        "gust","hail","halo","haze","heath","helm","heron","hilt",
        "holly","horn","husk","ice","ignite","inlet","iris","iron",
        "isle","ivory","jade","jasper","jet","joist","juniper","keel",
        "kelp","kestrel","knoll","larch","lava","ledge","limestone","lodge",
        "loch","lode","magnet","maple","marsh","mast","mesa","mire",
        "mist","moat","monolith","moon","moss","mound","nave","nebula",
        "needle","nettle","notch","nook","opal","orbit","outcrop","parch",
        "peak","pearl","peat","pebble","pine","prism","quarry","quartz",
        "ravine","realm","reef","ridge","rime","rune","rush","rust",
        "sage","salt","sand","scarp","schist","shale","shard","shore",
        "skiff","slate","snow","spar","spire","spring","spruce","stalactite",
        "steppe","stern","stone","summit","surge","thorn","tidal","tide",
        "timber","topaz","tor","torrent","trace","tundra","vale","veil",
        "vent","vine","volcanic","wake","ward","warden","wavelet","weald",
        "wedge","willows","wisp","wool","wren","xenon","yew","zenith"
    ];

    public static string DeriveFingerprint(byte[] nodeIdHash)
    {
        // Use first 8 bytes → 8 words. Each byte selects one of 256 words.
        var words = new string[8];
        for (int i = 0; i < 8; i++)
            words[i] = _words[nodeIdHash[i] % _words.Length];
        return string.Join("-", words);
    }

    // ── DPAPI persistence ─────────────────────────────────────────────────────

    private static void DpapiSave(string path, string json)
    {
        var plain     = Encoding.UTF8.GetBytes(json);
        var encrypted = SecretProtection.Current.Protect(plain);
        File.WriteAllBytes(path, encrypted);
    }

    private static string DpapiLoad(string path)
    {
        var encrypted = File.ReadAllBytes(path);
        var plain     = SecretProtection.Current.Unprotect(encrypted);
        return Encoding.UTF8.GetString(plain);
    }

    private sealed record StoredIdentity(
        string SigningPriv, string ExchangePriv,
        string HiveId = "", HiveRole HiveRole = HiveRole.Unset);

    public void Dispose()
    {
        _signingKey.Dispose();
        _exchangeKey.Dispose();
    }
}
