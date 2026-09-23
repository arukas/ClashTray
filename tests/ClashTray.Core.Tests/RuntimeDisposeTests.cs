using System.Diagnostics;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeDisposeTests
{
    [TestMethod]
    public async Task DisposeUsesBoundedAdmissionWaitAndKeepsGateAliveForOutstandingReader()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        await using ClashTrayRuntime runtime = new(
            paths,
            null,
            null,
            null,
            disposeCleanupTimeout: TimeSpan.FromMilliseconds(100));
        OperationGate.Lease reader = await runtime.AcquireSharedOperationForTestingAsync();

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            // If Runtime.DisposeCoreAsync disposed the gate after timing out,
            // releasing this still-admitted operation would throw.
            reader.Dispose();
        }
        finally
        {
            reader.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}