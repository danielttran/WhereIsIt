using System.IO;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class AppSettingsServiceTests : IDisposable
{
    private readonly string tempPath;
    private readonly AppSettingsService sut;

    public AppSettingsServiceTests()
    {
        tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        sut = new AppSettingsService(tempPath);
    }

    public void Dispose()
    {
        sut.Dispose();
        if (File.Exists(tempPath)) File.Delete(tempPath);
        var dir = Path.GetDirectoryName(tempPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileAbsent()
    {
        var settings = sut.Load();
        Assert.NotNull(settings);
        Assert.Empty(settings.ScopeRoots);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_ScopeRoots()
    {
        sut.Save(new AppSettings { ScopeRoots = ["C:\\", "D:\\Projects"] });
        var loaded = sut.Load();
        Assert.Equal(["C:\\", "D:\\Projects"], loaded.ScopeRoots);
    }

    [Fact]
    public void Save_CreatesDirectoryIfMissing()
    {
        sut.Save(new AppSettings());
        Assert.True(File.Exists(tempPath));
    }

    [Fact]
    public void Dispose_FlushesPendingBackgroundSave()
    {
        sut.SaveSearchHistory(["alpha", "beta"]);

        sut.Dispose();

        using var reload = new AppSettingsService(tempPath);
        var loaded = reload.Load();
        Assert.Equal(["alpha", "beta"], loaded.SearchHistory);
    }

    [Fact]
    public void Save_Throws_WhenSettingsPathCannotBeReplaced()
    {
        var directoryPath = Path.Combine(Path.GetDirectoryName(tempPath)!, "is-a-directory");
        Directory.CreateDirectory(directoryPath);
        using var service = new AppSettingsService(directoryPath);

        Assert.ThrowsAny<Exception>(() => service.Save(new AppSettings()));
    }

    [Fact]
    public void Load_DoesNotQuarantineValidSettings_WhenReadIsTemporarilyBlocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        File.WriteAllText(tempPath, "{}");
        using var exclusive = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var service = new AppSettingsService(tempPath);

        Assert.ThrowsAny<IOException>(() => service.Load());
        Assert.True(File.Exists(tempPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(tempPath)!, "settings.json.bak.*"));
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileIsCorrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        File.WriteAllText(tempPath, "{ invalid json {{");
        var settings = sut.Load();
        Assert.NotNull(settings);
        Assert.Empty(settings.ScopeRoots);
    }
}
