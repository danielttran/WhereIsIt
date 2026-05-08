using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using WhereIsIt.App.Contracts;

namespace WhereIsIt.App.ViewModels;

public partial class ResultRowViewModel : ObservableObject
{
    private readonly IEngineClient engineClient;
    private readonly uint id;
    private bool loaded;

    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string parentPath = string.Empty;
    [ObservableProperty] private string sizeText = string.Empty;
    [ObservableProperty] private string modifiedText = string.Empty;
    [ObservableProperty] private string attributesText = string.Empty;

    public ResultRowViewModel(IEngineClient engineClient, uint id)
    {
        this.engineClient = engineClient;
        this.id = id;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (loaded) return;
        var row = await engineClient.GetRowAsync(id, cancellationToken).ConfigureAwait(false);
        Name = row.Name;
        ParentPath = row.ParentPath;
        SizeText = FormatBytes(row.SizeBytes);
        ModifiedText = row.ModifiedUtc.ToString("u", CultureInfo.InvariantCulture);
        AttributesText = row.Attributes;
        loaded = true;
    }

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
