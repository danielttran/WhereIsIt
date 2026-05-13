using WhereIsIt.App.Services;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests.ViewModels;

public class SearchBoxViewModelTests
{
    [Fact]
    public void DisplayQuery_PropertyChanged_Fires()
    {
        var vm = new SearchBoxViewModel();
        string? changed = null;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SearchBoxViewModel.DisplayQuery)) changed = e.PropertyName; };
        vm.DisplayQuery = "hello";
        Assert.Equal(nameof(SearchBoxViewModel.DisplayQuery), changed);
    }

    [Fact]
    public void Submit_Command_CanExecute()
    {
        var vm = new SearchBoxViewModel();
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    // ── DisplayQuery vs. Query split ────────────────────────────────────

    [Fact]
    public void Typing_Updates_Query_Without_LeakingFilterIntoDisplay()
    {
        var vm = new SearchBoxViewModel();
        vm.ActiveFilter = "audio:";
        vm.DisplayQuery = "foo";
        // The TextBox-bound DisplayQuery stays clean — Everything-style.
        Assert.Equal("foo", vm.DisplayQuery);
        // The engine-facing Query carries the filter.
        Assert.Equal("foo audio:", vm.Query);
    }

    [Fact]
    public void TogglingModifier_LeavesDisplayQueryClean()
    {
        var vm = new SearchBoxViewModel { DisplayQuery = "foo" };
        vm.MatchCase = true;
        Assert.Equal("foo", vm.DisplayQuery);
        Assert.Equal("case: foo", vm.Query);
    }

    [Fact]
    public void TogglingFilter_AndModifier_TogetherComposesQuery()
    {
        var vm = new SearchBoxViewModel { DisplayQuery = "report" };
        vm.MatchCase   = true;
        vm.ActiveFilter = "doc:";
        Assert.Equal("report", vm.DisplayQuery);
        Assert.Equal("case: report doc:", vm.Query);
    }

    [Fact]
    public void ClearingFilter_RemovesItFromQuery()
    {
        var vm = new SearchBoxViewModel { DisplayQuery = "foo", ActiveFilter = "audio:" };
        vm.ActiveFilter = QueryComposer.AllFilter;
        Assert.Equal("foo", vm.Query);
    }

    // ── SetQueryFromRaw: reverse-sync from external sources ─────────────

    [Fact]
    public void SetQueryFromRaw_DecomposesFlagsFilter_AndCleanText()
    {
        var vm = new SearchBoxViewModel();
        vm.SetQueryFromRaw("case: audio: foo");
        Assert.True(vm.MatchCase);
        Assert.Equal("audio:", vm.ActiveFilter);
        Assert.Equal("foo", vm.DisplayQuery);
        // Recomposed Query must round-trip equivalently (ordering may differ
        // but every component is present).
        Assert.Contains("case:",  vm.Query);
        Assert.Contains("audio:", vm.Query);
        Assert.Contains("foo",    vm.Query);
    }

    [Fact]
    public void SetQueryFromRaw_Empty_ClearsEverything()
    {
        var vm = new SearchBoxViewModel
        {
            DisplayQuery = "foo",
            MatchCase = true,
            ActiveFilter = "audio:",
        };
        vm.SetQueryFromRaw("");
        Assert.Equal("",  vm.DisplayQuery);
        Assert.False(vm.MatchCase);
        Assert.Equal(QueryComposer.AllFilter, vm.ActiveFilter);
        Assert.Equal("",  vm.Query);
    }

    [Fact]
    public void SetQueryFromRaw_RawWithoutTokens_LeavesFlagsCleared()
    {
        var vm = new SearchBoxViewModel { MatchCase = true, ActiveFilter = "audio:" };
        vm.SetQueryFromRaw("plain query");
        Assert.False(vm.MatchCase);
        Assert.Equal(QueryComposer.AllFilter, vm.ActiveFilter);
        Assert.Equal("plain query", vm.DisplayQuery);
    }
}
