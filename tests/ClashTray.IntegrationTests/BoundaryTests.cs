using ClashTray.Contracts;
using ClashTray.Core;
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

    [TestMethod]
    public async Task ServiceRejectsCoreOutsideManagedProgramDataLocation()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayIntegrationTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string runtimeDirectory = Path.Combine(paths.RuntimeRoot, "mihomo");
        Directory.CreateDirectory(runtimeDirectory);
        string configurationPath = Path.Combine(runtimeDirectory, "active-config.yaml");
        await File.WriteAllTextAsync(configurationPath, "proxies: []\n");
        string localCore = Path.Combine(paths.LocalRoot, "core", "mihomo.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(localCore)!);
        File.Copy(Environment.ProcessPath!, localCore);

        try
        {
            await using ServiceRuntimeController controller = new ServiceRuntimeController(paths);
            ServiceRequest request = new ServiceRequest(
                Guid.NewGuid(),
                ServiceCommand.StartCore,
                JsonSerializer.Serialize(new ServiceCorePayload(
                    configurationPath,
                    runtimeDirectory,
                    9090,
                    string.Empty)));

            ServiceResponse response = await controller.HandleAsync(request, CancellationToken.None);

            Assert.IsFalse(response.Succeeded);
            StringAssert.Contains(response.Error, "受管 Mihomo 核心校验失败", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ServiceRejectsManagedCoreWithUnapprovedSourceMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayIntegrationTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        string runtimeDirectory = Path.Combine(paths.RuntimeRoot, "mihomo");
        Directory.CreateDirectory(runtimeDirectory);
        string configurationPath = Path.Combine(runtimeDirectory, "active-config.yaml");
        await File.WriteAllTextAsync(configurationPath, "proxies: []\n");
        Directory.CreateDirectory(paths.CoreRoot);
        File.Copy(Environment.ProcessPath!, paths.ManagedCoreExecutable);
        ManagedCoreMetadata metadata = new(
            "v0.0.0-test",
            new Uri("https://example.com/mihomo.zip"),
            new string('0', 64),
            new string('0', 64));
        await File.WriteAllTextAsync(
            paths.ManagedCoreMetadata,
            JsonSerializer.Serialize(metadata));

        try
        {
            await using ServiceRuntimeController controller = new ServiceRuntimeController(paths);
            ServiceRequest request = new ServiceRequest(
                Guid.NewGuid(),
                ServiceCommand.StartCore,
                JsonSerializer.Serialize(new ServiceCorePayload(
                    configurationPath,
                    runtimeDirectory,
                    9090,
                    string.Empty)));

            ServiceResponse response = await controller.HandleAsync(request, CancellationToken.None);

            Assert.IsFalse(response.Succeeded);
            StringAssert.Contains(response.Error, "受管 Mihomo 核心校验失败", StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
