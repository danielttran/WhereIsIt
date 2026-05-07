using CommunityToolkit.Mvvm.ComponentModel;

namespace WhereIsIt.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private string indexScopeConfig = string.Empty;
}
