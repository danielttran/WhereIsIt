using System;
using System.IO.Pipes;
using System.Security.Principal;
using WhereIsIt.App.Contracts;
using WhereIsIt.Pipe.Client;

namespace WhereIsIt.App.Services;

public static class EngineClientFactory
{
    private const string PipeName = "WhereIsIt.Engine";

    public static IEngineClient Create(bool? forceElevated = null)
    {
        var elevated = forceElevated ?? IsElevated();
        if (!elevated && CanConnectToPipe())
        {
            return new PipeEngineClient();
        }

        return new InProcEngineClient();
    }

    private static bool CanConnectToPipe()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(20);
            return client.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed class InProcEngineClient : IEngineClient
    {
        public IObservable<string> StatusChanges => System.Reactive.Linq.Observable.Empty<string>();
        public IObservable<int> MetricsChanges => System.Reactive.Linq.Observable.Empty<int>();
        public IObservable<IReadOnlyList<uint>> ObserveResults => System.Reactive.Linq.Observable.Empty<IReadOnlyList<uint>>();
        public System.Threading.Tasks.Task SearchAsync(string query, System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task SortAsync(string key, bool descending, System.Threading.CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task<ResultRowModel> GetRowAsync(uint id, System.Threading.CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new ResultRowModel($"InProcRow-{id}", "C:\\", 1, DateTimeOffset.UtcNow, "A"));
    }
}
