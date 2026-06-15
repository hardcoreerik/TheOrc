// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Text.Json;
using BenchmarkRunner.Models;

namespace BenchmarkRunner.Core;

public static class RunHistory
{
    private static readonly string HistoryPath =
        Path.Combine(AppContext.BaseDirectory, "run_history.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<TestRun> Load()
    {
        if (!File.Exists(HistoryPath)) return [];
        try
        {
            var json = File.ReadAllText(HistoryPath);
            return JsonSerializer.Deserialize<List<TestRun>>(json, Opts) ?? [];
        }
        catch { return []; }
    }

    public static void Save(List<TestRun> runs)
    {
        try { File.WriteAllText(HistoryPath, JsonSerializer.Serialize(runs, Opts)); }
        catch { /* non-fatal */ }
    }

    public static void AddOrUpdate(List<TestRun> runs, TestRun run)
    {
        var existing = runs.FindIndex(r => r.Id == run.Id);
        if (existing >= 0) runs[existing] = run;
        else runs.Insert(0, run);
        Save(runs);
    }
}
