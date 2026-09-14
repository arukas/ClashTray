using System.Diagnostics.CodeAnalysis;
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
    private readonly ClashTrayRuntime _runtime;
    private readonly string? _smokeDirectory;
    private bool _disposed;

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The runtime takes ownership of the network context source and disposes it with the application lifecycle.")]
    internal App(SingleInstanceCoordinator instanceCoordinator, string? smokeDirectory = null)
    {
        _instanceCoordinator = instanceCoordinator;
        _smokeDirectory = smokeDirectory;
        AppPaths? runtimePaths = smokeDirectory is null
            ? null
            : new AppPaths(
                Path.Combine(smokeDirectory, "user"),
                Path.Combine(smokeDirectory, "service"));
        INetworkContextSource? networkContextSource = smokeDirectory is null
            ? new WindowsNetworkContextSource()
            : null;
        _runtime = new ClashTrayRuntime(runtimePaths, networkContextSource);
        UnhandledException += (_, e) =>
        {
            string directory = _smokeDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashTray", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup-error.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n");
        };

        // Set the Windows App SDK language override after the Application object
        // exists but before InitializeComponent loads MRT resources.
        LocalizationService.ApplyStartupLanguage(
            smokeDirectory is null
                ? new AppPaths()
                : new AppPaths(
                    Path.Combine(smokeDirectory, "user"),
                    Path.Combine(smokeDirectory, "service")),
            forceChineseForDiagnostics: smokeDirectory is not null);

        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _instanceCoordinator.ActivationRequested += OnActivationRequested;
        _runtime.AppSnapshotChanged += OnAppSnapshotChanged;

        _mainWindow = new MainWindow(this);
        _mainWindow.Activate();
        _mainWindow.HidePanel();
        _trayIcon = new TrayIconService(_mainWindow, OnTrayInteraction, _mainWindow.HandleDeactivation);
        _trayIcon.MenuItemSelected += OnMenuItemSelected;
        if (_smokeDirectory is null)
        {
            _trayIcon.Install();
        }

        _mainWindow.Initialize(_trayIcon, _runtime);
        if (_smokeDirectory is null)
        {
            _ = InitializeRuntimeAsync();
        }
        else
        {
            _ = RunSmokeTestAsync();
        }
    }

    private async Task RunSmokeTestAsync()
    {
        try
        {
            await _mainWindow!.CaptureSmokeTestAsync(_smokeDirectory!);
            RequestQuit();
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(Path.Combine(_smokeDirectory!, "failure.log"), exception.ToString());
            Environment.Exit(1);
        }
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
        if (_runtime.Snapshot.Core.State is CoreState.Validating
            or CoreState.Starting
            or CoreState.Stopping
            or CoreState.Restarting)
        {
            return;
        }

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
        SystemProxyState currentState = _runtime.Snapshot.SystemProxy;
        if (currentState is SystemProxyState.Enabling or SystemProxyState.Disabling)
        {
            return;
        }

        bool enabled = currentState is not (SystemProxyState.On or SystemProxyState.RestoreRequired);
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
        if (_runtime.Snapshot.Tun is TunState.Enabling or TunState.Disabling)
        {
            return;
        }

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
                (1001, LocalizationService.Get("MenuOpenPanel")),
                (1002, _runtime.Snapshot.Core.State == CoreState.Running ? LocalizationService.Get("MenuRestartCore") : LocalizationService.Get("MenuStartCore")),
                (1003, LocalizationService.Get("MenuStopCore")),
                (0, string.Empty),
                (1004, _runtime.Snapshot.SystemProxy == SystemProxyState.On ? LocalizationService.Get("MenuDisableSystemProxy") : _runtime.Snapshot.SystemProxy == SystemProxyState.RestoreRequired ? LocalizationService.Get("MenuRestoreSystemProxy") : LocalizationService.Get("MenuEnableSystemProxy")),
                (1005, _runtime.Snapshot.Tun == TunState.On ? LocalizationService.Get("MenuDisableTun") : LocalizationService.Get("MenuEnableTun")),
                (0, string.Empty),
                (1006, LocalizationService.Get("MenuModeRule")),
                (1007, LocalizationService.Get("MenuModeGlobal")),
                (1008, LocalizationService.Get("MenuModeDirect")),
                (0, string.Empty),
                (1009, LocalizationService.Get("MenuQuit"))
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
                    _ = SetLocalModeFromTrayAsync(ProxyMode.Rule);
                    break;
                 case 1007:
                    _ = SetLocalModeFromTrayAsync(ProxyMode.Global);
                    break;
                 case 1008:
                    _ = SetLocalModeFromTrayAsync(ProxyMode.Direct);
                    break;
                case 1009:
                    RequestQuit();
                    break;
            }
        });
    }

    private async Task SetLocalModeFromTrayAsync(ProxyMode mode)
    {
        try
        {
            await _runtime.SetLocalModeAsync(mode);
        }
        catch (Exception exception)
        {
            _mainWindow?.ShowError(exception.Message);
        }
    }

    private void OnActivationRequested()
    {
        _dispatcherQueue?.TryEnqueue(() => _mainWindow?.ShowPanel());
    }

    private void OnAppSnapshotChanged(object? sender, AppSnapshot snapshot)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            RuntimeSnapshot runtimeSnapshot = RuntimeSnapshotAdapter.ToRuntimeSnapshot(snapshot);
            UpdateTrayState(runtimeSnapshot);
            _mainWindow?.UpdateAppSnapshot(snapshot);
        });
    }

    private void UpdateTrayState(RuntimeSnapshot snapshot)
    {
        TrayState state = snapshot.Core.State is CoreState.Failed
            ? TrayState.Error
            : snapshot.Core.State is CoreState.Validating
                or CoreState.Starting
                or CoreState.Stopping
                or CoreState.Restarting
                ? TrayState.Connecting
                : snapshot.Tun is TunState.On
                    ? TrayState.Tun
                    : snapshot.SystemProxy is SystemProxyState.On
                        ? TrayState.SystemProxy
                        : snapshot.Core.State is CoreState.Running
                            ? TrayState.Running
                            : snapshot.Core.State is CoreState.Stopped
                                ? TrayState.Paused
                                : TrayState.Stopped;
        _trayIcon?.SetState(state);
    }
}
