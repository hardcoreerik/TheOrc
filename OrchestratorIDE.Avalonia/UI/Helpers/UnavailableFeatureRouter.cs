// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UI;

internal sealed class UnavailableFeatureRouter
{
    private const string PortPhase = "Phase 4";
    private readonly Action<ActivityEvent> _addActivity;

    public UnavailableFeatureRouter(Action<ActivityEvent> addActivity)
        => _addActivity = addActivity;

    public void Report(string label, string featureName, string? fallback = null, ActivityKind kind = ActivityKind.Info)
    {
        var message = $"{featureName} not yet ported ({PortPhase}).";
        if (!string.IsNullOrWhiteSpace(fallback))
            message += " " + fallback.Trim();

        _addActivity(new ActivityEvent(kind, label, message, DateTime.Now));
    }

    public string BlockAskUser(string question)
    {
        _addActivity(new ActivityEvent(
            ActivityKind.Warning,
            "ask_user",
            $"ask_user blocked (UI dialog not yet ported): {question}",
            DateTime.Now));

        return "[ERROR: ask_user is unavailable in this build — user input dialog not yet ported. Do not proceed with this step.]";
    }
}
