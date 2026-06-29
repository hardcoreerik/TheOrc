// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// CF-5: Observable wrapper over FabricLibraryService for the Library drawer.
// Drop into OrchestratorIDE.Avalonia/UI/ViewModels/ (create folder if needed)

using System.Collections.ObjectModel;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UI.ViewModels;

/// <summary>
/// Thin observable wrapper over FabricLibraryService.
/// Follows the same "no-MVVM-framework" pattern as the rest of the Avalonia UI:
/// raise PropertyChanged manually; bind directly in code-behind.
/// </summary>
public sealed class LibraryViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private readonly FabricLibraryService _library;

    public ObservableCollection<CorpusCardViewModel> Corpora { get; } = [];
    public ObservableCollection<FabricSearchHit> SearchResults { get; } = [];

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value) return;
            _searchQuery = value;
            OnPropertyChanged();
            Dispatcher.UIThread.Post(RefreshSearch);
        }
    }

    private string _storagePath = "";
    public string StoragePath
    {
        get => _storagePath;
        set { _storagePath = value; OnPropertyChanged(); }
    }

    public LibraryViewModel(FabricLibraryService library, string storagePath)
    {
        _library = library;
        StoragePath = storagePath;
        Refresh();
    }

    public void Refresh()
    {
        Corpora.Clear();
        foreach (var corpus in _library.ListCorpora())
        {
            var docs = _library.ListDocuments(corpus.CorpusId);
            Corpora.Add(new CorpusCardViewModel(corpus, docs));
        }
    }

    public FabricCorpusEntry CreateCorpus(string name, string? description = null) =>
        _library.CreateCorpus(name, description);

    public async Task<FabricImportResult> ImportFileAsync(
        string corpusId, string path, CancellationToken ct = default)
    {
        var result = await _library.ImportFileAsync(corpusId, path, ct: ct).ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        return result;
    }

    public bool DeleteCorpus(string corpusId)
    {
        var ok = _library.DeleteCorpus(corpusId);
        if (ok) Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        return ok;
    }

    private void RefreshSearch()
    {
        SearchResults.Clear();
        if (string.IsNullOrWhiteSpace(_searchQuery)) return;
        foreach (var hit in _library.Search(_searchQuery, limit: 20))
            SearchResults.Add(hit);
    }

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public sealed class CorpusCardViewModel(
    FabricCorpusEntry corpus,
    IReadOnlyList<FabricDocumentEntry> documents)
{
    public string CorpusId => corpus.CorpusId;
    public string Name => corpus.Name;
    public string Description => corpus.Description ?? "";
    public string Status => corpus.Status;   // ready | indexing | stale | failed
    public IReadOnlyList<FabricDocumentEntry> Documents => documents;
    public int TotalSegments => documents.Sum(d => 0); // TODO: sum from repo
    public bool IsAttached { get; set; }
}
