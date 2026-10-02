using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class CoreLifecycleCoordinatorTests
{
    [TestMethod]
    [DataRow((int)ListenerOwnerState.Owned, true)]
    [DataRow((int)ListenerOwnerState.Unknown, false)]
    [DataRow((int)ListenerOwnerState.Foreign, false)]
    public async Task HealthAdmissionExecutesTheSamePolicyForInjectedOwnershipEvidence(int ownerState, bool accepted)
    {
        CoreRuntimeBinding binding = RuntimeTestHelpers.CreateRuntimeBinding(new AppSettings());
        LocalDeviceCoordinator device = new(EndpointKind.Local, new IsolatedServicePipeClient(), new FakeSystemProxyController(SystemProxyState.Off));
        await using CoreLifecycleCoordinator lifecycle = new(device, new Observations((ListenerOwnerState)ownerState));
        lifecycle.StoreBinding(binding);
        using RuntimeControllerHandler handler = new();
        using HttpClient client = new(handler);
        MihomoApiClient api = new(client, new Uri("http://127.0.0.1:9090/"), string.Empty);
        if (accepted)
        {
            Assert.AreEqual("v1.19.30", (await lifecycle.ObserveHealthAsync(api, binding, CancellationToken.None)).Version);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<ManagedCoreOwnershipException>(() => lifecycle.ObserveHealthAsync(api, binding, CancellationToken.None));
        }
    }

    [TestMethod]
    public async Task HealthStampCannotCrossBindingLifecycleOrControllerGeneration()
    {
        LocalDeviceCoordinator device = new(EndpointKind.Local, new IsolatedServicePipeClient(), new FakeSystemProxyController(SystemProxyState.Off));
        await using CoreLifecycleCoordinator lifecycle = new(device, new Observations(ListenerOwnerState.Owned));
        CoreRuntimeBinding binding = RuntimeTestHelpers.CreateRuntimeBinding(new AppSettings());
        lifecycle.StoreBinding(binding);
        lifecycle.ConfirmHealth(lifecycle.Epoch, lifecycle.Process.Generation, 7, binding);
        Assert.IsTrue(lifecycle.IsHealthConfirmed(7, true));
        Assert.IsFalse(lifecycle.IsHealthConfirmed(8, true));
        lifecycle.StoreBinding(binding with { InstanceId = Guid.NewGuid() });
        Assert.IsFalse(lifecycle.IsHealthConfirmed(7, true));
        lifecycle.ConfirmHealth(lifecycle.Epoch, lifecycle.Process.Generation, 7, lifecycle.Binding);
        lifecycle.BeginTransition();
        Assert.IsFalse(lifecycle.IsHealthConfirmed(7, true));
        lifecycle.InvalidateHealth();
        Assert.IsFalse(lifecycle.IsHealthConfirmed(7, true));
    }

    private sealed class Observations(ListenerOwnerState state) : ICoreOwnershipObservationSource
    {
        public MihomoListenerPorts ReadControllerPorts(JsonDocument configuration, CoreRuntimeBinding binding) => new(binding.HttpPort, binding.SocksPort, binding.MixedPort);
        public bool IsProcessCurrent(LocalCoreProcessIdentity identity) => true;
        public Func<LocalPortBinding, ListenerOwnerObservation> CreateListenerSnapshot(LocalCoreProcessIdentity identity, IReadOnlyList<LocalPortBinding> listeners) =>
            _ => new ListenerOwnerObservation(state);
    }
}
