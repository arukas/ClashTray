using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashTray.App;

public partial class App : Application, IAsyncDisposable
{
    private readonly SingleInstanceCoordinator _instanceCoordinator;
    private DispatcherQueue? _dispatcherQueue;
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIcon;
    private readonly ClashTrayRuntime _runtime = new();
    private bool _disposed;

    internal App(SingleInstanceCoordinator instanceCoordinator)
    {
        _instanceCoordinator = instanceCoordinator;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _instanceCoordinator.ActivationRequested += OnActivationRequested;
        _runtime.SnapshotChanged += OnRuntimeSnapshotChanged;

        _mainWindow = new MainWindow(this);
        _mainWindow.Activate();
        _mainWindow.HidePanel();
        _trayIcon = new TrayIconService(_mainWindow, OnTrayInteraction, _mainWindow.HandleDeactivation);
        _trayIcon.MenuItemSelected += OnMenuItemSelected;
        _trayIcon.Install();
        _mainWindow.Initialize(_trayIcon, _runtime);
        _ = InitializeRuntimeAsync();
    }

    public async void RequestQuit()
    {
        _mainWindow?.AllowClose();
        await DisposeAsync();
        _mainWindow?.Close();
        Environment.Exit(0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon?.Dispose();
        await _runtime.DisposeAsync();
        _instanceCoordinator.Dispose();
        GC.SuppressFinalize(this);
    }

    internal async Task ToggleCoreAsync()
    {
        try
        {
            if (_runtime.Snapshot.Core.State == CoreState.Running)
            {
                await _runtime.RestartCoreAsync();
            }
            else
            {
                await _runtime.StartCoreAsync();
            }
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowError(exception.Message);
        }
    }

    private async Task StopCoreAsync()
    {
        try
        {
            await _runtime.StopCoreAsync();
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowError(exception.Message);
        }
    }

    internal async Task ToggleSystemProxyAsync()
    {
        var currentState = _runtime.Snapshot.SystemProxy;
        var enabled = currentState is not (SystemProxyState.On or SystemProxyState.RestoreRequired);
        try
        {
            await _runtime.SetSystemProxyAsync(enabled);
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowError(exception.Message);
        }
    }

    internal async Task ToggleTunAsync()
    {
        try
        {
            await _runtime.SetTunAsync(_runtime.Snapshot.Tun is not TunState.On);
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowError(exception.Message);
        }
    }

    internal async Task SetModeAsync(ProxyMode mode)
    {
        try
        {
            await _runtime.SetModeAsync(mode);
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowError(exception.Message);
        }
    }

    internal async Task ImportLocalConfigurationAsync()
    {
        await _mainWindow!.ImportLocalConfigurationAsync();
    }

    internal async Task ImportSubscriptionAsync()
    {
        await _mainWindow!.ImportSubscriptionAsync();
    }

    internal Task SetActiveConfigurationAsync(string id) => _runtime.SetActiveConfigurationAsync(id);

    internal Task DeleteConfigurationAsync(ConfigurationProfile profile) => _runtime.DeleteConfigurationAsync(profile);

    internal Task RefreshSubscriptionAsync(ConfigurationProfile profile) => _runtime.RefreshSubscriptionAsync(profile);

    internal RuntimeSnapshot GetSnapshot() => _runtime.Snapshot;

    private async Task InitializeRuntimeAsync()
    {
        try
        {
            await _runtime.InitializeAsync();
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowError(exception.Message);
        }
    }

    private void OnTrayInteraction(TrayInteraction interaction)
    {
        if (interaction == TrayInteraction.LeftClick)
        {
            _mainWindow?.TogglePanel();
        }
        else
        {
            _trayIcon?.ShowContextMenu(
            [
                (1001, "打开控制面板"),
                (1002, _runtime.Snapshot.Core.State == CoreState.Running ? "重启核心" : "启动核心"),
                (1003, "停止核心"),
                (0, string.Empty),
                (1004, _runtime.Snapshot.SystemProxy == SystemProxyState.On ? "关闭系统代理" : _runtime.Snapshot.SystemProxy == SystemProxyState.RestoreRequired ? "恢复系统代理" : "开启系统代理"),
                (1005, _runtime.Snapshot.Tun == TunState.On ? "关闭 TUN" : "开启 TUN"),
                (0, string.Empty),
                (1006, "规则模式"),
                (1007, "全局模式"),
                (1008, "直连模式"),
                (0, string.Empty),
                (1009, "退出 ClashTray")
            ]);
        }
    }

    private void OnMenuItemSelected(uint id)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            switch (id)
            {
                case 1001:
                    _mainWindow?.ShowPanel();
                    break;
                case 1002:
                    _ = ToggleCoreAsync();
                    break;
                case 1003:
                    _ = StopCoreAsync();
                    break;
                case 1004:
                    _ = ToggleSystemProxyAsync();
                    break;
                case 1005:
                    _ = ToggleTunAsync();
                    break;
                case 1006:
                    _ = SetModeAsync(ProxyMode.Rule);
                    break;
                case 1007:
                    _ = SetModeAsync(ProxyMode.Global);
                    break;
                case 1008:
                    _ = SetModeAsync(ProxyMode.Direct);
                    break;
                case 1009:
                    RequestQuit();
                    break;
            }
        });
    }

    private void OnActivationRequested()
    {
        _dispatcherQueue?.TryEnqueue(() => _mainWindow?.ShowPanel());
    }

    private void OnRuntimeSnapshotChanged(object? sender, RuntimeSnapshot snapshot)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            _mainWindow?.UpdateSnapshot(snapshot);
            UpdateTrayState(snapshot);
        });
    }

    private void UpdateTrayState(RuntimeSnapshot snapshot)
    {
        var state = snapshot.Tun is TunState.On
            ? TrayState.Tun
            : snapshot.SystemProxy is SystemProxyState.On
                ? TrayState.SystemProxy
                : snapshot.Core.State is CoreState.Running
                    ? TrayState.Running
                    : snapshot.Core.State is CoreState.Failed
                        ? TrayState.Error
                    : TrayState.Stopped;
        _trayIcon?.SetState(state);
    }
}
