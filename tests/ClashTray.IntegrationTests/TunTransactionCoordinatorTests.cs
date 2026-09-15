using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Service;
using System.Net.NetworkInformation;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class TunTransactionCoordinatorTests
{
    private static readonly TunTransactionOptions FastOptions = new(
        TransitionTimeout: TimeSpan.FromMilliseconds(120),
        PollInterval: TimeSpan.FromMilliseconds(2),
        StableSamples: 2,
        MaxAutomaticRetries: 1);

    [TestMethod]
    public async Task ListenerErrorAfterAcceptedPatchNeverCommitsOnAndRecoversOnce()
    {
        FakeTunBackend backend = new() { EmitListenerErrorOnFirstEnable = true };
        TunTransactionCoordinator coordinator = CreateCoordinator(backend);

        TunTransactionResult result = await coordinator.ExecuteAsync(true);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(TunState.On, result.State);
        Assert.AreEqual(1, result.RetryCount);
        Assert.IsTrue(result.RestartedCore);
        Assert.AreEqual(1, backend.RestartCount);
        Assert.AreEqual(2, backend.EnableCount);
        Assert.AreEqual(1, backend.DisableCount);
        Assert.AreEqual(2, backend.Generation);
    }

    [TestMethod]
    public async Task ConfigWithoutUsableAddressIsRejectedBeforeAnyRecoveryRestart()
    {
        FakeTunBackend backend = new()
        {
            Configuration = new MihomoTunConfiguration(
                Enabled: false,
                DeviceName: "Mihomo",
                AutoRoute: true,
                HasExplicitAddress: true,
                HasValidAddress: false,
                HasFakeIpRange: false,
                RequiresDnsHealth: false)
        };
        TunTransactionCoordinator coordinator = CreateCoordinator(backend);

        TunTransactionResult result = await coordinator.ExecuteAsync(true);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(TunState.Off, result.State);
        Assert.AreEqual(TunOperationPhase.Failed, result.Phase);
        Assert.AreEqual(TunTransactionCoordinator.ConfigurationMissingAddressError, result.Error);
        Assert.AreEqual(ServiceErrorCode.TunConfigurationMissingAddress, result.ErrorCode);
        Assert.AreEqual(0, backend.EnableCount);
        Assert.AreEqual(0, backend.DisableCount);
        Assert.AreEqual(0, backend.RestartCount);
    }

    [TestMethod]
    public async Task ConfigAcceptedButWindowsAddressMissingNeverCommitsOnOrRetriesForever()
    {
        FakeTunBackend backend = new() { AlwaysFailEnabledProbe = true };
        TunTransactionCoordinator coordinator = CreateCoordinator(backend);

        TunTransactionResult result = await coordinator.ExecuteAsync(true);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(TunState.Off, result.State);
        Assert.AreEqual(TunOperationPhase.Failed, result.Phase);
        Assert.AreEqual(1, result.RetryCount);
        Assert.IsTrue(result.RestartedCore);
        Assert.AreEqual(1, backend.RestartCount);
        Assert.AreEqual(2, backend.EnableCount);
        Assert.IsTrue(backend.DisableCount >= 2);
        Assert.AreEqual(TunTransactionCoordinator.MissingAddressError, result.Error);
        Assert.AreEqual(ServiceErrorCode.TunMissingInterfaceAddress, result.ErrorCode);
    }

    [TestMethod]
    public async Task LatestOffIntentDuringRecoverySkipsRestartAndRetry()
    {
        FakeTunBackend backend = new()
        {
            EmitListenerErrorOnFirstEnable = true,
            BlockDisable = true
        };
        bool desired = true;
        TunTransactionCoordinator coordinator = new(
            backend,
            new FakeTunNetworkHealthProbe(backend),
            FastOptions,
            shouldRetryEnable: () => desired);

        Task<TunTransactionResult> transaction = coordinator.ExecuteAsync(true);
        await backend.DisableEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        desired = false;
        backend.ReleaseDisable.TrySetResult(true);

        TunTransactionResult result = await transaction;

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(TunState.Off, result.State);
        Assert.AreEqual(0, backend.RestartCount);
        Assert.AreEqual(1, backend.EnableCount);
    }

    [TestMethod]
    public async Task UnconfirmedDisableReturnsUnknownWithoutBlindRestart()
    {
        FakeTunBackend backend = new(initialEnabled: true)
        {
            DoNotApplyDisable = true
        };
        TunTransactionCoordinator coordinator = CreateCoordinator(backend);

        TunTransactionResult result = await coordinator.ExecuteAsync(false);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(TunState.Unknown, result.State);
        Assert.AreEqual(TunOperationPhase.Unknown, result.Phase);
        Assert.AreEqual(TunTransactionCoordinator.UnknownStateError, result.Error);
        Assert.AreEqual(0, backend.RestartCount);
        Assert.AreEqual(1, backend.DisableCount);
    }

    [TestMethod]
    public void UnknownWindowsProbeCannotConfirmEitherTunState()
    {
        TunNetworkHealth unknown = new(
            InterfaceFound: false,
            HasValidAddress: false,
            HasRequiredRoute: false,
            HasRequiredDns: false,
            InterfaceName: null,
            Diagnostic: "probe failed",
            ProbeSucceeded: false,
            RouteStateKnown: false);

        Assert.IsFalse(unknown.MeetsEnabled);
        Assert.IsFalse(unknown.MeetsDisabled);
    }

    [TestMethod]
    public void WindowsRouteInteropMatchesTheSupportedX64Abi()
    {
        Assert.IsTrue(WindowsTunRouteTableReader.IsInteropLayoutSupported);
    }

    [TestMethod]
    public void WindowsRouteTableCanBeReadWithoutMutation()
    {
        WindowsTunRouteTableReader reader = new();

        bool succeeded = reader.TryReadActiveRouteInterfaceIndices(
            out IReadOnlySet<uint> interfaceIndices,
            out string? diagnostic);

        Assert.IsTrue(succeeded, diagnostic);
        Assert.IsNotNull(interfaceIndices);
        Assert.IsTrue(interfaceIndices.All(index => index > 0));

        HashSet<uint> installedInterfaceIndices = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(networkInterface => ReadInterfaceIndices(networkInterface.GetIPProperties()))
            .ToHashSet();
        Assert.IsTrue(
            interfaceIndices.Overlaps(installedInterfaceIndices),
            "The native route table did not contain any index exposed by NetworkInterface.");
    }

    private static List<uint> ReadInterfaceIndices(IPInterfaceProperties properties)
    {
        List<uint> indices = [];
        try
        {
            IPv4InterfaceProperties? ipv4 = properties.GetIPv4Properties();
            if (ipv4 is not null && ipv4.Index > 0)
            {
                indices.Add(checked((uint)ipv4.Index));
            }
        }
        catch (NetworkInformationException)
        {
            // An adapter can legitimately expose only one IP family on Windows.
        }

        try
        {
            IPv6InterfaceProperties? ipv6 = properties.GetIPv6Properties();
            if (ipv6 is not null && ipv6.Index > 0)
            {
                indices.Add(checked((uint)ipv6.Index));
            }
        }
        catch (NetworkInformationException)
        {
            // An adapter can legitimately expose only one IP family on Windows.
        }

        return indices;
    }

    private static TunTransactionCoordinator CreateCoordinator(FakeTunBackend backend) =>
        new(backend, new FakeTunNetworkHealthProbe(backend), FastOptions);

    private sealed class FakeTunNetworkHealthProbe : ITunNetworkHealthProbe
    {
        private readonly FakeTunBackend _backend;

        public FakeTunNetworkHealthProbe(FakeTunBackend backend)
        {
            _backend = backend;
        }

        public Task<TunNetworkHealth> ProbeAsync(
            MihomoTunConfiguration configuration,
            TunNetworkExpectation expectation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectation == TunNetworkExpectation.Enabled && _backend.AlwaysFailEnabledProbe)
            {
                return Task.FromResult(new TunNetworkHealth(
                    InterfaceFound: true,
                    HasValidAddress: false,
                    HasRequiredRoute: true,
                    HasRequiredDns: true,
                    InterfaceName: configuration.DeviceName,
                    Diagnostic: "fake probe: no legal interface address",
                    HasActiveRoute: true,
                    HasActiveDns: true));
            }

            return Task.FromResult(expectation == TunNetworkExpectation.Enabled
                ? new TunNetworkHealth(
                    InterfaceFound: true,
                    HasValidAddress: true,
                    HasRequiredRoute: true,
                    HasRequiredDns: true,
                    InterfaceName: configuration.DeviceName,
                    Diagnostic: null,
                    HasActiveRoute: true,
                    HasActiveDns: true)
                : new TunNetworkHealth(
                    InterfaceFound: false,
                    HasValidAddress: false,
                    HasRequiredRoute: false,
                    HasRequiredDns: false,
                    InterfaceName: configuration.DeviceName,
                    Diagnostic: null));
        }
    }

    private sealed class FakeTunBackend : ITunTransactionBackend
    {
        public bool EmitListenerErrorOnFirstEnable { get; init; }

        public bool AlwaysFailEnabledProbe { get; init; }

        public bool DoNotApplyDisable { get; init; }

        public bool BlockDisable { get; init; }

        public MihomoTunConfiguration Configuration { get; init; } = new(
            Enabled: false,
            DeviceName: "Mihomo",
            AutoRoute: true,
            HasExplicitAddress: true,
            HasValidAddress: true,
            HasFakeIpRange: false,
            RequiresDnsHealth: false);

        public TaskCompletionSource<bool> DisableEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseDisable { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Enabled { get; private set; }

        public long Generation { get; private set; } = 1;

        public int EnableCount { get; private set; }

        public int DisableCount { get; private set; }

        public int RestartCount { get; private set; }

        private long ListenerErrorSequence { get; set; }

        private string? ListenerError { get; set; }

        public long CurrentGeneration => Generation;

        public bool IsControllerHealthy => true;

        public FakeTunBackend(bool initialEnabled = false)
        {
            Enabled = initialEnabled;
        }

        public long CaptureListenerErrorMarker() => ListenerErrorSequence;

        public bool HasListenerErrorSince(long marker, out string? error)
        {
            bool hasError = ListenerErrorSequence > marker;
            error = hasError ? ListenerError : null;
            return hasError;
        }

        public Task<TunObservation> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TunObservation(
                Enabled,
                Configuration with { Enabled = Enabled },
                Generation,
                ControllerHealthy: true));
        }

        public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled)
            {
                EnableCount++;
                Enabled = true;
                if (EmitListenerErrorOnFirstEnable && EnableCount == 1)
                {
                    ListenerErrorSequence++;
                    ListenerError = "Start TUN listening error: missing interface address";
                    Enabled = false;
                }

                return;
            }

            DisableCount++;
            DisableEntered.TrySetResult(true);
            if (!ReleaseDisable.Task.IsCompleted && BlockDisable)
            {
                await ReleaseDisable.Task.WaitAsync(cancellationToken);
            }

            if (!DoNotApplyDisable)
            {
                Enabled = false;
            }
        }

        public Task<bool> RestartCoreWithTunDisabledAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestartCount++;
            Generation++;
            Enabled = false;
            return Task.FromResult(true);
        }
    }
}
