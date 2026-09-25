using ClashTray.Contracts;

namespace ClashTray.Core;

public sealed partial class ClashTrayRuntime
{

    public Task<EndpointCatalogLoadResult> LoadEndpointCatalogAsync(
        CancellationToken cancellationToken = default) =>
        _endpointCatalog.LoadAsync(cancellationToken);

    public async Task<EndpointSession?> SelectEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId.Value);
        ThrowIfRuntimeQuiescing();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeCts.Token);
        CancellationToken token = linked.Token;
        EndpointSession? session = await _endpointCatalog.SelectAsync(endpointId, token)
            .ConfigureAwait(false);
        if (session is not null)
        {
            await _remoteRefresh.WaitForRefreshAsync(session, token).ConfigureAwait(false);
        }

        return session;
    }

    public async Task<EndpointHandshakeResult> TestRemoteEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId.Value);
        ThrowIfRuntimeQuiescing();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _runtimeCts.Token);
        return await _endpointCatalog.TestAsync(endpointId, linked.Token)
            .ConfigureAwait(false);
    }

    public async Task DisconnectEndpointAsync()
    {
        ThrowIfRuntimeQuiescing();
        await _endpointCatalog.DisconnectAsync().ConfigureAwait(false);
    }

    public Task<EndpointCatalogLoadResult> SaveRemoteEndpointAsync(
        EndpointRecord endpoint,
        CancellationToken cancellationToken = default) =>
        _endpointCatalog.SaveAsync(endpoint, cancellationToken);

    public Task<EndpointCatalogLoadResult> ProvisionRemoteEndpointAsync(
        EndpointDescriptor descriptor,
        string? secret,
        ReadOnlyMemory<byte>? customCaCertificate,
        DateTimeOffset? insecureHttpAcknowledgedAtUtc,
        CancellationToken cancellationToken = default) =>
        _endpointCatalog.ProvisionAsync(
            descriptor,
            secret,
            customCaCertificate,
            insecureHttpAcknowledgedAtUtc,
            cancellationToken);

    public Task<EndpointCatalogLoadResult> UpdateRemoteEndpointAsync(
        EndpointId endpointId,
        EndpointDescriptor descriptor,
        string? secret,
        ReadOnlyMemory<byte>? customCaCertificate,
        DateTimeOffset? insecureHttpAcknowledgedAtUtc,
        CancellationToken cancellationToken = default) =>
        _endpointCatalog.UpdateAsync(
            endpointId,
            descriptor,
            secret,
            customCaCertificate,
            insecureHttpAcknowledgedAtUtc,
            cancellationToken);

    public Task<EndpointRemovalResult> RemoveRemoteEndpointAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken = default) =>
        _endpointCatalog.RemoveAsync(endpointId, cancellationToken);

    public Task CloseConnectionAsync(string id, CancellationToken cancellationToken = default) =>
        CloseConnectionAsync(id, CaptureCurrentEndpointCommandTarget(), cancellationToken);

    public async Task CloseConnectionAsync(
        string id,
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default)
    {
        EndpointCommandTarget commandTarget = expectedTarget ?? CaptureCurrentEndpointCommandTarget();
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.CloseConnection,
                "关闭连接期间核心会话已切换，请重试。",
                "远程连接关闭结果无法确认，请重试。",
                (api, _, token) => api.CloseConnectionAsync(id, token),
                (session, token) => session.Api.CloseConnectionAsync(id, token),
                commandTarget,
                routeToRemote: true,
                includeRulesAndProviders: true,
                refreshScope: MutationRefreshScope.Full,
                cancellationToken: cancellationToken);
        }
    }

    public Task CloseAllConnectionsAsync(CancellationToken cancellationToken = default) =>
        CloseAllConnectionsAsync(CaptureCurrentEndpointCommandTarget(), cancellationToken);

    public async Task CloseAllConnectionsAsync(
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default)
    {
        EndpointCommandTarget commandTarget = expectedTarget ?? CaptureCurrentEndpointCommandTarget();
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.CloseConnection,
                "关闭连接期间核心会话已切换，请重试。",
                "远程连接清理结果无法确认，请重试。",
                (api, _, token) => api.CloseAllConnectionsAsync(token),
                (session, token) => session.Api.CloseAllConnectionsAsync(token),
                commandTarget,
                routeToRemote: true,
                includeRulesAndProviders: true,
                refreshScope: MutationRefreshScope.Full,
                cancellationToken: cancellationToken);
        }
    }

    public Task RefreshProviderAsync(
        string name,
        bool rules,
        CancellationToken cancellationToken = default) =>
        RefreshProviderAsync(name, rules, CaptureCurrentEndpointCommandTarget(), cancellationToken);

    public async Task RefreshProviderAsync(
        string name,
        bool rules,
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default)
    {
        EndpointCommandTarget commandTarget = expectedTarget ?? CaptureCurrentEndpointCommandTarget();
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.RefreshProvider,
                "刷新 Provider 期间核心会话已切换，请重试。",
                "远程 Provider 刷新结果无法确认，请重试。",
                (api, _, token) => rules
                    ? api.RefreshRuleProviderAsync(name, token)
                    : api.RefreshProviderAsync(name, token),
                (session, token) => rules
                    ? session.Api.RefreshRuleProviderAsync(name, token)
                    : session.Api.RefreshProviderAsync(name, token),
                commandTarget,
                routeToRemote: true,
                includeRulesAndProviders: true,
                refreshScope: MutationRefreshScope.Full,
                cancellationToken: cancellationToken);
        }
    }

    public void ClearLogs() => _logs.ClearLogs();

    public Task ClearFakeIpCacheAsync(CancellationToken cancellationToken = default) =>
        ClearFakeIpCacheAsync(CaptureCurrentEndpointCommandTarget(), cancellationToken);

    public async Task ClearFakeIpCacheAsync(
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default)
    {
        EndpointCommandTarget commandTarget = expectedTarget ?? CaptureCurrentEndpointCommandTarget();
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.ClearCache,
                "清理 FakeIP 缓存期间核心会话已切换，请重试。",
                "远程 FakeIP 缓存清理结果无法确认，请重试。",
                (api, _, token) => api.ClearFakeIpCacheAsync(token),
                (session, token) => session.Api.ClearFakeIpCacheAsync(token),
                commandTarget,
                routeToRemote: true,
                includeRulesAndProviders: true,
                refreshScope: MutationRefreshScope.Full,
                cancellationToken: cancellationToken);
        }
    }

    public Task ClearDnsCacheAsync(CancellationToken cancellationToken = default) =>
        ClearDnsCacheAsync(CaptureCurrentEndpointCommandTarget(), cancellationToken);

    public async Task ClearDnsCacheAsync(
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default)
    {
        EndpointCommandTarget commandTarget = expectedTarget ?? CaptureCurrentEndpointCommandTarget();
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.ClearCache,
                "清理 DNS 缓存期间核心会话已切换，请重试。",
                "远程 DNS 缓存清理结果无法确认，请重试。",
                (api, _, token) => api.ClearDnsCacheAsync(token),
                (session, token) => session.Api.ClearDnsCacheAsync(token),
                commandTarget,
                routeToRemote: true,
                includeRulesAndProviders: true,
                refreshScope: MutationRefreshScope.Full,
                cancellationToken: cancellationToken);
        }
    }

    public Task UpdateGeoAsync(CancellationToken cancellationToken = default) =>
        UpdateGeoAsync(CaptureCurrentEndpointCommandTarget(), cancellationToken);

    public async Task UpdateGeoAsync(
        EndpointCommandTarget? expectedTarget,
        CancellationToken cancellationToken = default)
    {
        EndpointCommandTarget commandTarget = expectedTarget ?? CaptureCurrentEndpointCommandTarget();
        using (OperationGate.Lease operationLease = await _operationLock.AcquireSharedAsync(cancellationToken))
        {
            await ExecuteControllerMutationAndRefreshAsync(
                EndpointCommand.UpdateGeo,
                "更新 Geo 数据库期间核心会话已切换，请重试。",
                "远程 Geo 数据库更新结果无法确认，请重试。",
                (api, _, token) => api.UpdateGeoAsync(token),
                (session, token) => session.Api.UpdateGeoAsync(token),
                commandTarget,
                routeToRemote: true,
                includeRulesAndProviders: true,
                refreshScope: MutationRefreshScope.Full,
                cancellationToken: cancellationToken);
        }
    }
    public async Task<string> InstallCoreUpdateAsync(CoreUpdateManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        CoreUpdater.ValidateManifest(manifest);
        using (OperationGate.Lease operationLease = await _operationLock.AcquireAsync(cancellationToken))
        {
            bool wasRunning = Snapshot.Core.State == CoreState.Running;
            if (wasRunning)
            {
                await StopCoreCoreAsync(operationLease, cancellationToken);
            }

            bool installed = false;
            bool rolledBack = false;
            try
            {
                ServiceCoreUpdatePayload payload = new(
                    manifest.Version,
                    manifest.DownloadUri,
                    manifest.Sha256);
                ServiceResponse response = await _localDevice.InstallCoreAsync(payload, cancellationToken);
                if (!response.Succeeded)
                {
                    throw new InvalidOperationException(response.Error ?? "ClashTray 服务无法安装 Mihomo 核心。");
                }

                installed = true;
                string path = response.Payload ?? _coreDiscovery.ManagedExecutablePath;
                _stateStore.Update(snapshot => snapshot with { Core = snapshot.Core with { Version = FindCoreVersion(), ErrorMessage = null }, ErrorMessage = null });
                Publish();
                if (wasRunning)
                {
                    await StartCoreCoreAsync(operationLease, cancellationToken);
                    if (!IsCoreHealthy())
                    {
                        await StopCoreCoreAsync(operationLease, CancellationToken.None);
                        await RollbackCoreUpdateAsync(CancellationToken.None);
                        rolledBack = true;
                        _stateStore.Update(snapshot => snapshot with
                        {
                            Core = snapshot.Core with
                            {
                                Version = FindCoreVersion(),
                                ErrorMessage = "新核心健康检查失败，已自动回滚。"
                            },
                            ErrorMessage = "新核心健康检查失败，已自动回滚。"
                        });
                        Publish();
                        await StartCoreCoreAsync(operationLease, CancellationToken.None);
                        if (!IsCoreHealthy())
                        {
                            throw new InvalidOperationException("核心更新失败，且回滚后的旧核心也未能恢复。");
                        }

                        _logs.AddApplicationLog(new LogEntry(
                            DateTimeOffset.UtcNow,
                            "ClashTray",
                            "warning",
                            "新核心健康检查失败，已自动回滚并恢复旧核心。"));
                        _stateStore.Update(snapshot => snapshot with { Logs = _logs.Snapshot() });
                        Publish();
                        throw new InvalidOperationException("核心更新健康检查失败，已自动回滚并恢复旧核心。");
                    }
                }

                return path;
            }
            catch
            {
                if (installed && !rolledBack)
                {
                    if (Snapshot.Core.State == CoreState.Running || _api is not null || _usingServiceCore)
                    {
                        await StopCoreCoreAsync(operationLease, CancellationToken.None);
                    }

                    await RollbackCoreUpdateAsync(CancellationToken.None);
                    _stateStore.Update(snapshot => snapshot with
                    {
                        Core = snapshot.Core with { Version = FindCoreVersion() }
                    });
                    Publish();
                }

                if (wasRunning && !IsCoreHealthy())
                {
                    await StartCoreCoreAsync(operationLease, CancellationToken.None);
                }

                throw;
            }
        }
    }

    private async Task RollbackCoreUpdateAsync(CancellationToken cancellationToken)
    {
        ServiceResponse response = await _localDevice.RollbackCoreAsync(cancellationToken);
        if (!response.Succeeded)
        {
            throw new InvalidOperationException(response.Error ?? "ClashTray 服务无法回滚 Mihomo 核心。");
        }
    }

    private MihomoApiClient CreateApiClient()
    {
        if (_controllerApiFactory is not null)
        {
            return _controllerApiFactory();
        }

        Uri controllerUri = new Uri($"http://127.0.0.1:{_settings.ControllerPort}/");
        return new MihomoApiClient(_httpClient, controllerUri, string.Empty);
    }

    private Task<EndpointRecord?> ResolveEndpointRecordAsync(
        EndpointId endpointId,
        CancellationToken cancellationToken) =>
        _endpointCatalog.ResolveRecordAsync(endpointId, cancellationToken);

    private EndpointCommandTarget CaptureCurrentEndpointCommandTarget()
    {
        EndpointSessionStatusEventArgs status = _endpointSessions.Status;
        return status.Endpoint.Kind == EndpointKind.Remote
            ? new EndpointCommandTarget(status.Endpoint.Id, status.Generation)
            : CaptureLocalEndpointCommandTarget();
    }

    private EndpointCommandTarget CaptureLocalEndpointCommandTarget() =>
        new(EndpointId.Local, ControllerGeneration);

