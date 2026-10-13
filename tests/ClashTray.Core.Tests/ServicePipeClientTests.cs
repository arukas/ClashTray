using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ServicePipeClientTests
{
    [TestMethod]
    [DataRow(ServiceCommand.StartCore)]
    [DataRow(ServiceCommand.StopCore)]
    [DataRow(ServiceCommand.RestartCore)]
    [DataRow(ServiceCommand.EnableTun)]
    [DataRow(ServiceCommand.DisableTun)]
    [DataRow(ServiceCommand.InstallCore)]
    [DataRow(ServiceCommand.RollbackCore)]
    public async Task LostResponseRejoinsTheOriginalBusinessOperationWithoutRepeatingItsSideEffect(ServiceCommand command)
    {
        LostResponseTransport transport = new();
        ServicePipeClient client = new(transport);
        ServiceResponse response = await client.SendAsync(command);
        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual(1, transport.ExecutionCount);
        Assert.AreEqual(2, transport.Attempts.Count);
        Assert.AreEqual(transport.Attempts[0].RequestId, transport.Attempts[1].RequestId);
        Assert.AreEqual(transport.Attempts[0].Payload, transport.Attempts[1].Payload);
        Assert.IsTrue(transport.Attempts[1].RecoveryOnly);
    }

    [TestMethod]
    public async Task ResponseIdentityMismatchAndOldProtocolFailSafely()
    {
        DelegateTransport mismatch = new(request => new ServiceResponse(Guid.NewGuid(), true, TunState.Off, ProtocolVersion: ServiceProtocol.CurrentVersion));
        ServiceRequestUnknownException unknown = await Assert.ThrowsExactlyAsync<ServiceRequestUnknownException>(() => new ServicePipeClient(mismatch).SendAsync(ServiceCommand.StopCore));
        Assert.AreEqual(ServiceDispatchState.DispatchedAwaitingResult, unknown.DispatchState);
        Assert.AreEqual(2, mismatch.AttemptCount);
        DelegateTransport old = new(request => new ServiceResponse(request.RequestId, false, TunState.Off, ProtocolVersion: 2));
        await Assert.ThrowsExactlyAsync<ServiceProtocolVersionMismatchException>(() => new ServicePipeClient(old).SendAsync(ServiceCommand.StopCore));
        Assert.AreEqual(1, old.AttemptCount);
    }

    [TestMethod]
    public async Task BoundedWireReaderRejectsGrowingResponseAndObservesCancellation()
    {
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(new string('x', ServiceProtocol.MaximumResponseCharacters + 1)));
        using StreamReader reader = new(stream);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ServiceMessageReader.ReadLineAsync(reader, ServiceProtocol.MaximumResponseCharacters, CancellationToken.None));
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => ServiceMessageReader.ReadLineAsync(reader, 10, cancelled.Token));
    }

    private sealed class DelegateTransport(Func<ServiceRequest, ServiceResponse> createResponse) : IServiceRequestTransport
    {
        public int AttemptCount { get; private set; }
        public Task<ServiceResponse> SendAsync(ServiceRequest request, CancellationToken cancellationToken)
        {
            AttemptCount++;
            return Task.FromResult(createResponse(request));
        }
    }

    private sealed class LostResponseTransport : IServiceRequestTransport
    {
        public List<ServiceRequest> Attempts { get; } = [];
        public int ExecutionCount { get; private set; }
        public Task<ServiceResponse> SendAsync(ServiceRequest request, CancellationToken cancellationToken)
        {
            Attempts.Add(request);
            if (Attempts.Count == 1)
            {
                ExecutionCount++;
                throw new ServiceRequestUnknownException("isolated transport dropped the successful response", null, request.RequestId, ServiceDispatchState.DispatchedAwaitingResult);
            }
            return Task.FromResult(new ServiceResponse(request.RequestId, true, TunState.Off, ProtocolVersion: ServiceProtocol.CurrentVersion));
        }
    }

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
