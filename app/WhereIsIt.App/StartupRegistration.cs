using System;
using Microsoft.Win32;
using WhereIsIt.App.Services;

namespace WhereIsIt.App;

/// <summary>
/// Keeps the per-user Windows sign-in registration in sync with settings.
/// Registry failures are non-fatal: search must still launch normally in
/// locked-down enterprise environments.
///
/// The exact value names + format live in <see cref="RegistrationFormat"/>
/// (App.Core) so xUnit can lock them down without touching the registry.
/// </summary>
internal static class StartupRegistration
{
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistrationFormat.RunKeyPath);
            if (key is null) return;
            if (!enabled)
            {
                key.DeleteValue(RegistrationFormat.RunValueName, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
                key.SetValue(RegistrationFormat.RunValueName,
                    RegistrationFormat.RunValue(executable));
        }
        catch
        {
            // Best effort only. Policy may deny writes to the Run key.
        }
    }
}
