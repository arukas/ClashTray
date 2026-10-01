using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.PerformanceHarness;

internal static class ListenerOwnershipBenchmark
{
    public static void Run()
    {
        using TcpListener controller = StartTcp();
        using TcpListener http = StartTcp();
        using TcpListener socks = StartTcp();
        using TcpListener mixed = StartTcp();
        using UdpClient socksUdp = new(new IPEndPoint(IPAddress.Loopback, Port(socks)));
        using UdpClient mixedUdp = new(new IPEndPoint(IPAddress.Loopback, Port(mixed)));
        LocalCoreProcessIdentity identity = WindowsListenerOwnerTable.CaptureProcessIdentity(Environment.ProcessId);
        CoreRuntimeBinding binding = new(Port(controller), Port(controller), Guid.NewGuid(), Guid.NewGuid(),
            identity.ProcessId, identity.StartTimeUtcTicks, 1, Port(http), Port(socks), Port(mixed),
            true, true, true, true, identity.ExecutablePath,
            ListenerBindings:
            [
                new("controller", "127.0.0.1", Port(controller), RuntimeListenerTransport.Tcp),
                new("http-tcp", "127.0.0.1", Port(http), RuntimeListenerTransport.Tcp),
                new("socks-tcp", "127.0.0.1", Port(socks), RuntimeListenerTransport.Tcp),
                new("socks-udp", "127.0.0.1", Port(socks), RuntimeListenerTransport.Udp),
                new("mixed-tcp", "127.0.0.1", Port(mixed), RuntimeListenerTransport.Tcp),
                new("mixed-udp", "127.0.0.1", Port(mixed), RuntimeListenerTransport.Udp)
            ]);
        Console.WriteLine($"ListenerOwnership runtime={Environment.Version} architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} listeners=6");
        Measure("single", () => RuntimeBindingValidator.AreListenersOwned(binding,
            listener => WindowsListenerOwnerTable.InspectListener(listener.Address, listener.Port,
                listener.Transport, identity, listener.DualMode)));
        Measure("batch", () => RuntimeBindingValidator.AreListenersOwned(binding, identity));
    }

    private static void Measure(string strategy, Func<bool> observe)
    {
        const int repetitions = 100;
        const int samples = 11;
        for (int warmup = 0; warmup < 20; warmup++)
        {
            RequireOwned(observe);
        }

        double[] allocations = new double[samples];
        double[] timings = new double[samples];
        for (int sample = 0; sample < samples; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int iteration = 0; iteration < repetitions; iteration++)
            {
                RequireOwned(observe);
            }

            timings[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds / repetitions;
            allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)repetitions;
        }

        Array.Sort(allocations);
        Array.Sort(timings);
        Console.WriteLine($"ListenerOwnership strategy={strategy} samples={samples} repetitions={repetitions} allocatedMedianBytes={allocations[samples / 2]:F0} elapsedMedianMs={timings[samples / 2]:F3} elapsedP95Ms={timings[^1]:F3}");
    }

    private static void RequireOwned(Func<bool> observe)
    {
        if (!observe())
        {
            throw new InvalidOperationException("The isolated benchmark's own TCP/UDP listeners were not confirmed.");
        }
    }

    private static TcpListener StartTcp()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
}
