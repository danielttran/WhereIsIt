using System.ComponentModel;
using System.Reactive.Concurrency;
using Microsoft.Reactive.Testing;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests.ViewModels;

public class SearchBoxViewModelTests
{
    [Fact]
    public void Query_PropertyChanged_Fires()
    {
        var vm = new SearchBoxViewModel();
        string? changed = null;
        vm.PropertyChanged += (_, e) => changed = e.PropertyName;
        vm.Query = "hello";
        Assert.Equal(nameof(SearchBoxViewModel.Query), changed);
    }

    [Fact]
    public void Submit_Command_CanExecute()
    {
        var vm = new SearchBoxViewModel();
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    // ── modifier toggles ↔ query sync ───────────────────────────────────

    [Fact]
    public void ToggleMatchCase_InjectsCaseTokenIntoQuery()
    {
        var vm = new SearchBoxViewModel { Query = "foo" };
        vm.MatchCase = true;
        Assert.Equal("case: foo", vm.Query);
    }

    [Fact]
    public void ClearingMatchCase_RemovesCaseToken()
    {
        var vm = new SearchBoxViewModel { Query = "case: foo" };
        // Setting Query already synced MatchCase to true above.
        Assert.True(vm.MatchCase);
        vm.MatchCase = false;
        Assert.Equal("foo", vm.Query);
    }

    [Fact]
    public void EditingQuery_SyncsTogglesAutomatically()
    {
        var vm = new SearchBoxViewModel();
        vm.Query = "regex: word: foo";
        Assert.True(vm.UseRegex);
        Assert.True(vm.WholeWord);
        Assert.False(vm.MatchCase);
        Assert.False(vm.MatchPath);
    }

    [Fact]
    public void TogglingMultipleModifiers_AccumulatesTokensInStableOrder()
    {
        var vm = new SearchBoxViewModel { Query = "foo" };
        vm.MatchCase = true;
        vm.UseRegex  = true;
        Assert.Equal("case: regex: foo", vm.Query);
    }

    [Fact]
    public void ToggleSync_DoesNotLoopOrDoubleApply()
    {
        var vm = new SearchBoxViewModel { Query = "foo" };
        int changes = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SearchBoxViewModel.Query)) changes++; };
        vm.MatchCase = true;          // 1 query change
        vm.MatchCase = false;         // 1 query change
        Assert.Equal(2, changes);
    }
}
