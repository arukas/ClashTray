using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace ClashTray.Testing;

internal static class LoopbackPortReservations
{
    internal const int AttemptsPerPort = 32;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Reservation lists own all TCP and bound UDP sockets and release them in finally; rejected UDP sockets have a per-attempt finally.")]
    public static int[] Allocate(int count, Func<int, TcpListener>? listenerFactory = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        int maximumAttempts = checked(count * AttemptsPerPort);
        List<TcpListener> tcpReservations = [];
        List<UdpClient> udpReservations = [];
        List<int> ports = [];
        int attempts = 0;
        int lastPort = 0;
        SocketException? lastFailure = null;
        try
        {
            while (ports.Count < count && attempts < maximumAttempts)
            {
                UdpClient? udp = null;
                try
                {
                    int attempt = attempts++;
                    lastPort = 0;
                    TcpListener tcp = listenerFactory?.Invoke(attempt) ?? new(IPAddress.Loopback, 0);
                    tcpReservations.Add(tcp);
                    tcp.ExclusiveAddressUse = true;
                    tcp.Start();
                    lastPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
                    udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, lastPort));
                    udpReservations.Add(udp);
                    udp = null;
                    ports.Add(lastPort);
                }
                catch (SocketException exception)
                {
                    lastFailure = exception;
                    // Retain rejected TCP candidates too: the OS must not return
                    // the same UDP-conflicted candidate throughout this batch.
                }
                finally
                {
                    udp?.Dispose();
                }
            }

            if (ports.Count != count)
            {
                throw new InvalidOperationException(
                    $"Test port allocation failed: requested={count}, allocated={ports.Count}, "
                    + $"attempts={attempts}, lastPort={lastPort}, "
                    + $"SocketError={lastFailure?.SocketErrorCode}, NativeError={lastFailure?.NativeErrorCode}.",
                    lastFailure);
            }

            return [.. ports];
        }
        finally
        {
            foreach (UdpClient udp in udpReservations) { udp.Dispose(); }
            foreach (TcpListener tcp in tcpReservations) { tcp.Stop(); }
        }
    }
}
