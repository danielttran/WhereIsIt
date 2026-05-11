using CommunityToolkit.Mvvm.ComponentModel;

namespace WhereIsIt.App.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty] private string statusText = "Starting...";
    [ObservableProperty] private int recordCount;
    [ObservableProperty] private string countSummaryText = string.Empty;
}
