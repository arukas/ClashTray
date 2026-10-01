using System.Net;
using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;

namespace ClashTray.Core;

/// <summary>
/// Reads the Windows IP Helper owner-PID tables and joins the result with the
/// process start time and executable path captured by the process owner.
/// </summary>
internal static class WindowsListenerOwnerTable
{
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorNoData = 232;
    private const uint AfInet = 2;
    private const uint AfInet6 = 23;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;
    private const int TcpStateListen = 2;

    public static ListenerOwnerObservationScope CreateObservation(LocalCoreProcessIdentity expectedOwner,
        IReadOnlyList<LocalPortBinding> listeners)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentNullException.ThrowIfNull(listeners);
        HashSet<int> ports = listeners.Select(listener => listener.Port).ToHashSet();
        return ListenerOwnerObservationScope.CreateSnapshot(listeners, expectedOwner.ProcessId,
            (family, transport) => ReadListenerRows(family, transport, ports), () => IsCurrentProcessIdentity(expectedOwner));
    }

    public static bool IsOwnedBy(
        IPAddress address,
        int port,
        PortTransport transport,
        LocalCoreProcessIdentity expectedOwner)
    {
        return InspectListener(address, port, transport, expectedOwner).State == ListenerOwnerState.Owned;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Listener table and process identity failures must become an explicit Unknown observation so startup can retry within its existing deadline.")]
    public static ListenerOwnerObservation InspectListener(
        IPAddress address,
        int port,
        PortTransport transport,
        LocalCoreProcessIdentity expectedOwner,
        bool dualMode = false)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(expectedOwner);
        if (!OperatingSystem.IsWindows())
        {
            return new ListenerOwnerObservation(
                ListenerOwnerState.Unknown,
                "Windows IP Helper listener tables are unavailable on this platform.");
        }

        if (port is < 1 or > 65535
            || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return new ListenerOwnerObservation(ListenerOwnerState.Unknown, "The listener address or port is invalid.");
        }

        if (dualMode && !address.Equals(IPAddress.IPv6Any))
        {
            return new ListenerOwnerObservation(
                ListenerOwnerState.Unknown,
                "Dual-mode listener ownership is valid only for the IPv6 wildcard address.");
        }

        try
        {
            HashSet<int> owners = transport == PortTransport.Tcp
                ? FindTcpOwners(address, port)
                : FindUdpOwners(address, port);
            if (dualMode)
            {
                HashSet<int> ipv4Owners = transport == PortTransport.Tcp
                    ? FindTcpOwners(IPAddress.Any, port)
                    : FindUdpOwners(IPAddress.Any, port);
                owners.UnionWith(ipv4Owners);
            }
            if (owners.Count == 0)
            {
                return new ListenerOwnerObservation(ListenerOwnerState.Missing);
            }

            if (owners.Any(processId => processId != expectedOwner.ProcessId))
            {
                return new ListenerOwnerObservation(
                    ListenerOwnerState.Foreign,
                    "The exact address, port, and transport has a different owner PID.");
            }

            if (!owners.Contains(expectedOwner.ProcessId)
                || !IsCurrentProcessIdentity(expectedOwner))
            {
                return new ListenerOwnerObservation(
                    ListenerOwnerState.Unknown,
                    "The owner PID could not be joined to the expected process creation time and image.");
            }

            return new ListenerOwnerObservation(ListenerOwnerState.Owned);
        }
        catch (Exception exception)
        {
            return new ListenerOwnerObservation(
                ListenerOwnerState.Unknown,
                $"Windows listener ownership could not be queried ({exception.GetType().Name}).");
        }
    }

    public static bool IsPortOwnedBy(
        int port,
        PortTransport transport,
        LocalCoreProcessIdentity expectedOwner)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        if (port is < 1 or > 65535 || !IsCurrentProcessIdentity(expectedOwner))
        {
            return false;
        }

        IPAddress[] addresses =
        [
            IPAddress.Loopback,
            IPAddress.IPv6Loopback
        ];
        return addresses.Any(address => IsOwnedBy(address, port, transport, expectedOwner));
    }

    public static bool HasListener(int port, PortTransport transport)
    {
        if (port is < 1 or > 65535)
        {
            return false;
        }

        return transport == PortTransport.Tcp
            ? FindTcpOwners(IPAddress.IPv6Loopback, port).Count > 0
                || FindTcpOwners(IPAddress.Loopback, port).Count > 0
            : FindUdpOwners(IPAddress.IPv6Loopback, port).Count > 0
                || FindUdpOwners(IPAddress.Loopback, port).Count > 0;
    }

    internal static bool IsCurrentProcessIdentity(LocalCoreProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!OperatingSystem.IsWindows()
            || identity.ProcessId <= 0
            || identity.StartTimeUtcTicks <= DateTime.MinValue.Ticks
            || identity.StartTimeUtcTicks >= DateTime.MaxValue.Ticks
            || string.IsNullOrWhiteSpace(identity.ExecutablePath)
            || !Path.IsPathFullyQualified(identity.ExecutablePath))
        {
            return false;
        }

        try
        {
            LocalCoreProcessIdentity actual = CaptureProcessIdentity(identity.ProcessId);
            return actual.StartTimeUtcTicks == identity.StartTimeUtcTicks
                && string.Equals(
                    Path.GetFullPath(actual.ExecutablePath),
                    Path.GetFullPath(identity.ExecutablePath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or Win32Exception
            or NotSupportedException
            or SecurityException
            or IOException)
        {
            return false;
        }
    }

    internal static LocalCoreProcessIdentity CaptureProcessIdentity(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Mihomo process identity confirmation requires Windows.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        using SafeProcessHandle processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (processHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not open the Mihomo process for limited identity queries.");
        }

        string executablePath = QueryProcessImagePath(processHandle);
        if (!GetProcessTimes(
            processHandle,
            out NativeFileTime creationTime,
            out _,
            out _,
            out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not confirm the Mihomo process creation time.");
        }

        ulong fileTime = ((ulong)creationTime.HighDateTime << 32) | creationTime.LowDateTime;
        long startTimeUtcTicks = DateTime.FromFileTimeUtc(unchecked((long)fileTime)).Ticks;
        return new LocalCoreProcessIdentity(
            processId,
            startTimeUtcTicks,
            Path.GetFullPath(executablePath));
    }

    private static string QueryProcessImagePath(SafeProcessHandle processHandle)
    {
        char[] path = ArrayPool<char>.Shared.Rent(32_768);
        try
        {
            uint length = 32_768;
            if (!QueryFullProcessImageName(processHandle, 0, path, ref length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not confirm the Mihomo process image path.");
            }

            return new string(path, 0, checked((int)length));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(path, clearArray: true);
        }
    }

    private static List<ListenerOwnerRow> ReadListenerRows(AddressFamily addressFamily, PortTransport transport, HashSet<int> ports)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows IP Helper listener tables are unavailable on this platform.");
        }

        uint family = addressFamily == AddressFamily.InterNetwork ? AfInet : AfInet6;
        bool tcp = transport == PortTransport.Tcp;
        bool ipv4 = family == AfInet;
        IntPtr table = tcp ? ReadTcpTable(family) : ReadUdpTable(family);
        try
        {
            List<ListenerOwnerRow> rows = [];
            int count = Marshal.ReadInt32(table);
            int rowSize = tcp ? ipv4 ? 24 : 56 : ipv4 ? 12 : 28;
            for (int index = 0, offset = sizeof(int); index < count; index++, offset += rowSize)
            {
                IntPtr row = IntPtr.Add(table, offset);
                if (tcp && Marshal.ReadInt32(row, ipv4 ? 0 : 48) != TcpStateListen)
                {
                    continue;
                }

                int port = ReadPort(row, ipv4 ? tcp ? 8 : 4 : 20);
                if (!ports.Contains(port))
                {
                    continue;
                }

                IPAddress address = ipv4 ? ReadIpv4Address(row, tcp ? 4 : 0) : ReadIpv6Address(row, 0);
                int processId = Marshal.ReadInt32(row, tcp ? ipv4 ? 20 : 52 : ipv4 ? 8 : 24);
                rows.Add(new(address, port, processId));
            }

            return rows;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    private static HashSet<int> FindTcpOwners(IPAddress expectedAddress, int expectedPort)
    {
        HashSet<int> owners = [];
        if (expectedAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            IntPtr table = ReadTcpTable(AfInet);
            try
            {
                int count = Marshal.ReadInt32(table);
                int offset = sizeof(int);
                const int rowSize = 24;
                for (int index = 0; index < count; index++, offset += rowSize)
                {
                    IntPtr row = IntPtr.Add(table, offset);
                    if (Marshal.ReadInt32(row, 0) != TcpStateListen
                        || ReadPort(row, 8) != expectedPort
                        || !AddressMatches(expectedAddress, ReadIpv4Address(row, 4)))
                    {
                        continue;
                    }

                    owners.Add(Marshal.ReadInt32(row, 20));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }
        else if (expectedAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            IntPtr table = ReadTcpTable(AfInet6);
            try
            {
                int count = Marshal.ReadInt32(table);
                int offset = sizeof(int);
                const int rowSize = 56;
                for (int index = 0; index < count; index++, offset += rowSize)
                {
                    IntPtr row = IntPtr.Add(table, offset);
                    if (Marshal.ReadInt32(row, 48) != TcpStateListen
                        || ReadPort(row, 20) != expectedPort
                        || !AddressMatches(expectedAddress, ReadIpv6Address(row, 0)))
                    {
                        continue;
                    }

                    owners.Add(Marshal.ReadInt32(row, 52));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(table);
            }
        }

        return owners;
    }

    private static HashSet<int> FindUdpOwners(IPAddress expectedAddress, int expectedPort)
    {
        HashSet<int> owners = [];
        uint family = expectedAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => AfInet,
            AddressFamily.InterNetworkV6 => AfInet6,
            _ => 0
        };
        if (family == 0)
        {
            return owners;
        }

        IntPtr table = ReadUdpTable(family);
        try
        {
            int count = Marshal.ReadInt32(table);
            int offset = sizeof(int);
            int rowSize = family == AfInet ? 12 : 28;
            for (int index = 0; index < count; index++, offset += rowSize)
            {
                IntPtr row = IntPtr.Add(table, offset);
                IPAddress localAddress = family == AfInet
                    ? ReadIpv4Address(row, 0)
                    : ReadIpv6Address(row, 0);
                int portOffset = family == AfInet ? 4 : 20;
                int processOffset = family == AfInet ? 8 : 24;
                if (ReadPort(row, portOffset) == expectedPort
                    && AddressMatches(expectedAddress, localAddress))
                {
                    owners.Add(Marshal.ReadInt32(row, processOffset));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }

        return owners;
    }

    private static IntPtr ReadTcpTable(uint family)
    {
        int size = 0;
        uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, order: false, family, TcpTableOwnerPidListener, 0);
        if (result == ErrorNoData)
        {
            return AllocateEmptyTable();
        }

        return ReadTable(
            (IntPtr table, ref int required) => GetExtendedTcpTable(
                table,
                ref required,
                order: false,
                family,
                TcpTableOwnerPidListener,
                0),
            size,
            result);
    }

    private static IntPtr ReadUdpTable(uint family)
    {
        int size = 0;
        uint result = GetExtendedUdpTable(IntPtr.Zero, ref size, order: false, family, UdpTableOwnerPid, 0);
        if (result == ErrorNoData)
        {
            return AllocateEmptyTable();
        }

        return ReadTable(
            (IntPtr table, ref int required) => GetExtendedUdpTable(
                table,
                ref required,
                order: false,
                family,
                UdpTableOwnerPid,
                0),
            size,
            result);
    }

    private static IntPtr ReadTable(TableQuery query, int initialSize, uint initialResult)
    {
        if (initialResult != ErrorInsufficientBuffer && initialResult != 0)
        {
            throw new Win32Exception(unchecked((int)initialResult), "Windows could not read the local listener owner table.");
        }

        int size = Math.Max(initialSize, sizeof(int));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr table = Marshal.AllocHGlobal(size);
            uint result = query(table, ref size);
            if (result == 0)
            {
                return table;
            }

            Marshal.FreeHGlobal(table);
            if (result != ErrorInsufficientBuffer)
            {
                if (result == ErrorNoData)
                {
                    return AllocateEmptyTable();
                }

                throw new Win32Exception(unchecked((int)result), "Windows could not read the local listener owner table.");
            }

            size = Math.Max(size, sizeof(int));
        }

        throw new IOException("The local listener owner table changed repeatedly while it was being read.");
    }

    private static IntPtr AllocateEmptyTable()
    {
        IntPtr table = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(table, 0);
        return table;
    }

    private static int ReadPort(IntPtr row, int offset)
    {
        uint rawPort = unchecked((uint)Marshal.ReadInt32(row, offset));
        return unchecked((ushort)IPAddress.NetworkToHostOrder((short)(rawPort & 0xffff)));
    }

    private static IPAddress ReadIpv4Address(IntPtr row, int offset)
    {
        byte[] address = new byte[sizeof(uint)];
        Marshal.Copy(IntPtr.Add(row, offset), address, 0, address.Length);
        return new IPAddress(address);
    }

    private static IPAddress ReadIpv6Address(IntPtr row, int offset)
    {
        byte[] address = new byte[16];
        Marshal.Copy(IntPtr.Add(row, offset), address, 0, address.Length);
        uint scope = unchecked((uint)Marshal.ReadInt32(row, offset + address.Length));
        return new IPAddress(address, scope);
    }

    internal static bool AddressMatches(IPAddress expected, IPAddress local) =>
        expected.Equals(local)
        || local.AddressFamily == AddressFamily.InterNetwork && local.Equals(IPAddress.Any)
        || local.AddressFamily == AddressFamily.InterNetworkV6 && local.Equals(IPAddress.IPv6Any)
            && expected.AddressFamily == AddressFamily.InterNetworkV6;

    private delegate uint TableQuery(IntPtr table, ref int size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family,
        int tableClass,
        uint reserved);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr udpTable,
        ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family,
        int tableClass,
        uint reserved);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle process,
        uint flags,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] char[] executablePath,
        ref uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}
