using System;
using Microsoft.Win32;

namespace WhereIsIt.App;

/// <summary>
/// Adds (or removes) a per-user Explorer context-menu entry — "Search with
/// WhereIsIt" on folders, drives and the folder background — using the classic
/// registry shell-verb mechanism (no COM handler required). The verb launches
/// the app with <c>-p &lt;path&gt;</c>, which <see cref="WhereIsIt.App.Services.CommandLineArgs"/>
/// already understands. Registry failures are non-fatal.
///
/// On Windows 11 classic verbs appear under "Show more options"; on Windows 10
/// they appear directly. A modern (always-visible) Win11 entry would need a
/// packaged <c>IExplorerCommand</c> COM handler.
/// </summary>
internal static class ShellMenuRegistration
{
    private const string Verb = "WhereIsIt";
    private const string Label = "Search with WhereIsIt";

    // Folder = a selected folder; Drive = a volume; Directory\Background = the
    // empty space inside a folder (uses %V for the current directory).
    private static readonly (string Class, string PathToken)[] Targets =
    {
        (@"Software\Classes\Directory\shell", "%1"),
        (@"Software\Classes\Drive\shell", "%1"),
        (@"Software\Classes\Directory\Background\shell", "%V"),
    };

    public static void Apply(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            foreach (var (cls, token) in Targets)
            {
                if (!enabled)
                {
                    using var shell = Registry.CurrentUser.OpenSubKey(cls, writable: true);
                    shell?.DeleteSubKeyTree(Verb, throwOnMissingSubKey: false);
                    continue;
                }

                using var verbKey = Registry.CurrentUser.CreateSubKey($@"{cls}\{Verb}");
                if (verbKey is null) continue;
                verbKey.SetValue(string.Empty, Label);
                verbKey.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = verbKey.CreateSubKey("command");
                cmd?.SetValue(string.Empty, $"\"{exe}\" -p \"{token}\"");
            }
        }
        catch
        {
            // Best effort only — policy may deny writes to HKCU\Software\Classes.
        }
    }
}
