using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WhereIsIt.App.ViewModels;

public partial class SearchBoxViewModel : ObservableObject
{
    [ObservableProperty]
    private string query = string.Empty;

    [RelayCommand]
    private void Submit() { }
}
