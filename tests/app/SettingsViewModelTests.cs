using System;
using System.IO;
using WhereIsIt.App.Services;
using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests;

public class SettingsViewModelTests : IDisposable
{
    private readonly string tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(tempPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Save_PersistsAllUserFacingOptions_AndDeduplicatesRoots()
    {
        var service = new AppSettingsService(tempPath);
        var vm = new SettingsViewModel(service)
        {
            IndexScopeConfig = @"C:\, c:\, D:\Projects",
            GlobalHotkey = "Ctrl+Shift+F",
            StartWithWindows = true,
            EnableHttpServer = true,
            HttpServerPortConfig = "43210",
        };

        vm.SaveCommand.Execute(null);
        var saved = service.Load();

        Assert.True(vm.Saved);
        Assert.Null(vm.ValidationMessage);
        Assert.Equal([@"C:\", @"D:\Projects"], saved.ScopeRoots);
        Assert.Equal("Ctrl+Shift+F", saved.GlobalHotkey);
        Assert.True(saved.StartWithWindows);
        Assert.True(saved.EnableHttpServer);
        Assert.Equal(43210, saved.HttpServerPort);
    }

    [Theory]
    [InlineData("Ctrl+NoSuchKey", "12321")]
    [InlineData("Ctrl+Alt+W", "70000")]
    public void Save_RejectsInvalidConfiguration(string hotkey, string port)
    {
        var service = new AppSettingsService(tempPath);
        var vm = new SettingsViewModel(service) { GlobalHotkey = hotkey, HttpServerPortConfig = port };

        vm.SaveCommand.Execute(null);

        Assert.False(vm.Saved);
        Assert.True(vm.HasValidationMessage);
        Assert.False(File.Exists(tempPath));
    }
}
