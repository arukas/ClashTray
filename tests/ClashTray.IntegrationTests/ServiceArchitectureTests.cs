using System.Text.RegularExpressions;
using ClashTray.Service;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class ServiceArchitectureTests
{
    [TestMethod]
    public void ServiceDoesNotReferenceDesktopOrPresentationUseCases()
    {
        foreach (System.Reflection.AssemblyName reference in typeof(ServiceRuntimeController).Assembly.GetReferencedAssemblies())
        {
            Assert.IsFalse(reference.Name is "ClashTray.App" or "ClashTray.Presentation" or "Microsoft.UI.Xaml");
        }
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClashTray.sln"))) { directory = directory.Parent; }
        Assert.IsNotNull(directory);
        Regex forbidden = new(@"\b(?:ClashTrayRuntime|SubscriptionScheduler|SubscriptionRefreshCoordinator|EndpointCatalogCoordinator|SettingsEditModel|SettingsCommitCoordinator|ProxyOperationCoordinator)\b", RegexOptions.CultureInvariant);
        foreach (string path in Directory.EnumerateFiles(Path.Combine(directory.FullName, "src", "ClashTray.Service"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.IsFalse(forbidden.IsMatch(File.ReadAllText(path)), $"Service must not consume desktop use cases: {path}");
        }
    }
}
