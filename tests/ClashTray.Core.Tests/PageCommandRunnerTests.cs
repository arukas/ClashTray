using ClashTray.App;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class PageCommandRunnerTests
{
    [TestMethod]
    public async Task CompletedResultIsRecheckedWhenUiPresentationOccursAfterNavigation()
    {
        PageCommandRunner runner = new();
        PageCommandResult completed = await runner.RunAsync(() => Task.CompletedTask);
        Assert.IsTrue(completed.CanPresent);
        // The operation has finished, but its UI continuation can still be
        // queued behind a navigation event. Check the generation at rendering.
        runner.Deactivate();
        runner.Activate();
        Assert.IsFalse(completed.CanPresent);
    }

    [TestMethod]
    public async Task RepeatedSaveDoesNotAdmitDuplicateOperationAndBusyClearsAfterCompletion()
    {
        PageCommandRunner runner = new();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int executions = 0;
        Task<PageCommandResult> first = runner.RunAsync(() => { executions++; return completion.Task; });
        Assert.IsTrue(runner.IsBusy);
        PageCommandResult second = await runner.RunAsync(() => { executions++; return Task.CompletedTask; });
        Assert.IsFalse(second.Admitted);
        completion.SetResult();
        Assert.IsTrue((await first).Succeeded);
        Assert.AreEqual(1, executions);
        Assert.IsFalse(runner.IsBusy);
        Assert.IsTrue((await runner.RunAsync(() => Task.CompletedTask)).Admitted);
    }

    [TestMethod]
    public async Task CompletionAfterLeavingAndReturningCannotPresentOldFeedback()
    {
        PageCommandRunner runner = new();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PageCommandResult> pending = runner.RunAsync(() => completion.Task);
        runner.Deactivate();
        runner.Activate();
        completion.SetResult();
        PageCommandResult result = await pending;
        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.CanPresent);
        Assert.IsTrue((await runner.RunAsync(() => Task.CompletedTask)).CanPresent);
    }

    [TestMethod]
    public async Task FailureIsSanitizedAndDoesNotLeaveBusyOrImplySuccess()
    {
        PageCommandRunner runner = new();
        PageCommandResult result = await runner.RunAsync(() => throw new IOException("subscription https://example.com/?token=secret"));
        Assert.IsTrue(result.Admitted);
        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.CanPresent);
        Assert.IsFalse(result.Error!.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(runner.IsBusy);
    }
}
