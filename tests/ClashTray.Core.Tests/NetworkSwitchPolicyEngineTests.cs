using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class NetworkSwitchPolicyEngineTests
{
    [TestMethod]
    public void DisabledOrPermissionDeniedNetworkDoesNotProduceSwitchRequest()
    {
        NetworkSwitchPolicyInput disabled = CreateInput(
            automaticEnabled: false,
            context: CreateContext(permission: NetworkPermissionState.Allowed),
            rules: [new NetworkSwitchRule("home", "Home", "home")],
            validConfigurations: ["home"]);
        NetworkSwitchPolicyInput permissionDenied = disabled with
        {
            AutomaticSwitchingEnabled = true,
            Context = CreateContext(permission: NetworkPermissionState.Denied)
        };

        NetworkSwitchDecision disabledDecision = NetworkSwitchPolicyEngine.Evaluate(disabled);
        NetworkSwitchDecision permissionDecision = NetworkSwitchPolicyEngine.Evaluate(permissionDenied);

        Assert.AreEqual(NetworkSwitchState.Disabled, disabledDecision.State);
        Assert.AreEqual(NetworkSwitchReason.Disabled, disabledDecision.Reason);
        Assert.IsNull(NetworkSwitchPolicyEngine.CreateSwitchRequest(disabledDecision));
        Assert.AreEqual(NetworkSwitchState.PermissionRequired, permissionDecision.State);
        Assert.AreEqual(NetworkSwitchReason.PermissionRequired, permissionDecision.Reason);
    }

    [TestMethod]
    public void UnstableOrAmbiguousNetworkWaitsWithoutEvaluatingRules()
    {
        NetworkSwitchPolicyInput unstable = CreateInput(
            context: CreateContext(stable: false),
            rules: [new NetworkSwitchRule("home", "Home", "home")],
            validConfigurations: ["home"]);
        NetworkSwitchPolicyInput ambiguous = unstable with
        {
            Context = CreateContext(ambiguous: true)
        };

        NetworkSwitchDecision unstableDecision = NetworkSwitchPolicyEngine.Evaluate(unstable);
        NetworkSwitchDecision ambiguousDecision = NetworkSwitchPolicyEngine.Evaluate(ambiguous);

        Assert.AreEqual(NetworkSwitchState.WaitingForNetwork, unstableDecision.State);
        Assert.AreEqual(NetworkSwitchReason.WaitingForNetwork, unstableDecision.Reason);
        Assert.AreEqual(NetworkSwitchState.AmbiguousNetwork, ambiguousDecision.State);
        Assert.AreEqual(NetworkSwitchReason.AmbiguousNetwork, ambiguousDecision.Reason);
    }

    [TestMethod]
    public void ExactSsidMatchIsCaseSensitiveAndCarriesRevisionIntoRequest()
    {
        NetworkSwitchRule rule = new NetworkSwitchRule("home-rule", "Home", "home");
        NetworkSwitchPolicyInput mismatched = CreateInput(
            context: CreateContext(ssid: "home", revision: 4),
            rules: [rule],
            validConfigurations: ["home"]);
        NetworkSwitchPolicyInput matched = mismatched with
        {
            Context = CreateContext(ssid: "Home", revision: 5)
        };

        NetworkSwitchDecision noMatch = NetworkSwitchPolicyEngine.Evaluate(mismatched);
        NetworkSwitchDecision decision = NetworkSwitchPolicyEngine.Evaluate(matched);
        ConfigurationSwitchRequest request = NetworkSwitchPolicyEngine.CreateSwitchRequest(decision)!;

        Assert.AreEqual(NetworkSwitchDecisionKind.KeepCurrentWithWarning, noMatch.Kind);
        Assert.AreEqual(NetworkSwitchReason.NoDefault, noMatch.Reason);
        Assert.AreEqual(NetworkSwitchDecisionKind.SwitchToMappedConfiguration, decision.Kind);
        Assert.AreEqual(NetworkSwitchState.Switching, decision.State);
        Assert.AreEqual("home-rule", decision.RuleId);
        Assert.AreEqual(ConfigurationSwitchSource.NetworkRule, request.Source);
        Assert.AreEqual(5, request.ExpectedNetworkRevision);
        Assert.AreEqual("home", request.TargetConfigurationId);
    }

    [TestMethod]
    public void InvalidMappedTargetFallsBackToValidDefault()
    {
        NetworkSwitchPolicyInput input = CreateInput(
            context: CreateContext(ssid: "Home"),
            rules: [new NetworkSwitchRule("home-rule", "Home", "deleted")],
            defaultConfigurationId: "fallback",
            validConfigurations: ["fallback"]);

        NetworkSwitchDecision decision = NetworkSwitchPolicyEngine.Evaluate(input);

        Assert.AreEqual(NetworkSwitchDecisionKind.SwitchToDefaultConfiguration, decision.Kind);
        Assert.AreEqual(NetworkSwitchReason.DefaultRule, decision.Reason);
        Assert.AreEqual("fallback", decision.TargetConfigurationId);
        Assert.IsNull(decision.RuleId);
    }

    [TestMethod]
    public void DuplicateMatchingRulesAndInvalidDefaultKeepCurrentConfiguration()
    {
        NetworkSwitchPolicyInput duplicate = CreateInput(
            context: CreateContext(ssid: "Home"),
            rules:
            [
                new NetworkSwitchRule("first", "Home", "one"),
                new NetworkSwitchRule("second", "Home", "two")
            ],
            defaultConfigurationId: "missing",
            validConfigurations: ["one", "two"]);
        NetworkSwitchPolicyInput invalidDefault = CreateInput(
            context: CreateContext(ssid: "Office"),
            rules: [],
            defaultConfigurationId: "missing",
            validConfigurations: ["home"]);

        NetworkSwitchDecision duplicateDecision = NetworkSwitchPolicyEngine.Evaluate(duplicate);
        NetworkSwitchDecision defaultDecision = NetworkSwitchPolicyEngine.Evaluate(invalidDefault);

        Assert.AreEqual(NetworkSwitchDecisionKind.KeepCurrentWithWarning, duplicateDecision.Kind);
        Assert.AreEqual(NetworkSwitchReason.DuplicateRules, duplicateDecision.Reason);
        Assert.AreEqual(NetworkSwitchState.Failed, duplicateDecision.State);
        Assert.AreEqual(NetworkSwitchReason.InvalidDefault, defaultDecision.Reason);
        Assert.IsNull(NetworkSwitchPolicyEngine.CreateSwitchRequest(defaultDecision));
    }

    [TestMethod]
    public void ManualOverrideAndCooldownTakePriorityOverTargetSelection()
    {
        NetworkSwitchPolicyInput manualOverride = CreateInput(
            context: CreateContext(revision: 7),
            rules: [new NetworkSwitchRule("home", "Home", "home")],
            validConfigurations: ["home"],
            manualOverrideConfigurationId: "manual",
            manualOverrideRevision: 7,
            coolingDownUntilUtc: DateTimeOffset.UtcNow.AddMinutes(1));
        NetworkSwitchPolicyInput cooling = manualOverride with
        {
            ManualOverrideConfigurationId = null,
            ManualOverrideRevision = null
        };

        NetworkSwitchDecision manualDecision = NetworkSwitchPolicyEngine.Evaluate(manualOverride);
        NetworkSwitchDecision coolingDecision = NetworkSwitchPolicyEngine.Evaluate(cooling);

        Assert.AreEqual(NetworkSwitchState.ManualOverride, manualDecision.State);
        Assert.AreEqual(NetworkSwitchReason.ManualOverride, manualDecision.Reason);
        Assert.AreEqual(NetworkSwitchState.CoolingDown, coolingDecision.State);
        Assert.AreEqual(NetworkSwitchReason.CoolingDown, coolingDecision.Reason);
    }

    [TestMethod]
    public void AlreadyActiveTargetDoesNotCreateRepeatedSwitch()
    {
        NetworkSwitchPolicyInput input = CreateInput(
            context: CreateContext(ssid: "Home"),
            rules: [new NetworkSwitchRule("home", "Home", "home")],
            activeConfigurationId: "home",
            validConfigurations: ["home"]);

        NetworkSwitchDecision decision = NetworkSwitchPolicyEngine.Evaluate(input);

        Assert.AreEqual(NetworkSwitchDecisionKind.NoAction, decision.Kind);
        Assert.AreEqual(NetworkSwitchReason.TargetAlreadyActive, decision.Reason);
        Assert.IsNull(NetworkSwitchPolicyEngine.CreateSwitchRequest(decision));
    }

    private static NetworkSwitchPolicyInput CreateInput(
        bool automaticEnabled = true,
        NetworkContextSnapshot? context = null,
        IReadOnlyList<NetworkSwitchRule>? rules = null,
        string? defaultConfigurationId = null,
        string? activeConfigurationId = null,
        IReadOnlyCollection<string>? validConfigurations = null,
        string? manualOverrideConfigurationId = null,
        long? manualOverrideRevision = null,
        DateTimeOffset? coolingDownUntilUtc = null) =>
        new(
            automaticEnabled,
            context ?? CreateContext(),
            rules ?? [],
            defaultConfigurationId,
            activeConfigurationId,
            new HashSet<string>(validConfigurations ?? [], StringComparer.OrdinalIgnoreCase),
            manualOverrideConfigurationId,
            manualOverrideRevision,
            coolingDownUntilUtc);

    private static NetworkContextSnapshot CreateContext(
        long revision = 1,
        string? ssid = "Home",
        NetworkPermissionState permission = NetworkPermissionState.Allowed,
        bool ambiguous = false,
        bool stable = true) =>
        new(
            revision,
            DateTimeOffset.UtcNow,
            NetworkConnectivityKind.WiFi,
            ssid,
            "wifi-interface",
            permission,
            ambiguous,
            stable);
}
