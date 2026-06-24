// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace OrchestratorIDE.Services;

/// <summary>
/// Persists Open Chat's system-prompt text across app restarts -- the actual fix for "the
/// model doesn't remember its name/persona across sessions." A system prompt is the only
/// thing Open mode injects at all (see ChatEngine), so making IT durable is the simplest
/// way to give a user-controlled fact (a chosen name, a persona, a standing instruction)
/// real persistence without needing autonomous model-driven memory writes -- Open mode
/// deliberately has no tools, so the model itself has no way to write to a memory store;
/// this is the user explicitly choosing what should carry over, not the model deciding.
/// Same Load/Save-to-a-small-JSON-file pattern as HiveHosts.
/// </summary>
public static class OpenChatMemory
{
    public static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "open-chat-memory.json");

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private sealed class Data
    {
        public string SystemPrompt { get; set; } = "";
    }

    /// <summary>Returns the persisted system prompt, or "" if none saved yet / load failed.</summary>
    public static string LoadSystemPrompt(string? storePath = null)
    {
        storePath ??= StorePath;
        try
        {
            if (!File.Exists(storePath)) return "";
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(storePath), _json);
            return data?.SystemPrompt ?? "";
        }
        catch { return ""; }   // corrupt file, permissions, race with a concurrent write -- fail closed, same as HiveHosts
    }

    /// <summary>Non-fatal on failure (disk full, permissions) -- same as AppSettings.Save,
    /// a failed save here should never block the user from continuing to chat.</summary>
    public static void SaveSystemPrompt(string systemPrompt, string? storePath = null)
    {
        storePath ??= StorePath;
        try
        {
            var dir = Path.GetDirectoryName(storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(storePath, JsonSerializer.Serialize(new Data { SystemPrompt = systemPrompt }, _json));
        }
        catch { /* non-fatal */ }
    }
}
