namespace WhereIsIt.App.Services;

/// <summary>
/// Pure formatters for the values WhereIsIt writes to the per-user registry.
/// The actual registry write lives in the WinUI App project (uses
/// <c>Microsoft.Win32</c> + <c>Environment.ProcessPath</c>) — these helpers
/// are split out so xUnit can lock down the exact strings that land in HKCU
/// without touching the real registry.
///
/// Two separate features are formatted here: the Run-on-startup value (one
/// per user) and the Explorer shell verb (Folder / Drive / Background).
/// </summary>
public static class RegistrationFormat
{
    /// <summary>Constants the App project uses when writing to the registry.
    /// Tests reference these so a rename of the registry key surfaces here.</summary>
    public const string RunKeyPath        = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValueName      = "WhereIsIt";
    public const string ShellVerbId       = "WhereIsIt";
    public const string ShellVerbLabel    = "Search with WhereIsIt";

    public static readonly string[] ShellVerbClasses =
    {
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Drive\shell",
        @"Software\Classes\Directory\Background\shell",
    };

    /// <summary>Path token used in the verb command for each class. The
    /// background class uses %V (current directory of the Explorer window);
    /// folders and drives use %1 (the clicked item).</summary>
    public static string PathTokenFor(string registryClass)
        => registryClass.EndsWith(@"Background\shell", System.StringComparison.OrdinalIgnoreCase)
            ? "%V" : "%1";

    /// <summary>Value of <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\WhereIsIt</c>.
    /// Quoted around the exe path so a Program Files install works.</summary>
    public static string RunValue(string exePath) => $"\"{exePath}\"";

    /// <summary>Command that the Explorer shell invokes when the user clicks
    /// "Search with WhereIsIt". <paramref name="pathToken"/> is %1 for
    /// folders/drives, %V for the background of a folder.</summary>
    public static string ShellVerbCommand(string exePath, string pathToken)
        => $"\"{exePath}\" -p \"{pathToken}\"";

    /// <summary>Icon string the verb registers — "<exe>,0" tells the shell
    /// to use icon group 0 from the exe. Both class paths reference the same
    /// running exe, so this is exe-bound, not class-specific.</summary>
    public static string ShellVerbIcon(string exePath) => $"\"{exePath}\",0";
}
