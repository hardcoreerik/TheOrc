using System.IO;
using System.Text.Json;

namespace OrchestratorIDE.Agents;

/// <summary>
/// Writes a JSONL trace file compatible with the Trace Field Notes analysis tool
/// (https://huggingface.co/spaces/build-small-hackathon/trace-field-notes).
///
/// Format per line:
///   {"timestamp":"...","type":"session_meta"|"response_item","payload":{...}}
///
/// One file per swarm run at: [workspaceRoot]/.orc/swarm/trace.jsonl
/// Thread-safe — workers write concurrently.
/// </summary>
public sealed class SwarmTraceLogger : IDisposable
{
    private readonly string _path;
    private readonly object _lock = new();
    private bool _disposed;

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented            = false,
        DefaultIgnoreCondition   = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public SwarmTraceLogger(string swarmDir)
    {
        Directory.CreateDirectory(swarmDir);
        _path = Path.Combine(swarmDir, "trace.jsonl");
        // Clear any previous run trace
        if (File.Exists(_path)) File.Delete(_path);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Write the opening session_meta record.</summary>
    public void WriteSessionMeta(string workspaceRoot, string bossModel,
        string coderModel, string researcherModel, string goal)
    {
        Write(new
        {
            timestamp = Now(),
            type      = "session_meta",
            payload   = new
            {
                originator       = "theorc_swarm",
                cwd              = workspaceRoot,
                boss_model       = bossModel,
                coder_model      = coderModel,
                researcher_model = researcherModel,
                goal,
            }
        });
    }

    /// <summary>Write a user-side message (task assignment handed to a worker).</summary>
    public void WriteUserMessage(string text, string? context = null)
    {
        Write(new
        {
            timestamp = Now(),
            type      = "response_item",
            payload   = new
            {
                type    = "message",
                role    = "user",
                context,
                content = new[] { new { type = "output_text", text } }
            }
        });
    }

    /// <summary>Write an assistant message (boss or worker output).</summary>
    public void WriteAssistantMessage(string text, string agent, string? model = null)
    {
        Write(new
        {
            timestamp = Now(),
            type      = "response_item",
            payload   = new
            {
                type    = "message",
                role    = "assistant",
                agent,
                model,
                content = new[] { new { type = "output_text", text } }
            }
        });
    }

    /// <summary>Write a lightweight event marker (phase transitions, errors).</summary>
    public void WriteEvent(string eventType, string description)
    {
        Write(new
        {
            timestamp = Now(),
            type      = "event_msg",
            payload   = new
            {
                event_type  = eventType,
                description,
            }
        });
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void Write(object record)
    {
        if (_disposed) return;
        try
        {
            var line = JsonSerializer.Serialize(record, _json);
            lock (_lock)
            {
                File.AppendAllText(_path, line + "\n");
            }
        }
        catch { /* non-fatal — trace is optional */ }
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public void Dispose() => _disposed = true;
}
