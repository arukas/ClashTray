using ClashTray.Contracts;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class BoundaryTests
{
    [TestMethod]
    public void CoreStateIncludesExplicitLifecycleStates()
    {
        Assert.IsTrue(Enum.IsDefined(CoreState.Starting));
        Assert.IsTrue(Enum.IsDefined(CoreState.Restarting));
        Assert.IsTrue(Enum.IsDefined(CoreState.Failed));
    }
}
