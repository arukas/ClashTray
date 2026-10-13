using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ClashTray.Testing;

internal static class LoopbackPortReservations
{
    internal const int AttemptsPerPort = 32;

    // Windows excluded port ranges (Hyper-V/WinNAT) are per protocol: an OS-assigned
    // TCP port can sit inside a UDP-excluded block, and bind(0) keeps returning
    // neighbours of the same blocked region until the retry budget is exhausted.
    // Probe explicit candidates strided across the whole dynamic range instead.
    private const int MinDynamicPort = 49152;
    private const int DynamicPortSpan = 65535 - MinDynamicPort + 1;
    private const int CandidateStride = 997; // Odd stride is coprime with the 2^14 span, so candidates never repeat within a batch.

    internal static int CandidatePort(int startOffset, int attempt)
        => MinDynamicPort + (int)(((long)startOffset + (long)attempt * CandidateStride) % DynamicPortSpan);

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
        int startOffset = RandomNumberGenerator.GetInt32(DynamicPortSpan);
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
                    TcpListener tcp = listenerFactory?.Invoke(attempt) ?? new(IPAddress.Loopback, CandidatePort(startOffset, attempt));
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
