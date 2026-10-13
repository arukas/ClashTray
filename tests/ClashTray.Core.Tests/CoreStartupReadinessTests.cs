using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class CoreStartupReadinessTests
{
    private const string ReadyConfiguration = "{\"port\":0,\"socks-port\":0,\"mixed-port\":17890,\"tun\":{\"enable\":false}}";

    [TestMethod]
    public async Task ConfirmedBindingRetainsActualPortIdentityAndListenerEvidence()
    {
        CoreStartupTarget target = Target();
        CoreRuntimeBinding binding = await CoreStartupReadiness.WaitAsync(target, Observations(), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.AreEqual(target.PreferredControllerPort, binding.PreferredControllerPort);
        Assert.AreEqual(target.ControllerPort, binding.ControllerPort);
        Assert.AreEqual(target.OwnerInstanceId, binding.OwnerInstanceId);
        Assert.AreEqual(target.ProcessGeneration, binding.ProcessGeneration);
        Assert.IsTrue(binding.ControllerReady);
        Assert.IsTrue(binding.MixedReady);
        Assert.IsFalse(binding.HttpReady);
        Assert.IsTrue(binding.ListenerBindings!.Any(listener => listener.Name == "controller" && listener.Port == target.ControllerPort));
        Assert.IsTrue(binding.ListenerBindings!.Any(listener => listener.Name == "mixed-tcp" && listener.Port == target.MixedPort));
    }

    [TestMethod]
    public async Task ForeignControllerIsRejectedBeforeAnyApiRequest()
    {
        int requests = 0;
        CoreStartupObservations observations = Observations() with
        {
            InspectController = () => new(ListenerOwnerState.Foreign),
            ReadConfigurationAsync = _ => { requests++; return Task.FromResult(JsonDocument.Parse(ReadyConfiguration)); }
        };
        ServiceCommandException error = await Assert.ThrowsExactlyAsync<ServiceCommandException>(() =>
            CoreStartupReadiness.WaitAsync(Target(), observations, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.AreEqual(ServiceErrorCode.ControllerOwnershipUnconfirmed, error.ErrorCode);
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    [DataRow("{}", ServiceErrorCode.CoreReadinessFailed)]
    [DataRow("{\"port\":0,\"socks-port\":0,\"mixed-port\":17891,\"tun\":{\"enable\":false}}", ServiceErrorCode.ProxyPortConflict)]
    [DataRow("{\"port\":0,\"socks-port\":0,\"mixed-port\":17890,\"tun\":{\"enable\":true}}", ServiceErrorCode.CoreReadinessFailed)]
    public async Task UnconfirmedEffectiveConfigurationCannotProduceABinding(string json, ServiceErrorCode expected)
    {
        CoreStartupObservations observations = Observations() with { ReadConfigurationAsync = _ => Task.FromResult(JsonDocument.Parse(json)) };
        ServiceCommandException error = await Assert.ThrowsExactlyAsync<ServiceCommandException>(() =>
            CoreStartupReadiness.WaitAsync(Target(), observations, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.AreEqual(expected, error.ErrorCode);
    }

    [TestMethod]
    [DataRow((int)ListenerOwnerState.Missing)]
    [DataRow((int)ListenerOwnerState.Unknown)]
    public async Task UnconfirmedProxyListenerTimesOutWithLastEvidence(int ownerState)
    {
        ListenerOwnerState state = (ListenerOwnerState)ownerState;
        CoreStartupObservations observations = Observations() with
        { CreateListenerSnapshot = () => _ => new(state, "fixture observation") };
        TimeoutException error = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            CoreStartupReadiness.WaitAsync(Target(), observations, TimeSpan.FromMilliseconds(30), CancellationToken.None));
        StringAssert.Contains(error.Message, "mixed", StringComparison.Ordinal);
        StringAssert.Contains(error.Message, state.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TransientApiFailureRetriesWithoutLosingProcessEvidence()
    {
        int requests = 0;
        CoreStartupObservations observations = Observations() with
        {
            ReadConfigurationAsync = _ => ++requests == 1
                ? Task.FromException<JsonDocument>(new HttpRequestException("starting"))
                : Task.FromResult(JsonDocument.Parse(ReadyConfiguration))
        };
        CoreRuntimeBinding binding = await CoreStartupReadiness.WaitAsync(Target(), observations, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.IsTrue(binding.ControllerReady);
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    public async Task ProcessChangeDuringApiReadRejectsTheFinalBinding()
    {
        bool current = true;
        CoreStartupObservations observations = Observations() with
        {
            IsProcessCurrent = () => current,
            ReadConfigurationAsync = _ => { current = false; return Task.FromResult(JsonDocument.Parse(ReadyConfiguration)); }
        };
        ServiceCommandException error = await Assert.ThrowsExactlyAsync<ServiceCommandException>(() =>
            CoreStartupReadiness.WaitAsync(Target(), observations, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.AreEqual(ServiceErrorCode.CoreReadinessFailed, error.ErrorCode);
    }

    [TestMethod]
    public async Task CallerCancellationRetainsControllerDiagnosticAndToken()
    {
        using CancellationTokenSource caller = new();
        CoreStartupObservations observations = Observations() with
        {
            InspectController = () => { caller.Cancel(); return new(ListenerOwnerState.Missing); }
        };
        ListenerReadinessCancelledException error = await Assert.ThrowsExactlyAsync<ListenerReadinessCancelledException>(() =>
            CoreStartupReadiness.WaitAsync(Target(), observations, TimeSpan.FromSeconds(1), caller.Token));
        Assert.AreEqual(caller.Token, error.CancellationToken);
        StringAssert.Contains(error.Diagnostic, "controller", StringComparison.Ordinal);
    }

    private static CoreStartupTarget Target() => new(9090, 19090, 0, 0, 17890,
        Guid.NewGuid(), Guid.NewGuid(), new LocalCoreProcessIdentity(1234, 42, "C:\\fixture\\mihomo.exe"), 4,
        MihomoListenerPlanAnalyzer.AnalyzeEffectiveLines(["allow-lan: false", "port: 0", "socks-port: 0", "mixed-port: 17890"]));

    private static CoreStartupObservations Observations() => new(() => true,
        () => new(ListenerOwnerState.Owned), _ => Task.FromResult(JsonDocument.Parse(ReadyConfiguration)),
        () => _ => new(ListenerOwnerState.Owned));
}
