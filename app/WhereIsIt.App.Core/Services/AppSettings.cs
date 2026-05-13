namespace WhereIsIt.App.Services;

public sealed class AppSettings
{
    public string[] ScopeRoots { get; set; } = [];
    public string[] SearchHistory { get; set; } = [];
    public Bookmark[] Bookmarks { get; set; } = [];
    public string GlobalHotkey { get; set; } = "Ctrl+Alt+W";
    public bool ShowCreatedColumn  { get; set; } = false;
    public bool ShowAccessedColumn { get; set; } = false;
    public bool ShowRunCountColumn { get; set; } = false;
    public System.Collections.Generic.Dictionary<string, int> RunCounts { get; set; } = new();
    public bool EnableHttpServer { get; set; } = false;
    public int  HttpServerPort   { get; set; } = 12321;
}
