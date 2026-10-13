using System.Security.Cryptography;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal static class CoreControllerPortPlanning
{
    public static ControllerPortAllocationResult Allocate(
        AppSettings settings,
        bool useAvailablePortOnce,
        IReadOnlyList<LocalPortBinding> fixedListeners,
        HashSet<int> attemptedPorts,
        int maximumFallbackCandidates,
        CancellationToken cancellationToken) => ControllerPortAllocator.Allocate(
            settings.ControllerPort,
            settings.ControllerPortConflictPolicy,
            useAvailablePortOnce,
            fixedListeners,
            static () => RandomNumberGenerator.GetInt32(
                ControllerPortAllocator.MinimumFallbackPort,
                ControllerPortAllocator.MaximumFallbackPort + 1),
            attemptedPorts: attemptedPorts,
            maximumFallbackCandidates: maximumFallbackCandidates,
            cancellationToken: cancellationToken);

    public static int Require(ControllerPortAllocationResult allocation)
    {
        if (allocation.Port is int port)
        {
            return port;
        }

        PortPlanConflict? conflict = allocation.Conflict;
        if (conflict is null)
        {
            throw new ServiceCommandException(ServiceErrorCode.ControllerCandidatesExhausted,
                "没有找到可用的高位控制器端口（最多探测 16 个候选）。");
        }

        bool controllerConflict = conflict.Listener.Name == "controller"
            || conflict.ConflictingWith == "controller";
        string message = conflict.IsInternalConflict
            ? $"本地监听配置冲突：{conflict.ConflictingWith} 与 {conflict.Listener.Name} 使用端口 {conflict.Listener.Port}。"
            : $"{conflict.Listener.Name} 监听端口 {conflict.Listener.Port} 无法使用（{conflict.Probe.Status}）。";
        throw new ServiceCommandException(
            controllerConflict ? ServiceErrorCode.ControllerPortConflict : ServiceErrorCode.ProxyPortConflict,
            message);
    }

}
