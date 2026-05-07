using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.ViewModels;

public partial class ResultsListViewModel : ObservableObject
{
    private readonly IEngineClient engineClient;

    public ObservableCollection<ResultRowViewModel> Rows { get; } = [];

    [ObservableProperty] private ResultRowViewModel? selectedRow;
    [ObservableProperty] private string sortKey = "name";
    [ObservableProperty] private bool sortDescending;

    public ResultsListViewModel(IEngineClient engineClient)
    {
        this.engineClient = engineClient;
    }

    public void BindResults(IReadOnlyList<uint> ids)
    {
        Rows.Clear();
        foreach (var id in ids) Rows.Add(new ResultRowViewModel(engineClient, id));
    }

    partial void OnSortKeyChanged(string value)
    {
        _ = engineClient.SortAsync(value, SortDescending, CancellationToken.None);
    }
}
