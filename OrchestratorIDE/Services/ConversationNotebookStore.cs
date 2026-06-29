// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// CF-5: per-conversation cited-conclusion store. Mirrors OpenChatMemory pattern.

using System.Text.Json;
using OrchestratorIDE.UI.ViewModels;

namespace OrchestratorIDE.Services;

public sealed record ConversationNotebookEntry(
    string ClaimText,
    IReadOnlyList<CitationViewModel> Citations,
    DateTimeOffset CreatedAt,
    string QueryRunId);

public static class ConversationNotebookStore
{
    private static readonly JsonSerializerOptions _json =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static string StorePath(string workspaceRoot, string conversationId) =>
        Path.Combine(workspaceRoot, ".orc", "chat", $"notebook-{conversationId}.json");

    public static IReadOnlyList<ConversationNotebookEntry> Load(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ConversationNotebookEntry>>(json, _json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Append(string path, ConversationNotebookEntry entry)
    {
        var entries = Load(path).ToList();
        entries.Add(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, _json));
    }

    public static void Clear(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
