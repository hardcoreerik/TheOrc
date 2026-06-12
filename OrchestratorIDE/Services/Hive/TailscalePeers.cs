using System.Diagnostics;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND Tailscale awareness: when Tailscale is installed, peers have
/// stable MagicDNS hostnames and 100.x IPs that work across LANs/NAT — far
/// better hive addresses than raw LAN IPs. Not every node needs Tailscale;
/// this just enriches discovery when it's present (docs/HIVE_MIND_SPEC.md H1).
///
/// Runs `tailscale status --json`; absent/erroring Tailscale is a no-op, never
/// a failure. Returns peers as (DnsName, Ip, Online) for the Hive panel.
/// </summary>
public static class TailscalePeers
{
    public record Peer(string DnsName, string Ip, bool Online);

    /// <summary>True if a tailscale CLI is on PATH or in the default install dir.</summary>
    public static bool IsInstalled => ExePath is not null;

    private static string? ExePath
    {
        get
        {
            var candidates = new[]
            {
                "tailscale",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Tailscale", "tailscale.exe"),
            };
            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            // bare "tailscale" — let Process resolve via PATH; verified by Discover()
            return "tailscale";
        }
    }

    /// <summary>
    /// Returns online Tailscale peers (excludes self). Empty list if Tailscale
    /// is not installed/up — callers treat that as "no Tailscale peers".
    /// </summary>
    public static List<Peer> Discover()
    {
        var peers = new List<Peer>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath!, Arguments = "status --json",
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return peers;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            if (string.IsNullOrWhiteSpace(json)) return peers;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Peer", out var peerMap)) return peers;

            foreach (var entry in peerMap.EnumerateObject())
            {
                var v = entry.Value;
                string dns = v.TryGetProperty("DNSName", out var d) ? (d.GetString() ?? "") : "";
                dns = dns.TrimEnd('.');                       // MagicDNS trailing dot
                string ip = v.TryGetProperty("TailscaleIPs", out var ips) && ips.GetArrayLength() > 0
                    ? ips[0].GetString() ?? "" : "";
                bool online = v.TryGetProperty("Online", out var on) && on.GetBoolean();
                if (ip.Length > 0)
                    peers.Add(new Peer(dns, ip, online));
            }
        }
        catch { /* tailscale absent or not running — no peers */ }
        return peers;
    }

    /// <summary>Short label from a MagicDNS name: "hardcorepc.tailnet.ts.net" → "hardcorepc".</summary>
    public static string ShortName(string dnsName)
        => dnsName.Length == 0 ? "" : dnsName.Split('.')[0];
}
