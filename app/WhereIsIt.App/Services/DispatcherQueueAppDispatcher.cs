using System;
using Microsoft.UI.Dispatching;

namespace WhereIsIt.App.Services;

public sealed class DispatcherQueueAppDispatcher : IAppDispatcher
{
    private readonly DispatcherQueue queue;

    public DispatcherQueueAppDispatcher(DispatcherQueue queue) => this.queue = queue;

    public void Enqueue(Action action) => queue.TryEnqueue(() => action());
}
