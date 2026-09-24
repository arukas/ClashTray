using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using ClashTray.Contracts;
using ClashTray.Core;

string root = Path.Combine(Path.GetTempPath(), "ClashTrayPerformanceHarness", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Console.WriteLine($"Runtime={Environment.Version}; Configuration=Release recommended; process={RuntimeInformation.ProcessArchitecture}");
    foreach (int lineCount in new[] { 256, 1024, 4096 })
    {
        await MeasureQuotedScalarAsync(root, $"short-{lineCount}", lineCount, 48);
    }

    await MeasureQuotedScalarAsync(root, "long-32", 32, 6_144);
    RunReconcilerBenchmarks();
}
finally
{
    if (Directory.Exists(root))
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task MeasureQuotedScalarAsync(string root, string name, int lineCount, int charactersPerLine)
{
    StringBuilder input = new();
    input.Append("secret: \"start").AppendLine();
    for (int index = 0; index < lineCount; index++)
    {
        input.Append("  ").Append('a', charactersPerLine).AppendLine();
    }

    input.Append("  finish\"").AppendLine()
        .Append("mode: direct").AppendLine()
        .Append("proxies: []").AppendLine()
        .Append("proxy-groups: []").AppendLine()
        .Append("rules: []").AppendLine()
        .Append("tun:").AppendLine()
        .Append("  enable: false").AppendLine();

    string sourcePath = Path.Combine(root, $"{name}.yaml");
    string destinationPath = Path.Combine(root, $"{name}.runtime.yaml");
    await File.WriteAllTextAsync(sourcePath, input.ToString());

    await RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, new AppSettings());
    long[] allocated = new long[7];
    double[] elapsedMs = new double[allocated.Length];
    for (int sample = 0; sample < allocated.Length; sample++)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
        long startTimestamp = Stopwatch.GetTimestamp();
        await RuntimeConfigBuilder.BuildAsync(sourcePath, destinationPath, new AppSettings());
        elapsedMs[sample] = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        allocated[sample] = GC.GetTotalAllocatedBytes(precise: true) - beforeBytes;
    }

    Array.Sort(allocated);
    Array.Sort(elapsedMs);
    long inputBytes = new FileInfo(sourcePath).Length;
    Console.WriteLine(
        $"YAML name={name} lines={lineCount} bytes={inputBytes} "
        + $"allocatedMedian={allocated[allocated.Length / 2]} allocatedMax={allocated[^1]} "
        + $"elapsedMedianMs={elapsedMs[elapsedMs.Length / 2]:F2} elapsedP95Ms={Percentile(elapsedMs, 0.95):F2}");
}

static void RunReconcilerBenchmarks()
{
    const int repetitions = 40;
    foreach (int size in new[] { 1_000, 2_000 })
    {
        BenchItem[] baseItems = Enumerable.Range(0, size).Select(id => new BenchItem(id, 0)).ToArray();
        int[] baseKeys = baseItems.Select(item => item.Id).ToArray();
        int[] reverseKeys = baseKeys.Reverse().ToArray();
        int[] partialKeys = baseKeys.Skip(size / 10).Concat(baseKeys.Take(size / 10)).ToArray();
        int[] filteredKeys = baseKeys.Where(id => id % 10 == 0).ToArray();
        BenchItem[] insertedItems = Enumerable.Range(-100, size + 100).Select(id => new BenchItem(id, 1)).ToArray();
        int[] insertedKeys = insertedItems.Select(item => item.Id).ToArray();
        BenchItem[] removedItems = baseItems.Skip(100).ToArray();
        int[] removedKeys = removedItems.Select(item => item.Id).ToArray();

        RunScenario(size, "stable", (baseItems, baseKeys), (baseItems, baseKeys));
        RunScenario(size, "reverse", (baseItems, reverseKeys), (baseItems, baseKeys));
        RunScenario(size, "partial", (baseItems, partialKeys), (baseItems, baseKeys));
        RunScenario(size, "head-insert", (insertedItems, insertedKeys), (baseItems, baseKeys));
        RunScenario(size, "head-evict", (baseItems, baseKeys), (removedItems, removedKeys));
        RunScenario(size, "filter", (baseItems, filteredKeys), (baseItems, baseKeys));
    }

    static void RunScenario(
        int size,
        string name,
        (BenchItem[] Source, int[] VisibleKeys) first,
        (BenchItem[] Source, int[] VisibleKeys) second)
    {
        StableRowReconciler<int, BenchItem, BenchRow> reconciler = new(item => item.Id);
        int collectionEvents = 0;
        reconciler.Rows.CollectionChanged += (_, _) => collectionEvents++;
        reconciler.Reconcile(
            Enumerable.Range(0, size).Select(id => new BenchItem(id, 0)).ToArray(),
            Enumerable.Range(0, size).ToArray(),
            item => new BenchRow(item),
            (row, item) => row.Update(item));

        for (int index = 0; index < 8; index++)
        {
            (BenchItem[] source, int[] keys) = index % 2 == 0
                ? first
                : second;
            reconciler.Reconcile(source, keys, item => new BenchRow(item), (row, item) => row.Update(item));
        }

        collectionEvents = 0;
        double[] samples = new double[repetitions];
        for (int index = 0; index < repetitions; index++)
        {
            (BenchItem[] source, int[] keys) = index % 2 == 0
                ? first
                : second;
            long started = Stopwatch.GetTimestamp();
            reconciler.Reconcile(source, keys, item => new BenchRow(item), (row, item) => row.Update(item));
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(samples);
        Console.WriteLine(
            $"ROWS n={size} case={name} repetitions={repetitions} p50Ms={samples[samples.Length / 2]:F2} "
            + $"p95Ms={Percentile(samples, 0.95):F2} collectionEvents={collectionEvents}");
    }
}

static double Percentile(double[] sortedSamples, double percentile)
{
    int index = Math.Clamp((int)Math.Ceiling(sortedSamples.Length * percentile) - 1, 0, sortedSamples.Length - 1);
    return sortedSamples[index];
}

internal sealed record BenchItem(int Id, int Revision);

internal sealed class BenchRow(BenchItem item)
{
    public int Revision { get; private set; } = item.Revision;

    public bool Update(BenchItem next)
    {
        if (Revision == next.Revision)
        {
            return false;
        }

        Revision = next.Revision;
        return true;
    }
}
