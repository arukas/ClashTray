using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record ServiceBindingAdmission(CoreRuntimeBinding? Binding, ServiceCommandException? Error);
internal sealed record ServiceStartReconciliation(ServiceResponse? Status, ServiceCommandException? BindingError, string? PendingMessage);

// Observes a previously dispatched request only. It never starts a local core,
// changes a controller session or treats a missing reply as a failed dispatch.
internal sealed class ServiceCoreReconciler(LocalDeviceCoordinator device)
{
    public static ServiceBindingAdmission Admit(ServiceResponse response)
    {
        RuntimeBindingValidationResult validation = RuntimeBindingValidator.Validate(response.RuntimeBinding);
        return response.RuntimeBinding is not null && validation.IsValid
            ? new(response.RuntimeBinding, null)
            : new(null, RuntimeBindingValidator.CreateAdmissionException(validation));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A bounded observation refines an already dispatched start; unavailable evidence must retain unknown ownership rather than permit local fallback.")]
    public async Task<ServiceStartReconciliation> ObserveDispatchedStartAsync(Exception startFailure, CancellationToken lifetime)
    {
        ServiceResponse? status = null;
        Exception? observationFailure = null;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            status = await device.GetStatusAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) { observationFailure = exception; }

        if (status?.Core == CoreState.Running)
        {
            ServiceBindingAdmission admission = Admit(status);
            if (admission.Error is { } error)
            {
                return new(status with
                {
                    Succeeded = false, Error = error.Message, ErrorCode = error.ErrorCode,
                    DispatchState = ServiceDispatchState.DispatchedAwaitingResult
                }, error, null);
            }
            return new(status with { Succeeded = true, Error = null }, null, null);
        }

        string detail = status is null
            ? $"状态查询失败：{ErrorSanitizer.Sanitize(observationFailure ?? startFailure)}"
            : $"服务当前报告 {status.Core}";
        return new(null, null,
            $"ClashTray 服务启动请求已发送，但结果尚未确认（{detail}）。程序将继续后台核对，并在退出时保守停止服务核心。");
    }
}
