using System;
using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.ViewModels;

public partial class ResultsListViewModel : ObservableObject
{
    public const int DisplayCap = 2000;
    public const int EagerLoadCount = 50;

    private readonly IEngineClient engineClient;

    // Row view-models are expensive to allocate on every keystroke (DisplayCap
    // = 2000). Cache by record-id so subsequent BindResults reuses the
    // existing instance — same id always describes the same record while the
    // engine is alive. Bounded so the cache doesn't grow without limit; on
    // overflow the oldest entries are evicted.
    private const int RowCacheCap = 8_000;
    private readonly Dictionary<uint, ResultRowViewModel> rowCache = new();
    private readonly Queue<uint> rowCacheOrder = new();

    // Replaced as a unit so the ListView refreshes in one pass (no per-item animations).
    [ObservableProperty] private IReadOnlyList<ResultRowViewModel> rows = [];

    [ObservableProperty] private ResultRowViewModel? selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortIndicator), nameof(PathSortIndicator),
                              nameof(SizeSortIndicator), nameof(ModifiedSortIndicator),
                              nameof(CreatedSortIndicator), nameof(AccessedSortIndicator),
                              nameof(ExtensionSortIndicator), nameof(AttributesSortIndicator))]
    private string sortKey = "name";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortIndicator), nameof(PathSortIndicator),
                              nameof(SizeSortIndicator), nameof(ModifiedSortIndicator),
                              nameof(CreatedSortIndicator), nameof(AccessedSortIndicator),
                              nameof(ExtensionSortIndicator), nameof(AttributesSortIndicator))]
    private bool sortDescending;

    [ObservableProperty] private int totalResultCount;

    public string NameSortIndicator => SortKey == "name" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string PathSortIndicator => SortKey == "path" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string SizeSortIndicator => SortKey == "size" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string ModifiedSortIndicator => SortKey == "modified" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string CreatedSortIndicator => SortKey == "created" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string AccessedSortIndicator => SortKey == "accessed" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string ExtensionSortIndicator => SortKey is "extension" or "type" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string AttributesSortIndicator => SortKey == "attributes" ? (SortDescending ? " ▼" : " ▲") : string.Empty;

    public ResultsListViewModel(IEngineClient engineClient)
    {
        this.engineClient = engineClient;
    }

    public void BindResults(IReadOnlyList<uint> ids)
    {
        var display = Math.Min(ids.Count, DisplayCap);
        var list = new List<ResultRowViewModel>(display);
        for (int i = 0; i < display; i++)
        {
            var id = ids[i];
            if (!rowCache.TryGetValue(id, out var row))
            {
                row = new ResultRowViewModel(engineClient, id);
                rowCache[id] = row;
                rowCacheOrder.Enqueue(id);
                while (rowCacheOrder.Count > RowCacheCap)
                {
                    var evict = rowCacheOrder.Dequeue();
                    // If the evicted id is on-screen in *this* BindResults, the
                    // strong reference from `list` keeps the VM alive — we can
                    // still drop it from the opportunistic cache, since the
                    // next BindResults that needs it will just re-create the VM.
                    rowCache.Remove(evict);
                }
            }
            if (i < EagerLoadCount)
                _ = row.EnsureLoadedAsync(CancellationToken.None);
            list.Add(row);
        }
        TotalResultCount = ids.Count;
        Rows = list;
    }

    partial void OnSortKeyChanged(string value)
    {
        _ = engineClient.SortAsync(value, SortDescending, CancellationToken.None);
    }

    [RelayCommand]
    private void SortBy(string key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
            _ = engineClient.SortAsync(key, SortDescending, CancellationToken.None);
        }
        else
        {
            SortDescending = false;
            SortKey = key;
        }
    }
}
