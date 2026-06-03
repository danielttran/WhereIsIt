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

    private readonly IEngineClient engineClient;
    private readonly uint id;
    private bool loaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExtensionText))]
    private string name = string.Empty;
    [ObservableProperty] private string parentPath = string.Empty;
    [ObservableProperty] private string sizeText = string.Empty;
    [ObservableProperty] private string modifiedText = string.Empty;
    [ObservableProperty] private string createdText = string.Empty;
    [ObservableProperty] private string accessedText = string.Empty;
    [ObservableProperty] private string attributesText = string.Empty;
    [ObservableProperty] private int runCount;

    // Optional metadata-property columns, filled lazily by the App layer (which
    // reads the file header off the UI thread) only when the column is shown.
    [ObservableProperty] private string dimensionsText = string.Empty;
    [ObservableProperty] private string artistText = string.Empty;
    [ObservableProperty] private string albumText = string.Empty;
    [ObservableProperty] private string authorText = string.Empty;

    // Newline-separated literal terms the result list should highlight in the
    // Name column. Shared across all rows of the current search; set by
    // ResultsListViewModel.BindResults so the highlighter attached property in
    // the App layer can mark up matched spans without this assembly touching WinUI.
    [ObservableProperty] private string highlightTerms = string.Empty;

    // Loosely typed as object so this assembly doesn't need to reference
    // WinUI. At runtime it's a Microsoft.UI.Xaml.Media.ImageSource set by
    // the App's ThumbnailService; the Image's Source property accepts it
    // because Image.Source is typed ImageSource and runtime cast succeeds.
    [ObservableProperty] private object? thumbnailSource;

    // Per-row cancellation handle, refreshed when the ListView container is
    // realized and tripped when it recycles. Without this, a fast scroll
    // would pile up dozens of in-flight StorageFile.GetThumbnailAsync tasks
    // for rows the user already scrolled past.
    private System.Threading.CancellationTokenSource? thumbnailCts;
    public System.Threading.CancellationToken BeginThumbnailLoad()
    {
        CancelThumbnail();
        var cts = new System.Threading.CancellationTokenSource();
        thumbnailCts = cts;
        return cts.Token;
    }
    public void CancelThumbnail()
    {
        var prior = System.Threading.Interlocked.Exchange(ref thumbnailCts, null);
        if (prior is null) return;
        try { prior.Cancel(); } catch { }
        prior.Dispose();
    }

    public ulong SizeBytes { get; private set; }
    public DateTimeOffset ModifiedUtc { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset AccessedUtc { get; private set; }

    public string FullPath =>
        string.IsNullOrEmpty(ParentPath) ? Name : Path.Combine(ParentPath, Name);

    public string ExtensionText => Path.GetExtension(Name).TrimStart('.');

    public ResultRowModel ToModel() => new(Name, ParentPath, SizeBytes, ModifiedUtc, AttributesText)
    {
        CreatedUtc = this.CreatedUtc,
        AccessedUtc = this.AccessedUtc,
    };

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
        CreatedUtc = row.CreatedUtc;
        AccessedUtc = row.AccessedUtc;
        CreatedText  = FormatOptionalDate(CreatedUtc);
        AccessedText = FormatOptionalDate(AccessedUtc);
        AttributesText = row.Attributes;
        loaded = true;
        OnPropertyChanged(nameof(FullPath));
    }

    public static string FormatOptionalDate(DateTimeOffset d)
        => d == default ? "—" : d.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    // Hoisted out of FormatBytes so the array isn't re-allocated per call.
    // FormatBytes is called for every row (DisplayCap = 2000) — the original
    // collection-expression-in-method-body allocated a fresh string[] each time.
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatBytes(ulong bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        var idx = 0;
        while (value >= 1024 && idx < SizeUnits.Length - 1) { value /= 1024; idx++; }
        return $"{value:0.##} {SizeUnits[idx]}";
    }
}
