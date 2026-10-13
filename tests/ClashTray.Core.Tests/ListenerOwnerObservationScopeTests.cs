using System.ComponentModel;
using System.Net;
using System.Net.Sockets;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ListenerOwnerObservationScopeTests
{
    [TestMethod]
    public void OneObservationReadsEachTableAndConfirmsIdentityOnlyOnce()
    {
        LocalPortBinding[] listeners =
        [
            new("http-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp),
            new("socks-tcp", IPAddress.Loopback, 18081, PortTransport.Tcp),
            new("socks-udp", IPAddress.Loopback, 18081, PortTransport.Udp),
            new("mixed-tcp", IPAddress.Loopback, 18082, PortTransport.Tcp),
            new("mixed-udp", IPAddress.Loopback, 18082, PortTransport.Udp)
        ];
        Dictionary<PortTransport, int> queries = [];
        int identityQueries = 0;
        ListenerOwnerObservationScope scope = ListenerOwnerObservationScope.CreateSnapshot(listeners, 123,
            (family, transport) =>
            {
                Assert.AreEqual(AddressFamily.InterNetwork, family);
                queries[transport] = queries.GetValueOrDefault(transport) + 1;
                return listeners.Where(listener => listener.Transport == transport)
                    .Select(listener => new ListenerOwnerRow(listener.Address, listener.Port, 123)).ToArray();
            },
            () => { identityQueries++; return true; });

        Assert.IsTrue(listeners.All(listener => scope.Inspect(listener).State == ListenerOwnerState.Owned));
        Assert.AreEqual(1, queries[PortTransport.Tcp]);
        Assert.AreEqual(1, queries[PortTransport.Udp]);
        Assert.AreEqual(1, identityQueries);
    }

    [TestMethod]
    public void RetryUsesNewTablesAndRejectsAnExpiredProcessIdentity()
    {
        LocalPortBinding listener = new("mixed-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp);
        int tableQueries = 0;
        int identityQueries = 0;
        bool current = true;
        ListenerOwnerObservationScope Create() => ListenerOwnerObservationScope.CreateSnapshot([listener], 123,
            (_, _) => { tableQueries++; return [new(listener.Address, listener.Port, 123)]; },
            () => { identityQueries++; return current; });
        Assert.AreEqual(ListenerOwnerState.Owned, Create().Inspect(listener).State);
        current = false;
        Assert.AreEqual(ListenerOwnerState.Unknown, Create().Inspect(listener).State);
        Assert.AreEqual(2, tableQueries);
        Assert.AreEqual(2, identityQueries);
    }

    [TestMethod]
    public void IdentityIsConfirmedAfterAllPlannedTablesHaveBeenRead()
    {
        LocalPortBinding[] listeners =
        [new("mixed-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp), new("mixed-udp", IPAddress.Loopback, 18080, PortTransport.Udp)];
        int tableQueries = 0;
        bool current = true;
        ListenerOwnerObservationScope scope = ListenerOwnerObservationScope.CreateSnapshot(listeners, 123,
            (_, _) =>
            {
                tableQueries++;
                if (tableQueries == 2) { current = false; }
                return [new(IPAddress.Loopback, 18080, 123)];
            },
            () => { Assert.AreEqual(2, tableQueries); return current; });

        Assert.IsTrue(listeners.All(listener => scope.Inspect(listener).State == ListenerOwnerState.Unknown));
    }

    [TestMethod]
    public void IdentityQueryFailureRemainsUnknownForEveryListenerInTheObservation()
    {
        LocalPortBinding[] listeners =
        [new("http-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp), new("mixed-tcp", IPAddress.Loopback, 18081, PortTransport.Tcp)];
        int identityQueries = 0;
        ListenerOwnerObservationScope scope = ListenerOwnerObservationScope.CreateSnapshot(listeners, 123,
            (_, _) => [new(IPAddress.Loopback, 18080, 123), new(IPAddress.Loopback, 18081, 123)],
            () => { identityQueries++; throw new Win32Exception(5); });

        foreach (LocalPortBinding listener in listeners)
        {
            ListenerOwnerObservation observation = scope.Inspect(listener);
            Assert.AreEqual(ListenerOwnerState.Unknown, observation.State);
            StringAssert.Contains(observation.Detail, nameof(Win32Exception), StringComparison.Ordinal);
        }
        Assert.AreEqual(1, identityQueries);
    }

    [TestMethod]
    public void DualModeIncludesForeignIpv4OwnerBeforeClaimingOwnership()
    {
        int identityQueries = 0;
        List<AddressFamily> tables = [];
        LocalPortBinding listener = new("mixed-tcp", IPAddress.IPv6Any, 18080, PortTransport.Tcp, DualMode: true);
        ListenerOwnerObservationScope scope = ListenerOwnerObservationScope.CreateSnapshot([listener], 123,
            (family, _) =>
            {
                tables.Add(family);
                return [new(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any,
                    18080, family == AddressFamily.InterNetwork ? 456 : 123)];
            },
            () => { identityQueries++; return true; });

        ListenerOwnerObservation observation = scope.Inspect(listener);
        Assert.AreEqual(ListenerOwnerState.Foreign, observation.State);
        CollectionAssert.AreEqual(new[] { AddressFamily.InterNetworkV6, AddressFamily.InterNetwork }, tables);
        Assert.AreEqual(0, identityQueries);
    }

    [TestMethod]
    public void QueryFailureStaysUnknownWithinObservationAndCanRecoverOnRetry()
    {
        int queries = 0;
        bool queryFailed = true;
        ListenerOwnerObservationScope Create() => ListenerOwnerObservationScope.CreateSnapshot(
            [new("http-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp)], 123,
            (_, _) =>
            {
                queries++;
                if (queryFailed) { throw new Win32Exception(5); }
                return [new(IPAddress.Loopback, 18080, 123), new(IPAddress.Loopback, 18081, 123)];
            }, () => true);
        ListenerOwnerObservationScope first = Create();
        Assert.AreEqual(ListenerOwnerState.Unknown, first.Inspect(new("http-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp)).State);
        Assert.AreEqual(ListenerOwnerState.Unknown, first.Inspect(new("mixed-tcp", IPAddress.Loopback, 18081, PortTransport.Tcp)).State);
        Assert.AreEqual(1, queries);
        queryFailed = false;
        Assert.AreEqual(ListenerOwnerState.Owned, Create().Inspect(new("http-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp)).State);
        Assert.AreEqual(2, queries);
    }

    [TestMethod]
    public void MissingForeignAndScopedAddressesRemainDistinct()
    {
        IPAddress scoped = IPAddress.Parse("fe80::1%7");
        ListenerOwnerObservationScope scope = ListenerOwnerObservationScope.CreateSnapshot(
            [new("scoped", scoped, 18080, PortTransport.Tcp)], 123, (_, _) =>
            [new(scoped, 18080, 123), new(IPAddress.Parse("fe80::1%8"), 18081, 456)], () => true);
        Assert.AreEqual(ListenerOwnerState.Owned, scope.Inspect(new("scoped", scoped, 18080, PortTransport.Tcp)).State);
        Assert.AreEqual(ListenerOwnerState.Missing, scope.Inspect(new("other-scope", IPAddress.Parse("fe80::1%8"), 18080, PortTransport.Tcp)).State);
        Assert.AreEqual(ListenerOwnerState.Foreign, scope.Inspect(new("foreign", IPAddress.Parse("fe80::1%8"), 18081, PortTransport.Tcp)).State);
    }

    [TestMethod]
    public void ReadinessOrderingRemainsBoundedWhenUdpIsMissing()
    {
        LocalPortBinding[] proxy =
        [new("mixed-tcp", IPAddress.Loopback, 18080, PortTransport.Tcp), new("mixed-udp", IPAddress.Loopback, 18080, PortTransport.Udp)];
        int additionalInspections = 0;
        ListenerOwnerObservationScope scope = ListenerOwnerObservationScope.CreateSnapshot(proxy, 123, (_, transport) => transport == PortTransport.Tcp
            ? [new(IPAddress.Loopback, 18080, 123)] : [], () => true);
        ListenerReadinessResult result = ListenerReadinessEvaluator.Evaluate(proxy,
            [new("dns-udp", IPAddress.Loopback, 15353, PortTransport.Udp)], listener =>
            {
                if (listener.Name == "dns-udp") { additionalInspections++; }
                return scope.Inspect(listener);
            });
        Assert.AreEqual(ListenerReadinessDisposition.WaitingForListener, result.Disposition);
        Assert.AreEqual("mixed-udp", result.ListenerName);
        Assert.AreEqual(0, additionalInspections);
    }
}
