using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ServicePipeClientTests
{
    [TestMethod]
    public void CommandTimeoutPolicyUsesBoundedBudgets()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(3), ServicePipeClient.GetCommandTimeout(ServiceCommand.GetStatus));
        Assert.AreEqual(TimeSpan.FromSeconds(30), ServicePipeClient.GetCommandTimeout(ServiceCommand.StartCore));
        Assert.AreEqual(TimeSpan.FromSeconds(30), ServicePipeClient.GetCommandTimeout(ServiceCommand.StopCore));
        Assert.AreEqual(TimeSpan.FromSeconds(30), ServicePipeClient.GetCommandTimeout(ServiceCommand.RestartCore));
        Assert.AreEqual(TimeSpan.FromMinutes(6), ServicePipeClient.GetCommandTimeout(ServiceCommand.InstallCore));
        Assert.AreEqual(TimeSpan.FromMinutes(6), ServicePipeClient.GetCommandTimeout(ServiceCommand.RollbackCore));
        Assert.AreEqual(TimeSpan.FromSeconds(15), ServicePipeClient.GetCommandTimeout(ServiceCommand.EnableTun));
        Assert.AreEqual(TimeSpan.FromSeconds(15), ServicePipeClient.GetCommandTimeout(ServiceCommand.DisableTun));
    }
}
