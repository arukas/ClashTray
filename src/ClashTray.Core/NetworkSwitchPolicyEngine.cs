using ClashTray.Contracts;

namespace ClashTray.Core;

public static class NetworkSwitchPolicyEngine
{
    public static NetworkSwitchDecision Evaluate(NetworkSwitchPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Context);
        ArgumentNullException.ThrowIfNull(input.Rules);
        ArgumentNullException.ThrowIfNull(input.ValidConfigurationIds);
        if (input.Context.Revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Network context revision cannot be negative.");
        }

        long revision = input.Context.Revision;
        if (!input.AutomaticSwitchingEnabled)
        {
            return NoAction(
                NetworkSwitchState.Disabled,
                NetworkSwitchReason.Disabled,
                revision,
                "自动切换未启用。");
        }

        if (input.Context.PermissionState is NetworkPermissionState.Denied or NetworkPermissionState.Unavailable)
        {
            return NoAction(
                NetworkSwitchState.PermissionRequired,
                NetworkSwitchReason.PermissionRequired,
                revision,
                "Windows 当前不允许读取 Wi-Fi 名称。");
        }

        if (!input.Context.IsStable)
        {
            return NoAction(
                NetworkSwitchState.WaitingForNetwork,
                NetworkSwitchReason.WaitingForNetwork,
                revision,
                "网络尚未稳定，等待下一次评估。");
        }

        if (input.Context.IsAmbiguous)
        {
            return NoAction(
                NetworkSwitchState.AmbiguousNetwork,
                NetworkSwitchReason.AmbiguousNetwork,
                revision,
                "存在多个活动 Wi-Fi 接口，无法确定主要连接。");
        }

        if (!string.IsNullOrWhiteSpace(input.ManualOverrideConfigurationId)
            && input.ManualOverrideRevision == revision)
        {
            return new NetworkSwitchDecision(
                NetworkSwitchDecisionKind.NoAction,
                NetworkSwitchState.ManualOverride,
                NetworkSwitchReason.ManualOverride,
                revision,
                Message: "当前网络会话存在手动配置覆盖。");
        }

        if (input.CoolingDownUntilUtc is DateTimeOffset coolingUntil
            && coolingUntil > input.Context.ObservedAtUtc)
        {
            return NoAction(
                NetworkSwitchState.CoolingDown,
                NetworkSwitchReason.CoolingDown,
                revision,
                "自动切换正在冷却，保留最新网络状态。");
        }

        HashSet<string> validConfigurationIds = new(
            input.ValidConfigurationIds,
            StringComparer.OrdinalIgnoreCase);
        NetworkSwitchRule[] matchingRules = input.Context.ConnectivityKind == NetworkConnectivityKind.WiFi
            && !string.IsNullOrEmpty(input.Context.CurrentSsid)
            ? input.Rules
                .Where(rule => rule.Enabled
                    && string.Equals(rule.Ssid, input.Context.CurrentSsid, StringComparison.Ordinal))
                .ToArray()
            : [];

        if (matchingRules.Length > 1)
        {
            return new NetworkSwitchDecision(
                NetworkSwitchDecisionKind.KeepCurrentWithWarning,
                NetworkSwitchState.Failed,
                NetworkSwitchReason.DuplicateRules,
                revision,
                Message: "当前网络存在重复自动切换规则，未执行切换。");
        }

        string? targetConfigurationId = null;
        NetworkSwitchDecisionKind decisionKind = NetworkSwitchDecisionKind.KeepCurrentWithWarning;
        NetworkSwitchReason reason;
        string? ruleId = null;
        string? message;

        if (matchingRules is [{ } matchingRule])
        {
            ruleId = matchingRule.RuleId;
            if (validConfigurationIds.Contains(matchingRule.ConfigurationId))
            {
                targetConfigurationId = matchingRule.ConfigurationId;
                decisionKind = NetworkSwitchDecisionKind.SwitchToMappedConfiguration;
                reason = NetworkSwitchReason.MappedRule;
                message = null;
            }
            else
            {
                reason = NetworkSwitchReason.MissingTarget;
                message = "自动切换规则的目标配置不可用，未执行切换。";
            }
        }
        else
        {
            reason = NetworkSwitchReason.NoDefault;
            message = "当前网络没有匹配规则，也未配置有效默认配置。";
        }

        if (targetConfigurationId is null
            && !string.IsNullOrWhiteSpace(input.DefaultConfigurationId))
        {
            if (validConfigurationIds.Contains(input.DefaultConfigurationId))
            {
                targetConfigurationId = input.DefaultConfigurationId;
                decisionKind = NetworkSwitchDecisionKind.SwitchToDefaultConfiguration;
                reason = NetworkSwitchReason.DefaultRule;
                message = null;
                ruleId = null;
            }
            else if (matchingRules.Length == 0)
            {
                reason = NetworkSwitchReason.InvalidDefault;
                message = "默认配置不可用，未执行自动切换。";
            }
        }

        if (targetConfigurationId is null)
        {
            return new NetworkSwitchDecision(
                decisionKind,
                NetworkSwitchState.Failed,
                reason,
                revision,
                RuleId: ruleId,
                Message: message);
        }

        if (string.Equals(targetConfigurationId, input.ActiveConfigurationId, StringComparison.OrdinalIgnoreCase))
        {
            return new NetworkSwitchDecision(
                NetworkSwitchDecisionKind.NoAction,
                NetworkSwitchState.WaitingForNetwork,
                NetworkSwitchReason.TargetAlreadyActive,
                revision,
                TargetConfigurationId: targetConfigurationId,
                RuleId: ruleId,
                Message: "目标配置已经生效，无需重复切换。");
        }

        return new NetworkSwitchDecision(
            decisionKind,
            NetworkSwitchState.Switching,
            reason,
            revision,
            targetConfigurationId,
            ruleId,
            message);
    }

    public static ConfigurationSwitchRequest? CreateSwitchRequest(NetworkSwitchDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Kind is not (NetworkSwitchDecisionKind.SwitchToMappedConfiguration
            or NetworkSwitchDecisionKind.SwitchToDefaultConfiguration)
            || string.IsNullOrWhiteSpace(decision.TargetConfigurationId))
        {
            return null;
        }

        ConfigurationSwitchSource source = decision.Kind == NetworkSwitchDecisionKind.SwitchToMappedConfiguration
            ? ConfigurationSwitchSource.NetworkRule
            : ConfigurationSwitchSource.NetworkDefault;
        return ConfigurationSwitchRequest.Create(
            source,
            decision.TargetConfigurationId,
            expectedNetworkRevision: decision.NetworkRevision);
    }

    private static NetworkSwitchDecision NoAction(
        NetworkSwitchState state,
        NetworkSwitchReason reason,
        long revision,
        string message) =>
        new(
            NetworkSwitchDecisionKind.NoAction,
            state,
            reason,
            revision,
            Message: message);
}
