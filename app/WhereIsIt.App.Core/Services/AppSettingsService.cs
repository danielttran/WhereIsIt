using System;
using System.IO;
using System.Text.Json;

namespace WhereIsIt.App.Services;

public sealed class AppSettingsService
{
    private readonly string settingsPath;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettingsService(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhereIsIt", "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(settingsPath)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            // Corrupt JSON would silently wipe bookmarks/history/run-counts/
            // scope-roots if we just returned defaults — move the bad file
            // aside so the user (or a later schema-migrating Load) can recover.
            TryBackupCorruptSettings();
            return new AppSettings();
        }
    }

    private void TryBackupCorruptSettings()
    {
        try
        {
            var bak = settingsPath + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(settingsPath, bak);
        }
        catch { /* best-effort; never throw out of Load */ }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, JsonOpts));
    }

    /// <summary>
    /// Persists just the search-history field, leaving other settings (scope roots
    /// in particular) untouched. Safe to call after every history-modifying event.
    /// </summary>
    public void SaveSearchHistory(string[] history)
    {
        var current = Load();
        current.SearchHistory = history;
        Save(current);
    }

    public void SaveBookmarks(Bookmark[] bookmarks)
    {
        var current = Load();
        current.Bookmarks = bookmarks;
        Save(current);
    }

    public void SaveColumnVisibility(bool showCreated, bool showAccessed, bool showRunCount)
    {
        var current = Load();
        current.ShowCreatedColumn  = showCreated;
        current.ShowAccessedColumn = showAccessed;
        current.ShowRunCountColumn = showRunCount;
        Save(current);
    }

    public void SaveRunCounts(System.Collections.Generic.Dictionary<string, int> counts)
    {
        var current = Load();
        current.RunCounts = counts;
        Save(current);
    }

    public void SaveLastSessionTabs(string[] tabs)
    {
        var current = Load();
        current.LastSessionTabs = tabs ?? [];
        Save(current);
    }
}
