using System.Text.Json;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Trust;

/// <summary>
/// Persists ProjectSession to disk as JSON.
/// Auto-save after every message. Crash recovery on startup.
/// Sessions stored in: %APPDATA%\OrchestratorIDE\sessions\
/// </summary>
public class SessionStore
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "sessions");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SessionStore() => Directory.CreateDirectory(_dir);

    public async Task SaveAsync(ProjectSession session)
    {
        var path = GetPath(session.Id);
        var json = JsonSerializer.Serialize(session, _json);
        await File.WriteAllTextAsync(path, json);
    }

    public async Task<ProjectSession?> LoadAsync(Guid id)
    {
        var path = GetPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<ProjectSession>(json, _json);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns the most recently modified session (for crash recovery).
    /// </summary>
    public async Task<ProjectSession?> LoadLatestAsync()
    {
        var files = Directory.GetFiles(_dir, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(1).ToList();

        if (files.Count == 0) return null;
        var id = Guid.Parse(Path.GetFileNameWithoutExtension(files[0]));
        return await LoadAsync(id);
    }

    public List<(Guid Id, DateTime Modified, string WorkspaceRoot)> ListSessions()
    {
        return Directory.GetFiles(_dir, "*.json")
            .Select(f =>
            {
                var id = Guid.Parse(Path.GetFileNameWithoutExtension(f));
                var modified = File.GetLastWriteTimeUtc(f).ToLocalTime();
                // Peek at workspace root without full deserialize
                string root = "";
                try
                {
                    var text = File.ReadAllText(f);
                    var doc = JsonDocument.Parse(text);
                    root = doc.RootElement.GetProperty("workspaceRoot").GetString() ?? "";
                }
                catch { }
                return (id, modified, root);
            })
            .OrderByDescending(t => t.modified)
            .ToList();
    }

    private string GetPath(Guid id) => Path.Combine(_dir, $"{id}.json");
}
