using System.Net;
using System.Text;
using System.Text.Json;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// HIVE MIND H1 — embedded HTTP node-info endpoint (port 7078).
///
/// Peers probe <c>GET http://&lt;ip&gt;:7078/hive/info</c> to get this machine's
/// capability JSON (models, VRAM, lanes) before routing tasks to it.
///
/// Uses an in-process HttpListener so no separate service process is needed.
/// Falls back to localhost-only binding if wildcard binding fails (no admin).
/// </summary>
public sealed class HiveNodeServer : IDisposable
{
    public const int ApiPort = 7078;

    private HttpListener?           _listener;
    private HiveNodeInfo            _info = new("", "", [], 0, 0, []);
    private CancellationTokenSource _cts  = new();

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start(HiveNodeInfo info)
    {
        _info = info;

        _listener = new HttpListener();
        var started = TryBind($"http://+:{ApiPort}/hive/");
        if (!started)
            TryBind($"http://localhost:{ApiPort}/hive/");

        if (_listener.IsListening)
            _ = ServeAsync(_cts.Token);
    }

    public void UpdateInfo(HiveNodeInfo info) => _info = info;

    // ── Static probe helper ───────────────────────────────────────────────────

    /// <summary>
    /// Probes <c>GET http://&lt;host&gt;:7078/hive/info</c>.
    /// Returns null if the endpoint is not reachable (not a HIVE MIND node).
    /// </summary>
    public static async Task<HiveNodeInfo?> ProbeAsync(
        string host, int timeoutMs = 2000,
        CancellationToken ct = default)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient
                { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            var url  = $"http://{host.TrimEnd('/')}:{ApiPort}/hive/info";
            var json = await http.GetStringAsync(url, ct);
            return JsonSerializer.Deserialize<HiveNodeInfo>(json);
        }
        catch { return null; }
    }

    // ── Serve loop ────────────────────────────────────────────────────────────

    private bool TryBind(string prefix)
    {
        try
        {
            _listener!.Prefixes.Add(prefix);
            _listener.Start();
            return true;
        }
        catch
        {
            _listener!.Prefixes.Clear();
            return false;
        }
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = HandleAsync(ctx);
            }
            catch { break; }
        }
    }

    private Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var json  = JsonSerializer.Serialize(_info);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType     = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
        }
        catch { }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
    }
}

/// <summary>Capability JSON returned by GET /hive/info.</summary>
public sealed record HiveNodeInfo(
    string   Name,
    string   OllamaUrl,
    string[] Models,
    int      VramFreeMb,
    int      VramTotalMb,
    string[] Lanes,
    int      RpcPort = 0);    // 0 = RPC not available; 50052 = llama-rpc-server running
