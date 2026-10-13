using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class TestEnvironmentDiagnostics
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        context.WriteLine($"OS={RuntimeInformation.OSDescription}; architecture={RuntimeInformation.ProcessArchitecture}; runtime={Environment.Version}; administrator={new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}; impersonation={identity.ImpersonationLevel}");
        context.WriteLine("Required capability categories: RequiresWindowsAcl, RequiresRestrictedToken, RequiresTls, RequiresOfficialMihomo. Missing capabilities remain failures in mandatory gates.");
    }
}
