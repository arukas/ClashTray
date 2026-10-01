using System.Diagnostics;
using System.Runtime.InteropServices;
using ClashTray.Contracts;
using ClashTray.Core;

namespace ClashTray.PerformanceHarness;

internal static class LogSnapshotBenchmark
{
    internal static void Run()
    {
        LogEntry[] entries = Enumerable.Range(0, 10_000)
            .Select(i => new LogEntry(DateTimeOffset.UnixEpoch, "mihomo", "info", $"sample {i}"))
            .ToArray();
        Console.WriteLine($"Runtime={Environment.Version}; process={RuntimeInformation.ProcessArchitecture}; lines={entries.Length}; capacity=500; batch=250");
        foreach (bool batched in new[] { false, true })
        {
            Measure(entries, batched);
            long[] allocations = new long[7];
            double[] durations = new double[7];
            for (int i = 0; i < allocations.Length; i++)
            {
                (allocations[i], durations[i]) = Measure(entries, batched);
            }

            Array.Sort(allocations);
            Array.Sort(durations);
            Console.WriteLine($"strategy={(batched ? "batched" : "per-line")} allocatedMedian={allocations[3]} elapsedMedianMs={durations[3]:F3} elapsedMaxMs={durations[^1]:F3}");
        }
    }

    private static (long Allocated, double ElapsedMs) Measure(LogEntry[] entries, bool batched)
    {
        RuntimeStateStore store = new(new RuntimeSnapshot(
            new CoreStatus(CoreState.Stopped, null, null, ProxyMode.Rule, 0, 0, 0, 0, 0, 0, null),
            SystemProxyState.Off, TunState.Off, SubscriptionState.Idle, [], [], [], [], [], [], [], [], null));
        using RuntimeLogCoordinator logs = new(store, static () => null, static () => false,
            static () => "info", static () => { }, static () => { }, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < entries.Length; i++)
        {
            if (batched)
            {
                logs.AddMihomoLog(entries[i]);
                if ((i + 1) % 250 == 0)
                {
                    logs.FlushPendingLogs();
                }
            }
            else
            {
                // Same bounded ingestion, with the former per-line materialization policy.
                logs.AddApplicationLog(entries[i]);
            }
        }

        logs.FlushPendingLogs();
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (store.Snapshot.Logs.Count != 500 || store.Snapshot.Logs[^1].Message != entries[^1].Message)
        {
            throw new InvalidOperationException("The benchmark did not retain the newest bounded log window.");
        }

        return (allocated, elapsed);
    }
}
