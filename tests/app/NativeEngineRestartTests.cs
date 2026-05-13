/*
 * NativeEngineRestartTests.cs
 *
 * Proves the engine restart path: index.dat is saved on Stop, reloaded on next
 * Start, and the USN journal catch-up surfaces files that appeared while the
 * engine was off.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

// Share the "NativeEngine" collection so these tests don't run in parallel
// with NativeEngineIntegrationTests — they all touch %LOCALAPPDATA%\WhereIsIt\
// index.dat plus the native engine's named shared memory mappings.
[Collection("NativeEngine")]
public sealed class NativeEngineRestartTests
{
    private static bool DllAvailable => NativeEngineClient.IsAvailable();

    private static async Task<IReadOnlyList<uint>> SearchWaitAsync(
        NativeEngineClient client, string query, int timeoutSec = 30)
    {
        IReadOnlyList<uint>? received = null;
        DateTime lastEmit = DateTime.MinValue;
        using var sub = client.ObserveResults.Subscribe(ids => { received = ids; lastEmit = DateTime.UtcNow; });
        await client.SearchAsync(query, CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            if (received is not null && (DateTime.UtcNow - lastEmit).TotalMilliseconds >= 400)
                break;
        }
        return received ?? Array.Empty<uint>();
    }

    private static void WaitForReady(NativeEngineClient client, int timeoutSec = 300)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (!client.GetCurrentStatus().StartsWith("Ready", StringComparison.OrdinalIgnoreCase)
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
        }
    }

    [Fact]
    public async Task SecondEngine_PicksUp_FileCreatedWhileFirstWasOff()
    {
        if (!DllAvailable)
        {
            // No DLL → integration test irrelevant; xunit treats no assertion as a pass.
            return;
        }

        // Fresh scope on this drive so the USN journal will see the create.
        var scope = Path.Combine(Path.GetTempPath(), "whereisit-restart-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scope);
        try
        {
            File.WriteAllText(Path.Combine(scope, "preexisting.txt"), "");
            using (var first = new NativeEngineClient(scopeRoots: new[] { scope }))
            {
                WaitForReady(first);
                var ids = await SearchWaitAsync(first, "preexisting");
                ids.Count.Should().BeGreaterThan(0,
                    "the file created before the first engine starts should be indexed");
            }

            var addedName = "added-while-off-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt";
            File.WriteAllText(Path.Combine(scope, addedName), "");

            // Give the USN journal a moment to flush the entry to disk.
            await Task.Delay(250);

            using var second = new NativeEngineClient(scopeRoots: new[] { scope });
            WaitForReady(second);

            var found = await SearchWaitAsync(second, addedName);
            found.Count.Should().BeGreaterThan(0,
                "the file added while the engine was off must surface after restart " +
                "via the LoadIndex + DrainUsnJournal catch-up path");
        }
        finally
        {
            try { Directory.Delete(scope, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task IndexFile_IsWritten_OnDispose()
    {
        if (!DllAvailable) return;

        var appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (appData is null) return;
        var indexPath = Path.Combine(appData, "WhereIsIt", "index.dat");

        // Back up any existing user index so a dev dogfooding the app doesn't
        // lose state by running this test. Restored in `finally`.
        string? backupPath = null;
        if (File.Exists(indexPath))
        {
            backupPath = indexPath + ".test-backup";
            File.Move(indexPath, backupPath, overwrite: true);
        }

        var scope = Path.Combine(Path.GetTempPath(), "whereisit-restart-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scope);
        try
        {
            File.WriteAllText(Path.Combine(scope, "marker.txt"), "");

            using (var client = new NativeEngineClient(scopeRoots: new[] { scope }))
            {
                WaitForReady(client);
                // Wait for steady-state before disposing so SaveIndex has data to flush.
                await SearchWaitAsync(client, "marker");
            }

            File.Exists(indexPath).Should().BeTrue(
                "the engine must SaveIndex on Stop so a subsequent start can skip full re-scan");
            new FileInfo(indexPath).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            try { Directory.Delete(scope, recursive: true); } catch { }
            if (backupPath is not null)
            {
                try { File.Move(backupPath, indexPath, overwrite: true); } catch { }
            }
        }
    }
}
