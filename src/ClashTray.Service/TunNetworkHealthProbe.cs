using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using ClashTray.Core;

namespace ClashTray.Service;

internal enum TunNetworkExpectation
{
    Enabled,
    Disabled
}

internal sealed record TunNetworkHealth(
    bool InterfaceFound,
    bool HasValidAddress,
    bool HasRequiredRoute,
    bool HasRequiredDns,
    string? InterfaceName,
    string? Diagnostic,
    bool HasActiveRoute = false,
    bool HasActiveDns = false,
    bool ProbeSucceeded = true,
    bool RouteStateKnown = true)
{
    public bool MeetsEnabled =>
        ProbeSucceeded && InterfaceFound && HasValidAddress && HasRequiredRoute && HasRequiredDns;

    public bool MeetsDisabled =>
        ProbeSucceeded
        && RouteStateKnown
        && (!InterfaceFound
            || (!HasValidAddress && !HasActiveRoute && !HasActiveDns));
}

internal interface ITunNetworkHealthProbe
{
    public Task<TunNetworkHealth> ProbeAsync(
        MihomoTunConfiguration configuration,
        TunNetworkExpectation expectation,
        CancellationToken cancellationToken);
}

internal interface ITunRouteTableReader
{
    public bool TryReadActiveRouteInterfaceIndices(
        out IReadOnlySet<uint> interfaceIndices,
        out string? diagnostic);
}

/// <summary>
/// Reads the Windows route table without invoking a shell. GatewayAddresses
/// cannot be used here: it describes configured IPv4 gateways, while Wintun
/// routes are commonly on-link entries whose next hop is all zeroes.
/// </summary>
internal sealed class WindowsTunRouteTableReader : ITunRouteTableReader
{
    private const ushort AfUnspecified = 0;
    private const uint NoError = 0;
    private const uint LocalRouteProtocol = 2;
    private const int ExpectedRowSizeX64 = 104;
    private const int FirstRowOffsetX64 = 8;

