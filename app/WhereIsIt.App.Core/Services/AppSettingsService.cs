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
        try
        {
            if (!File.Exists(settingsPath)) return new AppSettings();
            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
