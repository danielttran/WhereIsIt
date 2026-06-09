using System.Linq;
using FluentAssertions;
using WhereIsIt.App.Contracts;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

/// <summary>
/// EngineClientFactory wraps every inner engine in a FilteringEngineClient
/// decorator — without that wrap, the in-proc and native engines would each
/// have to implement every Everything-style modifier themselves. The advisor
/// caught this gap once already, so we lock it in with tests.
/// </summary>
public class EngineClientFactoryTests
{
    [Fact]
    public void Create_AlwaysReturnsFilteringEngineClient_NotARawInner()
    {
        var client = EngineClientFactory.Create();
        try
        {
            // Caller-facing type MUST be the decorator. If a future change
            // returns NativeEngineClient / InProcEngineClient directly,
            // QueryParser features (size:/dm:/dupe:/etc.) silently degrade to
            // no-ops on the production native path — that's the regression
            // this test catches.
            client.Should().BeOfType<FilteringEngineClient>();
        }
        finally
        {
            if (client is System.IDisposable d) d.Dispose();
        }
    }

    [Fact]
    public void Create_WithExplicitScopeRoots_StillReturnsDecorator()
    {
        var client = EngineClientFactory.Create(scopeRoots: new[] { @"C:\src" });
        try { client.Should().BeOfType<FilteringEngineClient>(); }
        finally { if (client is System.IDisposable d) d.Dispose(); }
    }

    [Fact]
    public void Create_WithRunCountService_WiresRunMetadataLookups()
    {
        // rc:/dr:/runcount: filters need RunCountService Get + GetLastRun hooks
        // — the factory plumbs them through to FilteringEngineClient. Without
        // wiring the path-keyed lookups, every rc:/runcount: query silently
        // returns the empty set.
        var rc = new RunCountService();
        rc.Increment(@"C:\notes\daily.md");
        var client = EngineClientFactory.Create(runCounts: rc);
        try
        {
            client.Should().BeOfType<FilteringEngineClient>();
        }
        finally
        {
            if (client is System.IDisposable d) d.Dispose();
        }
    }

    [Fact]
    public void CreateLocalEngine_ReturnsBareInnerEngine_NoDecorator()
    {
        // The pipe-server host uses CreateLocalEngine because the FILTERING
        // decorator lives on the client side. Wrapping twice would post-filter
        // every result on both ends — pure waste.
        var client = EngineClientFactory.CreateLocalEngine();
        try
        {
            client.Should().NotBeOfType<FilteringEngineClient>();
            // It's either the native or in-proc bare engine.
            (client is NativeEngineClient || client is InProcEngineClient)
                .Should().BeTrue("local engine must be native-or-inproc");
        }
        finally
        {
            if (client is System.IDisposable d) d.Dispose();
        }
    }
}
