using WhereIsIt.App.ViewModels;
using Xunit;

namespace WhereIsIt.App.Tests.ViewModels;

public class ResultRowViewModelTests
{
    [Theory]
    [InlineData((ulong)512, "512 B")]
    [InlineData((ulong)1024, "1 KB")]
    [InlineData((ulong)1536, "1.5 KB")]
    public void FormatBytes_Works(ulong bytes, string expected)
    {
        var actual = ResultRowViewModel.FormatBytes(bytes);
        Assert.Equal(expected, actual);
    }
}
