using System.Reflection;
using System.Xml.Linq;
using ClashTray.App;
using ClashTray.Contracts;

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
    public void ProductionRuntimeContainsNoOwnedFixtureObserverOrImplicitCompatibilityConstructor()
    {
        Assembly core = typeof(ClashTrayRuntime).Assembly;
        Assert.IsNull(core.GetType("ClashTray.Core.FixtureCoreOwnershipObservationSource"));
        ConstructorInfo composition = typeof(ClashTrayRuntime).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        Assert.AreEqual(typeof(RuntimeEnvironmentDependencies), composition.GetParameters()[0].ParameterType);
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
