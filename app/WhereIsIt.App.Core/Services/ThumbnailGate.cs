using System.IO;

namespace WhereIsIt.App.Services;

/// <summary>
/// Cheap path eligibility check the WinUI <c>ThumbnailService</c> runs before
/// touching <c>StorageFile.GetFileFromPathAsync</c>. Lives in App.Core so the
/// gate logic is xUnit-testable without a WinUI dispatcher — the WinUI side
/// of the service then becomes a thin orchestrator.
///
/// The gate exists because <c>GetFileFromPathAsync</c> raises a structured
/// exception (SEH) on bad paths, and that exception escapes the managed
/// try/catch under WinUI dispatcher load — producing the
/// STATUS_STOWED_EXCEPTION (0xc000027b) crash the user reported during
/// indexing. Filtering at this gate stops the WinRT call from running on a
/// path the OS will reject anyway.
/// </summary>
public static class ThumbnailGate
{
    /// <summary>True when <paramref name="path"/> is safe to hand to the
    /// Windows shell thumbnail APIs. False means: skip, leave thumbnail
    /// null, do not throw.</summary>
    public static bool IsEligible(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // GetFileFromPathAsync requires an absolute path. The engine's
        // synthetic ancestor records expose row.FullPath = bare segment
        // names ("Users", "Temp"); those would crash the WinRT call.
        if (!Path.IsPathRooted(path)) return false;
        // Existence check protects against indexing-phase IDs that point at
        // files already removed by the time the UI realises the row.
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
