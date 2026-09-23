namespace ClashTray.Core;

public enum ShutdownCleanupStatus
{
    NotRequired,
    Completed,
    TimedOutUnknown,
    Failed
}

public sealed record ShutdownCleanupStepResult(
    string Name,
    ShutdownCleanupStatus Status,
    string? Detail = null);

public sealed record ShutdownRecoveryResponsibility(
    string Owner,
    string Trigger,
    string CurrentGuarantee,
    string Limitation);

public sealed record RuntimeShutdownResult(
    ShutdownCleanupStepResult OperationGate,
    ShutdownCleanupStepResult Tun,
    ShutdownCleanupStepResult SystemProxy,
    ShutdownCleanupStepResult Core,
    ShutdownCleanupStepResult RuntimeResources,
    ShutdownCleanupStepResult LocalCoreRecovery,
    IReadOnlyList<ShutdownCleanupStepResult> AdditionalSteps,
    IReadOnlyList<ShutdownRecoveryResponsibility> RecoveryResponsibilities)
{
    public bool NetworkCleanupConfirmed =>
        IsResolved(Tun.Status)
        && IsResolved(SystemProxy.Status)
        && IsResolved(Core.Status);

    public bool IsFullyClean =>
        NetworkCleanupConfirmed
        && IsResolved(OperationGate.Status)
        && IsResolved(RuntimeResources.Status)
        && IsResolved(LocalCoreRecovery.Status)
        && AdditionalSteps.All(step => IsResolved(step.Status));

    public string ToDiagnosticSummary()
    {
        string summary =
            $"Shutdown: gate={OperationGate.Status}; TUN={Tun.Status}; SystemProxy={SystemProxy.Status}; core={Core.Status}; resources={RuntimeResources.Status}; local recovery={LocalCoreRecovery.Status}.";
        string[] details =
        [
            OperationGate.Detail ?? string.Empty,
            Tun.Detail ?? string.Empty,
            SystemProxy.Detail ?? string.Empty,
            Core.Detail ?? string.Empty,
            RuntimeResources.Detail ?? string.Empty,
            LocalCoreRecovery.Detail ?? string.Empty
        ];
        string detailText = string.Join(
            " ",
            details.Where(detail => !string.IsNullOrWhiteSpace(detail)));
        return string.IsNullOrEmpty(detailText)
            ? summary
            : $"{summary} {detailText}";
    }

    private static bool IsResolved(ShutdownCleanupStatus status) =>
        status is ShutdownCleanupStatus.NotRequired or ShutdownCleanupStatus.Completed;
}

internal sealed class RuntimeShutdownResultBuilder
{
    private readonly List<ShutdownCleanupStepResult> _additionalSteps = [];
    private readonly AppPaths _paths;
    private readonly bool _serviceOwnsCore;
    private readonly bool _hasLocalCoreRecovery;

    public RuntimeShutdownResultBuilder(
        AppPaths paths,
        bool serviceOwnsCore,
        bool hasTunCleanup,
        bool hasProxyCleanup,
        bool hasCoreCleanup,
        bool hasLocalCoreRecovery)
    {
        _paths = paths;
        _serviceOwnsCore = serviceOwnsCore;
        _hasLocalCoreRecovery = hasLocalCoreRecovery;
        OperationGate = Pending("运行时操作安全点");
        Tun = PendingOrNotRequired("关闭 TUN", hasTunCleanup);
        SystemProxy = PendingOrNotRequired("恢复系统代理", hasProxyCleanup);
        Core = PendingOrNotRequired("停止核心", hasCoreCleanup);
        RuntimeResources = Pending("释放运行时资源");
        LocalCoreRecovery = PendingOrNotRequired("本地核心退出恢复记录", hasLocalCoreRecovery);
    }

    public ShutdownCleanupStepResult OperationGate { get; private set; }

    public ShutdownCleanupStepResult Tun { get; private set; }

    public ShutdownCleanupStepResult SystemProxy { get; private set; }

    public ShutdownCleanupStepResult Core { get; private set; }

    public ShutdownCleanupStepResult RuntimeResources { get; private set; }

    public ShutdownCleanupStepResult LocalCoreRecovery { get; private set; }

    public void SetOperationGate(ShutdownCleanupStepResult result) => OperationGate = result;

    public void SetTun(ShutdownCleanupStepResult result) => Tun = result;

    public void SetSystemProxy(ShutdownCleanupStepResult result) => SystemProxy = result;

    public void SetCore(ShutdownCleanupStepResult result) => Core = result;

    public void SetRuntimeResources(ShutdownCleanupStepResult result) => RuntimeResources = result;

