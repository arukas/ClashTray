using System.Text.Json;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ControllerResourceSamplerTests
{
    [TestMethod]
    public async Task UnrequestedCancellationRemainsOptionalLocallyAndPropagatesForRemoteSession()
    {
        static Task<JsonDocument> CancelRead() => Task.FromException<JsonDocument>(new OperationCanceledException());
        ControllerResourceRead<int> local = await ControllerResourceSampler.ReadDocumentAsync(
            CancelRead, static _ => 1, static () => 9, CancellationToken.None, ControllerReadCancellationPolicy.PropagateRequested);
        Assert.IsFalse(local.Succeeded);
        Assert.AreEqual(9, local.Value);
        Assert.IsInstanceOfType<OperationCanceledException>(local.Failure);
        await Assert.ThrowsAsync<OperationCanceledException>(() => ControllerResourceSampler.ReadDocumentAsync(
            CancelRead, static _ => 1, static () => 9, CancellationToken.None));
    }

    [TestMethod]
    public async Task CallerCancellationNeverPublishesFallbackData()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        foreach (ControllerReadCancellationPolicy policy in Enum.GetValues<ControllerReadCancellationPolicy>())
        {
            int reads = 0;
            int fallbacks = 0;
            await Assert.ThrowsAsync<OperationCanceledException>(() => ControllerResourceSampler.ReadDocumentAsync(
                () => { reads++; return Task.FromResult(JsonDocument.Parse("{}")); },
                static _ => 1, () => { fallbacks++; return 9; }, cancellation.Token, policy));
            Assert.AreEqual(0, reads);
            Assert.AreEqual(0, fallbacks);
        }
    }

    [TestMethod]
    public async Task LocalFailureUsesTheMostRecentConfirmedFallbackAndSanitizesItsDiagnostic()
    {
        TaskCompletionSource<JsonDocument> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int confirmed = 2;
        Task<ControllerResourceRead<int>> pending = ControllerResourceSampler.ReadDocumentAsync(
            () => response.Task, static _ => 1, () => confirmed, CancellationToken.None,
            ControllerReadCancellationPolicy.PropagateRequested);
        confirmed = 3;
        response.SetException(new IOException("https://example.test/config?token=private-secret"));
        ControllerResourceRead<int> result = await pending;
        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(3, result.Value);
        Assert.IsNotNull(result.Elapsed);
        Assert.IsFalse(result.ErrorMessage!.Contains("private-secret", StringComparison.Ordinal));
    }
}
