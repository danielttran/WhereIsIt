using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// Locks down the exact strings WhereIsIt writes to HKCU. The actual registry
/// writes live in StartupRegistration / ShellMenuRegistration (WinUI App
/// project) which use <see cref="RegistrationFormat"/> as the single source
/// of truth for the values. Renaming a key or changing the verb shape will
/// break these tests, which is the whole point.
/// </summary>
public class RegistrationFormatTests
{
    // ── Run-on-startup ──────────────────────────────────────────────────

    [Fact]
    public void RunKeyPath_IsTheUserRunKey() =>
        RegistrationFormat.RunKeyPath
            .Should().Be(@"Software\Microsoft\Windows\CurrentVersion\Run");

    [Fact]
    public void RunValueName_IsWhereIsIt() =>
        RegistrationFormat.RunValueName.Should().Be("WhereIsIt");

    [Fact]
    public void RunValue_QuotesTheExePath_SoSpacesWork()
    {
        // Without quoting, an install under "C:\Program Files\WhereIsIt\" would
        // fail at sign-in because the Run registry parses the value as
        // "C:\Program" followed by arguments "Files\WhereIsIt\...".
        var v = RegistrationFormat.RunValue(@"C:\Program Files\WhereIsIt\WhereIsIt.App.exe");
        v.Should().Be("\"C:\\Program Files\\WhereIsIt\\WhereIsIt.App.exe\"");
    }

    // ── Shell verb (Explorer context menu) ──────────────────────────────

    [Fact]
    public void ShellVerbClasses_CoverFolderDriveAndBackground()
    {
        RegistrationFormat.ShellVerbClasses.Should().BeEquivalentTo(new[]
        {
            @"Software\Classes\Directory\shell",
            @"Software\Classes\Drive\shell",
            @"Software\Classes\Directory\Background\shell",
        });
    }

    [Theory]
    [InlineData(@"Software\Classes\Directory\shell",            "%1")]
    [InlineData(@"Software\Classes\Drive\shell",                "%1")]
    [InlineData(@"Software\Classes\Directory\Background\shell", "%V")]
    public void PathTokenFor_BackgroundUsesV_OthersUse1(string cls, string token) =>
        RegistrationFormat.PathTokenFor(cls).Should().Be(token);

    [Fact]
    public void ShellVerbCommand_QuotesExe_AndUsesP_FlagWithPathToken()
    {
        // The CommandLineArgs parser reads `-p <path>` and seeds a child:<path>
        // query, so this exact shape is the contract between Explorer and the
        // app. Don't change `-p` to `--path` without updating CommandLineArgs.
        var cmd = RegistrationFormat.ShellVerbCommand(@"C:\app\WhereIsIt.App.exe", "%1");
        cmd.Should().Be("\"C:\\app\\WhereIsIt.App.exe\" -p \"%1\"");
    }

    [Fact]
    public void ShellVerbIcon_FormatsAs_ExeCommaZero() =>
        RegistrationFormat.ShellVerbIcon(@"C:\a\b.exe").Should().Be("\"C:\\a\\b.exe\",0");

    [Fact]
    public void ShellVerbLabel_IsHumanReadable_NotIdentifier() =>
        RegistrationFormat.ShellVerbLabel.Should().Be("Search with WhereIsIt");
}
