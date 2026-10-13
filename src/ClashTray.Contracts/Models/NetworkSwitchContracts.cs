namespace ClashTray.Contracts;

public enum NetworkConnectivityKind
{
    None,
    Ethernet,
    WiFi,
    Other
}

public enum NetworkPermissionState
{
    Unknown,
    Allowed,
    Denied,
    Unavailable
}

public enum NetworkSwitchState
{
    Disabled,
    WaitingForNetwork,
    PermissionRequired,
    AmbiguousNetwork,
    Evaluating,
    Switching,
    ManualOverride,
    CoolingDown,
    Failed
}

public enum NetworkSwitchDecisionKind
{
    NoAction,
    SwitchToMappedConfiguration,
    SwitchToDefaultConfiguration,
    KeepCurrentWithWarning
}

public enum NetworkSwitchReason
{
    Disabled,
    PermissionRequired,
    WaitingForNetwork,
    AmbiguousNetwork,
    ManualOverride,
    CoolingDown,
    MappedRule,
    DefaultRule,
    TargetAlreadyActive,
    DuplicateRules,
    MissingTarget,
    InvalidDefault,
    NoDefault,
    ExecutionFailed
}

public sealed record NetworkContextSnapshot(
    long Revision,
    DateTimeOffset ObservedAtUtc,
    NetworkConnectivityKind ConnectivityKind,
    string? CurrentSsid,
    string? InterfaceIdentity,
    NetworkPermissionState PermissionState,
    bool IsAmbiguous,
    bool IsStable,
    string? ErrorMessage = null);

public sealed record NetworkSwitchRule(
    string RuleId,
    string Ssid,
    string ConfigurationId,
    bool Enabled = true);

public sealed record NetworkSwitchRuleSet(
    bool AutomaticSwitchingEnabled,
    string? DefaultConfigurationId,
    IReadOnlyList<NetworkSwitchRule> Rules);

public sealed record NetworkSwitchPolicyInput(
    bool AutomaticSwitchingEnabled,
    NetworkContextSnapshot Context,
    IReadOnlyList<NetworkSwitchRule> Rules,
    string? DefaultConfigurationId,
    string? ActiveConfigurationId,
    IReadOnlySet<string> ValidConfigurationIds,
    string? ManualOverrideConfigurationId = null,
    long? ManualOverrideRevision = null,
    DateTimeOffset? CoolingDownUntilUtc = null);

public sealed record NetworkSwitchDecision(
    NetworkSwitchDecisionKind Kind,
    NetworkSwitchState State,
    NetworkSwitchReason Reason,
    long NetworkRevision,
    string? TargetConfigurationId = null,
    string? RuleId = null,
    string? Message = null);

public sealed record NetworkSwitchStatus(
    bool Available,
    NetworkSwitchState State,
    NetworkPermissionState PermissionState,
    NetworkContextSnapshot? Context,
    NetworkSwitchDecision? LastDecision,
    string? ErrorMessage,
    ErrorCode ErrorCode = ErrorCode.None);
