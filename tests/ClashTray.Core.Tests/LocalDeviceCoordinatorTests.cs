using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class LocalDeviceCoordinatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task LocalCoordinatorDelegatesProxyOperationsAndServiceCommands()
    {
        RecordingServicePipeClient service = new RecordingServicePipeClient();
        RecordingSystemProxyController proxy = new RecordingSystemProxyController();
        LocalDeviceCoordinator coordinator = new LocalDeviceCoordinator(
            EndpointKind.Local,
            service,
            proxy);

        Assert.AreEqual(SystemProxyState.Off, coordinator.DetectSystemProxyState());
        await coordinator.EnableSystemProxyAsync(7890, "localhost;127.*");
        Assert.AreEqual(SystemProxyState.On, coordinator.SystemProxyState);
        Assert.AreEqual(1, proxy.EnableCount);
        Assert.AreEqual(7890, proxy.LastPort);
        Assert.AreEqual("localhost;127.*", proxy.LastBypassList);

        ServiceCorePayload corePayload = new(
            "C:\\ProgramData\\ClashTray\\active-config.yaml",
            "C:\\ProgramData\\ClashTray\\mihomo",
            9090,
            string.Empty);
        await coordinator.StartCoreAsync(corePayload);
        await coordinator.StopCoreAsync();
        await coordinator.EnableTunAsync(new ServiceTunPayload(9090, string.Empty, true));
        await coordinator.DisableTunAsync(new ServiceTunPayload(9090, string.Empty, false));
        await coordinator.GetStatusAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                ServiceCommand.StartCore,
                ServiceCommand.StopCore,
                ServiceCommand.EnableTun,
                ServiceCommand.DisableTun,
                ServiceCommand.GetStatus
            },
            service.Commands.ToArray());
        ServiceCorePayload? sentPayload = JsonSerializer.Deserialize<ServiceCorePayload>(
            service.Payloads[0]!,
            JsonOptions);
        Assert.IsNotNull(sentPayload);
        Assert.AreEqual(corePayload, sentPayload);

        await coordinator.DisableSystemProxyAsync();
        Assert.AreEqual(SystemProxyState.Off, coordinator.SystemProxyState);
        Assert.AreEqual(1, proxy.DisableCount);
    }

    [TestMethod]
    public void RemoteEndpointCannotConstructLocalCoordinator()
    {
        RecordingServicePipeClient service = new RecordingServicePipeClient();
        RecordingSystemProxyController proxy = new RecordingSystemProxyController();

        EndpointCommandDeniedException exception = Assert.ThrowsExactly<EndpointCommandDeniedException>(() =>
            new LocalDeviceCoordinator(EndpointKind.Remote, service, proxy));

        Assert.AreEqual(ErrorCode.EndpointCommandDenied, exception.ErrorCode);
        Assert.AreEqual(EndpointCommand.ControlLocalCore, exception.Command);
        Assert.AreEqual(0, service.Commands.Count);
        Assert.AreEqual(0, proxy.EnableCount);
        Assert.AreEqual(0, proxy.DisableCount);
    }

    private sealed class RecordingServicePipeClient : IServicePipeClient
    {
        public List<ServiceCommand> Commands { get; } = [];

        public List<string?> Payloads { get; } = [];

        public Task<ServiceResponse> SendAsync(
            ServiceCommand command,
            string? payload = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            Payloads.Add(payload);
            return Task.FromResult(new ServiceResponse(
                Guid.NewGuid(),
                true,
                TunState.Off,
                Core: command == ServiceCommand.StartCore ? CoreState.Running : CoreState.Stopped));
        }
    }

    private sealed class RecordingSystemProxyController : ISystemProxyController
    {
        public RecordingSystemProxyController()
        {
            State = SystemProxyState.Off;
        }

        public SystemProxyState State { get; private set; }

        public int EnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public int LastPort { get; private set; }

        public string LastBypassList { get; private set; } = string.Empty;

        public SystemProxyState DetectState() => State;

        public Task EnableAsync(int port, string bypassList, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableCount++;
            LastPort = port;
            LastBypassList = bypassList;
            State = SystemProxyState.On;
            return Task.CompletedTask;
        }

        public Task DisableAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisableCount++;
            State = SystemProxyState.Off;
            return Task.CompletedTask;
        }
    }
}
