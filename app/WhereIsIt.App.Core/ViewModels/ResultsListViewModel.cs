using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.ViewModels;

public partial class ResultsListViewModel : ObservableObject
{
    public const int DisplayCap = 2000;

    private readonly IEngineClient engineClient;

    public ObservableCollection<ResultRowViewModel> Rows { get; } = [];

    [ObservableProperty] private ResultRowViewModel? selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortIndicator), nameof(PathSortIndicator),
                              nameof(SizeSortIndicator), nameof(ModifiedSortIndicator))]
    private string sortKey = "name";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameSortIndicator), nameof(PathSortIndicator),
                              nameof(SizeSortIndicator), nameof(ModifiedSortIndicator))]
    private bool sortDescending;

    [ObservableProperty] private int totalResultCount;

    public string NameSortIndicator => SortKey == "name" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string PathSortIndicator => SortKey == "path" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string SizeSortIndicator => SortKey == "size" ? (SortDescending ? " ▼" : " ▲") : string.Empty;
    public string ModifiedSortIndicator => SortKey == "modified" ? (SortDescending ? " ▼" : " ▲") : string.Empty;

    public ResultsListViewModel(IEngineClient engineClient)
    {
        this.engineClient = engineClient;
    }

    public void BindResults(IReadOnlyList<uint> ids)
    {
        TotalResultCount = ids.Count;
        Rows.Clear();
        var display = Math.Min(ids.Count, DisplayCap);
        for (int i = 0; i < display; i++)
        {
            var row = new ResultRowViewModel(engineClient, ids[i]);
            _ = row.EnsureLoadedAsync(CancellationToken.None);
            Rows.Add(row);
        }
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
