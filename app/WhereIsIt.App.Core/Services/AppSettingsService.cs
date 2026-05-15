using System;
using System.IO;
using System.Text.Json;

namespace WhereIsIt.App.Services;

public sealed class AppSettingsService
{
    private readonly string settingsPath;
    private readonly object gate = new();

    // In-memory cache of the parsed settings file. SaveXxx methods all need
    // the full current state (settings.json is one document) — without this
    // cache, every Save reloads and re-parses the JSON from disk. Run-counts
    // in particular fire on every file open, so the per-event syscalls add up.
    private AppSettings? cached;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettingsService(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhereIsIt", "settings.json");
    }

    public AppSettings Load()
    {
        lock (gate)
        {
            if (cached is not null) return Clone(cached);
            var fresh = LoadFromDisk();
            cached = fresh;
            return Clone(fresh);
        }
    }

    private AppSettings LoadFromDisk()
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
        lock (gate)
        {
            cached = Clone(settings);
            WriteToDisk(cached);
        }
    }

    private void WriteToDisk(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(dir);
        // Atomic write: serialise to a sibling .tmp file, then File.Replace so
        // a crash mid-write doesn't leave a half-written settings.json that
        // would then be backed up as corrupt on next Load.
        var tmp = settingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        try
        {
            if (File.Exists(settingsPath)) File.Replace(tmp, settingsPath, destinationBackupFileName: null);
            else File.Move(tmp, settingsPath);
        }
        catch
        {
            // Fallback if Replace fails (e.g. cross-volume tmp): plain overwrite.
            try { File.Copy(tmp, settingsPath, overwrite: true); File.Delete(tmp); } catch { }
        }
    }

    private static AppSettings Clone(AppSettings src)
    {
        // Cheap defensive copy so external callers can mutate the returned
        // AppSettings without polluting our cached snapshot before they call
        // Save back. The collections are short so JSON round-trip is fine and
        // keeps the cloner schema-agnostic.
        var json = JsonSerializer.Serialize(src, JsonOpts);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
    }

    /// <summary>
    /// Persists just the search-history field, leaving other settings (scope roots
    /// in particular) untouched. Safe to call after every history-modifying event.
    /// </summary>
    public void SaveSearchHistory(string[] history)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.SearchHistory = history;
            WriteToDisk(cached);
        }
    }

    public void SaveBookmarks(Bookmark[] bookmarks)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.Bookmarks = bookmarks;
            WriteToDisk(cached);
        }
    }

    public void SaveColumnVisibility(bool showCreated, bool showAccessed, bool showRunCount)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.ShowCreatedColumn  = showCreated;
            cached.ShowAccessedColumn = showAccessed;
            cached.ShowRunCountColumn = showRunCount;
            WriteToDisk(cached);
        }
    }

    public void SaveRunCounts(System.Collections.Generic.Dictionary<string, int> counts)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.RunCounts = counts;
            WriteToDisk(cached);
        }
    }

    public void SaveLastSessionTabs(string[] tabs)
    {
        lock (gate)
        {
            cached ??= LoadFromDisk();
            cached.LastSessionTabs = tabs ?? [];
            WriteToDisk(cached);
        }
    }
}
