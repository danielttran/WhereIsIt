using System;

namespace WhereIsIt.App.Services;

public interface IAppDispatcher
{
    void Enqueue(Action action);
}

public sealed class InlineDispatcher : IAppDispatcher
{
    public void Enqueue(Action action) => action();
}
