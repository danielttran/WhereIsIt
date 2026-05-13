using FluentAssertions;
using WhereIsIt.App.Services;
using Xunit;

namespace WhereIsIt.App.Tests;

public class QueryComposerTests
{
    // ── ApplyFilter — fresh query ─────────────────────────────────────────

    [Fact]
    public void ApplyFilter_OnEmptyQuery_AddsFilterToken()
    {
        QueryComposer.ApplyFilter("", "audio:").Should().Be("audio:");
    }

    [Fact]
    public void ApplyFilter_PreservesUserTextAndAddsFilter()
    {
        QueryComposer.ApplyFilter("report", "audio:").Should().Be("report audio:");
    }

    // ── ApplyFilter — replaces existing same-category filter ─────────────

    [Fact]
    public void ApplyFilter_AudioReplacesVideo_SinceBothAreTypeFilters()
    {
        QueryComposer.ApplyFilter("report video:", "audio:").Should().Be("report audio:");
    }

    [Fact]
    public void ApplyFilter_AllRemovesTypeFilter()
    {
        QueryComposer.ApplyFilter("report video:", QueryComposer.AllFilter)
            .Should().Be("report");
    }

    [Fact]
    public void ApplyFilter_FolderReplacesAudio()
    {
        QueryComposer.ApplyFilter("audio:", "folder:").Should().Be("folder:");
    }

    // ── ActiveFilter — recognises which preset is active ─────────────────

    [Fact]
    public void ActiveFilter_RecognisesAudioPreset()
    {
        QueryComposer.ActiveFilter("audio:").Should().Be("audio:");
        QueryComposer.ActiveFilter("foo audio: bar").Should().Be("audio:");
    }

    [Fact]
    public void ActiveFilter_NoneMatches_ReturnsAll()
    {
        QueryComposer.ActiveFilter("plain query").Should().Be(QueryComposer.AllFilter);
    }

    // ── trimming ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyFilter_DoesNotDoubleSpace()
    {
        QueryComposer.ApplyFilter("  report   ", "audio:").Should().Be("report audio:");
    }
}
