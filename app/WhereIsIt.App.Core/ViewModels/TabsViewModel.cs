using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WhereIsIt.App.ViewModels;

/// <summary>
/// One open search tab. The engine itself remains single-search at a time;
/// selecting a tab re-runs its query against the shared engine.
/// </summary>
public partial class TabRecord : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string query = string.Empty;

    public string Title => string.IsNullOrWhiteSpace(Query) ? "(new)" : Query;
}

/// <summary>
/// Owns the open tab list and the currently-selected tab. The Main view-model
/// listens for CurrentTab changes and re-issues the search against the shared
/// engine.
/// </summary>
public partial class TabsViewModel : ObservableObject
{
    public ObservableCollection<TabRecord> Tabs { get; } = new();

    [ObservableProperty] private TabRecord? currentTab;

    public TabsViewModel()
    {
        var first = new TabRecord();
        Tabs.Add(first);
        CurrentTab = first;
    }

    public TabRecord AddTab(string query = "")
    {
        var t = new TabRecord { Query = query };
        Tabs.Add(t);
        CurrentTab = t;
        return t;
    }

    public void CloseTab(TabRecord tab)
    {
        if (tab is null || Tabs.Count <= 1) return;
        var idx = Tabs.IndexOf(tab);
        if (idx < 0) return;
        var wasCurrent = CurrentTab == tab;
        Tabs.RemoveAt(idx);
        if (wasCurrent)
        {
            var next = idx < Tabs.Count ? Tabs[idx] : Tabs[Tabs.Count - 1];
            CurrentTab = next;
        }
    }
}
