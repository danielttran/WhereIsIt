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
}
