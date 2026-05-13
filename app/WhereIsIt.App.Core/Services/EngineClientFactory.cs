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
        var inner = CreateInner(scopeRoots, forceElevated);
        // Wrap with the filtering decorator so the C# QueryParser features
        // (ext:/size:/dm:/attrib:/child:/parent:/!/|/...) work against engines
        // that don't speak those modifiers natively. InProcEngineClient already
        // applies them internally, so wrapping is a no-op for it but still
        // keeps a single code path.
        return new FilteringEngineClient(inner);
    }

    private static IEngineClient CreateInner(string[]? scopeRoots, bool? forceElevated)
    {
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
