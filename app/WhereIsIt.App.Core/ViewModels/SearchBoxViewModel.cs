using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhereIsIt.App.Services;

namespace WhereIsIt.App.ViewModels;

public partial class SearchBoxViewModel : ObservableObject
{
    public SearchHistory History { get; }

    // What the user sees in the TextBox — never includes the leading
    // case:/regex:/word:/path:/audio:/video:/doc:/... tokens. Matches Everything's
    // UX: the active filter and modifier flags live in the menu, not in the text.
    [ObservableProperty] private string displayQuery = string.Empty;

    // Modifier toggles bound two-way to Search-menu ToggleMenuFlyoutItem checkmarks.
    [ObservableProperty] private bool matchCase;
    [ObservableProperty] private bool useRegex;
    [ObservableProperty] private bool wholeWord;
    [ObservableProperty] private bool matchPath;

    // The currently-selected quick filter (Everything / Audio / Video / ...).
    [ObservableProperty] private string activeFilter = QueryComposer.AllFilter;

    // Engine-facing composed query. Recomputed automatically whenever any of
    // the inputs above changes. External callers (bookmarks, tab restore,
    // CLI arg, search-history recall) write through SetQueryFromRaw to keep
    // every input consistent in one pass.
    [ObservableProperty] private string query = string.Empty;

    private bool _suppressRecompose;

    public SearchBoxViewModel() : this(new SearchHistory()) { }

    public SearchBoxViewModel(SearchHistory history) { History = history; }

    /// <summary>
    /// Replaces all inputs from a raw query string (the engine-facing form).
    /// Splits the string into its modifier flags, active type filter, and
    /// remaining text — so a bookmark like "case: audio: foo" restores
    /// MatchCase=true, ActiveFilter="audio:", DisplayQuery="foo".
    /// </summary>
    public void SetQueryFromRaw(string raw)
    {
        raw ??= string.Empty;
        _suppressRecompose = true;
        try
        {
            var mods = SearchModifiersComposer.Detect(raw);
            var withoutMods = SearchModifiersComposer.StripModifiers(raw);
            ActiveFilter = QueryComposer.ActiveFilter(withoutMods);
            DisplayQuery = QueryComposer.ApplyFilter(withoutMods, QueryComposer.AllFilter);
            MatchCase = mods.CaseSensitive;
            UseRegex  = mods.Regex;
            WholeWord = mods.WholeWord;
            MatchPath = mods.MatchPath;
        }
        finally { _suppressRecompose = false; }
        Recompose();
    }

    private void Recompose()
    {
        if (_suppressRecompose) return;
        var withFilter = QueryComposer.ApplyFilter(DisplayQuery, ActiveFilter);
        var mods = new SearchModifiers(MatchCase, UseRegex, WholeWord, MatchPath);
        Query = SearchModifiersComposer.Apply(withFilter, mods);
    }

    partial void OnDisplayQueryChanged(string value) => Recompose();
    partial void OnMatchCaseChanged(bool value)     => Recompose();
    partial void OnUseRegexChanged(bool value)      => Recompose();
    partial void OnWholeWordChanged(bool value)     => Recompose();
    partial void OnMatchPathChanged(bool value)     => Recompose();
    partial void OnActiveFilterChanged(string value) => Recompose();

    /// <summary>Commits the current query into the history MRU list.</summary>
    [RelayCommand]
    public void Submit() => History.Add(Query);

    public void RecallPrev()
    {
        var prev = History.RecallPrev();
        if (prev is not null) SetQueryFromRaw(prev);
    }

    public void RecallNext()
    {
        var next = History.RecallNext();
        SetQueryFromRaw(next ?? string.Empty);
    }
}
