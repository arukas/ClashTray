using ClashTray.Contracts;

namespace ClashTray.Core;

[Flags]
public enum ControllerDataDemand
{
    None = 0,
    Metrics = 1,
    Proxies = 2,
    Connections = 4,
    Rules = 8,
    Providers = 16,
    RulesAndProviders = Rules | Providers,
    All = Metrics | Proxies | Connections | RulesAndProviders
}

public enum ControllerPanelPage
{
    Proxy,
    Rules,
    Connections,
    Logs,
    Settings
}

/// <summary>
/// Selects optional reads only. Core health, ownership and recovery do not use
/// this policy. Hosts without a panel keep the previous polling behavior.
/// </summary>
internal sealed class PanelRefreshPolicy(TimeProvider? timeProvider = null)
{
    private sealed class Lane
    {
        public long Background { get; set; }
        public long? HeaderConnections { get; set; }
        public long? RulesAndProviders { get; set; }
    }

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lane _local = new();
    private readonly Lane _remote = new();
    private bool _configured;
    private bool _visible;
    private ControllerPanelPage _page;

    internal bool Set(bool visible, ControllerPanelPage page)
    {
        if (!Enum.IsDefined(page))
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        lock (_gate)
        {
            if (_configured && _visible == visible && _page == page)
            {
                return false;
            }

            _configured = true;
            _visible = visible;
            _page = page;
            long now = _timeProvider.GetTimestamp();
            foreach (Lane lane in new[] { _local, _remote })
            {
                lane.Background = now;
                lane.HeaderConnections = null;
                lane.RulesAndProviders = null;
            }

            return true;
        }
    }

    internal ControllerDataDemand GetImmediateDemand()
    {
        lock (_gate)
        {
            return !_configured ? ControllerDataDemand.All
                : !_visible ? ControllerDataDemand.None
                : VisiblePageDemand(_page) | ControllerDataDemand.Connections;
        }
    }

    internal ControllerDataDemand GetPollingDemand(EndpointKind kind, bool activeController)
    {
        lock (_gate)
        {
            if (!_configured)
            {
                return kind == EndpointKind.Local
                    ? ControllerDataDemand.Metrics | ControllerDataDemand.Proxies | ControllerDataDemand.Connections
                    : ControllerDataDemand.All;
            }

            Lane lane = kind == EndpointKind.Local ? _local : _remote;
            long now = _timeProvider.GetTimestamp();
            if (!_visible || !activeController)
            {
                if (_timeProvider.GetElapsedTime(lane.Background, now) < TimeSpan.FromSeconds(30))
                {
                    return ControllerDataDemand.None;
                }

                lane.Background = now;
                return ControllerDataDemand.Metrics | ControllerDataDemand.Connections;
            }

            ControllerDataDemand demand = VisiblePageDemand(_page);
            if (_page != ControllerPanelPage.Connections)
            {
                if (IsDue(lane.HeaderConnections, now))
                {
                    demand |= ControllerDataDemand.Connections;
                    lane.HeaderConnections = now;
                }
            }

            if ((demand & ControllerDataDemand.RulesAndProviders) != 0)
            {
                if (!IsDue(lane.RulesAndProviders, now))
                {
                    demand &= ~ControllerDataDemand.RulesAndProviders;
                }
                else
                {
                    lane.RulesAndProviders = now;
                }
            }

            return demand;
        }
    }

    private bool IsDue(long? last, long now) =>
        last is null || _timeProvider.GetElapsedTime(last.Value, now) >= TimeSpan.FromSeconds(10);

    private static ControllerDataDemand VisiblePageDemand(ControllerPanelPage page) =>
        ControllerDataDemand.Metrics | page switch
        {
            ControllerPanelPage.Proxy => ControllerDataDemand.Proxies | ControllerDataDemand.Providers,
            ControllerPanelPage.Connections => ControllerDataDemand.Connections,
            ControllerPanelPage.Rules => ControllerDataDemand.RulesAndProviders,
            ControllerPanelPage.Settings => ControllerDataDemand.Providers,
            _ => ControllerDataDemand.None
        };
}
