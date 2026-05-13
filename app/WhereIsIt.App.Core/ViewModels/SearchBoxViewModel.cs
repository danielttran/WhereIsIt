using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhereIsIt.App.Services;

namespace WhereIsIt.App.ViewModels;

public partial class SearchBoxViewModel : ObservableObject
{
    public SearchHistory History { get; }

    [ObservableProperty]
    private string query = string.Empty;

    public SearchBoxViewModel() : this(new SearchHistory()) { }

    public SearchBoxViewModel(SearchHistory history)
    {
        History = history;
    }

    /// <summary>Commits the current query into the history MRU list.</summary>
    [RelayCommand]
    public void Submit() => History.Add(Query);

    /// <summary>Replaces the current query with the previous history entry, if any.</summary>
    public void RecallPrev()
    {
        var prev = History.RecallPrev();
        if (prev is not null) Query = prev;
    }

    /// <summary>Replaces the current query with the next history entry (newer direction).</summary>
    public void RecallNext()
    {
        var next = History.RecallNext();
        Query = next ?? string.Empty;
    }
}
