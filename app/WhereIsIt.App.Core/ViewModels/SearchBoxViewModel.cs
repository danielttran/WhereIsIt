using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhereIsIt.App.Services;

namespace WhereIsIt.App.ViewModels;

public partial class SearchBoxViewModel : ObservableObject
{
    public SearchHistory History { get; }

    [ObservableProperty]
    private string query = string.Empty;

    // ── Modifier toggles ────────────────────────────────────────────────
    // Bound two-way to ToggleMenuFlyoutItem.IsChecked in the Search menu.
    // Toggling rewrites Query through SearchModifiersComposer; when the user
    // edits Query directly we re-detect the flags via Sync to keep the menu's
    // checkmarks in lockstep with the actual leading tokens.
    [ObservableProperty] private bool matchCase;
    [ObservableProperty] private bool useRegex;
    [ObservableProperty] private bool wholeWord;
    [ObservableProperty] private bool matchPath;

    private bool _suppressSync;

    public SearchBoxViewModel() : this(new SearchHistory()) { }

    public SearchBoxViewModel(SearchHistory history)
    {
        History = history;
    }

    partial void OnQueryChanged(string value)
    {
        if (_suppressSync) return;
        _suppressSync = true;
        try
        {
            var detected = SearchModifiersComposer.Detect(value);
            MatchCase = detected.CaseSensitive;
            UseRegex  = detected.Regex;
            WholeWord = detected.WholeWord;
            MatchPath = detected.MatchPath;
        }
        finally { _suppressSync = false; }
    }

    partial void OnMatchCaseChanged(bool value) => ApplyModifiers();
    partial void OnUseRegexChanged(bool value)  => ApplyModifiers();
    partial void OnWholeWordChanged(bool value) => ApplyModifiers();
    partial void OnMatchPathChanged(bool value) => ApplyModifiers();

    private void ApplyModifiers()
    {
        if (_suppressSync) return;
        _suppressSync = true;
        try
        {
            var mods = new SearchModifiers(MatchCase, UseRegex, WholeWord, MatchPath);
            Query = SearchModifiersComposer.Apply(Query, mods);
        }
        finally { _suppressSync = false; }
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
