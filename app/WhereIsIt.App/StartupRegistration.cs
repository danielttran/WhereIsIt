using System;
using Microsoft.Win32;

namespace WhereIsIt.App;

/// <summary>
/// Keeps the per-user Windows sign-in registration in sync with settings.
/// Registry failures are non-fatal: search must still launch normally in
/// locked-down enterprise environments.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhereIsIt";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
                key.SetValue(ValueName, $"\"{executable}\"");
        }
        catch
        {
            // Best effort only. Policy may deny writes to the Run key.
        }
    }
}
