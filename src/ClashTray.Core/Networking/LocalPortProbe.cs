using System.Net;
using System.Net.Sockets;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal enum PortTransport
{
    Tcp,
    Udp
}

internal enum PortProbeStatus
{
    Available,
    InUse,
    AccessDenied,
    InvalidAddress,
    Unknown
}

internal sealed record PortProbeResult(PortProbeStatus Status, SocketError? SocketError = null)
{
    public bool IsAvailable => Status == PortProbeStatus.Available;
}

internal sealed record LocalPortBinding(
    string Name,
    IPAddress Address,
    int Port,
    PortTransport Transport,
    bool DualMode = false);

internal sealed record PortPlanConflict(
    LocalPortBinding Listener,
    PortProbeResult Probe,
    string? ConflictingWith = null)
{
    public bool IsInternalConflict => ConflictingWith is not null;
}

internal sealed record LocalPortPlanResult(PortPlanConflict? Conflict)
{
    public bool IsAvailable => Conflict is null;
}

internal sealed record ControllerPortAllocationResult(
    int? Port,
    bool UsedFallback,
    PortPlanConflict? Conflict,
    int FallbackCandidatesExamined = 0)
{
    public bool Succeeded => Port is not null;
}

/// <summary>
/// Selects an actual controller port while leaving all non-controller
/// listeners fixed. Production still probes the Windows sockets; the small
/// delegate seams make candidate budgets deterministic in tests.
/// </summary>
internal static class ControllerPortAllocator
{
    public const int MinimumFallbackPort = 49152;
    public const int MaximumFallbackPort = 65535;
    public const int MaximumFallbackCandidates = 16;
    public const int MaximumStartAttempts = 3;

    public static ControllerPortAllocationResult Allocate(
        int preferredPort,
        ControllerPortConflictPolicy policy,
        bool useAvailablePortOnce,
        IReadOnlyList<LocalPortBinding> fixedListeners,
        Func<int> nextCandidate,
        Func<IReadOnlyList<LocalPortBinding>, LocalPortPlanResult>? probePlan = null,
        HashSet<int>? attemptedPorts = null,
        int maximumFallbackCandidates = MaximumFallbackCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fixedListeners);
        ArgumentNullException.ThrowIfNull(nextCandidate);
        if (preferredPort is < 1 or > MaximumFallbackPort)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredPort));
        }

        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maximumFallbackCandidates);

        probePlan ??= bindings => LocalPortProbe.ProbePlan(bindings, cancellationToken);
        HashSet<int> seen = attemptedPorts ?? [];
        cancellationToken.ThrowIfCancellationRequested();
        LocalPortPlanResult preferred;
        if (seen.Add(preferredPort))
        {
            preferred = Probe(preferredPort, fixedListeners, probePlan);
            if (preferred.IsAvailable)
            {
                return new ControllerPortAllocationResult(preferredPort, false, null);
            }
        }
        else
        {
            preferred = new LocalPortPlanResult(new PortPlanConflict(
                new LocalPortBinding("controller", IPAddress.Loopback, preferredPort, PortTransport.Tcp),
                new PortProbeResult(PortProbeStatus.InUse)));
        }

        bool automatic = policy == ControllerPortConflictPolicy.AutomaticFallback || useAvailablePortOnce;
        if (!automatic || IsFixedListenerConflict(preferred.Conflict!))
        {
            return new ControllerPortAllocationResult(null, false, preferred.Conflict);
        }

        PortPlanConflict? lastConflict = preferred.Conflict;
        int examined = 0;
        int draws = 0;
        int maximumDraws = checked(maximumFallbackCandidates * 4 + 4);
        while (examined < maximumFallbackCandidates && draws++ < maximumDraws)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int candidate = nextCandidate();
            if (candidate is < MinimumFallbackPort or > MaximumFallbackPort || !seen.Add(candidate))
            {
                continue;
            }

            examined++;
            LocalPortPlanResult plan = Probe(candidate, fixedListeners, probePlan);
            if (plan.IsAvailable)
            {
                return new ControllerPortAllocationResult(candidate, true, null, examined);
            }

            if (IsFixedListenerConflict(plan.Conflict!))
            {
                return new ControllerPortAllocationResult(null, true, plan.Conflict, examined);
            }

            lastConflict = plan.Conflict;
        }

        return new ControllerPortAllocationResult(null, true, lastConflict, examined);
    }

    private static LocalPortPlanResult Probe(
        int controllerPort,
        IReadOnlyList<LocalPortBinding> fixedListeners,
        Func<IReadOnlyList<LocalPortBinding>, LocalPortPlanResult> probePlan)
    {
        List<LocalPortBinding> bindings = new(fixedListeners.Count + 1)
        {
            new LocalPortBinding("controller", IPAddress.Loopback, controllerPort, PortTransport.Tcp)
        };
        bindings.AddRange(fixedListeners);
        return probePlan(bindings);
    }

    private static bool IsFixedListenerConflict(PortPlanConflict conflict) =>
        conflict.Listener.Name != "controller"
        && !(conflict.IsInternalConflict && conflict.ConflictingWith == "controller");
}

