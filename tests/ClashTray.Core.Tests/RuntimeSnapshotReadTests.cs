using System.Reflection;
using ClashTray.Contracts;
using ClashTray.Testing;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class RuntimeSnapshotReadTests
{
    [TestMethod]
    public async Task ReadsDoNotDrainPendingLogsButTheNextPublishIncludesThem()
    {
        string root = TestFixtureDirectory.Create();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            await using ClashTrayRuntime runtime = RuntimeTestBuilder.Create(
                new AppPaths(Path.Combine(root, "user"), Path.Combine(root, "service")));
            RuntimeLogCoordinator logs = Field<RuntimeLogCoordinator>(runtime, "_logs");
            RuntimeStateStore state = Field<RuntimeStateStore>(runtime, "_stateStore");
            TaskCompletionSource<RuntimeSnapshot> published = new(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.SnapshotChanged += (_, snapshot) => published.TrySetResult(snapshot);

            // Hold publication at its real boundary to avoid racing the throttle.
            // Pending data must remain pending even if consumers repeatedly read.
            lock (Field<object>(runtime, "_publishGate"))
            {
                RuntimeSnapshot confirmed = state.Snapshot;
                long revision = state.Revision;
                logs.AddMihomoLog(new(DateTimeOffset.UtcNow, "mihomo", "info", "pending line"));
                Assert.AreSame(confirmed, runtime.Snapshot);
                Assert.AreSame(confirmed.Logs, runtime.AppSnapshot.ActiveController.Logs);
                Assert.AreSame(confirmed, runtime.Snapshot);
                Assert.AreEqual(revision, state.Revision);
            }

            RuntimeSnapshot next = await published.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual("pending line", next.Logs.Single().Message);
            Assert.AreSame(next, runtime.Snapshot);
        });
    }

    private static T Field<T>(ClashTrayRuntime runtime, string name) where T : class =>
        typeof(ClashTrayRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(runtime) as T
        ?? throw new InvalidOperationException($"Runtime field {name} was unavailable.");
}
