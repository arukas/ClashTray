using ClashTray.App;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointCapabilityPolicyTests
{
    [TestMethod]
    public void ReadOnlyControllerDisallowsActionsEvenWhenCapabilityIsPresent()
    {
        Assert.IsFalse(EndpointCapabilityPolicy.Allows(
            false,
            EndpointCapability.RefreshProvider,
            EndpointCapability.RefreshProvider));
    }

    [TestMethod]
    public void MissingCapabilityDisallowsActionsAfterAnOperationCompletes()
    {
        Assert.IsFalse(EndpointCapabilityPolicy.Allows(
            true,
            EndpointCapability.SwitchProxy,
            EndpointCapability.RefreshProvider));
    }

    [TestMethod]
    public void WritableControllerRequiresAllRequestedFlags()
    {
        EndpointCapability required = EndpointCapability.ObserveRules | EndpointCapability.ObserveProviders;

        Assert.IsFalse(EndpointCapabilityPolicy.Allows(true, EndpointCapability.ObserveRules, required));
        Assert.IsTrue(EndpointCapabilityPolicy.Allows(true, required, required));
    }
}
