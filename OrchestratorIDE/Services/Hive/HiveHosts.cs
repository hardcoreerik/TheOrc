using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND Phase A — named remote Ollama hosts (see docs/HIVE_MIND_SPEC.md).
///
/// A host is a (name, url) pair: "BIGRIG → http://localhost:11434",
/// "HARDCOREPC → http://192.168.1.20:11434". Phase A keeps entry manual;
/// H1 replaces manual entry with discovery but reuses this store and the
/// reachability probe unchanged.
///
/// Stored separately from settings.json so hive config can later be shared
/// with the node service (V2 architecture) without dragging app settings along.
/// </summary>
public sealed class HiveHost
{
    public string Name { get; set; } = "";
    public string Url  { get; set; } = "";

    /// <summary>Stable DNS/MagicDNS hostname when known (Tailscale, mDNS).</summary>
    public string Hostname { get; set; } = "";

    /// <summary>"manual" | "lan" | "tailscale" — how this node was added.</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Set by ProbeAsync — not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? Reachable { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Models { get; set; } = [];

    /// <summary>Free VRAM in MB. 0 = unknown. Set by ProbeHiveApiAsync — not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int VramFreeMb { get; set; }

    /// <summary>Lane labels reported by the node's /hive/info endpoint. Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Lanes { get; set; } = [];

    public override string ToString() => $"{Name} ({Url})";
}

public static class HiveHosts
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "hive-hosts.json");

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <summary>Loads hosts; always contains "This PC" (localhost) first.</summary>
    public static List<HiveHost> Load(string? localUrl = null, string? storePath = null)
    {
        storePath ??= StorePath;
        List<HiveHost> hosts = [];
        if (File.Exists(storePath))
        {
            // Only CORRUPT content starts fresh; IO failures (locks, permissions)
            // must bubble so a transient error can't silently discard saved hosts
            // on the next Save (codex finding: fail closed, not open).
            try
            {
                hosts = JsonSerializer.Deserialize<List<HiveHost>>(
                    File.ReadAllText(storePath), _json) ?? [];
            }
            catch (JsonException) { hosts = []; }
        }

        var local = localUrl ?? "http://localhost:11434";
        var thisPc = hosts.FirstOrDefault(h => h.Name == "This PC");
        if (thisPc is null)
        {
            hosts.Insert(0, new HiveHost { Name = "This PC", Url = local });
        }
        else
        {
            thisPc.Url = local;
            hosts.Remove(thisPc);
            hosts.Insert(0, thisPc);   // contract: "This PC" is always first
        }
        return hosts;
    }

    public static void Save(IEnumerable<HiveHost> hosts, string? storePath = null)
    {
        storePath ??= StorePath;
        var dir = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(storePath,
            JsonSerializer.Serialize(hosts.ToList(), _json));
    }

    /// <summary>
    /// Probes the HIVE MIND node API (port 7078) to get VRAM + lanes.
    /// Updates <see cref="HiveHost.VramFreeMb"/> and <see cref="HiveHost.Lanes"/>.
    /// Best-effort — silently no-ops if the endpoint is unavailable (Phase A node).
    /// </summary>
    public static async Task ProbeHiveApiAsync(HiveHost host, int timeoutMs = 2000)
    {
        try
        {
            var addr = new Uri(host.Url).Host;
            var info = await HiveNodeServer.ProbeAsync(addr, timeoutMs);
            if (info is null) return;
            host.VramFreeMb = info.VramFreeMb;
            host.Lanes      = info.Lanes;
            // Merge model list from hive API when Ollama probe missed some.
            if (info.Models.Length > 0 && host.Models.Count == 0)
                host.Models = info.Models;
        }
        catch { /* non-fatal */ }
    }

    /// <summary>
    /// Probes a host's Ollama API: sets Reachable and the installed model list.
    /// Never throws — unreachable is a state, not an error.
    /// </summary>
    public static async Task ProbeAsync(HiveHost host, int timeoutSeconds = 3)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var json = await http.GetStringAsync($"{host.Url.TrimEnd('/')}/api/tags");
            using var doc = JsonDocument.Parse(json);
            host.Models = [.. doc.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "")
                .Where(n => n.Length > 0)];
            host.Reachable = true;
        }
        catch
        {
            host.Reachable = false;
            host.Models = [];
        }
    }
}
