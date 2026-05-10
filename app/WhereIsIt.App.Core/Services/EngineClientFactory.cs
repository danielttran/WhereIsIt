using System;
using System.IO.Pipes;
using System.Security.Principal;
using WhereIsIt.App.Contracts;
using WhereIsIt.Pipe.Client;

namespace WhereIsIt.App.Services;

public static class EngineClientFactory
{
    private const string PipeName = "WhereIsIt.Engine";

    public static IEngineClient Create(string[]? scopeRoots = null, bool? forceElevated = null)
    {
        // Prefer the native engine (full NTFS/USN indexing) when the DLL is present
        if (NativeEngineClient.IsAvailable())
        {
            try { return new NativeEngineClient(scopeRoots); }
            catch { /* DLL present but failed to load; fall through */ }
        }

        var elevated = forceElevated ?? IsElevated();
        if (!elevated && CanConnectToPipe())
            return new PipeEngineClient();

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
}