/// <summary>
/// Probes the exact local address and transport Mihomo will bind by requesting
/// an exclusive socket bind. A failed connection attempt is not evidence that
/// a local port can be bound.
/// </summary>
internal sealed class LocalPortProbe
{
    public static PortProbeResult Probe(
        IPAddress address,
        int port,
        PortTransport transport,
        bool dualMode = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using Socket socket = CreateBoundSocket(
                new LocalPortBinding("probe", address, port, transport, dualMode),
                cancellationToken);
            return new PortProbeResult(PortProbeStatus.Available);
        }
        catch (SocketException exception)
        {
            return new PortProbeResult(Classify(exception), exception.SocketErrorCode);
        }
        catch (ArgumentException)
        {
            return new PortProbeResult(PortProbeStatus.InvalidAddress, SocketError.InvalidArgument);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public static LocalPortPlanResult ProbePlan(
        IReadOnlyList<LocalPortBinding> listeners,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        List<(LocalPortBinding Binding, Socket Socket)> reservations = new(listeners.Count);
        try
        {
            foreach (LocalPortBinding listener in listeners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    Socket socket = CreateBoundSocket(listener, cancellationToken);
                    reservations.Add((listener, socket));
                }
                catch (SocketException exception)
                {
                    PortProbeStatus probeStatus = Classify(exception);
                    string? conflictingListener = probeStatus is PortProbeStatus.InUse or PortProbeStatus.AccessDenied
                        ? reservations
                            .Select(reservation => reservation.Binding)
                            .FirstOrDefault(previous => BindingsOverlap(previous, listener))
                            ?.Name
                        : null;
                    return new LocalPortPlanResult(new PortPlanConflict(
                        listener,
                        new PortProbeResult(probeStatus, exception.SocketErrorCode),
                        conflictingListener));
                }
                catch (ArgumentException)
                {
                    return new LocalPortPlanResult(new PortPlanConflict(
                        listener,
                        new PortProbeResult(PortProbeStatus.InvalidAddress, SocketError.InvalidArgument)));
                }
            }

            return new LocalPortPlanResult(null);
        }
        finally
        {
            foreach ((_, Socket socket) in reservations)
            {
                socket.Dispose();
            }
        }
    }

    internal static PortProbeStatus Classify(SocketException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SocketErrorCode switch
        {
            SocketError.AddressAlreadyInUse => PortProbeStatus.InUse,
            SocketError.AccessDenied => PortProbeStatus.AccessDenied,
            SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported
                or SocketError.InvalidArgument => PortProbeStatus.InvalidAddress,
            _ => PortProbeStatus.Unknown
        };
    }

    private static Socket CreateBoundSocket(LocalPortBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding.Address);
        cancellationToken.ThrowIfCancellationRequested();
        if (binding.Port is < 1 or > 65535)
        {
            throw new ArgumentException("A port probe requires a fixed TCP or UDP port.", nameof(binding));
        }

        AddressFamily family = binding.Address.AddressFamily;
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new ArgumentException("Only IPv4 and IPv6 local listeners can be probed.", nameof(binding));
        }

        Socket socket = new(
            family,
            binding.Transport == PortTransport.Tcp ? SocketType.Stream : SocketType.Dgram,
            binding.Transport == PortTransport.Tcp ? ProtocolType.Tcp : ProtocolType.Udp);
        try
        {
            if (family == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = binding.DualMode;
            }

            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(binding.Address, binding.Port));
            if (binding.Transport == PortTransport.Tcp)
            {
                socket.Listen(backlog: 1);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool BindingsOverlap(LocalPortBinding left, LocalPortBinding right)
    {
        if (left.Port != right.Port || left.Transport != right.Transport)
        {
            return false;
        }

        if (left.Address.Equals(right.Address))
        {
            return true;
        }

        if (left.Address.AddressFamily == AddressFamily.InterNetwork
            && right.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            return left.Address.Equals(IPAddress.Any) || right.Address.Equals(IPAddress.Any);
        }

        LocalPortBinding ipv6 = left.Address.AddressFamily == AddressFamily.InterNetworkV6 ? left : right;
        LocalPortBinding ipv4 = left.Address.AddressFamily == AddressFamily.InterNetwork ? left : right;
        return ipv6.Address.AddressFamily == AddressFamily.InterNetworkV6
            && ipv4.Address.AddressFamily == AddressFamily.InterNetwork
            && ipv6.DualMode
            && ipv6.Address.Equals(IPAddress.IPv6Any);
    }
}
