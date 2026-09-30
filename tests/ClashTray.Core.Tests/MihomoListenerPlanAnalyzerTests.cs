using System.Net;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class MihomoListenerPlanAnalyzerTests
{
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
}
