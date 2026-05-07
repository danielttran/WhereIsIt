using System.Threading.Tasks;
using Xunit;

namespace WhereIsIt.App.Tests;

public class InProcEngineClientSmoke
{
    [Fact]
    public async Task Placeholder_InProcEngineClientSmoke_Passes()
    {
        await Task.Delay(1);
        Assert.True(true);
    }
}
