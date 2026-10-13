using System.Diagnostics.CodeAnalysis;
using System.Net;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal enum RuntimeBindingValidationFailure
{
    None,
    MetadataMissing,
    Malformed
}

internal sealed record RuntimeBindingValidationResult(
    RuntimeBindingValidationFailure Failure,
    string? Detail,
    IReadOnlyList<LocalPortBinding> Listeners)
{
    public bool IsValid => Failure == RuntimeBindingValidationFailure.None;
}

/// <summary>Validates the complete listener contract before any OS ownership query.</summary>
internal static class RuntimeBindingValidator
{
    public static RuntimeBindingValidationResult Validate(CoreRuntimeBinding? binding)
    {
        if (binding?.ListenerBindings is null)
        {
            return new(RuntimeBindingValidationFailure.MetadataMissing, "缺少完整运行监听元数据。", []);
        }

        if (binding.ListenerBindings.Count is < 1 or > 256
            || binding.AdditionalListeners is { Count: > 256 }
            || binding.ControllerPort is < 1 or > 65535
            || binding.PreferredControllerPort is < 1 or > 65535
            || binding.HttpPort is < 0 or > 65535
            || binding.SocksPort is < 0 or > 65535
            || binding.MixedPort is < 0 or > 65535
            || binding.InstanceId == Guid.Empty || binding.OwnerInstanceId == Guid.Empty
            || binding.ProcessId <= 0 || binding.ProcessGeneration <= 0
            || binding.ProcessStartedUtcTicks <= DateTime.MinValue.Ticks
            || binding.ProcessStartedUtcTicks >= DateTime.MaxValue.Ticks
            || string.IsNullOrWhiteSpace(binding.ExecutablePath)
            || !Path.IsPathFullyQualified(binding.ExecutablePath)
            || !binding.ControllerReady
            || binding.HttpReady != (binding.HttpPort > 0)
            || binding.SocksReady != (binding.SocksPort > 0)
            || binding.MixedReady != (binding.MixedPort > 0)
            || binding.ListenerPlanWarning is { Length: > 512 }
            || !binding.ListenerPlanComplete && string.IsNullOrWhiteSpace(binding.ListenerPlanWarning))
        {
            return Invalid("运行绑定的身份、端口、就绪标记或计划说明无效。");
        }

        int[] enabledPorts = new[] { binding.ControllerPort, binding.HttpPort, binding.SocksPort, binding.MixedPort }
            .Where(port => port > 0).ToArray();
        if (enabledPorts.Distinct().Count() != enabledPorts.Length)
        {
            return Invalid("控制器与主代理的启用端口必须不同。");
        }

        Dictionary<string, LocalPortBinding> listeners = new(StringComparer.Ordinal);
        HashSet<(IPAddress Address, int Port, PortTransport Transport)> endpoints = [];
        foreach (RuntimeListenerBinding? record in binding.ListenerBindings)
        {
            if (!TryParse(record, out LocalPortBinding? listener)
                || !listeners.TryAdd(listener.Name, listener)
                || !endpoints.Add((listener.Address, listener.Port, listener.Transport)))
            {
                return Invalid("运行监听包含无效、重复或相互矛盾的记录。");
            }
        }

        if (!listeners.TryGetValue("controller", out LocalPortBinding? controller)
            || controller.Port != binding.ControllerPort || controller.Transport != PortTransport.Tcp
            || !controller.Address.Equals(IPAddress.Loopback) || controller.DualMode)
        {
            return Invalid("控制器监听必须与确认端口和 127.0.0.1 TCP 一致。");
        }

        Dictionary<string, (int Port, PortTransport Transport)> required = new(StringComparer.Ordinal)
        {
            ["controller"] = (binding.ControllerPort, PortTransport.Tcp)
        };
        AddRequired("http-tcp", binding.HttpPort, PortTransport.Tcp);
        AddRequired("socks-tcp", binding.SocksPort, PortTransport.Tcp);
        AddRequired("socks-udp", binding.SocksPort, PortTransport.Udp);
        AddRequired("mixed-tcp", binding.MixedPort, PortTransport.Tcp);
        AddRequired("mixed-udp", binding.MixedPort, PortTransport.Udp);
        foreach ((string name, (int port, PortTransport transport)) in required)
        {
            if (!listeners.TryGetValue(name, out LocalPortBinding? listener)
                || listener.Port != port || listener.Transport != transport)
            {
                return Invalid($"必要监听 {name} 缺失或与确认端口/协议不一致。");
            }
        }

        LocalPortBinding[] proxyListeners = required.Keys.Where(name => name != "controller")
            .Select(name => listeners[name]).ToArray();
        if (proxyListeners.Any(listener => !listener.Address.Equals(proxyListeners[0].Address)
            || listener.DualMode != proxyListeners[0].DualMode))
        {
            return Invalid("主代理 TCP/UDP 监听地址与双栈语义不一致。");
        }

        HashSet<string> additionalNames = new(StringComparer.Ordinal);
        foreach (RuntimeListenerBinding? record in binding.AdditionalListeners ?? [])
        {
            if (!TryParse(record, out LocalPortBinding? additional)
                || required.ContainsKey(additional.Name) || !additionalNames.Add(additional.Name)
                || !listeners.TryGetValue(additional.Name, out LocalPortBinding? listener)
                || listener != additional)
            {
                return Invalid("附加监听必须与完整监听集合中的唯一记录一致。");
            }
        }

        if (listeners.Count != required.Count + additionalNames.Count)
        {
            return Invalid("完整监听集合包含未声明的监听或已禁用代理的监听。");
        }

        return new(RuntimeBindingValidationFailure.None, null, listeners.Values.ToArray());

        void AddRequired(string name, int port, PortTransport transport)
        {
            if (port > 0)
            {
                required.Add(name, (port, transport));
            }
        }
    }