    internal static bool IsInteropLayoutSupported =>
        OperatingSystem.IsWindows()
        && IntPtr.Size == 8
        && Marshal.SizeOf<MibIpForwardRow2>() == ExpectedRowSizeX64
        && Marshal.OffsetOf<MibIpForwardRow2>(nameof(MibIpForwardRow2.InterfaceIndex)).ToInt32() == 8
        && Marshal.OffsetOf<MibIpForwardRow2>(nameof(MibIpForwardRow2.Protocol)).ToInt32() == 88
        && Marshal.OffsetOf<MibIpForwardRow2>(nameof(MibIpForwardRow2.Loopback)).ToInt32() == 92;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031",
        Justification = "A read-only native probe failure is returned as unknown and must never crash the service.")]
    public bool TryReadActiveRouteInterfaceIndices(
        out IReadOnlySet<uint> interfaceIndices,
        out string? diagnostic)
    {
        HashSet<uint> activeInterfaces = [];
        interfaceIndices = activeInterfaces;
        diagnostic = null;
        if (!IsInteropLayoutSupported)
        {
            diagnostic = "当前进程不支持安全读取 Windows 路由表布局。";
            return false;
        }

        nint table = nint.Zero;
        try
        {
            uint result = GetIpForwardTable2(AfUnspecified, out table);
            if (result != NoError)
            {
                diagnostic = $"读取 Windows 路由表失败：{new Win32Exception(checked((int)result)).Message}";
                return false;
            }

            if (table == nint.Zero)
            {
                diagnostic = "Windows 返回了空路由表指针。";
                return false;
            }

            uint count = unchecked((uint)Marshal.ReadInt32(table));
            if (count > 1_000_000)
            {
                diagnostic = "Windows 路由表条目数超出安全上限。";
                return false;
            }

            int rowSize = Marshal.SizeOf<MibIpForwardRow2>();
            for (uint index = 0; index < count; index++)
            {
                int offset = checked(FirstRowOffsetX64 + checked((int)index) * rowSize);
                MibIpForwardRow2 row = Marshal.PtrToStructure<MibIpForwardRow2>(table + offset);
                if (row.InterfaceIndex != 0
                    && row.Loopback == 0
                    && row.Protocol != LocalRouteProtocol)
                {
                    activeInterfaces.Add(row.InterfaceIndex);
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            diagnostic = $"读取 Windows 路由表失败：{exception.GetType().Name}";
            return false;
        }
        finally
        {
            if (table != nint.Zero)
            {
                FreeMibTable(table);
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 28)]
    private struct SockaddrInet
    {
        [FieldOffset(0)]
        public ushort Family;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct IpAddressPrefix
    {
        [FieldOffset(0)]
        public SockaddrInet Prefix;

        [FieldOffset(28)]
        public byte PrefixLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpForwardRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet NextHop;
        public byte SitePrefixLength;
        public uint ValidLifetime;
        public uint PreferredLifetime;
        public uint Metric;
        public uint Protocol;
        public byte Loopback;
        public byte AutoconfigureAddress;
        public byte Publish;
        public byte Immortal;
        public uint Age;
        public uint Origin;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable2(ushort family, out nint table);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(nint memory);
}

/// <summary>
/// Read-only probe for the adapter state used by Mihomo TUN. It intentionally
/// does not delete an adapter or execute netsh/PowerShell commands: an adapter
/// may legitimately remain installed while its address, route, and DNS state
/// are released.
/// </summary>
internal sealed class WindowsTunNetworkHealthProbe : ITunNetworkHealthProbe
{
    private readonly ITunRouteTableReader _routeTableReader;

    public WindowsTunNetworkHealthProbe()
        : this(new WindowsTunRouteTableReader())
    {
    }

    internal WindowsTunNetworkHealthProbe(ITunRouteTableReader routeTableReader)
    {
        _routeTableReader = routeTableReader ?? throw new ArgumentNullException(nameof(routeTableReader));
    }

    public Task<TunNetworkHealth> ProbeAsync(
        MihomoTunConfiguration configuration,
        TunNetworkExpectation expectation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            NetworkInterface[] candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(networkInterface => IsCandidate(networkInterface, configuration.DeviceName))
                .ToArray();
            if (candidates.Length == 0)
            {
                return Task.FromResult(
                    new TunNetworkHealth(false, false, false, false, null, "未找到 Mihomo TUN 网卡。"));
            }

            IReadOnlySet<uint> activeRouteInterfaces = new HashSet<uint>();
            string? routeDiagnostic = null;
            bool routeStateKnown = _routeTableReader.TryReadActiveRouteInterfaceIndices(
                out activeRouteInterfaces,
                out routeDiagnostic);
            IEnumerable<TunNetworkHealth> observations = candidates.Select(networkInterface => ReadHealth(
                networkInterface,
                configuration,
                activeRouteInterfaces,
                routeStateKnown,
                routeDiagnostic));
            TunNetworkHealth health = expectation == TunNetworkExpectation.Enabled
                ? observations
                    .OrderByDescending(value => value.MeetsEnabled)
                    .ThenByDescending(value => value.HasValidAddress)
                    .First()
                : observations
                    .OrderBy(value => value.MeetsDisabled)
                    .ThenByDescending(value => value.HasValidAddress || value.HasActiveRoute || value.HasActiveDns)
                    .First();
            return Task.FromResult(health);
        }
        catch (NetworkInformationException exception)
        {
            return Task.FromResult(new TunNetworkHealth(
                InterfaceFound: false,
                HasValidAddress: false,
                HasRequiredRoute: false,
                HasRequiredDns: false,
                InterfaceName: null,
                Diagnostic: $"读取 Windows 网络状态失败：{exception.GetType().Name}",
                ProbeSucceeded: false,
                RouteStateKnown: false));
        }
    }

    private static TunNetworkHealth ReadHealth(
        NetworkInterface networkInterface,
        MihomoTunConfiguration configuration,
        IReadOnlySet<uint> activeRouteInterfaces,
        bool routeStateKnown,
        string? routeDiagnostic)
    {
        IPInterfaceProperties properties = networkInterface.GetIPProperties();
        bool hasAddress = properties.UnicastAddresses.Any(address => IsLegalAddress(address));
        bool hasActiveRoute = ReadInterfaceIndices(properties).Any(activeRouteInterfaces.Contains);
        bool hasRequiredRoute = !configuration.AutoRoute || routeStateKnown && hasActiveRoute;
        bool hasActiveDns = configuration.RequiresDnsHealth
            && properties.DnsAddresses.Any(IsLegalDnsAddress);
        bool hasRequiredDns = !configuration.RequiresDnsHealth || hasActiveDns;
        bool interfaceFound = networkInterface.OperationalStatus == OperationalStatus.Up
            || hasAddress
            || hasActiveRoute
            || hasActiveDns;
        return new TunNetworkHealth(
            interfaceFound,
            hasAddress,
            hasRequiredRoute,
            hasRequiredDns,
            networkInterface.Name,
            !routeStateKnown
                ? routeDiagnostic ?? "Windows 路由状态无法确认。"
                : interfaceFound
                    ? null
                    : $"接口 {networkInterface.Name} 尚未处于活动状态。",
            HasActiveRoute: hasActiveRoute,
            HasActiveDns: hasActiveDns,
            ProbeSucceeded: !configuration.AutoRoute || routeStateKnown,
            RouteStateKnown: routeStateKnown);
    }

    private static List<uint> ReadInterfaceIndices(IPInterfaceProperties properties)
    {
        List<uint> indices = [];
        try
        {
            IPv4InterfaceProperties? ipv4 = properties.GetIPv4Properties();
            if (ipv4 is not null && ipv4.Index > 0)
            {
                indices.Add(checked((uint)ipv4.Index));
            }
        }
        catch (NetworkInformationException)
        {
            // Some Windows adapters expose only one IP family. The other family
            // must not make the complete TUN health probe indeterminate.
        }

        try
        {
            IPv6InterfaceProperties? ipv6 = properties.GetIPv6Properties();
            if (ipv6 is not null && ipv6.Index > 0)
            {
                indices.Add(checked((uint)ipv6.Index));
            }
        }
        catch (NetworkInformationException)
        {
            // See the IPv4 comment above.
        }

        return indices;
    }

    private static bool IsCandidate(NetworkInterface networkInterface, string? configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName))
        {
            return string.Equals(networkInterface.Name, configuredName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(networkInterface.Description, configuredName, StringComparison.OrdinalIgnoreCase);
        }

        string identity = $"{networkInterface.Name} {networkInterface.Description}";
        return identity.Contains("mihomo", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("clashtray", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("clash tunnel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLegalAddress(UnicastIPAddressInformation address)
    {
        IPAddress value = address.Address;
        return IsLegalAddress(value)
            && address.PrefixLength <= (value.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
    }

    private static bool IsLegalAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.IPv6Any);

    private static bool IsLegalDnsAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.IPv6Any);
}
