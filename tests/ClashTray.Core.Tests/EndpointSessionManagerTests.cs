using System.Collections.Concurrent;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class EndpointSessionManagerTests
{
    [TestMethod]
    public async Task SelectCreatesConnectedSessionWithGenerationAndRevision()
    {
        EndpointDescriptor endpoint = CreateEndpoint("office");
        RecordingConnector connector = new((target, generation, revision, _) =>
            Task.FromResult(CreateSession(target, generation, revision)));
        await using EndpointSessionManager manager = CreateManager(connector);
        List<EndpointSessionState> states = [];
        manager.StatusChanged += (_, status) => states.Add(status.State);

        EndpointSession? session = await manager.SelectAsync(endpoint);

        Assert.IsNotNull(session);
        Assert.AreSame(session, manager.Current);
        Assert.AreEqual(EndpointSessionState.Connected, manager.Status.State);
        Assert.AreEqual(1, manager.Status.Generation);
        Assert.AreEqual(1, manager.Status.SelectionRevision);
        Assert.AreEqual(endpoint.Id, session.Endpoint.Id);
        CollectionAssert.AreEqual(
            new[] { EndpointSessionState.Connecting, EndpointSessionState.Connected },
            states);
        Assert.AreEqual(1, connector.CallCount);
        Assert.AreEqual(1, connector.Calls.Single().Generation);
        Assert.AreEqual(1, connector.Calls.Single().SelectionRevision);
    }

    [TestMethod]
    public async Task TestPerformsReadOnlyHandshakeWithoutReplacingCurrentSession()
    {
        EndpointDescriptor endpoint = CreateEndpoint("office");
        RecordingConnector connector = new((target, generation, revision, _) =>
            Task.FromResult(CreateSession(target, generation, revision)));
        await using EndpointSessionManager manager = CreateManager(connector);
        List<EndpointSessionState> states = [];
        manager.StatusChanged += (_, status) => states.Add(status.State);

        EndpointSession? current = await manager.SelectAsync(endpoint);
        EndpointSessionStatusEventArgs before = manager.Status;

        EndpointHandshakeResult handshake = await manager.TestAsync(endpoint);

        Assert.IsNotNull(current);
        Assert.AreEqual(EndpointSessionState.Connected, handshake.State);
        Assert.AreSame(current, manager.Current);
        Assert.AreEqual(before.Endpoint.Id, manager.Status.Endpoint.Id);
        Assert.AreEqual(before.Generation, manager.Status.Generation);
        Assert.AreEqual(before.SelectionRevision, manager.Status.SelectionRevision);
        Assert.AreEqual(before.State, manager.Status.State);
        CollectionAssert.AreEqual(
            new[] { EndpointSessionState.Connecting, EndpointSessionState.Connected },
            states);
        Assert.AreEqual(2, connector.CallCount);
        Assert.AreEqual(1, connector.Calls.First().Generation);
        Assert.AreEqual(1, connector.Calls.Last().Generation);
    }

    [TestMethod]
    public async Task TransientFailuresUseBoundedBackoffBeforeConnecting()
    {
        EndpointDescriptor endpoint = CreateEndpoint("office");
        int calls = 0;
        RecordingConnector connector = new((target, generation, revision, _) =>
        {
            int call = Interlocked.Increment(ref calls);
            return call <= 3
                ? Task.FromException<EndpointSession>(new EndpointSessionConnectException(
                    EndpointSessionState.Failed,
                    isTransient: true,
                    "temporary controller unavailable"))
                : Task.FromResult(CreateSession(target, generation, revision));
        });
        List<TimeSpan> delays = [];
        EndpointSessionBackoffPolicy policy = new(
            [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5)],
            TimeSpan.Zero);
        await using EndpointSessionManager manager = new(
            ControllerEndpointFactory.CreateLocal(9090),
            connector,
            policy,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        EndpointSession? session = await manager.SelectAsync(endpoint);

        Assert.IsNotNull(session);
        Assert.AreEqual(4, connector.CallCount);
        CollectionAssert.AreEqual(
            new[]
            {
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(2),
                TimeSpan.FromMilliseconds(5)
            },
            delays);
        Assert.AreEqual(EndpointSessionState.Connected, manager.Status.State);
        Assert.AreEqual(4, manager.Status.Attempt);
    }

    [TestMethod]
    public async Task AuthenticationFailureWaitsForUserWithoutRetrying()
    {
        EndpointDescriptor endpoint = CreateEndpoint("office");
        RecordingConnector connector = new((_, _, _, _) =>
            Task.FromException<EndpointSession>(new EndpointSessionConnectException(
                EndpointSessionState.AuthenticationFailed,
                isTransient: false,
                "controller rejected Authorization: Bearer secret")));
        int delayCount = 0;
        await using EndpointSessionManager manager = new(
            ControllerEndpointFactory.CreateLocal(9090),
            connector,
            new EndpointSessionBackoffPolicy([TimeSpan.FromMilliseconds(1)], TimeSpan.Zero),
            (_, _) =>
            {
                Interlocked.Increment(ref delayCount);
                return Task.CompletedTask;
            });

        EndpointSession? session = await manager.SelectAsync(endpoint);

        Assert.IsNull(session);
        Assert.AreEqual(1, connector.CallCount);
        Assert.AreEqual(0, delayCount);
        Assert.AreEqual(EndpointSessionState.AuthenticationFailed, manager.Status.State);
        Assert.AreEqual(ErrorCode.EndpointAuthenticationFailed, manager.Status.ErrorCode);
        Assert.IsNotNull(manager.Status.ErrorMessage);
        Assert.IsFalse(manager.Status.ErrorMessage!.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task NewSelectionInvalidatesLateResultFromPreviousGeneration()
    {
        EndpointDescriptor firstEndpoint = CreateEndpoint("first");
        EndpointDescriptor secondEndpoint = CreateEndpoint("second");
        TaskCompletionSource<EndpointSession> firstResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingConnector connector = new((target, generation, revision, _) =>
        {
            if (target.Id == firstEndpoint.Id)
            {
                firstCall.TrySetResult(true);
                return firstResult.Task;
            }

            return Task.FromResult(CreateSession(target, generation, revision));
        });
        await using EndpointSessionManager manager = CreateManager(connector);

        Task<EndpointSession?> firstSelection = manager.SelectAsync(firstEndpoint);
        await firstCall.Task;
        EndpointSession? secondSession = await manager.SelectAsync(secondEndpoint);

        Assert.IsNotNull(secondSession);
        await using EndpointSession lateSession = CreateSession(firstEndpoint, 1, 1);
        firstResult.SetResult(lateSession);
        EndpointSession? staleSession = await firstSelection;

        Assert.IsNull(staleSession);
        Assert.AreSame(secondSession, manager.Current);
        Assert.AreEqual(secondEndpoint.Id, manager.Status.Endpoint.Id);
        Assert.AreEqual(2, manager.Status.Generation);
        Assert.AreEqual(2, manager.Status.SelectionRevision);
    }

    [TestMethod]
    public async Task DisconnectInvalidatesCurrentGenerationAndReturnsToLocalTarget()
    {
        EndpointDescriptor endpoint = CreateEndpoint("office");
        RecordingConnector connector = new((target, generation, revision, _) =>
            Task.FromResult(CreateSession(target, generation, revision)));
        await using EndpointSessionManager manager = CreateManager(connector);

        await manager.SelectAsync(endpoint);
        await manager.DisconnectAsync();

        Assert.IsNull(manager.Current);
        Assert.AreEqual(EndpointId.Local, manager.Status.Endpoint.Id);
        Assert.AreEqual(EndpointSessionState.Disconnected, manager.Status.State);
        Assert.AreEqual(2, manager.Status.Generation);
        Assert.AreEqual(2, manager.Status.SelectionRevision);
    }

    [TestMethod]
    public void BackoffPolicyCapsAtThirtySecondsAndAddsOnlyBoundedJitter()
    {
        EndpointSessionBackoffPolicy policy = new(
            maxJitter: TimeSpan.FromMilliseconds(10),
            random: new FixedRandom(0.5));

        TimeSpan first = policy.GetDelay(1);
        TimeSpan later = policy.GetDelay(99);

        Assert.AreEqual(TimeSpan.FromSeconds(1), first);
        Assert.AreEqual(TimeSpan.FromSeconds(30), later);
    }

    private static EndpointSessionManager CreateManager(RecordingConnector connector) =>
        new(ControllerEndpointFactory.CreateLocal(9090), connector);

    private static EndpointDescriptor CreateEndpoint(string id) =>
        EndpointUriNormalizer.CreateRemoteDescriptor(
            new EndpointId(id),
            id,
            new Uri($"https://{id}.example.test"));

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "EndpointSession takes ownership of the transport and disposes it asynchronously.")]
    private static EndpointSession CreateSession(
        EndpointDescriptor endpoint,
        long generation,
        long selectionRevision)
    {
        EndpointTransport transport = EndpointTransportFactory.Create(endpoint);
        return new EndpointSession(
            transport,
            EndpointCapabilityDefaults.Remote,
            generation,
            selectionRevision);
    }

    private sealed class RecordingConnector : IEndpointSessionConnector
    {
        private readonly Func<EndpointDescriptor, long, long, CancellationToken, Task<EndpointSession>> _handler;
        private int _callCount;

        public RecordingConnector(
            Func<EndpointDescriptor, long, long, CancellationToken, Task<EndpointSession>> handler)
        {
            _handler = handler;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public ConcurrentQueue<Call> Calls { get; } = new();

        public Task<EndpointSession> ConnectAsync(
            EndpointDescriptor endpoint,
            long generation,
            long selectionRevision,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Calls.Enqueue(new Call(endpoint.Id, generation, selectionRevision));
            return _handler(endpoint, generation, selectionRevision, cancellationToken);
        }

        public sealed record Call(EndpointId EndpointId, long Generation, long SelectionRevision);
    }

    private sealed class FixedRandom : Random
    {
        private readonly double _value;

        public FixedRandom(double value)
        {
            _value = value;
        }

        public override double NextDouble() => _value;
    }
}
