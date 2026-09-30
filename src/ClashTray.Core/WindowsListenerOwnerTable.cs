using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.ComponentModel;
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
    private const int TcpTableOwnerPidListener = 3;
    private const int UdpTableOwnerPid = 1;
    private const int TcpStateListen = 2;

    public static bool IsOwnedBy(
        IPAddress address,
        int port,
        PortTransport transport,
        LocalCoreProcessIdentity expectedOwner)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(expectedOwner);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Mihomo listener ownership confirmation requires Windows IP Helper tables.");
        }

        if (port is < 1 or > 65535 || !IsCurrentProcessIdentity(expectedOwner))
        {
            return false;
        }

        return transport == PortTransport.Tcp
            ? FindTcpOwners(address, port).Contains(expectedOwner.ProcessId)
            : FindUdpOwners(address, port).Contains(expectedOwner.ProcessId);
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
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartTimeUtcTicks)
            {
                return false;
            }

            string? executablePath;
            try
            {
                executablePath = process.MainModule?.FileName;
            }
            catch (Win32Exception)
            {
                executablePath = QueryProcessImagePath(process.SafeHandle);
            }

            executablePath ??= QueryProcessImagePath(process.SafeHandle);
            return !string.IsNullOrWhiteSpace(executablePath)
                && string.Equals(
                    Path.GetFullPath(executablePath),
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

    private static string QueryProcessImagePath(SafeProcessHandle processHandle)
    {
        char[] path = new char[32_768];
        uint length = (uint)path.Length;
        if (!QueryFullProcessImageName(processHandle, 0, path, ref length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not confirm the Mihomo process image path.");
        }

        return new string(path, 0, checked((int)length));
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
        return new IPAddress(address);
    }

    private static bool AddressMatches(IPAddress expected, IPAddress local) =>
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
}
