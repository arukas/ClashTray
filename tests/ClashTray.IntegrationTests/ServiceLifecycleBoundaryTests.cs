using System.Text.Json;
using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Service;
using ClashTray.Testing;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class ServiceLifecycleBoundaryTests
{
    [TestMethod]
    [DataRow(ServiceCommand.StartCore)]
    [DataRow(ServiceCommand.RestartCore)]
    public async Task LifecycleRejectsUntrustedPayloadBeforeCreatingAnyProcessOrRuntimeFile(ServiceCommand command)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            string working = Path.Combine(paths.RuntimeRoot, "mihomo");
            Directory.CreateDirectory(working);
            string source = Path.Combine(working, "source.yaml");
            const string original = "proxies: []\nrules: []\n";
            await File.WriteAllTextAsync(source, original);
            ServiceCorePayload valid = new(source, working, 51999, string.Empty);
            ServiceCorePayload[] untrusted =
            [
                valid with { ConfigurationPath = Path.Combine(root, "outside.yaml") },
                valid with { WorkingDirectory = root },
                valid with { ControllerSecret = "unexpected-secret" },
                valid with { ControllerPort = 65536 },
                valid with { ControllerPortConflictPolicy = (ControllerPortConflictPolicy)999 }
            ];
            await using ServiceRuntimeController controller = new(paths, null, null, restoreOwnedProxyStates: () => { });
            CoreState initial = controller.CoreState;
            foreach (ServiceCorePayload payload in untrusted)
            {
                ServiceRequest request = new(Guid.NewGuid(), command, JsonSerializer.Serialize(payload),
                    ProtocolVersion: ServiceProtocol.CurrentVersion);
                ServiceResponse response = await controller.HandleAsync(request, CancellationToken.None);
                Assert.IsFalse(response.Succeeded);
                Assert.IsNull(response.RuntimeBinding);
                Assert.AreEqual(initial, controller.CoreState);
                Assert.AreEqual(ServiceProtocol.CurrentVersion, response.ProtocolVersion);
                Assert.IsFalse(File.Exists(Path.Combine(working, "active-service-config.yaml")));
                Assert.AreEqual(original, await File.ReadAllTextAsync(source));
            }
        });
    }
}
