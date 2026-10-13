using ClashTray.Testing;
namespace ClashTray.Core.Tests;

[TestClass]
public sealed class TestDirectoryCleanupTests
{
    [TestMethod]
    public async Task AtomicTemporaryFileDisappearanceDuringOwnedTreeInspectionDoesNotFailCleanup()
    {
        string root = TestFixtureDirectory.Create();
        string temporary = Path.Combine(root, "atomic.json.tmp");
        await File.WriteAllTextAsync(temporary, "fixture");
        try
        {
            await TestFixtureDirectory.DeleteAsync(root, beforeEntryInspection: entry =>
            {
                if (entry == temporary) { File.Delete(temporary); }
            });
            Assert.IsFalse(Directory.Exists(root));
        }
        finally { await TestFixtureDirectory.DeleteAsync(root); }
    }

    [TestMethod]
    public async Task FixtureReportsBothPrimaryAndCleanupFailureWithoutLosingEither()
    {
        string root = TestFixtureDirectory.Create();
        InvalidOperationException body = new("body failed");
        IOException cleanup = new("cleanup failed");
        try
        {
            AggregateException error = await Assert.ThrowsExactlyAsync<AggregateException>(() => TestFixtureDirectory.RunAsync(
                root, () => Task.FromException(body), () => Task.FromException(cleanup)));
            Assert.AreSame(body, error.InnerExceptions[0]);
            Assert.AreSame(cleanup, error.InnerExceptions[1]);
        }
        finally { await TestFixtureDirectory.DeleteAsync(root); }
    }

    [TestMethod]
    public async Task FixtureRefusesAnUnregisteredDirectoryEvenUnderTheTestParent()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => TestFixtureDirectory.DeleteAsync(root));
            Assert.IsTrue(Directory.Exists(root));
        }
        finally { Directory.Delete(root); }
    }

    [TestMethod]
    public async Task FixtureCleanupRetriesSharingViolationUntilOwnedHolderReleases()
    {
        string root = TestFixtureDirectory.Create();
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "held.tmp");
        await File.WriteAllTextAsync(path, "fixture");
        TaskCompletionSource sharing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? cleanup = null;
        try
        {
            using (FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                cleanup = TestDirectoryCleanup.DeleteAsync(root, () => sharing.TrySetResult());
                await sharing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(cleanup.IsCompleted);
            }

            await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(Directory.Exists(root));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => TestDirectoryCleanup.DeleteAsync(Path.GetTempPath()));
        }
        finally
        {
            if (cleanup is not null) { await cleanup; }
            await TestDirectoryCleanup.DeleteAsync(root);
        }
    }
}
