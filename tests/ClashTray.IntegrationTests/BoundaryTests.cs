using ClashTray.Contracts;
using ClashTray.Service;
using System.Text.Json;

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

    [TestMethod]
    [DataRow("test-secret")]
    [DataRow("")]
    public async Task TunCommandCannotTargetControllerWithoutRunningCore(string secret)
    {
        await using ServiceRuntimeController controller = new ServiceRuntimeController();
        ServiceRequest request = new ServiceRequest(
            Guid.NewGuid(),
            ServiceCommand.EnableTun,
            JsonSerializer.Serialize(new ServiceTunPayload(9090, secret, true)));

        ServiceResponse response = await controller.HandleAsync(request, CancellationToken.None);

        Assert.IsFalse(response.Succeeded);
        Assert.AreEqual(CoreState.Stopped, response.Core);
        StringAssert.Contains(response.Error, "核心尚未运行", StringComparison.Ordinal);
    }
}
