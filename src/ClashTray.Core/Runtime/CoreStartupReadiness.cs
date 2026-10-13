using System.Net;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record CoreStartupTarget(
    int PreferredControllerPort,
    int ControllerPort,
    int HttpPort,
    int SocksPort,
    int MixedPort,
    Guid InstanceId,
    Guid OwnerInstanceId,
    LocalCoreProcessIdentity ProcessIdentity,
    long ProcessGeneration,
    MihomoEffectiveListenerPlan ListenerPlan);

// Each process owner supplies evidence from its own process and API session.
// Desktop observations are never sent to, or trusted by, the privileged service.
internal sealed record CoreStartupObservations(
    Func<bool> IsProcessCurrent,
    Func<ListenerOwnerObservation> InspectController,
    Func<CancellationToken, Task<JsonDocument>> ReadConfigurationAsync,
    Func<Func<LocalPortBinding, ListenerOwnerObservation>> CreateListenerSnapshot);

internal static class CoreStartupReadiness
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<CoreRuntimeBinding> WaitAsync(
        CoreStartupTarget target,
        CoreStartupObservations observations,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        using ListenerReadinessDeadline deadline = new(budget, cancellationToken);
        LocalPortBinding controller = new("controller", IPAddress.Loopback, target.ControllerPort, PortTransport.Tcp);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                RequireCurrentProcess(observations);
                ListenerOwnerObservation owner = observations.InspectController();
                deadline.Observe(controller, owner);
                RejectForeignController(owner);
                if (owner.State != ListenerOwnerState.Owned)
                {
                    await Task.Delay(RetryInterval, deadline.Token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    using JsonDocument configuration = await observations.ReadConfigurationAsync(deadline.Token).ConfigureAwait(false);
                    if (observations.InspectController().State != ListenerOwnerState.Owned)
                    {
                        throw new ManagedCoreOwnershipException();
                    }
                    if (!TryValidatePorts(configuration, target.HttpPort, target.SocksPort, target.MixedPort,
                        out MihomoListenerPorts ports, out ServiceErrorCode code, out string? error))
                    {
                        throw new ServiceCommandException(code, error!);
                    }
                    if (MihomoDataParser.ParseTunEnabled(configuration) is not false)
                    {
                        throw new ServiceCommandException(ServiceErrorCode.CoreReadinessFailed,
                            "Mihomo 有效配置未确认 TUN 关闭，拒绝提交启动就绪状态。");
                    }

                    ListenerReadinessResult readiness = ListenerReadinessEvaluator.Evaluate(
                        target.ListenerPlan.ProxyBindings, target.ListenerPlan.AdditionalListenerPlan.Bindings,
                        observations.CreateListenerSnapshot());
                    deadline.Observe(readiness);
                    if (readiness.Disposition == ListenerReadinessDisposition.ForeignOwner)
                    {
                        throw new ServiceCommandException(ServiceErrorCode.ProxyPortConflict,
                            $"{readiness.ListenerName} 监听由其他进程占用：{readiness.Detail ?? "无法确认当前核心持有该监听。"}");
                    }
                    if (readiness.Disposition == ListenerReadinessDisposition.Ready)
                    {
                        RequireCurrentProcess(observations);
                        return CreateBinding(target, ports);
                    }
                }
                catch (ManagedCoreOwnershipException exception)
                {
                    RejectForeignController(observations.InspectController());
                    deadline.RecordError(exception);
                }
                catch (HttpRequestException exception) { deadline.RecordError(exception); }
                catch (TimeoutException exception) { deadline.RecordError(exception); }

                await Task.Delay(RetryInterval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw deadline.CreateTimeoutException(exception); }
        catch (OperationCanceledException exception)
        { throw deadline.CreateCancellationException(exception); }
    }

    public static bool TryValidatePorts(JsonDocument configuration, int httpPort, int socksPort, int mixedPort,
        out MihomoListenerPorts ports, out ServiceErrorCode errorCode, out string? message)
    {
        ports = MihomoDataParser.ParseListenerPorts(configuration);
        errorCode = ServiceErrorCode.CoreReadinessFailed;
        message = null;
        if (ports.Http is null || ports.Socks is null || ports.Mixed is null)
        {
            message = "Mihomo /configs 未返回完整 HTTP、SOCKS、Mixed 监听端口信息。";
            return false;
        }
        if (httpPort > 0 && ports.Http != httpPort || socksPort > 0 && ports.Socks != socksPort
            || mixedPort > 0 && ports.Mixed != mixedPort)
        {
            errorCode = ServiceErrorCode.ProxyPortConflict;
            message = "Mihomo 有效代理端口与本次启动设置不一致。";
            return false;
        }
        return true;
    }

    public static List<RuntimeListenerBinding> CreateListenerBindings(int controllerPort, MihomoEffectiveListenerPlan plan)
    {
        List<RuntimeListenerBinding> bindings =
        [new("controller", IPAddress.Loopback.ToString(), controllerPort, RuntimeListenerTransport.Tcp)];
        bindings.AddRange(plan.ToContractBindings());
        return bindings;
    }

    private static CoreRuntimeBinding CreateBinding(CoreStartupTarget target, MihomoListenerPorts ports) => new(
        target.PreferredControllerPort, target.ControllerPort, target.InstanceId, target.OwnerInstanceId,
        target.ProcessIdentity.ProcessId, target.ProcessIdentity.StartTimeUtcTicks, target.ProcessGeneration,
        ports.Http!.Value, ports.Socks!.Value, ports.Mixed!.Value, ControllerReady: true,
        ports.Http > 0 && target.HttpPort > 0, ports.Socks > 0 && target.SocksPort > 0,
        ports.Mixed > 0 && target.MixedPort > 0, target.ProcessIdentity.ExecutablePath,
        target.ListenerPlan.IsComplete, target.ListenerPlan.AdditionalListenerPlan.ToContractBindings(),
        target.ListenerPlan.Warning, CreateListenerBindings(target.ControllerPort, target.ListenerPlan));

    private static void RequireCurrentProcess(CoreStartupObservations observations)
    {
        if (!observations.IsProcessCurrent())
        {
            throw new ServiceCommandException(ServiceErrorCode.CoreReadinessFailed,
                "受管 Mihomo 进程在监听就绪前退出或代际发生变化。");
        }
    }

    private static void RejectForeignController(ListenerOwnerObservation observation)
    {
        if (observation.State == ListenerOwnerState.Foreign)
        {
            throw new ServiceCommandException(ServiceErrorCode.ControllerOwnershipUnconfirmed,
                "控制器端口不属于当前受管核心；未向该端口发送控制器请求。");
        }
    }
}
