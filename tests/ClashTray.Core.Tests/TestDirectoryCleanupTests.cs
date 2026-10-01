namespace ClashTray.Core.Tests;

[TestClass]
public sealed class TestDirectoryCleanupTests
{
    [TestMethod]
    public async Task FixtureCleanupRetriesSharingViolationUntilOwnedHolderReleases()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));
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
