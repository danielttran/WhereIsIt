using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhereIsIt.App.Services;

namespace WhereIsIt.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService settingsService;

    [ObservableProperty] private string indexScopeConfig = string.Empty;
    [ObservableProperty] private bool saved;

    public SettingsViewModel(AppSettingsService settingsService)
    {
        this.settingsService = settingsService;
        var settings = settingsService.Load();
        IndexScopeConfig = string.Join(", ", settings.ScopeRoots);
    }

    partial void OnIndexScopeConfigChanged(string value) => Saved = false;

    [RelayCommand]
    private void Save()
    {
        var roots = IndexScopeConfig
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();

        settingsService.Save(new AppSettings { ScopeRoots = roots });
        Saved = true;
    }
}
