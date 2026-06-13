using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND H1 — zero-config LAN discovery via UDP broadcast (port 7077).
///
/// Broadcasting side: sends a compact JSON heartbeat every 5 seconds so every
/// machine on the same subnet can see this node without any manual configuration.
///
/// Listening side: receives heartbeats from peers; fires OnNodeSeen when a new
/// (or updated) node announces itself. HivePanel subscribes to update the UI.
///
/// Security: beacons contain only name + URL + model list — no credentials.
/// The actual hive job API (port 7078) requires HMAC-signed requests (Phase H2+).
/// Beacons are only sent/accepted on the Private Windows Firewall profile; the
/// installer adds the inbound rule for port 7077 (Private only) via HiveEnroller.
/// </summary>
public sealed class HiveBeacon : IDisposable
{
    public const int BeaconPort = 7077;

    private UdpClient?  _broadcaster;
    private Timer?      _broadcastTimer;
    private string      _payload    = "";
    private bool        _disposed;
    private bool        _listening;

    /// <summary>Fired on a thread-pool thread when any UDP beacon arrives.</summary>
    public event Action<HiveBeaconMessage>? OnNodeSeen;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Start broadcasting and listening. Safe to call multiple times.</summary>
    public void Start(string nodeName, string ollamaUrl,
                      IReadOnlyList<string> models, int vramFreeMb)
    {
        if (_disposed) return;
        UpdatePayload(nodeName, ollamaUrl, models, vramFreeMb);

        if (_broadcaster is null)
        {
            _broadcaster = new UdpClient();
            _broadcaster.EnableBroadcast = true;
        }

        _broadcastTimer ??= new Timer(_ => Broadcast(), null,
                                      TimeSpan.Zero, TimeSpan.FromSeconds(5));

        if (!_listening)
        {
            _listening = true;
            _ = ListenAsync();
        }
    }

    /// <summary>Update the payload that will go out on the next beacon tick.</summary>
    public void UpdatePayload(string nodeName, string ollamaUrl,
                              IReadOnlyList<string> models, int vramFreeMb)
    {
        var msg = new HiveBeaconMessage(
            nodeName, ollamaUrl, HiveNodeServer.ApiPort,
            [.. models], vramFreeMb);
        _payload = JsonSerializer.Serialize(msg);
    }

    // ── One-shot LAN scan ─────────────────────────────────────────────────────

    /// <summary>
    /// Sends a single broadcast then listens for replies for <paramref name="durationMs"/> ms.
    /// Returns every unique node that answered. Does NOT require Start() to be called first.
    /// </summary>
    public static async Task<List<HiveBeaconMessage>> ScanAsync(
        int durationMs = 2500,
        CancellationToken ct = default)
    {
        var seen = new Dictionary<string, HiveBeaconMessage>(StringComparer.OrdinalIgnoreCase);

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;

        // Try to bind the listener first so we don't miss immediate replies.
        using var listener = new UdpClient();
        try
        {
            listener.Client.SetSocketOption(SocketOptionLevel.Socket,
                                            SocketOptionName.ReuseAddress, true);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
        }
        catch
        {
            // Port already bound (our own Start() listener is running) — that's fine,
            // OnNodeSeen will capture the replies and we return what we already know.
            return [];
        }

        // Send a minimal probe so other nodes reply on their next broadcast tick.
        // (We still collect whatever broadcasts arrive during the window.)
        var probe = Encoding.UTF8.GetBytes("{\"probe\":true}");
        try { udp.Send(probe, probe.Length,
                       new IPEndPoint(IPAddress.Broadcast, BeaconPort)); }
        catch { /* non-fatal */ }

        var deadline = DateTime.UtcNow.AddMilliseconds(durationMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            listener.Client.ReceiveTimeout = Math.Max(50,
                (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            try
            {
                System.Net.IPEndPoint? ep = null;
                var result = listener.Receive(ref ep);
                var json   = Encoding.UTF8.GetString(result);
                var msg    = JsonSerializer.Deserialize<HiveBeaconMessage>(json);
                if (msg is { Name.Length: > 0 })
                    seen[msg.OllamaUrl] = msg;
            }
            catch { /* timeout or parse error — keep looping */ }
        }

        return [.. seen.Values];
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void Broadcast()
    {
        if (_disposed || _broadcaster is null || _payload.Length == 0) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(_payload);
            _broadcaster.Send(bytes, bytes.Length,
                              new IPEndPoint(IPAddress.Broadcast, BeaconPort));
        }
        catch { /* non-fatal — firewall may block outbound broadcast */ }
    }

    private async Task ListenAsync()
    {
        using var udp = new UdpClient();
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket,
                                       SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
        }
        catch { _listening = false; return; }

        while (!_disposed)
        {
            try
            {
                var result = await udp.ReceiveAsync();
                var json   = Encoding.UTF8.GetString(result.Buffer);
                var msg    = JsonSerializer.Deserialize<HiveBeaconMessage>(json);
                if (msg is { Name.Length: > 0 })
                    OnNodeSeen?.Invoke(msg);
            }
            catch { /* parse error or socket reset */ }
        }
        _listening = false;
    }

    public void Dispose()
    {
        _disposed = true;
        _broadcastTimer?.Dispose();
        _broadcaster?.Dispose();
    }
}

/// <summary>Payload carried in every UDP beacon packet.</summary>
public sealed record HiveBeaconMessage(
    string   Name,
    string   OllamaUrl,
    int      HivePort,
    string[] Models,
    int      VramFreeMb);
