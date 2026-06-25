// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
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

    /// <summary>
    /// Fallback address for the SAME machine reached a different way (e.g. this host's primary
    /// <see cref="Url"/> is its LAN IP and AltUrl is its Tailscale name, or vice versa). Set by
    /// <see cref="HiveHosts.Dedupe"/> when it merges a LAN + Tailscale entry of one machine into
    /// a single node. <see cref="ProbeAsync"/> falls back to this when the primary is
    /// unreachable (and promotes it to primary if it works), so one node stays reachable whether
    /// you're on your LAN or roaming on Tailscale. Empty when the machine is single-homed.
    /// </summary>
    public string AltUrl { get; set; } = "";

    /// <summary>"manual" | "lan" | "tailscale" | "paired" — how this node was added.</summary>
    public string Source { get; set; } = "manual";

    /// <summary>Set by ProbeAsync — not persisted. This is OLLAMA (port 11434) reachability,
    /// NOT whether the node's HIVE port (7078) is up -- see <see cref="HiveApiReachable"/>
    /// for that. A node can show "online" here while pairing still fails, because the two
    /// ports are independent (found 2026-06-21: a node's Ollama was reachable while its
    /// HIVE node server wasn't running at all, and nothing distinguished the two in the UI).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? Reachable { get; set; }

    /// <summary>
    /// Set by ProbeHiveApiAsync (port 7078) -- distinct from <see cref="Reachable"/> (Ollama,
    /// port 11434). True/false once probed; null before the first probe. Pairing and other
    /// HIVE-protocol actions need THIS to be true, not just Reachable.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? HiveApiReachable { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Models { get; set; } = [];

    /// <summary>Free VRAM in MB. 0 = unknown. Set by ProbeHiveApiAsync — not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int VramFreeMb { get; set; }

    /// <summary>Lane labels reported by the node's /hive/info endpoint. Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] Lanes { get; set; } = [];

    /// <summary>
    /// llama.cpp RPC port (default 50052) if the node is running llama-rpc-server.
    /// 0 = RPC not available. Set by ProbeHiveApiAsync — not persisted.
    /// When non-zero the coordinator can add this node to its --rpc chain.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RpcPort { get; set; }

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
        // Never persist synthesized paired-peer hosts (Source == "paired"): they are derived
        // live from hive-peers.json by MergePairedPeers on every load/probe, so writing them
        // here would duplicate that state, strip the "paired" marker on reload (it'd come back
        // as a "manual" host), and resurrect a stale address after the peer's IP changed. The
        // named-host store holds ONLY manually-added / discovered hosts.
        File.WriteAllText(storePath,
            JsonSerializer.Serialize(hosts.Where(h => h.Source != "paired").ToList(), _json));
    }

    /// <summary>
    /// Adds a synthesized host for every paired peer (hive-peers.json) not already represented
    /// in <paramref name="hosts"/> (the named-host store), so a node you've paired with always
    /// appears in the constellation -- even one paired via the headless daemon, which only ever
    /// writes hive-peers.json, never hive-hosts.json. Without this a fully-trusted, reachable
    /// HIVE member is simply invisible in the GUI (found 2026-06-24: HARDCOREPI paired and
    /// serving /hive/info, but absent from the constellation because it was never also added as
    /// a named Ollama host).
    ///
    /// Dedup is by name, case-insensitive, matching in EITHER direction so a host is not
    /// duplicated by its own paired twin -- the either-direction prefix match also absorbs the
    /// Windows NetBIOS 15-char name cap (a peer "HARDCORELAPTOPM" vs a host "hardcorelaptopmsi").
    /// The local machine's own self-records are skipped by NodeId. A peer with no known address
    /// yet (never seen on the wire) is skipped -- there is nothing to probe or draw.
    /// </summary>
    public static void MergePairedPeers(List<HiveHost> hosts,
                                        IEnumerable<HivePeer>? peers = null,
                                        string? selfNodeId = null)
    {
        peers      ??= HivePeerStore.Default.All();
        selfNodeId ??= HiveIdentity.Load().NodeId;

        foreach (var peer in peers)
        {
            if (peer.Revoked || peer.NodeId == selfNodeId || string.IsNullOrWhiteSpace(peer.Name))
                continue;

            if (hosts.Any(h => SameMachine(h.Name, peer.Name))) continue;

            // LastKnownAddress is "ip:port" of the peer's HIVE node server (7078). The HiveHost
            // model's Url is the OLLAMA url (11434); ProbeHiveApiAsync re-derives :7078 from the
            // host portion of this Url, so HIVE reachability still probes the right port.
            var ip = peer.LastKnownAddress
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(ip)) continue;

            hosts.Add(new HiveHost
            {
                Name     = peer.Name,
                Url      = $"http://{ip}:11434",
                Hostname = ip,
                Source   = "paired",
            });
        }
    }

    /// <summary>
    /// True when two node names denote the same physical machine — tolerant of case and the
    /// Windows NetBIOS 15-char name cap (so a Tailscale short-name "hardcorelaptopmsi" matches a
    /// beacon name "HARDCORELAPTOPM"). Used to collapse a machine's LAN / Tailscale / paired
    /// entries into one node. "This PC" only ever matches itself. Prefix matching requires the
    /// shorter name to be ≥4 chars so trivially-short names can't false-merge distinct machines.
    /// </summary>
    public static bool SameMachine(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (a.Equals("This PC", StringComparison.OrdinalIgnoreCase) ||
            b.Equals("This PC", StringComparison.OrdinalIgnoreCase))
            return a.Equals(b, StringComparison.OrdinalIgnoreCase);
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        var shorter = a.Length <= b.Length ? a : b;
        if (shorter.Length < 4) return false;
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collapses multiple entries for the SAME physical machine (e.g. a LAN address and a
    /// Tailscale name) into ONE node, so the constellation never shows a machine twice. Keeps
    /// the best-sourced entry as primary (paired &gt; manual &gt; lan &gt; tailscale) and stashes
    /// the other's address in <see cref="HiveHost.AltUrl"/> for reachable fallback. "This PC" is
    /// never merged. Mutates <paramref name="hosts"/> in place.
    /// </summary>
    public static void Dedupe(List<HiveHost> hosts)
    {
        static int Rank(HiveHost h) => h.Source switch
        {
            "paired" => 0, "manual" => 1, "lan" => 2, "tailscale" => 3, _ => 4,
        };

        for (int i = 0; i < hosts.Count; i++)
        {
            var a = hosts[i];
            if (a.Name.Equals("This PC", StringComparison.OrdinalIgnoreCase)) continue;

            for (int j = hosts.Count - 1; j > i; j--)
            {
                var b = hosts[j];
                if (b.Name.Equals("This PC", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SameMachine(a.Name, b.Name)) continue;

                var primary = Rank(a) <= Rank(b) ? a : b;
                var other   = ReferenceEquals(primary, a) ? b : a;

                // Keep a cross-network fallback address (e.g. primary=LAN, alt=Tailscale).
                if (string.IsNullOrEmpty(primary.AltUrl) &&
                    !primary.Url.Equals(other.Url, StringComparison.OrdinalIgnoreCase))
                    primary.AltUrl = other.Url;
                if (string.IsNullOrEmpty(primary.Hostname) && other.Hostname.Length > 0)
                    primary.Hostname = other.Hostname;
                // Prefer the fuller name for display — avoids showing the NetBIOS 15-char
                // truncation ("HARDCORELAPTOPM") when a complete name ("hardcorelaptopmsi") is
                // available from the other entry. Address/source still come from the primary.
                if (other.Name.Length > primary.Name.Length) primary.Name = other.Name;

                a = hosts[i] = primary;   // keep the primary at slot i
                hosts.RemoveAt(j);        // drop the duplicate
            }
        }
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
            // Explicitly record success/failure -- the previous version silently returned on
            // null with nothing set, so a node whose Ollama (Reachable) was up but whose HIVE
            // port wasn't running looked identical to one that had never been probed at all,
            // and "online" in the UI meant only Ollama, not HIVE-readiness (Codex/Grok-style
            // finding from a live pairing-failure report, 2026-06-21).
            host.HiveApiReachable = info is not null;
            if (info is null) return;
            host.VramFreeMb = info.VramFreeMb;
            host.Lanes      = info.Lanes;
            host.RpcPort    = info.RpcPort;
            // Merge model list from hive API when Ollama probe missed some.
            if (info.Models.Length > 0 && host.Models.Count == 0)
                host.Models = info.Models;
        }
        catch
        {
            host.HiveApiReachable = false;
        }
    }

    /// <summary>
    /// Probes a host's Ollama API: sets Reachable and the installed model list.
    /// Never throws — unreachable is a state, not an error.
    /// </summary>
    public static async Task ProbeAsync(HiveHost host, int timeoutSeconds = 3)
    {
        if (await TryOllamaAsync(host, host.Url, timeoutSeconds))
        {
            host.Reachable = true;
            return;
        }

        // Primary address unreachable — try the same machine's cross-network fallback (set by
        // Dedupe: e.g. its Tailscale name when the primary was its LAN IP). If that works,
        // promote it to primary so the node stays reachable while roaming, no duplicate node.
        if (!string.IsNullOrEmpty(host.AltUrl) && await TryOllamaAsync(host, host.AltUrl, timeoutSeconds))
        {
            (host.Url, host.AltUrl) = (host.AltUrl, host.Url);
            host.Reachable = true;
            return;
        }

        host.Reachable = false;
        host.Models = [];
    }

    /// <summary>Tries one Ollama address; on success records the model list and returns true.
    /// Never throws — unreachable is a state, not an error.</summary>
    private static async Task<bool> TryOllamaAsync(HiveHost host, string url, int timeoutSeconds)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            var json = await http.GetStringAsync($"{url.TrimEnd('/')}/api/tags");
            using var doc = JsonDocument.Parse(json);
            host.Models = [.. doc.RootElement.GetProperty("models")
                .EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "")
                .Where(n => n.Length > 0)];
            return true;
        }
        catch { return false; }
    }
}
