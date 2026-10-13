using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class LocalPortProbeTests
{
    [TestMethod]
    public void ControllerPortAllocatorFallsBackToFirstAvailableDistinctHighPort()
    {
        Queue<int> candidates = new([49152, 49153]);
        ControllerPortAllocationResult result = ControllerPortAllocator.Allocate(
            19090,
            ControllerPortConflictPolicy.AutomaticFallback,
            useAvailablePortOnce: false,
            fixedListeners: [],
            nextCandidate: () => candidates.Dequeue(),
            probePlan: bindings => new LocalPortPlanResult(bindings[0].Port == 19090 || bindings[0].Port == 49152
                ? new PortPlanConflict(bindings[0], new PortProbeResult(PortProbeStatus.InUse))
                : null));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(49153, result.Port);
        Assert.IsTrue(result.UsedFallback);
    }

    [TestMethod]
    public void FixedControllerPortFailsUnlessOneTimeOverrideIsRequested()
    {
        static LocalPortPlanResult Probe(IReadOnlyList<LocalPortBinding> bindings) =>
            new(new PortPlanConflict(bindings[0], new PortProbeResult(PortProbeStatus.InUse)));

        ControllerPortAllocationResult fixedResult = ControllerPortAllocator.Allocate(
            19090,
            ControllerPortConflictPolicy.Fixed,
            useAvailablePortOnce: false,
            fixedListeners: [],
            nextCandidate: static () => 49152,
            probePlan: Probe);
        ControllerPortAllocationResult overrideResult = ControllerPortAllocator.Allocate(
            19090,
            ControllerPortConflictPolicy.Fixed,
            useAvailablePortOnce: true,
            fixedListeners: [],
            nextCandidate: static () => 49152,
            probePlan: bindings => bindings[0].Port == 19090
                ? new LocalPortPlanResult(new PortPlanConflict(bindings[0], new PortProbeResult(PortProbeStatus.InUse)))
                : new LocalPortPlanResult(null));

        Assert.IsFalse(fixedResult.Succeeded);
        Assert.AreEqual(49152, overrideResult.Port);
    }

    [TestMethod]
    public void ControllerPortAllocatorStopsAfterSixteenDistinctFallbackCandidates()
    {
        Queue<int> candidates = new(Enumerable.Range(49152, ControllerPortAllocator.MaximumFallbackCandidates + 1));
        HashSet<int> probedCandidates = [];
        ControllerPortAllocationResult result = ControllerPortAllocator.Allocate(
            19090,
            ControllerPortConflictPolicy.AutomaticFallback,
            useAvailablePortOnce: false,
            fixedListeners: [],
            nextCandidate: () => candidates.Dequeue(),
            probePlan: bindings =>
            {
                int port = bindings[0].Port;
                if (port != 19090)
                {
                    Assert.IsTrue(probedCandidates.Add(port), "A failed candidate must not be probed twice.");
                }

                return new LocalPortPlanResult(new PortPlanConflict(
                    bindings[0],
                    new PortProbeResult(PortProbeStatus.InUse)));
            });

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ControllerPortAllocator.MaximumFallbackCandidates, result.FallbackCandidatesExamined);
        Assert.AreEqual(ControllerPortAllocator.MaximumFallbackCandidates, probedCandidates.Count);
        Assert.AreEqual(1, candidates.Count, "The allocator must stop before drawing a seventeenth fallback candidate.");
    }

    [TestMethod]
    public void ControllerPortAllocatorHonorsCancellationBeforeOpeningSockets()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        bool probeCalled = false;

        Assert.ThrowsExactly<OperationCanceledException>(() => ControllerPortAllocator.Allocate(
            19090,
            ControllerPortConflictPolicy.AutomaticFallback,
            useAvailablePortOnce: false,
            fixedListeners: [],
            nextCandidate: static () => 49152,
            probePlan: _ =>
            {
                probeCalled = true;
                return new LocalPortPlanResult(null);
            },
            cancellationToken: cancellation.Token));

        Assert.IsFalse(probeCalled, "A canceled startup must not continue probing or reserve listener sockets.");
    }

    [TestMethod]
    public void ControllerPortAllocatorDoesNotMaskAConflictingFixedProxyListener()
    {
        LocalPortBinding mixed = new("mixed", IPAddress.Loopback, 7890, PortTransport.Tcp);
        ControllerPortAllocationResult result = ControllerPortAllocator.Allocate(
            19090,
            ControllerPortConflictPolicy.AutomaticFallback,
            useAvailablePortOnce: false,
            fixedListeners: [mixed],
            nextCandidate: static () => throw new AssertFailedException("Proxy conflict must not trigger controller fallback."),
            probePlan: bindings => new LocalPortPlanResult(new PortPlanConflict(
                bindings[1],
                new PortProbeResult(PortProbeStatus.InUse))));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("mixed", result.Conflict?.Listener.Name);
    }

    [TestMethod]
    public void ProbeReportsAnOccupiedTcpListenerWithoutTreatingUdpAsConflicting()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.ExclusiveAddressUse = true;
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        PortProbeResult tcp = LocalPortProbe.Probe(IPAddress.Loopback, port, PortTransport.Tcp);
        PortProbeResult udp = LocalPortProbe.Probe(IPAddress.Loopback, port, PortTransport.Udp);

        Assert.AreEqual(PortProbeStatus.InUse, tcp.Status);
        Assert.AreEqual(SocketError.AddressAlreadyInUse, tcp.SocketError);
        Assert.AreEqual(PortProbeStatus.Available, udp.Status);
    }

    [TestMethod]
    public void ProbeDoesNotTreatIpv6OnlyBindingAsIpv4LoopbackConflict()
    {
        using Socket listener = new(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp)
        {
            DualMode = false,
            ExclusiveAddressUse = true
        };
        listener.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        PortProbeResult result = LocalPortProbe.Probe(IPAddress.Loopback, port, PortTransport.Tcp);

        Assert.AreEqual(PortProbeStatus.Available, result.Status);
    }

    [TestMethod]
    public void ProbeClassifiesAccessDeniedAndUnrecognizedSocketErrorsSeparately()
    {
        Assert.AreEqual(
            PortProbeStatus.AccessDenied,
            LocalPortProbe.Classify(new SocketException((int)SocketError.AccessDenied)));
        Assert.AreEqual(
            PortProbeStatus.InvalidAddress,
            LocalPortProbe.Classify(new SocketException((int)SocketError.AddressNotAvailable)));
        Assert.AreEqual(
            PortProbeStatus.Unknown,
            LocalPortProbe.Classify(new SocketException((int)SocketError.OperationNotSupported)));
    }

    [TestMethod]
    public void ProbePlanReportsInternalDuplicateAndWildcardConflicts()
    {
        int duplicatePort = GetAvailableTcpPort();
        LocalPortPlanResult duplicate = LocalPortProbe.ProbePlan(
        [
            new LocalPortBinding("controller", IPAddress.Loopback, duplicatePort, PortTransport.Tcp),
            new LocalPortBinding("mixed", IPAddress.Loopback, duplicatePort, PortTransport.Tcp)
        ]);

        Assert.IsNotNull(duplicate.Conflict);
        Assert.IsTrue(duplicate.Conflict.IsInternalConflict);
        Assert.AreEqual("controller", duplicate.Conflict.ConflictingWith);
        Assert.AreEqual("mixed", duplicate.Conflict.Listener.Name);

        int dualStackPort = GetAvailableTcpPort();
        LocalPortPlanResult dualStack = LocalPortProbe.ProbePlan(
        [
            new LocalPortBinding("ipv6-dual-stack", IPAddress.IPv6Any, dualStackPort, PortTransport.Tcp, DualMode: true),
            new LocalPortBinding("ipv4-wildcard", IPAddress.Any, dualStackPort, PortTransport.Tcp)
        ]);

        Assert.IsNotNull(dualStack.Conflict);
        Assert.IsTrue(dualStack.Conflict.IsInternalConflict);
    }

    [TestMethod]
    public void ProbePlanAllowsNonDualModeIpv6OnlyAndIpv4LoopbackOnSamePort()
    {
        int port = GetAvailableTcpPort();

        LocalPortPlanResult result = LocalPortProbe.ProbePlan(
        [
            new LocalPortBinding("ipv6-only", IPAddress.IPv6Any, port, PortTransport.Tcp),
            new LocalPortBinding("ipv4-loopback", IPAddress.Loopback, port, PortTransport.Tcp)
        ]);

        Assert.IsTrue(result.IsAvailable, result.Conflict?.ToString());
    }

    [TestMethod]
    public void ListenerOwnerTableConfirmsCurrentTcpPidAndRejectsStaleProcessIdentity()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.ExclusiveAddressUse = true;
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        LocalCoreProcessIdentity owner = CaptureCurrentProcessIdentity();

        Assert.IsTrue(WindowsListenerOwnerTable.IsOwnedBy(IPAddress.Loopback, port, PortTransport.Tcp, owner));
        Assert.IsFalse(WindowsListenerOwnerTable.IsOwnedBy(
            IPAddress.Loopback,
            port,
            PortTransport.Tcp,
            owner with { StartTimeUtcTicks = owner.StartTimeUtcTicks + 1 }));
    }

    [TestMethod]
    public void ListenerOwnerTableConfirmsUdpBindingOwner()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        listener.ExclusiveAddressUse = true;
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Assert.IsTrue(WindowsListenerOwnerTable.IsOwnedBy(
            IPAddress.Loopback,
            port,
            PortTransport.Udp,
            CaptureCurrentProcessIdentity()));
    }

    private static LocalCoreProcessIdentity CaptureCurrentProcessIdentity()
    {
        using Process process = Process.GetCurrentProcess();
        return new LocalCoreProcessIdentity(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            Path.GetFullPath(process.MainModule!.FileName!));
    }

    private static int GetAvailableTcpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
