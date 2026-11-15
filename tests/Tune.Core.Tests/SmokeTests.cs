namespace Tune.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Core_assembly_loads() =>
        Assert.NotNull(typeof(Tune.Core.Models.Track).Assembly);
}
