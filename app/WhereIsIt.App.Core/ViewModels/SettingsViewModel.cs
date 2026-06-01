using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WhereIsIt.App.Services;

namespace WhereIsIt.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService settingsService;

    [ObservableProperty] private string indexScopeConfig = string.Empty;
    [ObservableProperty] private string globalHotkey = "Ctrl+Alt+W";
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool enableHttpServer;
    [ObservableProperty] private string httpServerPortConfig = "12321";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string? validationMessage;
    [ObservableProperty] private bool saved;

    public SettingsViewModel(AppSettingsService settingsService)
    {
        this.settingsService = settingsService;
        var settings = settingsService.Load();
        IndexScopeConfig = string.Join(", ", settings.ScopeRoots);
        GlobalHotkey = settings.GlobalHotkey;
        StartWithWindows = settings.StartWithWindows;
        EnableHttpServer = settings.EnableHttpServer;
        HttpServerPortConfig = settings.HttpServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    partial void OnIndexScopeConfigChanged(string value) => MarkDirty();
    partial void OnGlobalHotkeyChanged(string value) => MarkDirty();
    partial void OnStartWithWindowsChanged(bool value) => MarkDirty();
    partial void OnEnableHttpServerChanged(bool value) => MarkDirty();
    partial void OnHttpServerPortConfigChanged(string value) => MarkDirty();

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    private void MarkDirty()
    {
        Saved = false;
        ValidationMessage = null;
    }

    [RelayCommand]
    private void Save()
    {
        var hotkey = GlobalHotkey.Trim();
        if (HotkeyBinding.Parse(hotkey) is null)
        {
            ValidationMessage = "Enter a valid hotkey, such as Ctrl+Alt+W.";
            return;
        }
        if (!int.TryParse(HttpServerPortConfig, out var httpServerPort) || httpServerPort is < 1 or > 65535)
        {
            ValidationMessage = "HTTP server port must be between 1 and 65535.";
            return;
        }

        var roots = IndexScopeConfig
            .Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var current = settingsService.Load();
        current.ScopeRoots = roots;
        current.GlobalHotkey = hotkey;
        current.StartWithWindows = StartWithWindows;
        current.EnableHttpServer = EnableHttpServer;
        current.HttpServerPort = httpServerPort;
        settingsService.Save(current);
        ValidationMessage = null;
        Saved = true;
    }
}
