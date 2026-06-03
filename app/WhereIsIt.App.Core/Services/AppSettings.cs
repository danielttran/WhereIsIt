namespace WhereIsIt.App.Services;

public sealed class AppSettings
{
    public string[] ScopeRoots { get; set; } = [];
    public string[] SearchHistory { get; set; } = [];
    public Bookmark[] Bookmarks { get; set; } = [];
    public string GlobalHotkey { get; set; } = "Ctrl+Alt+W";
    public bool StartWithWindows { get; set; } = false;
    public bool ShellContextMenu { get; set; } = false;
    public bool ShowCreatedColumn  { get; set; } = false;
    public bool ShowAccessedColumn { get; set; } = false;
    public bool ShowRunCountColumn { get; set; } = false;
    public bool ShowPreviewPane { get; set; } = false;
    public bool ShowDimensionsColumn { get; set; } = false;
    public bool ShowArtistColumn { get; set; } = false;
    public bool ShowAlbumColumn { get; set; } = false;
    public bool ShowAuthorColumn { get; set; } = false;
    public System.Collections.Generic.Dictionary<string, int> RunCounts { get; set; } = new();
    /// <summary>Last-run timestamps (UTC ticks) per full path, for the <c>dr:</c>
    /// run-date filter. Parallel to <see cref="RunCounts"/>.</summary>
    public System.Collections.Generic.Dictionary<string, long> RunDates { get; set; } = new();
    public bool EnableHttpServer { get; set; } = false;
    public int  HttpServerPort   { get; set; } = 12321;
    public bool EnableFtpServer  { get; set; } = false;
    public int  FtpServerPort    { get; set; } = 12322;

    /// <summary>Tab queries from the last session — empty if there was just one
    /// blank tab. Used by the Chrome-style "Restore previous tabs?" prompt.</summary>
    public string[] LastSessionTabs { get; set; } = [];

    /// <summary>0 = Off, 32 = Small, 64 = Medium, 128 = Large, 256 = Extra Large.</summary>
    public int ThumbnailSizePx { get; set; } = (int)ThumbnailSize.Off;
}
