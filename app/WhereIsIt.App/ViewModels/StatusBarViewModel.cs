using CommunityToolkit.Mvvm.ComponentModel;

namespace WhereIsIt.App.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private int recordCount;
}
