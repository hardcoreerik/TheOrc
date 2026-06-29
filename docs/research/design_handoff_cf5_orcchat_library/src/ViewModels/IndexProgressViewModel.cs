// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UI.ViewModels;

public sealed class IndexProgressViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string DocumentId { get; }
    private IndexStageKind _stage = IndexStageKind.Reading;
    private int _completed;
    private int _total;
    public ObservableCollection<string> FailedSegmentIds { get; } = [];

    public IndexStageKind Stage { get => _stage; private set { _stage = value; Notify(); } }
    public int CompletedSegments { get => _completed; private set { _completed = value; Notify(); } }
    public int TotalSegments { get => _total; private set { _total = value; Notify(); } }
    public double ProgressFraction => TotalSegments == 0 ? 0 : (double)CompletedSegments / TotalSegments;
    public string StageLabel => Stage switch
    {
        IndexStageKind.Parsing   => "Parsing source…",
        IndexStageKind.Reading   => $"Reading evidence cards · {CompletedSegments}/{TotalSegments}",
        IndexStageKind.Reducing  => "Building memory hierarchy…",
        IndexStageKind.Complete  => "Ready",
        IndexStageKind.Failed    => "Failed",
        _ => ""
    };

    public IndexProgressViewModel(string documentId) => DocumentId = documentId;

    public void Apply(IndexStageEvent ev)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Stage = ev.Stage;
            CompletedSegments = ev.CompletedSegments;
            TotalSegments = ev.TotalSegments;
            FailedSegmentIds.Clear();
            foreach (var id in ev.FailedSegmentIds) FailedSegmentIds.Add(id);
        });
    }

    private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}
