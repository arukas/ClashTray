using System.Net;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoListenerPlanAnalyzerTests
{
    [TestMethod]
    [DataRow("\"d\\u006es\":")]
    [DataRow("\"d\\x6es\":")]
    [DataRow("\"d\\U0000006es\":")]
    public void EscapedDnsRootKeyStillPlansBothTransports(string rootKey)
    {
        string[] yaml = [rootKey, "    enable: true", "    listen: 127.0.0.1:15357"];
        string[] original = yaml.ToArray();

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsTrue(plan.IsComplete, plan.Warning);
        Assert.AreEqual(2, plan.Bindings.Count);
        Assert.AreEqual(15357, plan.Bindings[0].Port);
        CollectionAssert.AreEqual(original, yaml);
    }

    [TestMethod]
    [DataRow("!!str dns:")]
    [DataRow("&key dns:")]
    [DataRow("? dns")]
    [DataRow("\"d\\qns\":")]
    [DataRow("{dns: {enable: true, listen: '127.0.0.1:15357'}}")]
    public void UnknownRootSyntaxNeverClaimsACompleteListenerPlan(string root)
    {
        string[] yaml = ["allow-lan: false", "mixed-port: 18080", root, "    enable: true", "    listen: 127.0.0.1:15357"];

        MihomoEffectiveListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeEffectiveLines(yaml);

        Assert.IsFalse(plan.IsComplete);
        Assert.IsNotNull(plan.Warning);
    }

    [TestMethod]
    public void UnknownRootAfterDnsIsNotHiddenInsideItsSection()
    {
        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(
            ["dns:", "  enable: false", "!!str listeners: []"]);

        Assert.IsFalse(plan.IsComplete);
        Assert.IsNotNull(plan.Warning);
    }

    [TestMethod]
    public void IndentationlessCustomListenerSequenceStillBelongsToItsSection()
    {
        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(
            ["listeners:", "- name: local", "  type: http", "  listen: 127.0.0.1", "  port: 15358", "rules: []"]);

        Assert.IsTrue(plan.IsComplete, plan.Warning);
        Assert.AreEqual(1, plan.Bindings.Count);
        Assert.AreEqual(15358, plan.Bindings[0].Port);
    }

    [TestMethod]
    public void EscapedDnsChildKeysAreDecodedWithoutChangingSingleQuotedBackslashes()
    {
        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(
            ["dns:", "  \"\\x65nable\": true", "  \"l\\u0069sten\": 127.0.0.1:15358"]);
        Assert.IsTrue(plan.IsComplete, plan.Warning);
        Assert.AreEqual(2, plan.Bindings.Count);

        MihomoListenerPlan literal = MihomoListenerPlanAnalyzer.AnalyzeLines(
            ["'d\\u006es':", "  enable: true", "  listen: 127.0.0.1:15358"]);
        Assert.AreEqual(0, literal.Bindings.Count, "Single quotes preserve a literal backslash.");
    }

    [TestMethod]
    public void EnabledDnsWithFixedIpv4AddressPlansTcpAndUdpWithoutChangingYaml()
    {
        string[] yaml =
        [
            "dns:",
            "  enable: true",
            "  listen: 127.0.0.1:15353",
            "  nameserver: [1.1.1.1, 8.8.8.8]"
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsTrue(plan.IsComplete, plan.Warning);
        Assert.IsNull(plan.Warning);
        Assert.AreEqual(2, plan.Bindings.Count);
        Assert.IsTrue(plan.Bindings.All(binding => binding.Address.Equals(IPAddress.Loopback)));
        Assert.AreEqual(15353, plan.Bindings[0].Port);
        Assert.AreEqual(PortTransport.Tcp, plan.Bindings[0].Transport);
        Assert.AreEqual(PortTransport.Udp, plan.Bindings[1].Transport);
    }

    [TestMethod]
    public void SupportedCustomMixedListenerPreservesTcpAndUdpSemantics()
    {
        string[] yaml =
        [
            "listeners:",
            "  - name: local-inbound",
            "    type: mixed",
            "    port: 15432",
            "    listen: 127.0.0.1",
            "    udp: true"
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsTrue(plan.IsComplete);
        Assert.AreEqual(2, plan.Bindings.Count);
        Assert.AreEqual(15432, plan.Bindings[0].Port);
        Assert.AreEqual(PortTransport.Tcp, plan.Bindings[0].Transport);
        Assert.AreEqual(PortTransport.Udp, plan.Bindings[1].Transport);
    }

    [TestMethod]
    public void EnabledDnsWithBracketedIpv6AddressKeepsTheExactLoopbackFamily()
    {
        Assert.IsTrue(MihomoListenerPlanAnalyzer.TryParseListenEndpoint("\"[::1]:15354\"", out IPAddress? parsedAddress, out int parsedPort));
        Assert.AreEqual(IPAddress.IPv6Loopback, parsedAddress);
        Assert.AreEqual(15354, parsedPort);
        string[] yaml =
        [
            "dns:",
            "  enable: true",
            "  listen: \"[::1]:15354\""
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsTrue(plan.IsComplete, plan.Warning);
        Assert.IsTrue(plan.Bindings.All(binding => binding.Address.Equals(IPAddress.IPv6Loopback)));
        Assert.AreEqual(15354, plan.Bindings[0].Port);
    }

    [TestMethod]
    public void HostnameDnsListenerIsNotMarkedAsPreflightedOrRewritten()
    {
        string[] yaml =
        [
            "dns:",
            "  enable: true",
            "  listen: dns.local:53"
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsFalse(plan.IsComplete);
        Assert.IsNotNull(plan.Warning);
        Assert.AreEqual(0, plan.Bindings.Count);
        Assert.AreEqual("  listen: dns.local:53", yaml[2]);
    }

    [TestMethod]
    public void UnknownCustomListenerTypeIsPreservedAndReportedAsIncomplete()
    {
        string[] yaml =
        [
            "listeners:",
            "  - name: inbound",
            "    type: tuic",
            "    port: 15432",
            "    listen: 127.0.0.1"
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsFalse(plan.IsComplete);
        Assert.AreEqual(0, plan.Bindings.Count);
        Assert.AreEqual("tuic", yaml[2][10..]);
    }

    [TestMethod]
    public void DnsWithFourSpaceChildIndentIsPlannedCompletely()
    {
        string[] yaml =
        [
            "dns:",
            "    enable: true",
            "    listen: 127.0.0.1:15355"
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsTrue(plan.IsComplete, plan.Warning);
        Assert.IsNull(plan.Warning);
        Assert.AreEqual(2, plan.Bindings.Count);
        Assert.AreEqual(15355, plan.Bindings[0].Port);
        Assert.IsTrue(plan.Bindings.All(binding => binding.Address.Equals(IPAddress.Loopback)));
    }

    [TestMethod]
    public void QuotedDnsRootKeyIsPlannedCompletely()
    {
        string[] yaml =
        [
            "'dns':",
            "  enable: true",
            "  listen: 127.0.0.1:15356"
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsTrue(plan.IsComplete, plan.Warning);
        Assert.AreEqual(2, plan.Bindings.Count);
        Assert.AreEqual(15356, plan.Bindings[0].Port);
    }

    [TestMethod]
    public void DnsMergeAliasIsExplicitlyMarkedIncomplete()
    {
        string[] yaml =
        [
            "dns:",
            "  <<: *dnsDefaults"
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsFalse(plan.IsComplete);
        Assert.IsNotNull(plan.Warning);
        StringAssert.Contains(plan.Warning, "DNS", StringComparison.Ordinal);
    }

    [TestMethod]
    public void EffectivePlanUsesSpecifiedBindAddressAndPreservesTransportOrder()
    {
        string[] yaml =
        [
            "allow-lan: true",
            "bind-address: '127.0.0.2'",
            "port: 18080",
            "socks-port: 18081",
            "mixed-port: 18082"
        ];

        MihomoEffectiveListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeEffectiveLines(yaml);

        Assert.IsTrue(plan.ProxyPlanComplete, plan.ProxyPlanWarning);
        Assert.AreEqual(5, plan.ProxyBindings.Count);
        Assert.IsTrue(plan.ProxyBindings.All(binding => binding.Address.Equals(IPAddress.Parse("127.0.0.2"))));
        CollectionAssert.AreEqual(
            new List<string> { "http-tcp", "socks-tcp", "socks-udp", "mixed-tcp", "mixed-udp" },
            plan.ProxyBindings.Select(binding => binding.Name).ToList());
        Assert.AreEqual(PortTransport.Tcp, plan.ProxyBindings[1].Transport);
        Assert.AreEqual(PortTransport.Udp, plan.ProxyBindings[2].Transport);
    }

    [TestMethod]
    public void EffectiveWildcardBindAddressRecordsDualModeIpv6Family()
    {
        string[] yaml =
        [
            "allow-lan: true",
            "bind-address: \"*\"",
            "port: 18080",
            "socks-port: 0",
            "mixed-port: 18082"
        ];

        MihomoEffectiveListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeEffectiveLines(yaml);

        Assert.IsTrue(plan.ProxyPlanComplete, plan.ProxyPlanWarning);
        Assert.AreEqual(IPAddress.IPv6Any, plan.ProxyBindings[0].Address);
        Assert.IsTrue(plan.ProxyBindings[0].DualMode);
        Assert.AreEqual(18082, plan.ProxyBindings[1].Port);
        Assert.IsTrue(plan.ProxyBindings.All(binding => binding.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6));
    }

    [TestMethod]
    public void EffectiveUnknownBindAddressIsIncompleteInsteadOfAssumingLoopbackOrWildcard()
    {
        string[] yaml =
        [
            "allow-lan: true",
            "bind-address: proxy.local",
            "port: 18080",
            "socks-port: 18081",
            "mixed-port: 18082"
        ];

        MihomoEffectiveListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeEffectiveLines(yaml);

        Assert.IsFalse(plan.ProxyPlanComplete);
        Assert.IsNotNull(plan.ProxyPlanWarning);
        StringAssert.Contains(plan.ProxyPlanWarning, "bind-address", StringComparison.Ordinal);
        Assert.AreEqual(0, plan.ProxyBindings.Count);
    }

    [TestMethod]
    public void CustomListenerMergeAliasKeepsPlanIncompleteEvenWhenOtherFieldsAreReadable()
    {
        string[] yaml =
        [
            "listeners:",
            "  - <<: *listenerDefaults",
            "    type: mixed",
            "    port: 18090",
            "    listen: 127.0.0.1",
            "    udp: true"
        ];

        MihomoListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeLines(yaml);

        Assert.IsFalse(plan.IsComplete);
        Assert.IsNotNull(plan.Warning);
        StringAssert.Contains(plan.Warning, "alias", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void EffectiveRootMergeDoesNotClaimACompleteProxyPlan()
    {
        string[] yaml =
        [
            "<<: *defaults",
            "allow-lan: false",
            "port: 18080",
            "socks-port: 18081",
            "mixed-port: 18082"
        ];

        MihomoEffectiveListenerPlan plan = MihomoListenerPlanAnalyzer.AnalyzeEffectiveLines(yaml);

        Assert.IsFalse(plan.ProxyPlanComplete);
        Assert.IsNotNull(plan.ProxyPlanWarning);
    }
}
