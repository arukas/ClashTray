using System.Net;
using System.Net.Sockets;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class LoopbackPortReservationsTests
{
    [TestMethod]
    public void UdpConflictIsRetainedDuringSelectionAndAllReservationsAreReleased()
    {
        using UdpClient conflict = CreateUdpConflict();
        int conflictedPort = ((IPEndPoint)conflict.Client.LocalEndPoint!).Port;
        int attempts = 0;
        int[] ports = LoopbackPortReservations.Allocate(4, attempt =>
        {
            attempts++;
            if (attempt == 0) { return new TcpListener(IPAddress.Loopback, conflictedPort); }
            using TcpListener probe = new(IPAddress.Loopback, conflictedPort);
            Assert.ThrowsExactly<SocketException>(() => probe.Start());
            return new TcpListener(IPAddress.Loopback, 0);
        });

        Assert.IsTrue(attempts >= 5, "The UDP-conflicted candidate must be rejected before allocating four ports.");
        Assert.AreEqual(4, ports.Distinct().Count());
        Assert.IsFalse(ports.Contains(conflictedPort));
        using TcpListener releasedConflict = new(IPAddress.Loopback, conflictedPort);
        releasedConflict.Start();
        foreach (int port in ports)
        {
            using TcpListener tcp = new(IPAddress.Loopback, port);
            tcp.Start();
            using UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, port));
        }
    }

    [TestMethod]
    public void ExhaustedUdpConflictsReportDiagnosticsAndReleaseTcpReservations()
    {
        List<UdpClient> conflicts = [];
        try
        {
            for (int i = 0; i < LoopbackPortReservations.AttemptsPerPort; i++)
            {
                conflicts.Add(CreateUdpConflict());
            }
            int[] ports = conflicts.Select(udp => ((IPEndPoint)udp.Client.LocalEndPoint!).Port).ToArray();
            int attempts = 0;
            InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() =>
                LoopbackPortReservations.Allocate(1, attempt =>
                {
                    attempts++;
                    return new TcpListener(IPAddress.Loopback, ports[attempt]);
                }));

            Assert.AreEqual(LoopbackPortReservations.AttemptsPerPort, attempts);
            StringAssert.Contains(error.Message, "requested=1, allocated=0", StringComparison.Ordinal);
            StringAssert.Contains(error.Message, $"attempts={attempts}, lastPort={ports[^1]}", StringComparison.Ordinal);
            StringAssert.Contains(error.Message, "SocketError=", StringComparison.Ordinal);
            Assert.IsInstanceOfType<SocketException>(error.InnerException);
            foreach (int port in ports)
            {
                using TcpListener tcp = new(IPAddress.Loopback, port);
                tcp.Start();
            }
        }
        finally
        {
            foreach (UdpClient udp in conflicts) { udp.Dispose(); }
        }
    }

    private static UdpClient CreateUdpConflict()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            using TcpListener tcp = new(IPAddress.Loopback, 0);
            tcp.Start();
            int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            UdpClient udp = new(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
            try
            {
                udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                return udp;
            }
            catch (SocketException)
            {
                udp.Dispose();
            }
        }
        throw new InvalidOperationException("Could not create a UDP-conflicted TCP candidate for the test.");
    }
}
