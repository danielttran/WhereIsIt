using System;
using System.IO;
using System.Text.Json;

namespace WhereIsIt.App.Services;

/// <summary>
/// Wire-shape + sentinel-file helpers for the SingleInstance launch
/// forwarder. Lives in App.Core (not the WinUI App project) so xUnit can
/// exercise the JSON contract and sentinel format without needing the WinUI
/// dispatcher. The WinUI <c>SingleInstance</c> wraps these — keep it that
/// way so the test surface and the runtime surface can't drift.
/// </summary>
public static class LaunchForwarding
{
    public const string PipeName       = "WhereIsIt.Launch";
    public const string SentinelName   = "whereisit-last-forward.txt";
    public const int    ConnectTimeoutMs = 1500;

    public sealed class Payload
    {
        public string? Query { get; set; }
        public string? ScopeRoot { get; set; }
        public bool StartMinimised { get; set; }
    }

    /// <summary>Render a launch's args into the on-wire JSON line.</summary>
    public static string Serialize(CommandLineArgs cli)
        => JsonSerializer.Serialize(new Payload
        {
            Query = cli.Query,
            ScopeRoot = cli.ScopeRoot,
            StartMinimised = cli.StartMinimised,
        });

    /// <summary>Parse what the listener side received. Returns null on garbage
    /// so a malformed forward can't crash the listener loop.</summary>
    public static Payload? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<Payload>(json); }
        catch { return null; }
    }

    /// <summary>Write the sentinel that audit harnesses use to prove the
    /// primary actually received and processed a forward. Best-effort —
    /// returns false on IO failure so a temp-dir glitch doesn't break
    /// SingleInstance.Dispatch. Path is <see cref="DefaultSentinelPath"/>.</summary>
    public static bool WriteSentinel(Payload payload, string? overridePath = null)
    {
        try
        {
            var path = overridePath ?? DefaultSentinelPath;
            File.WriteAllText(path, SentinelText(payload));
            return true;
        }
        catch { return false; }
    }

    public static string DefaultSentinelPath
        => Path.Combine(Path.GetTempPath(), SentinelName);

    /// <summary>The exact text written by <see cref="WriteSentinel"/> —
    /// stable so the audit can grep it.</summary>
    public static string SentinelText(Payload payload)
        => $"query={payload.Query ?? string.Empty}\n" +
           $"scope={payload.ScopeRoot ?? string.Empty}\n" +
           $"min={payload.StartMinimised}\n" +
           $"time={DateTimeOffset.UtcNow:O}\n";
}
