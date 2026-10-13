using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace ClashTray.Core;

internal sealed record ListenerOwnerRow(IPAddress Address, int Port, int ProcessId);

/// <summary>
/// Reuses tables and identity only within one synchronous readiness/health observation.
/// Create a new scope for every retry, generation and controller write guard.
/// </summary>
internal sealed class ListenerOwnerObservationScope
{
    private readonly int _expectedProcessId;
    private readonly Func<bool> _confirmIdentity;
    private readonly Dictionary<(AddressFamily Family, PortTransport Transport), TableObservation> _tables;
    private bool? _identityConfirmed;
    private string? _identityError;

    private sealed record TableObservation(IReadOnlyList<ListenerOwnerRow> Rows, string? Error);

    private ListenerOwnerObservationScope(int expectedProcessId,
        Dictionary<(AddressFamily Family, PortTransport Transport), TableObservation> tables, Func<bool> confirmIdentity)
    {
        _expectedProcessId = expectedProcessId;
        _tables = tables;
        _confirmIdentity = confirmIdentity;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Native table failures become explicit Unknown observations and must fail closed.")]
    public static ListenerOwnerObservationScope CreateSnapshot(IReadOnlyList<LocalPortBinding> listeners, int expectedProcessId,
        Func<AddressFamily, PortTransport, IReadOnlyList<ListenerOwnerRow>> readTable, Func<bool> confirmIdentity)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(readTable);
        ArgumentNullException.ThrowIfNull(confirmIdentity);
        HashSet<(AddressFamily Family, PortTransport Transport)> required = [];
        foreach (LocalPortBinding listener in listeners)
        {
            required.Add((listener.Address.AddressFamily, listener.Transport));
            if (listener.DualMode)
            {
                required.Add((AddressFamily.InterNetwork, listener.Transport));
            }
        }

        Dictionary<(AddressFamily Family, PortTransport Transport), TableObservation> tables = [];
        foreach ((AddressFamily family, PortTransport transport) in required)
        {
            try
            {
                tables.Add((family, transport), new(readTable(family, transport), null));
            }
            catch (Exception exception)
            {
                tables.Add((family, transport), new([], $"Windows listener ownership could not be queried ({exception.GetType().Name})."));
            }
        }

        // Complete every OS table read before joining its PIDs to the process
        // identity. No later table can be joined to an earlier cached identity.
        return new(expectedProcessId, tables, confirmIdentity);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An identity query failure is an Unknown observation and must fail closed.")]
    public ListenerOwnerObservation Inspect(LocalPortBinding listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (listener.Port is < 1 or > 65535
            || listener.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || !Enum.IsDefined(listener.Transport)
            || listener.DualMode && !listener.Address.Equals(IPAddress.IPv6Any))
        {
            return new(ListenerOwnerState.Unknown, "The listener address, port or transport is invalid.");
        }

        HashSet<int> owners = [];
        string? error = AddOwners(listener.Address, listener.Port, listener.Transport, owners);
        if (error is null && listener.DualMode)
        {
            error = AddOwners(IPAddress.Any, listener.Port, listener.Transport, owners);
        }

        if (error is not null)
        {
            return new(ListenerOwnerState.Unknown, error);
        }

        if (owners.Count == 0)
        {
            return new(ListenerOwnerState.Missing);
        }

        if (owners.Any(owner => owner != _expectedProcessId))
        {
            return new(ListenerOwnerState.Foreign, "The exact address, port, and transport has a different owner PID.");
        }

        try
        {
            _identityConfirmed ??= _confirmIdentity();
        }
        catch (Exception exception)
        {
            _identityConfirmed = false;
            _identityError = $"Process identity could not be queried ({exception.GetType().Name}).";
        }

        return _identityConfirmed == true
            ? new(ListenerOwnerState.Owned)
            : new(ListenerOwnerState.Unknown, _identityError ?? "The owner PID could not be joined to the expected process creation time and image.");
    }

    private string? AddOwners(IPAddress address, int port, PortTransport transport, HashSet<int> owners)
    {
        if (!_tables.TryGetValue((address.AddressFamily, transport), out TableObservation? table))
        {
            return "The listener was not included in this observation's planned tables.";
        }
        if (table.Error is not null)
        {
            return table.Error;
        }

        foreach (ListenerOwnerRow row in table.Rows)
        {
            if (row.Port == port && WindowsListenerOwnerTable.AddressMatches(address, row.Address))
            {
                owners.Add(row.ProcessId);
            }
        }

        return null;
    }

}
