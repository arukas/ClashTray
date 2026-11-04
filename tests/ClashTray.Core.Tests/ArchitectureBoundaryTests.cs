using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using ClashTray.App;
using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ArchitectureBoundaryTests
{
    [TestMethod]
    public void ProjectDependenciesFlowTowardCoreAndContracts()
    {
        Dictionary<string, string[]> expected = new(StringComparer.Ordinal)
        {
            ["ClashTray.Contracts"] = [],
            ["ClashTray.Core"] = ["ClashTray.Contracts"],
            ["ClashTray.Presentation"] = ["ClashTray.Core", "ClashTray.Contracts"],
            ["ClashTray.App"] = ["ClashTray.Core", "ClashTray.Contracts", "ClashTray.Presentation"],
            ["ClashTray.Service"] = ["ClashTray.Core", "ClashTray.Contracts"]
        };
        foreach ((string project, string[] references) in expected)
        {
            XDocument source = XDocument.Load(Path.Combine(RepositoryRoot, "src", project, project + ".csproj"));
            string[] actual = source.Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value)).ToArray();
            CollectionAssert.AreEquivalent(references, actual, $"Unexpected dependency in {project}.");
        }
    }

    [TestMethod]
    public void DomainAndPresentationAssembliesHaveNoWinUiOrServiceDependency()
    {
        Assembly[] assemblies = [typeof(ClashTrayRuntime).Assembly, typeof(AppSettings).Assembly, typeof(PanelSnapshotContext).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                Assert.IsFalse(reference.Name is "ClashTray.App" or "ClashTray.Service" or "Microsoft.UI.Xaml" or "Microsoft.WindowsAppSDK",
                    $"{assembly.GetName().Name} references forbidden boundary {reference.Name}.");
            }
        }
    }

    [TestMethod]
    public void ProductionAssemblyContainsNoOwnedFixtureObserver()
    {
        Assembly core = typeof(ClashTrayRuntime).Assembly;
        Assert.IsNull(core.GetType("ClashTray.Core.FixtureCoreOwnershipObservationSource"));
    }

    [TestMethod]
    [DataRow("production")]
    [DataRow("isolated-fixture-tests")]
    public async Task RelocatingStorageNeverTurnsReportedPortsOrForgedProcessesIntoOwnership(string directoryName)
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, () =>
        {
            AppPaths paths = new(Path.Combine(root, directoryName, "user"), Path.Combine(root, directoryName, "service"));
            RuntimeEnvironmentDependencies[] environments =
                [RuntimeEnvironmentDependencies.Production(paths), RuntimeEnvironmentDependencies.Isolated(paths)];
            CoreRuntimeBinding advertised = RuntimeTestHelpers.CreateRuntimeBinding(new());
            using JsonDocument actual = JsonDocument.Parse("{\"port\":18101,\"socks-port\":18102,\"mixed-port\":18103}");
            LocalCoreProcessIdentity forged = new(int.MaxValue, 1, paths.ManagedCoreExecutable);
            foreach (RuntimeEnvironmentDependencies environment in environments)
            {
                MihomoListenerPorts parsed = environment.Ownership.ReadControllerPorts(actual, advertised);
                Assert.AreEqual(18101, parsed.Http);
                Assert.AreEqual(18102, parsed.Socks);
                Assert.AreEqual(18103, parsed.Mixed);
                Assert.IsFalse(environment.Ownership.IsProcessCurrent(forged));
            }
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task ExplicitIsolationCannotReachTheServiceOrChangeWindowsStartupAndProxyState()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "user"), Path.Combine(root, "service"));
            RuntimeEnvironmentDependencies isolated = RuntimeEnvironmentDependencies.Isolated(paths);
            WindowsSystemProxyRegistry registry = WindowsSystemProxyRegistry.ForCurrentUser();
            ProxyRegistryState originalProxy = registry.ReadCurrentState();
            StartupManager windowsStartup = new();
            StartupRegistrationStatus originalStartup = windowsStartup.GetStatus();
            await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => isolated.Service.SendAsync(ServiceCommand.GetStatus));
            StartupRegistrationChange change = isolated.Startup.Ensure(true, Environment.ProcessPath);
            Assert.IsTrue(isolated.Startup.GetStatus().IsEnabled);
            isolated.Startup.Rollback(change);
            Assert.IsFalse(isolated.Startup.GetStatus().IsRegistered);
            await isolated.SystemProxy.EnableAsync(51999, "<local>");
            Assert.AreEqual(SystemProxyState.On, isolated.SystemProxy.DetectState());
            await isolated.SystemProxy.DisableAsync();
            Assert.AreEqual(SystemProxyState.Off, isolated.SystemProxy.DetectState());
            Assert.AreEqual(originalProxy, registry.ReadCurrentState());
            Assert.AreEqual(originalStartup, windowsStartup.GetStatus());
            Assert.IsFalse(File.Exists(paths.ProxyBackupFile));
        });
    }

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClashTray.sln"))) { directory = directory.Parent; }
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was unavailable.");
        }
    }
}
