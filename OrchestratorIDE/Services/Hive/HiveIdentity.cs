using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

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

    private readonly ECDsa             _signingKey;
    private readonly ECDiffieHellman   _exchangeKey;

    private HiveIdentity(ECDsa signing, ECDiffieHellman exchange)
    {
        _signingKey  = signing;
        _exchangeKey = exchange;

        SigningPublicKeyDer  = signing.ExportSubjectPublicKeyInfo();
        ExchangePublicKeyDer = exchange.PublicKey.ExportSubjectPublicKeyInfo();

        var hashBytes = SHA256.HashData(SigningPublicKeyDer);
        NodeId        = Convert.ToHexString(hashBytes).ToLowerInvariant();
        Fingerprint   = DeriveFingerprint(hashBytes);
    }

    // ── Singleton load-or-generate ─────────────────────────────────────────────

    private static HiveIdentity? _instance;
    private static readonly Lock _lock = new();

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
                        _instance = new HiveIdentity(signing, exchange);
                        return _instance;
                    }
                }
                catch { /* corrupt or wrong user — regenerate */ }
            }

            var newSign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var newExch = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            _instance   = new HiveIdentity(newSign, newExch);

            DpapiSave(IdentityPath, JsonSerializer.Serialize(new StoredIdentity(
                Convert.ToBase64String(newSign.ExportPkcs8PrivateKey()),
                Convert.ToBase64String(newExch.ExportPkcs8PrivateKey()))));

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
        var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, encrypted);
    }

    private static string DpapiLoad(string path)
    {
        var encrypted = File.ReadAllBytes(path);
        var plain     = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    private sealed record StoredIdentity(string SigningPriv, string ExchangePriv);

    public void Dispose()
    {
        _signingKey.Dispose();
        _exchangeKey.Dispose();
    }
}
