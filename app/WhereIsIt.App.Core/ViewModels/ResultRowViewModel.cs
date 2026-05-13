using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.ViewModels;

public partial class ResultRowViewModel : ObservableObject
{
    // Plain-bool toggles for column visibility — set at bootstrap from
    // AppSettings; the App-project's static `ColumnSettings` class projects
    // these into WinUI GridLength/Visibility values for OneTime XAML binding.
    public static bool ShowCreatedColumn  { get; set; }
    public static bool ShowAccessedColumn { get; set; }
    public static bool ShowRunCountColumn { get; set; }

    private readonly IEngineClient engineClient;
    private readonly uint id;
    private bool loaded;

    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string parentPath = string.Empty;
    [ObservableProperty] private string sizeText = string.Empty;
    [ObservableProperty] private string modifiedText = string.Empty;
    [ObservableProperty] private string createdText = string.Empty;
    [ObservableProperty] private string accessedText = string.Empty;
    [ObservableProperty] private string attributesText = string.Empty;
    [ObservableProperty] private int runCount;

    public ulong SizeBytes { get; private set; }
    public DateTimeOffset ModifiedUtc { get; private set; }

    public string FullPath =>
        string.IsNullOrEmpty(ParentPath) ? Name : Path.Combine(ParentPath, Name);

    public ResultRowModel ToModel() => new(Name, ParentPath, SizeBytes, ModifiedUtc, AttributesText);

    public ResultRowViewModel(IEngineClient engineClient, uint id)
    {
        this.engineClient = engineClient;
        this.id = id;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (loaded) return;
        var row = await engineClient.GetRowAsync(id, cancellationToken);
        Name = row.Name;
        ParentPath = row.ParentPath;
        SizeBytes = row.SizeBytes;
        SizeText = FormatBytes(row.SizeBytes);
        ModifiedUtc = row.ModifiedUtc;
        ModifiedText = row.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        CreatedText  = FormatOptionalDate(row.CreatedUtc);
        AccessedText = FormatOptionalDate(row.AccessedUtc);
        AttributesText = row.Attributes;
        loaded = true;
        OnPropertyChanged(nameof(FullPath));
    }

    public static string FormatOptionalDate(DateTimeOffset d)
        => d == default ? "—" : d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string FormatBytes(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var idx = 0;
        while (value >= 1024 && idx < units.Length - 1) { value /= 1024; idx++; }
        return $"{value:0.##} {units[idx]}";
    }
}
