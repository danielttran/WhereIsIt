using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WhereIsIt.App.Services;
using WhereIsIt.Pipe.Client;

// WhereIsIt engine service — runs the native (or in-proc) engine and exposes it
// on a named pipe so non-elevated app instances get a live index without their
// own elevation. EngineClientFactory connects to this pipe automatically when
// the foreground app isn't elevated.
//
// Runs as a console (Ctrl+C to stop) or as a true Windows service —
// UseWindowsService auto-detects the SCM. Install with, e.g.:
//   sc create WhereIsItEngine binPath= "C:\path\WhereIsIt.EngineService.exe" start= auto

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "WhereIsItEngine");
builder.Services.AddHostedService<EnginePipeHostedService>();
await builder.Build().RunAsync();

/// <summary>Hosts the <see cref="EnginePipeServer"/> for the lifetime of the host.</summary>
internal sealed class EnginePipeHostedService : IHostedService
{
    private IEngineClientHandle? handle;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string[]? scope = null;
        try
        {
            var settings = new AppSettingsService().Load();
            if (settings.ScopeRoots.Length > 0) scope = settings.ScopeRoots;
        }
        catch { /* settings optional → default roots */ }

        var engine = EngineClientFactory.CreateLocalEngine(scope);
        var server = new EnginePipeServer(engine, PipeProtocol.DefaultPipeName);
        server.Start();
        handle = new IEngineClientHandle(engine, server);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        handle?.Dispose();
        handle = null;
        return Task.CompletedTask;
    }

    // Bundles the disposables so both are torn down together on shutdown.
    private sealed class IEngineClientHandle : IDisposable
    {
        private readonly IDisposable? engine;
        private readonly EnginePipeServer server;
        public IEngineClientHandle(WhereIsIt.App.Contracts.IEngineClient engine, EnginePipeServer server)
        {
            this.engine = engine as IDisposable;
            this.server = server;
        }
        public void Dispose()
        {
            try { server.Dispose(); } catch { }
            try { engine?.Dispose(); } catch { }
        }
    }
}
