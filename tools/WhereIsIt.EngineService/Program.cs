using System;
using System.Threading;
using WhereIsIt.App.Services;
using WhereIsIt.Pipe.Client;

// WhereIsIt engine host — runs the native (or in-proc) engine and exposes it on
// a named pipe so non-elevated app instances get a live index without their own
// elevation. Run elevated (it reads the USN journal / MFT); the foreground app
// then connects automatically (EngineClientFactory uses NamedPipeEngineClient
// when this pipe is listening and the app isn't elevated).
//
// Run as a console (Ctrl+C to stop) or register as a Windows service:
//   sc create WhereIsItEngine binPath= "C:\path\WhereIsIt.EngineService.exe" start= auto

string[]? scope = null;
try
{
    var settings = new AppSettingsService().Load();
    if (settings.ScopeRoots.Length > 0) scope = settings.ScopeRoots;
}
catch { /* settings optional → default roots */ }

using var engine = EngineClientFactory.CreateLocalEngine(scope);
using var server = new EnginePipeServer(engine, PipeProtocol.DefaultPipeName);
server.Start();

Console.WriteLine($"WhereIsIt engine service listening on pipe '{PipeProtocol.DefaultPipeName}'. Press Ctrl+C to stop.");

using var stop = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
// Also stop if the host process is asked to terminate.
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();
stop.Wait();
