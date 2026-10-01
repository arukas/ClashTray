using System.Net;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ListenerReadinessEvaluatorTests
{
    [TestMethod]
    public void OwnedTcpBeforeMissingUdpWaitsThenAdvancesToAdditionalListeners()
    {
        LocalPortBinding[] proxyListeners =
        [
            new("http-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp),
            new("socks-tcp", IPAddress.Loopback, 18081, PortTransport.Tcp),
            new("socks-udp", IPAddress.Loopback, 18081, PortTransport.Udp),
            new("mixed-tcp", IPAddress.Loopback, 18082, PortTransport.Tcp),
            new("mixed-udp", IPAddress.Loopback, 18082, PortTransport.Udp)
        ];
        LocalPortBinding[] additionalListeners =
        [
            new("dns-tcp", IPAddress.Loopback, 18083, PortTransport.Tcp),
            new("dns-udp", IPAddress.Loopback, 18083, PortTransport.Udp),
            new("custom-tcp", IPAddress.Loopback, 18084, PortTransport.Tcp)
        ];
        Dictionary<string, ListenerOwnerState> state = proxyListeners
            .Concat(additionalListeners)
            .ToDictionary(binding => binding.Name, _ => ListenerOwnerState.Owned, StringComparer.Ordinal);
        List<string> observed = [];
        ListenerOwnerObservation Inspect(LocalPortBinding binding)
        {
            observed.Add(binding.Name);
            return new ListenerOwnerObservation(state[binding.Name]);
        }

        state["socks-udp"] = ListenerOwnerState.Missing;
        ListenerReadinessResult first = ListenerReadinessEvaluator.Evaluate(proxyListeners, additionalListeners, Inspect);
        Assert.AreEqual(ListenerReadinessDisposition.WaitingForListener, first.Disposition);
        Assert.AreEqual("socks-udp", first.ListenerName);
        CollectionAssert.AreEqual(
            new List<string> { "http-tcp", "socks-tcp", "socks-udp" },
            observed);

        state["socks-udp"] = ListenerOwnerState.Owned;
        observed.Clear();
        state["dns-tcp"] = ListenerOwnerState.Missing;
        ListenerReadinessResult second = ListenerReadinessEvaluator.Evaluate(proxyListeners, additionalListeners, Inspect);
        Assert.AreEqual(ListenerReadinessDisposition.WaitingForListener, second.Disposition);
        Assert.AreEqual("dns-tcp", second.ListenerName);
        CollectionAssert.AreEqual(
            new List<string> { "http-tcp", "socks-tcp", "socks-udp", "mixed-tcp", "mixed-udp", "dns-tcp", "dns-udp" },
            observed);

        state["dns-tcp"] = ListenerOwnerState.Owned;
        observed.Clear();
        ListenerReadinessResult final = ListenerReadinessEvaluator.Evaluate(proxyListeners, additionalListeners, Inspect);
        Assert.AreEqual(ListenerReadinessDisposition.Ready, final.Disposition);
        CollectionAssert.AreEqual(
            new List<string> { "http-tcp", "socks-tcp", "socks-udp", "mixed-tcp", "mixed-udp", "dns-tcp", "dns-udp", "custom-tcp" },
            observed);
    }

    [TestMethod]
    public void ForeignAndUnknownHaveDistinctBoundedRetryOutcomes()
    {
        LocalPortBinding[] proxyListeners =
        [
            new("socks-tcp", IPAddress.Loopback, 18181, PortTransport.Tcp),
            new("socks-udp", IPAddress.Loopback, 18181, PortTransport.Udp)
        ];

        ListenerReadinessResult foreign = ListenerReadinessEvaluator.Evaluate(
            proxyListeners,
            [],
            binding => new ListenerOwnerObservation(
                binding.Name == "socks-udp" ? ListenerOwnerState.Foreign : ListenerOwnerState.Owned,
                "specific foreign UDP owner"));
        Assert.AreEqual(ListenerReadinessDisposition.ForeignOwner, foreign.Disposition);
        Assert.AreEqual("socks-udp", foreign.ListenerName);

        ListenerReadinessResult unknown = ListenerReadinessEvaluator.Evaluate(
            proxyListeners,
            [],
            binding => new ListenerOwnerObservation(
                binding.Name == "socks-udp" ? ListenerOwnerState.Unknown : ListenerOwnerState.Owned,
                "owner table unavailable"));
        Assert.AreEqual(ListenerReadinessDisposition.OwnershipUnknown, unknown.Disposition);
        Assert.AreEqual("owner table unavailable", unknown.Detail);
    }
}
