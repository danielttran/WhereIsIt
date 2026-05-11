using System.ComponentModel;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests.ViewModels;

public class StatusBarViewModelTests
{
    [Fact]
    public void DefaultStatusText_IsStarting()
    {
        var vm = new StatusBarViewModel();
        Assert.Equal("Starting...", vm.StatusText);
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

    [Fact]
    public void CountSummaryText_DefaultsToEmpty()
    {
        var vm = new StatusBarViewModel();
        Assert.Equal(string.Empty, vm.CountSummaryText);
    }

    [Fact]
    public void CountSummaryText_PropertyChanged_Fires()
    {
        var vm = new StatusBarViewModel();
        string? changed = null;
        vm.PropertyChanged += (_, e) => changed = e.PropertyName;
        vm.CountSummaryText = "3 results";
        Assert.Equal(nameof(StatusBarViewModel.CountSummaryText), changed);
        Assert.Equal("3 results", vm.CountSummaryText);
    }
}