    public static bool AreListenersOwned(CoreRuntimeBinding binding, Func<LocalPortBinding, ListenerOwnerObservation> inspect)
    {
        ArgumentNullException.ThrowIfNull(inspect);
        RuntimeBindingValidationResult validation = Validate(binding);
        return validation.IsValid
            && validation.Listeners.All(listener => inspect(listener).State == ListenerOwnerState.Owned);
    }

    public static bool AreListenersOwned(CoreRuntimeBinding binding, LocalCoreProcessIdentity identity)
    {
        RuntimeBindingValidationResult validation = Validate(binding);
        if (!validation.IsValid)
        {
            return false;
        }

        ListenerOwnerObservationScope observation = WindowsListenerOwnerTable.CreateObservation(identity, validation.Listeners);
        return validation.Listeners.All(listener => observation.Inspect(listener).State == ListenerOwnerState.Owned);
    }

    public static ServiceCommandException CreateAdmissionException(RuntimeBindingValidationResult validation) =>
        validation.Failure == RuntimeBindingValidationFailure.MetadataMissing
            ? new(ServiceErrorCode.RuntimeBindingMetadataMissing,
                "ClashTray 服务缺少当前版本所需的核心运行信息，请同步升级桌面程序与服务后重试。")
            : new(ServiceErrorCode.RuntimeBindingInvalid,
                $"ClashTray 服务返回的核心运行信息无效，无法确认控制器与代理监听。{validation.Detail}");

    private static RuntimeBindingValidationResult Invalid(string detail) =>
        new(RuntimeBindingValidationFailure.Malformed, detail, []);

    private static bool TryParse(RuntimeListenerBinding? record, [NotNullWhen(true)] out LocalPortBinding? listener)
    {
        listener = null;
        if (record is null || string.IsNullOrWhiteSpace(record.Name) || record.Name.Length > 64
            || record.Name.Any(char.IsControl) || record.Port is < 1 or > 65535
            || !Enum.IsDefined(record.Transport) || !IPAddress.TryParse(record.Address, out IPAddress? address)
            || record.DualMode && !address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        listener = new(record.Name, address, record.Port,
            record.Transport == RuntimeListenerTransport.Tcp ? PortTransport.Tcp : PortTransport.Udp, record.DualMode);
        return true;
    }
}