    public void SetLocalCoreRecovery(ShutdownCleanupStepResult result) => LocalCoreRecovery = result;

    public void AddAdditional(ShutdownCleanupStepResult result) => _additionalSteps.Add(result);

    public RuntimeShutdownResult Build()
    {
        List<ShutdownRecoveryResponsibility> responsibilities = [];
        bool proxyUnresolved = IsUnresolved(SystemProxy.Status);
        bool tunUnresolved = IsUnresolved(Tun.Status);
        bool coreUnresolved = IsUnresolved(Core.Status);

        if (proxyUnresolved)
        {
            bool hasPersistentRecord = File.Exists(_paths.ProxyBackupFile)
                || File.Exists(_paths.ProxyOwnershipFile)
                || File.Exists(_paths.ProxyTransactionFile);
            responsibilities.Add(new ShutdownRecoveryResponsibility(
                hasPersistentRecord ? "System Proxy ownership journal" : "System Proxy state",
                hasPersistentRecord
                    ? "The next app launch detects RestoreRequired and exposes the existing restore action; the service recovery entry point also replays owned state when the service shuts down."
                    : "The next app launch rechecks current ownership before offering recovery.",
                "Registry restoration checks whether current values still match ClashTray ownership and preserves external changes.",
                hasPersistentRecord
                    ? "The journal is durable; recovery runs on a later app restore action or service shutdown, not in this exiting process."
                    : "No durable ownership record was found for this state."));
        }

        if (tunUnresolved && _serviceOwnsCore)
        {
            responsibilities.Add(new ShutdownRecoveryResponsibility(
                "Existing Mihomo service",
                "A dispatched named-pipe operation runs under the service lifetime token; a later app launch queries service status. Service shutdown also uses TunShutdownGuard before stopping Mihomo.",
                "The service, if still available, retains the Mihomo child and serializes TUN changes with core operations.",
                "A lost response does not confirm TUN Off. If the service is unavailable, no automatic restoration is claimed."));
        }

        if (coreUnresolved && !_serviceOwnsCore)
        {
            bool durableRecordConfirmed =
                _hasLocalCoreRecovery
                && LocalCoreRecovery.Status == ShutdownCleanupStatus.Completed
                && File.Exists(_paths.LocalCoreShutdownFile);
            responsibilities.Add(durableRecordConfirmed
                ? new ShutdownRecoveryResponsibility(
                    "Local core shutdown journal",
                    "On the next app launch, validate PID, process start time, and executable path, then stop only an exact identity match.",
                    "The versioned record is written atomically before waiting for operation-gate ownership; a matching process is stopped before automatic core startup.",
                    "If the process identity no longer matches or the record cannot be read, no process is killed automatically; startup reports the error and skips automatic core startup.")
                : new ShutdownRecoveryResponsibility(
                    "Local desktop runtime",
                    "The state is reported as unconfirmed and the current Mihomo snapshot is not changed to Stopped.",
                    "OperationGate, cancellation, and transport resources remain alive until an in-flight cleanup task settles.",
                    "No durable local-core recovery record was confirmed; the process may outlive this app process."));
        }
        else if (tunUnresolved && !_serviceOwnsCore)
        {
            responsibilities.Add(new ShutdownRecoveryResponsibility(
                "Local desktop runtime",
                "The TUN state is reported as unconfirmed and is not changed to Off.",
                "OperationGate and service transport resources remain alive until an in-flight cleanup task settles.",
                "TUN is service-owned; if the service is unavailable, no automatic restoration is claimed."));
        }
        else if (coreUnresolved && _serviceOwnsCore)
        {
            responsibilities.Add(new ShutdownRecoveryResponsibility(
                "Existing Mihomo service",
                "The next app launch can query service status; service shutdown retains its existing TUN shutdown guard.",
                "The service owns the child process independently of this UI process.",
                "The desktop result does not claim the core stopped."));
        }

        return new RuntimeShutdownResult(
            OperationGate,
            Tun,
            SystemProxy,
            Core,
            RuntimeResources,
            LocalCoreRecovery,
            _additionalSteps.ToArray(),
            responsibilities.ToArray());
    }

    private static bool IsUnresolved(ShutdownCleanupStatus status) =>
        status is ShutdownCleanupStatus.TimedOutUnknown or ShutdownCleanupStatus.Failed;

    private static ShutdownCleanupStepResult Pending(string name) =>
        new(name, ShutdownCleanupStatus.TimedOutUnknown, "Cleanup was not confirmed before shutdown returned.");

    private static ShutdownCleanupStepResult PendingOrNotRequired(string name, bool required) =>
        required
            ? Pending(name)
            : new ShutdownCleanupStepResult(name, ShutdownCleanupStatus.NotRequired);
}