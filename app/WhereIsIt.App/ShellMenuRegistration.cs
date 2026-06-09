using System;
using Microsoft.Win32;
using WhereIsIt.App.Services;

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
///
/// The value names, classes, label, and command strings live in
/// <see cref="RegistrationFormat"/> (App.Core) so xUnit can lock them down
/// without touching the registry.
/// </summary>
internal static class ShellMenuRegistration
{
    public static void Apply(bool enabled)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            foreach (var cls in RegistrationFormat.ShellVerbClasses)
            {
                var token = RegistrationFormat.PathTokenFor(cls);
                if (!enabled)
                {
                    using var shell = Registry.CurrentUser.OpenSubKey(cls, writable: true);
                    shell?.DeleteSubKeyTree(RegistrationFormat.ShellVerbId,
                        throwOnMissingSubKey: false);
                    continue;
                }

                using var verbKey = Registry.CurrentUser.CreateSubKey(
                    $@"{cls}\{RegistrationFormat.ShellVerbId}");
                if (verbKey is null) continue;
                verbKey.SetValue(string.Empty, RegistrationFormat.ShellVerbLabel);
                verbKey.SetValue("Icon", RegistrationFormat.ShellVerbIcon(exe));
                using var cmd = verbKey.CreateSubKey("command");
                cmd?.SetValue(string.Empty,
                    RegistrationFormat.ShellVerbCommand(exe, token));
            }
        }
        catch
        {
            // Best effort only — policy may deny writes to HKCU\Software\Classes.
        }
    }
}
