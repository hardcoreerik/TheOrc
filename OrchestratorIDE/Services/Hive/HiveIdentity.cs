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

    /// <summary>
    /// The role THIS node was granted within the hive, as recorded in the
    /// <c>AssignedRole</c> field of the <see cref="HivePairingResponse"/> the approver sent
    /// back when this node joined (HIVE_MEMBERSHIP_SPEC.md §5.4). Distinct from
    /// <c>HivePeer.Role</c>, which records roles THIS node has granted to OTHERS — there was
    /// previously no field anywhere for "what role did somebody else grant me." Defaults to
    /// Observer (safe/no-authority) until a pairing completes and sets it explicitly. A
    /// founder's authority comes from <see cref="HiveRole"/> == Founder, not from this field
    /// — see <see cref="CanIssueMembershipCerts"/>.
    /// </summary>
    public HiveNodeRole       SelfRole            { get; private set; } = HiveNodeRole.Observer;

    /// <summary>
    /// Base64 JSON of the membership certificate this node received when it joined the hive
    /// (HIVE_MEMBERSHIP_SPEC.md §5.4) — "" if none was issued (approver wasn't a Controller)
    /// or this node is the founder (founders don't need a cert; they ARE the root of trust).
    /// Presented to third-party peers this node has never directly paired with, via the
    /// X-Hive-Membership-Cert header (§5.5).
    /// </summary>
    public string             OwnMembershipCertJson { get; private set; } = "";

    /// <summary>
    /// Whether this node may issue membership certificates to others (§5.2: Controllers only,
    /// no delegation chains — a Controller introduced transitively through someone else's
    /// vouching does not count, only a role this node's own pairing approver directly granted,
    /// or founder status). Founders are implicitly authoritative from the moment they mint
    /// HiveId; everyone else needs an explicit Controller grant recorded in <see cref="SelfRole"/>.
    /// </summary>
    public bool CanIssueMembershipCerts => HiveRole == HiveRole.Founder || SelfRole == HiveNodeRole.Controller;

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
                         HiveRole hiveRole = HiveRole.Unset, bool isEphemeral = false,
                         HiveNodeRole selfRole = HiveNodeRole.Observer, string ownCertJson = "")
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
        SelfRole = selfRole;
        OwnMembershipCertJson = ownCertJson;
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
            if (!_isEphemeral) Persist(hiveId: hiveId, hiveRole: role);

            HiveId   = hiveId;
            HiveRole = role;
        }
    }

    /// <summary>
    /// Explicitly abandons this node's current hive, resetting HiveId to unset
    /// (<see cref="HiveRole.Unset"/>) so the node can JOIN a DIFFERENT hive on its next pairing —
    /// where <see cref="SetHive"/> would otherwise throw rather than bridge two hives
    /// (HIVE_MEMBERSHIP_SPEC.md §4.3). This is a hive-MEMBERSHIP reset, not an identity reset:
    /// the node keeps its signing/exchange keys, NodeId, and paired-peer shared secrets. The
    /// own-membership cert is cleared, since it was issued by the hive being left and no longer
    /// applies. Deliberately an explicit operator action (surfaced in the repair wizard), never
    /// something pairing does on its own — so the §4.3 "no silent bridge" guarantee is preserved:
    /// leaving is a choice the human makes, not a side effect.
    /// </summary>
    public void LeaveHive()
    {
        lock (_hiveLock)
        {
            if (!_isEphemeral) Persist(hiveId: "", hiveRole: HiveRole.Unset, ownCertJson: "");
            HiveId   = "";
            HiveRole = HiveRole.Unset;
            OwnMembershipCertJson = "";
        }
    }

    /// <summary>
    /// Records the role this node's pairing approver granted it (HivePairingResponse.
    /// AssignedRole) — see <see cref="SelfRole"/>. Unlike <see cref="SetHive"/>, overwriting
    /// is always allowed: a node's granted role can legitimately change (re-pairing,
    /// promotion) without that implying any hive-bridging risk.
    /// </summary>
    public void SetSelfRole(HiveNodeRole role)
    {
        lock (_hiveLock)
        {
            if (!_isEphemeral) Persist(selfRole: role);
            SelfRole = role;
        }
    }

    /// <summary>Stores the membership certificate this node received at pairing time (§5.4).</summary>
    public void SetOwnMembershipCert(string certJson)
    {
        lock (_hiveLock)
        {
            if (!_isEphemeral) Persist(ownCertJson: certJson);
            OwnMembershipCertJson = certJson;
        }
    }

    private void Persist(string? hiveId = null, HiveRole? hiveRole = null,
                         HiveNodeRole? selfRole = null, string? ownCertJson = null) =>
        DpapiSave(IdentityPath, JsonSerializer.Serialize(new StoredIdentity(
            Convert.ToBase64String(_signingKey.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(_exchangeKey.ExportPkcs8PrivateKey()),
            hiveId      ?? HiveId,
            hiveRole    ?? HiveRole,
            selfRole    ?? SelfRole,
            ownCertJson ?? OwnMembershipCertJson)));

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
                        _instance = new HiveIdentity(signing, exchange, stored.HiveId, stored.HiveRole,
                            selfRole: stored.SelfRole, ownCertJson: stored.OwnMembershipCertJson);
                        return _instance;
                    }
                }
                catch { /* corrupt or wrong user — regenerate */ }
            }

            var newSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var newExch = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _instance   = new HiveIdentity(newSign, newExch);
            _instance.Persist(hiveId: "", hiveRole: HiveRole.Unset);

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
        string HiveId = "", HiveRole HiveRole = HiveRole.Unset,
        HiveNodeRole SelfRole = HiveNodeRole.Observer, string OwnMembershipCertJson = "");

    public void Dispose()
    {
        _signingKey.Dispose();
        _exchangeKey.Dispose();
    }
}
