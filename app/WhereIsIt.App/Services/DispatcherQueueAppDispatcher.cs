using System;
using Microsoft.UI.Dispatching;

namespace WhereIsIt.App.Services;

public sealed class DispatcherQueueAppDispatcher : IAppDispatcher
{
    private readonly DispatcherQueue queue;

    public DispatcherQueueAppDispatcher(DispatcherQueue queue) => this.queue = queue;

    public void Enqueue(Action action) => queue.TryEnqueue(() =>
    {
        // Any exception thrown inside the dispatcher callback is rethrown by
        // WinUI as a stowed exception (0xc000027b) — which terminates the
        // process. The engine emits StatusChanges + MetricsChanges to the
        // status-bar VM through this dispatcher; a transient binding hiccup
        // during indexing must not kill the app. Swallow + log instead.
        try { action(); }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "whereisit-crashes.log"),
                    $"[{DateTimeOffset.Now:O}] DispatcherQueueAppDispatcher.Enqueue: {ex}\n\n");
            }
            catch { }
        }
    });
}
