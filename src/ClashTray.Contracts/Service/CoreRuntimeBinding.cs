namespace ClashTray.Contracts;

/// <summary>
/// Facts confirmed by the process owner for one running local Mihomo instance.
/// ControllerPort is the actual bound port; ControllerPortPreference remains a
/// user setting and is never inferred from an unconfirmed service response.
/// </summary>
public sealed record CoreRuntimeBinding(
    int PreferredControllerPort,
    int ControllerPort,
    Guid InstanceId,
    Guid OwnerInstanceId,
    int ProcessId,
    long ProcessStartedUtcTicks,
    long ProcessGeneration,
    int HttpPort,
    int SocksPort,
    int MixedPort,
    bool ControllerReady,
    bool HttpReady,
    bool SocksReady,
    bool MixedReady,
    string ExecutablePath = "",
    bool ListenerPlanComplete = true,
    IReadOnlyList<RuntimeListenerBinding>? AdditionalListeners = null,
    string? ListenerPlanWarning = null,
    IReadOnlyList<RuntimeListenerBinding>? ListenerBindings = null);

public enum RuntimeListenerTransport
{
    Tcp,
    Udp
}

public sealed record RuntimeListenerBinding(
    string Name,
    string Address,
    int Port,
    RuntimeListenerTransport Transport,
    bool DualMode = false);
