using System.ComponentModel;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests.ViewModels;

public class StatusBarViewModelTests
{
    [Fact]
    public void DefaultStatusText_IsReady()
    {
        var vm = new StatusBarViewModel();
        Assert.Equal("Ready", vm.StatusText);
    }

    [Fact]
    public void StatusText_PropertyChanged_Fires()
    {
        var vm = new StatusBarViewModel();
        string? changed = null;
        vm.PropertyChanged += (_, e) => changed = e.PropertyName;
        vm.StatusText = "Searching...";
        Assert.Equal(nameof(StatusBarViewModel.StatusText), changed);
        Assert.Equal("Searching...", vm.StatusText);
    }

    [Fact]
    public void RecordCount_PropertyChanged_Fires()
    {
        var vm = new StatusBarViewModel();
        string? changed = null;
        vm.PropertyChanged += (_, e) => changed = e.PropertyName;
        vm.RecordCount = 42;
        Assert.Equal(nameof(StatusBarViewModel.RecordCount), changed);
        Assert.Equal(42, vm.RecordCount);
    }
}