private CoreBindingEpochs CaptureCoreBindingEpochs() => new(
        Volatile.Read(ref _coreLifecycleEpoch),
        _processManager.Generation,
        ControllerGeneration);

    private bool IsCurrentCoreBinding(
        MihomoApiClient api,
        long controllerGeneration,
        long lifecycleEpoch,
        long processGeneration) =>
        !_runtimeCts.IsCancellationRequested
        && lifecycleEpoch == Volatile.Read(ref _coreLifecycleEpoch)
        && processGeneration == _processManager.Generation
        && _controllerSessions.IsCurrent(api, controllerGeneration);

    private async Task ExecuteControllerMutationAndRefreshAsync(
        EndpointCommand command,
        string staleSessionMessage,
        string remoteRefreshFailureMessage,
        Func<MihomoApiClient, long, CancellationToken, Task> localOperation,
        Func<EndpointSession, CancellationToken, Task> remoteOperation,
        EndpointCommandTarget expectedTarget,
        bool routeToRemote,
        bool includeRulesAndProviders,
        MutationRefreshScope refreshScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localOperation);
        ArgumentNullException.ThrowIfNull(remoteOperation);

if (expectedTarget.EndpointId == EndpointId.Local)
        {
            if (expectedTarget.Generation != ControllerGeneration
                || (routeToRemote && _endpointSessions.Status.Endpoint.Kind == EndpointKind.Remote))
            {
                throw new InvalidOperationException(staleSessionMessage);
            }

}
        else
        {
            EndpointSessionStatusEventArgs activeStatus = _endpointSessions.Status;
            if (!routeToRemote
                || activeStatus.Endpoint.Kind != EndpointKind.Remote
                || activeStatus.Endpoint.Id != expectedTarget.EndpointId
                || activeStatus.Generation != expectedTarget.Generation)
            {
                throw new InvalidOperationException(staleSessionMessage);
            }

            EndpointSession? remoteSession = _remoteRefresh.CaptureActiveRemoteSession(command, staleSessionMessage);
            EndpointSessionStatusEventArgs remoteStatus = _endpointSessions.Status;
            if (remoteSession is null
                || remoteSession.Endpoint.Id != expectedTarget.EndpointId
                || remoteSession.Generation != expectedTarget.Generation
                || !_remoteRefresh.IsCurrentRemoteSession(remoteSession, remoteStatus))
            {
                throw new InvalidOperationException(staleSessionMessage);
            }

            await remoteOperation(remoteSession, cancellationToken).ConfigureAwait(false);
            if (!_remoteRefresh.IsCurrentRemoteSession(remoteSession, remoteStatus))
            {
                throw new InvalidOperationException(staleSessionMessage);
            }

            if (!await _remoteRefresh.RefreshMutationSnapshotAsync(
                    remoteSession,
                    remoteStatus,
                    refreshScope,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new InvalidOperationException(remoteRefreshFailureMessage);
            }

            return;
        }

        if (_api is null)
        {
            // _api is derived from the controller session registry, so a missing
            // client means there is no session: Capture() throws and the mutation
            // must never complete silently as if it had succeeded.
            _ = CaptureControllerSession();
            return;
        }

        (MihomoApiClient api, long generation) = CaptureControllerSession();
        if (expectedTarget.EndpointId != EndpointId.Local || expectedTarget.Generation != generation)
        {
            throw new InvalidOperationException(staleSessionMessage);
        }

        EnsureControllerCommand(api, generation, command, staleSessionMessage);
        await localOperation(api, generation, cancellationToken).ConfigureAwait(false);
        EnsureControllerSession(api, generation, staleSessionMessage);
        if (refreshScope == MutationRefreshScope.Mode)
        {
            await _dataRefresh.RefreshModeSnapshotAsync(api, generation, cancellationToken).ConfigureAwait(false);
        }
        else if (refreshScope == MutationRefreshScope.ProxySelection)
        {
            await _dataRefresh.RefreshProxySelectionSnapshotAsync(api, generation, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RefreshFromApiAsync(
                    cancellationToken,
                    includeRulesAndProviders)
                .ConfigureAwait(false);
        }
        EnsureControllerSession(api, generation, staleSessionMessage);
    }

    private void SetController(MihomoApiClient? api)
    {
        if (api is null)
        {
            _controllerSessions.Detach();
            return;
        }

        _controllerSessions.Attach(
            api,
            ControllerEndpointFactory.CreateLocal(_settings.ControllerPort),
            EndpointCapabilityDefaults.Local);
    }

    private (MihomoApiClient Api, long Generation) CaptureControllerSession() =>
        _controllerGuard.Capture();

    private void EnsureControllerSession(
        MihomoApiClient api,
        long generation,
        string message) =>
        _controllerGuard.EnsureSession(api, generation, message);

    private void EnsureControllerCommand(
        MihomoApiClient api,
        long generation,
        EndpointCommand command,
        string staleSessionMessage) =>
        _controllerGuard.EnsureCommand(api, generation, command, staleSessionMessage);

    private string? FindCoreVersion()
    {
        string? path = _coreDiscovery.FindExecutable();
        if (path is null)
        {
            return null;
        }

        string? version = CoreDiscovery.GetVersion(path);
        return string.IsNullOrWhiteSpace(version)
            ? ManagedCoreVerifier.TryReadInstalledVersion(_paths)
            : version;
    }
}
