using System.Diagnostics.CodeAnalysis;
using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime
{

    public Task<RuntimeShutdownResult> ShutdownAsync()
    {
        lock (_disposeGate)
        {
            _shutdownTask ??= DisposeCoreAsync();
            return _shutdownTask;
        }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The deadline and cleanup lease are released by a finally block or transferred to the continuation that observes an abandoned cleanup task.")]
    [SuppressMessage("Reliability", "CA2025:Ensure that tasks are completed before disposing of instances", Justification = "An unresponsive cleanup task retains its dependent resources and operation lease until the task completes.")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Shutdown must be bounded and must retain resources while an incomplete cleanup action can still use them.")]
    private async Task<RuntimeShutdownResult> DisposeCoreAsync()
    {
        RuntimeSnapshot initialSnapshot = Snapshot;
        bool usingServiceCore = _usingServiceCore;
        bool managerOwnsLocalProcess = !usingServiceCore
            && _processManager.State is (CoreState.Starting or CoreState.Running or CoreState.Stopping or CoreState.Restarting);
        LocalCoreProcessIdentity? localCoreIdentity = null;
        Exception? localCoreIdentityFailure = null;
        try
        {
            localCoreIdentity = _localCoreProcessIdentityProvider is null
                ? _processManager.CaptureRunningProcessIdentity()
                : _localCoreProcessIdentityProvider() ?? _processManager.CaptureRunningProcessIdentity();
        }
        catch (Exception exception)
        {
            localCoreIdentityFailure = exception;
        }

        bool hasLocalCoreRecovery = !usingServiceCore
            && (managerOwnsLocalProcess || localCoreIdentity is not null || localCoreIdentityFailure is not null);
        bool hasTunCleanup = usingServiceCore
            && initialSnapshot.Tun is not (TunState.Off or TunState.Unavailable);
        bool hasProxyCleanup = initialSnapshot.SystemProxy is SystemProxyState.On or SystemProxyState.RestoreRequired;
        bool hasCoreCleanup = usingServiceCore
            || hasLocalCoreRecovery
            || initialSnapshot.Core.State is CoreState.Running
                or CoreState.Starting
                or CoreState.Stopping
                or CoreState.Restarting
            || _api is not null;
        RuntimeShutdownResultBuilder resultBuilder = new(
            _paths,
            usingServiceCore,
            hasTunCleanup,
            hasProxyCleanup,
            hasCoreCleanup,
            hasLocalCoreRecovery);
        if (localCoreIdentityFailure is not null)
        {
            resultBuilder.SetLocalCoreRecovery(new ShutdownCleanupStepResult(
                "本地核心退出恢复记录",
                ShutdownCleanupStatus.Failed,
                ErrorSanitizer.Sanitize(localCoreIdentityFailure)));
        }
        else if (hasLocalCoreRecovery && localCoreIdentity is null)
        {
            resultBuilder.SetLocalCoreRecovery(new ShutdownCleanupStepResult(
                "本地核心退出恢复记录",
                ShutdownCleanupStatus.Failed,
                "无法读取本地核心进程身份，未能创建持久退出恢复记录。"));
        }

        List<Exception> cleanupFailures = [];
        CancellationTokenSource shutdownDeadline = new(_disposeCleanupTimeout);
        TimeSpan preNetworkBudget = TimeSpan.FromTicks(Math.Max(
            1,
            Math.Min(_disposeCleanupTimeout.Ticks / 3, TimeSpan.FromSeconds(5).Ticks)));
        CancellationTokenSource preNetworkDeadline = new(preNetworkBudget);
        bool shutdownDeadlineTransferred = false;
        bool preNetworkDeadlineTransferred = false;
        bool cleanupLeaseTransferred = false;
        OperationGate.Lease? cleanupLease = null;

        List<Task> pendingCleanupOperations = [];

        async Task<bool> RunStepAsync(
            string operationName,
            Func<CancellationToken, Task> operation,
            CancellationToken waitDeadline,
            Action<ShutdownCleanupStepResult>? record = null,
            CancellationToken? operationCancellationToken = null,
            bool continueAfterIncomplete = false)
        {
            CancellationToken operationToken = operationCancellationToken ?? waitDeadline;

            async Task ExecuteAsync(CancellationToken token)
            {
                if (_shutdownStepTestHook is not null)
                {
                    await _shutdownStepTestHook(operationName, token).ConfigureAwait(false);
                }

                await operation(token).ConfigureAwait(false);
            }

            BoundedCleanupStepResult runResult = await BoundedCleanupStepRunner.RunAsync(
                    ExecuteAsync,
                    waitDeadline,
                    operationToken)
                .ConfigureAwait(false);

            bool deadlineExpired = waitDeadline.IsCancellationRequested || operationToken.IsCancellationRequested;
            ShutdownCleanupStatus status;
            string? detail = null;
            if (runResult.IncompleteOperation is not null
                || (deadlineExpired && runResult.Failure is OperationCanceledException))
            {
                status = ShutdownCleanupStatus.TimedOutUnknown;
                detail = $"{operationName} 未在对应退出阶段期限内完成，结果未确认。";
            }
            else if (runResult.Failure is not null)
            {
                status = ShutdownCleanupStatus.Failed;
                detail = $"{operationName} 失败：{ErrorSanitizer.Sanitize(runResult.Failure)}";
            }
            else
            {
                status = ShutdownCleanupStatus.Completed;
            }

            ShutdownCleanupStepResult stepResult = new(operationName, status, detail);
            if (record is null)
            {
                resultBuilder.AddAdditional(stepResult);
            }
            else
            {
                record(stepResult);
            }

            if (runResult.Failure is not null)
            {
                Exception failure = deadlineExpired
                    && runResult.Failure is OperationCanceledException
                    ? new TimeoutException($"{operationName} 达到退出阶段期限。", runResult.Failure)
                    : runResult.Failure;
                RecordCleanupFailure(cleanupFailures, operationName, failure);
            }

            if (runResult.IncompleteOperation is Task pendingOperation)
            {
                pendingCleanupOperations.Add(pendingOperation);
                return continueAfterIncomplete;
            }

            return !deadlineExpired || runResult.Failure is not OperationCanceledException;
        }

        async Task<bool> ObserveCompletedCleanupOperationsAsync()
        {
            bool hasUnsettledOperation = false;
            foreach (Task pendingOperation in pendingCleanupOperations)
            {
                if (!pendingOperation.IsCompleted)
                {
                    hasUnsettledOperation = true;
                    continue;
                }

                try
                {
                    await pendingOperation.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    RecordCleanupFailure(cleanupFailures, "迟到的退出清理操作", exception);
                }
            }

            if (!hasUnsettledOperation)
            {
                pendingCleanupOperations.Clear();
            }

            return !hasUnsettledOperation;
        }

        try
        {
            if (hasLocalCoreRecovery && localCoreIdentity is not null)
            {
                bool journalWriteContinues = await RunStepAsync(
                    "写入本地核心退出恢复记录",
                    async token =>
                    {
                        LocalCoreShutdownJournalResult result = await _localCoreShutdownJournal.SavePendingStopAsync(
                            localCoreIdentity,
                            token).ConfigureAwait(false);
                        if (!result.Succeeded)
                        {
                            throw new IOException(result.Detail ?? "无法写入本地核心退出恢复记录。");
                        }
                    },
                    preNetworkDeadline.Token,
                    resultBuilder.SetLocalCoreRecovery).ConfigureAwait(false);
                if (!journalWriteContinues)
                {
                    return resultBuilder.Build();
                }
            }

            _operationLock.BeginQuiescing();
            Interlocked.Increment(ref _coreLifecycleEpoch);

            if (!await RunStepAsync(
                    "取消运行时工作",
                    _ => _runtimeCts.CancelAsync(),
                    preNetworkDeadline.Token,
                    operationCancellationToken: shutdownDeadline.Token,
                    continueAfterIncomplete: true).ConfigureAwait(false))
            {
                return resultBuilder.Build();
            }

            _endpointSessions.StatusChanged -= _remoteRefresh.HandleSessionStatusChanged;
            _networkSwitchRuntimeController.StatusChanged -= OnNetworkSwitchStatusChanged;
            _processManager.StateChanged -= OnProcessStateChanged;
            _processManager.LogLineReceived -= _logs.OnProcessLogLine;

            if (!await RunStepAsync(
                    "停止远程端点刷新",
                    _ => _remoteRefresh.StopRefreshAsync(),
                    preNetworkDeadline.Token,
                    operationCancellationToken: shutdownDeadline.Token,
                    continueAfterIncomplete: true).ConfigureAwait(false)
                || !await RunStepAsync(
                    "停止订阅调度器",
                    _ => _subscriptionScheduler.DisposeAsync().AsTask(),
                    preNetworkDeadline.Token,
                    operationCancellationToken: shutdownDeadline.Token,
                    continueAfterIncomplete: true).ConfigureAwait(false))
            {
                return resultBuilder.Build();
            }

            OperationGate.Lease cleanupOwnership;
            try
            {
                cleanupOwnership = await _operationLock.AcquireCleanupOwnershipAsync(shutdownDeadline.Token)
                    .ConfigureAwait(false);
                cleanupLease = cleanupOwnership;
                resultBuilder.SetOperationGate(new ShutdownCleanupStepResult(
                    "运行时操作安全点",
                    ShutdownCleanupStatus.Completed));
            }
            catch (OperationCanceledException exception) when (shutdownDeadline.IsCancellationRequested)
            {
                ShutdownCleanupStepResult step = new(
                    "运行时操作安全点",
                    ShutdownCleanupStatus.TimedOutUnknown,
                    "等待已准入操作结束时达到退出总期限；TUN、代理和核心状态均未宣称已清理。");
                resultBuilder.SetOperationGate(step);
                RecordCleanupFailure(
                    cleanupFailures,
                    "等待运行时操作安全点",
                    new TimeoutException("等待已准入操作结束时达到退出清理总期限。", exception));
                return resultBuilder.Build();
            }
            catch (Exception exception)
            {
                resultBuilder.SetOperationGate(new ShutdownCleanupStepResult(
                    "运行时操作安全点",
                    ShutdownCleanupStatus.Failed,
                    ErrorSanitizer.Sanitize(exception)));
                RecordCleanupFailure(cleanupFailures, "等待运行时操作安全点", exception);
                return resultBuilder.Build();
            }

            if (!await RunStepAsync(
                    "等待 TUN 操作安全点",
                    token => _tunOperation.WaitForIdleAsync(_disposeCleanupTimeout, token),
                    shutdownDeadline.Token).ConfigureAwait(false))
            {
                return resultBuilder.Build();
            }

            if (hasTunCleanup)
            {
                bool stepContinues = await RunStepAsync(
                    "关闭 TUN",
                    async token =>
                    {
                        await RequestTunOperationAsync(
                                enabled: false,
                                persistPreference: false,
                                operationLease: cleanupOwnership,
                                cancellationToken: CancellationToken.None)
                            .ConfigureAwait(false);
                    },
                    shutdownDeadline.Token,
                    result =>
                    {
                        if (result.Status == ShutdownCleanupStatus.Completed)
                        {
                            resultBuilder.SetTun(Snapshot.Tun == TunState.Off
                                ? result
                                : result with
                                {
                                    Status = ShutdownCleanupStatus.Failed,
                                    Detail = "服务未确认 TUN 已关闭。"
                                });
                        }
                        else
                        {
                            resultBuilder.SetTun(result);
                        }
                    }).ConfigureAwait(false);
                if (!stepContinues)
                {
                    return resultBuilder.Build();
                }
            }

            bool coreStopWillHandleProxy = hasCoreCleanup
                && (!usingServiceCore || !hasTunCleanup || Snapshot.Tun == TunState.Off);
            if (hasProxyCleanup && !coreStopWillHandleProxy)
            {
                bool stepContinues = await RunStepAsync(
                    "恢复系统代理",
                    async token =>
                    {
                        await SetSystemProxyCoreAsync(
                                false,
                                persistPreference: false,
                                cancellationToken: CancellationToken.None,
                                operationLease: cleanupOwnership)
                            .ConfigureAwait(false);
                    },
                    shutdownDeadline.Token,
                    result =>
                    {
                        if (result.Status == ShutdownCleanupStatus.Completed)
                        {
                            resultBuilder.SetSystemProxy(Snapshot.SystemProxy is SystemProxyState.Off
                                ? result
                                : result with
                                {
                                    Status = ShutdownCleanupStatus.Failed,
                                    Detail = "系统代理仍要求恢复或状态未确认。"
                                });
                        }
                        else
                        {
                            resultBuilder.SetSystemProxy(result);
                        }
                    }).ConfigureAwait(false);
                if (!stepContinues)
                {
                    return resultBuilder.Build();
                }
            }

            if (usingServiceCore && hasTunCleanup && Snapshot.Tun != TunState.Off)
            {
                resultBuilder.SetCore(new ShutdownCleanupStepResult(
                    "停止核心",
                    ShutdownCleanupStatus.TimedOutUnknown,
                    "TUN 已关闭状态未确认，因此没有请求服务停止核心。"));
                return resultBuilder.Build();
            }
            if (hasCoreCleanup)
            {
                bool stepContinues = await RunStepAsync(
                    "停止核心",
                    async token =>
                    {
                        await StopCoreCoreAsync(cleanupOwnership, CancellationToken.None).ConfigureAwait(false);
                    },
                    shutdownDeadline.Token,
                    result =>
                    {
                        if (result.Status == ShutdownCleanupStatus.Completed)
                        {
                            resultBuilder.SetCore(Snapshot.Core.State is CoreState.Stopped or CoreState.Missing
                                ? result
                                : result with
                                {
                                    Status = ShutdownCleanupStatus.Failed,
                                    Detail = "核心停止操作结束，但状态未确认是已停止。"
                                });
                        }
                        else
                        {
                            resultBuilder.SetCore(result);
                        }
                    }).ConfigureAwait(false);
                if (hasProxyCleanup && coreStopWillHandleProxy)
                {
                    resultBuilder.SetSystemProxy(!stepContinues
                        ? new ShutdownCleanupStepResult(
                            "恢复系统代理",
                            ShutdownCleanupStatus.TimedOutUnknown,
                            "核心停止期间的系统代理恢复结果未确认。")
                        : Snapshot.SystemProxy == SystemProxyState.Off
                            ? new ShutdownCleanupStepResult(
                                "恢复系统代理",
                                ShutdownCleanupStatus.Completed)
                            : new ShutdownCleanupStepResult(
                                "恢复系统代理",
                                ShutdownCleanupStatus.Failed,
                                "核心停止后的系统代理仍要求恢复或状态未确认。"));
                }
                if (!stepContinues)
                {
                    return resultBuilder.Build();
                }
            }

            if (hasLocalCoreRecovery
                && resultBuilder.Core.Status == ShutdownCleanupStatus.Completed)
            {
                bool journalClearContinues = await RunStepAsync(
                    "清理本地核心退出恢复记录",
                    token => _localCoreShutdownJournal.ClearPendingStopAsync(token),
                    shutdownDeadline.Token,
                    resultBuilder.SetLocalCoreRecovery).ConfigureAwait(false);
                if (!journalClearContinues)
                {
                    return resultBuilder.Build();
                }
            }

            if (!await ObserveCompletedCleanupOperationsAsync().ConfigureAwait(false))
            {
                resultBuilder.SetRuntimeResources(new ShutdownCleanupStepResult(
                    "释放运行时资源",
                    ShutdownCleanupStatus.TimedOutUnknown,
                    "前置退出工作仍在使用运行时资源；保留资源与操作所有权，等待迟到任务结束。"));
                return resultBuilder.Build();
            }

            cleanupOwnership.Dispose();
            cleanupLease = null;
            SetController(null);

            if (!await RunStepAsync(
                    "释放端点会话",
                    _ => _endpointSessions.DisposeAsync().AsTask(),
                    shutdownDeadline.Token).ConfigureAwait(false)
                || !await RunStepAsync(
                    "释放网络切换运行时",
                    _ => _networkSwitchRuntimeController.DisposeAsync().AsTask(),
                    shutdownDeadline.Token).ConfigureAwait(false)
                || !await RunStepAsync(
                    "停止日志流",
                    _ => _logs.StopLogStreamAsync(),
                    shutdownDeadline.Token).ConfigureAwait(false)
                || !await RunStepAsync(
                    "等待数据刷新",
                    token => AwaitTaskBoundedAsync(_dataRefreshTask, token),
                    shutdownDeadline.Token).ConfigureAwait(false)
                || !await RunStepAsync(
                    "等待轮询",
                    token => AwaitTaskBoundedAsync(_pollingTask, token),
                    shutdownDeadline.Token).ConfigureAwait(false)
                || !await RunStepAsync(
                    "等待系统代理恢复",
                    _ => AwaitQueuedProxyRecoveryAsync(),
                    shutdownDeadline.Token).ConfigureAwait(false)
                || !await RunStepAsync(
                    "释放核心进程管理器",
                    _ => _processManager.DisposeAsync().AsTask(),
                    shutdownDeadline.Token).ConfigureAwait(false)
                || !await RunStepAsync(
                    "释放配置切换协调器",
                    _ => _configurationSwitchCoordinator.DisposeAsync().AsTask(),
                    shutdownDeadline.Token).ConfigureAwait(false)
                || !await RunStepAsync(
                    "停止发布 worker",
                    _ => _throttledPublisher.DisposeAsync().AsTask(),
                    shutdownDeadline.Token).ConfigureAwait(false))
            {
                resultBuilder.SetRuntimeResources(new ShutdownCleanupStepResult(
                    "释放运行时资源",
                    ShutdownCleanupStatus.TimedOutUnknown,
                    "非网络运行时清理达到总期限；未释放仍可能被使用的资源。"));
                return resultBuilder.Build();
            }

            try
            {
                _logs.Dispose();
                _remoteRefresh.Dispose();
                _httpClient.Dispose();
                _subscriptionOperationLock.Dispose();
                _dataRefreshLock.Dispose();
                _operationLock.Dispose();
                _runtimeCts.Dispose();
                resultBuilder.SetRuntimeResources(new ShutdownCleanupStepResult(
                    "释放运行时资源",
                    ShutdownCleanupStatus.Completed));
            }
            catch (Exception exception)
            {
                resultBuilder.SetRuntimeResources(new ShutdownCleanupStepResult(
                    "释放运行时资源",
                    ShutdownCleanupStatus.Failed,
                    ErrorSanitizer.Sanitize(exception)));
                RecordCleanupFailure(cleanupFailures, "释放运行时资源", exception);
            }

            if (cleanupFailures.Count > 0)
            {
                string message = $"ClashTray 退出清理有 {cleanupFailures.Count} 项失败或超时。";
                _stateStore.Update(snapshot => snapshot with
                {
                    ErrorMessage = message,
                    Logs = _logs.Snapshot()
                });
                try
                {
                    Publish();
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }

            return resultBuilder.Build();
        }
        finally
        {
            if (pendingCleanupOperations.Count > 0)
            {
                cleanupLeaseTransferred = cleanupLease is not null;
                shutdownDeadlineTransferred = true;
                preNetworkDeadlineTransferred = true;
                _ = CompleteAbandonedCleanupAsync(
                    Task.WhenAll(pendingCleanupOperations),
                    cleanupLease,
                    shutdownDeadline,
                    preNetworkDeadline);
            }

            if (!cleanupLeaseTransferred)
            {
                cleanupLease?.Dispose();
            }

            if (!shutdownDeadlineTransferred)
            {
                shutdownDeadline.Dispose();
            }

            if (!preNetworkDeadlineTransferred)
            {
                preNetworkDeadline.Dispose();
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A timed-out cleanup task must be observed before its operation gate and deadline tokens are released.")]
    private static async Task CompleteAbandonedCleanupAsync(
        Task pendingOperation,
        OperationGate.Lease? cleanupLease,
        CancellationTokenSource shutdownDeadline,
        CancellationTokenSource preNetworkDeadline)
    {
        try
        {
            await pendingOperation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The initiating shutdown already recorded the timeout. This continuation
            // observes completion and retains ownership until the operation settles.
        }
        finally
        {
            cleanupLease?.Dispose();
            shutdownDeadline.Dispose();
            preNetworkDeadline.Dispose();
        }
    }
    private void RecordCleanupFailure(
        List<Exception> failures,
        string operationName,
        Exception exception)
    {
        Exception sanitized = new InvalidOperationException(
            $"{operationName}：{ErrorSanitizer.Sanitize(exception)}",
            exception);
        failures.Add(sanitized);
        _logs.AddApplicationLog(new LogEntry(
            DateTimeOffset.UtcNow,
            "ClashTray",
            "error",
            sanitized.Message));
    }

    private static async Task AwaitTaskBoundedAsync(Task? task, CancellationToken cancellationToken)
    {
        if (task is not null)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ThrowIfRuntimeQuiescing()
    {
        if (_operationLock.IsQuiescing)
        {
            throw new RuntimeQuiescingException();
        }
    }
}
