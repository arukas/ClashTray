using ClashTray.Contracts;
using ClashTray.Core;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using System.Reflection;

namespace ClashTray.App;

public sealed partial class MainWindow : Window
{
    private enum PanelPage
    {
        Proxy,
        Rules,
        Connections,
        Logs,
        Settings
    }

    private readonly App _app;
    private TrayIconService? _trayIcon;
    private ClashTrayRuntime? _runtime;
    private ProxyPage? _proxyPage;
    private RulesPage? _rulesPage;
    private ConnectionsPage? _connectionsPage;
    private LogsPage? _logsPage;
    private SettingsPage? _settingsPage;
    private IntPtr _windowHandle;
    private bool _isPinned;
    private bool _isVisible;
    private bool _allowClose;
    private string? _selectedConfigurationId;
    private AppWindow? _appWindow;
    private bool _updatingSnapshot;
    private bool _updatingThemeControls;
    private bool _pageRefreshInProgress;
    private EndpointKind _activeEndpointKind = EndpointKind.Local;
    private readonly Queue<(double Up, double Down)> _trafficHistory = new();
    private DateTime _lastTrafficSample;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        InitializeThemeTracking();
        _windowHandle = WindowNative.GetWindowHandle(this);
    }

    internal void Initialize(TrayIconService trayIcon, ClashTrayRuntime runtime)
    {
        _trayIcon = trayIcon;
        _runtime = runtime;
        _windowHandle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_windowHandle));
        _appWindow.Closing += AppWindow_Closing;
        ApplyFlyoutWindowStyle();
        _proxyPage = new ProxyPage(runtime);
        _rulesPage = new RulesPage(runtime);
        _connectionsPage = new ConnectionsPage(runtime);
        _logsPage = new LogsPage(runtime);
        _settingsPage = new SettingsPage(runtime);
        ProxyPageContent.Content = _proxyPage;
        ApplyTheme(runtime.Settings.Theme);
        NavigateTo(_proxyPage, PanelPage.Proxy);
        UpdateAppSnapshot(runtime.AppSnapshot);
    }

    public void HandleDeactivation()
    {
        if (_isVisible && !_isPinned)
        {
            HidePanel();
        }
    }

    public void TogglePanel()
    {
        if (_isVisible)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    public void ShowPanel()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
        }

        NativeMethods.Rect trayRect = GetTrayRect();
        nint monitor = NativeMethods.MonitorFromRect(ref trayRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        NativeMethods.MonitorInfo info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }

        uint dpi = NativeMethods.GetDpiForMonitor(monitor, 0, out uint monitorDpi, out _) == 0
            ? monitorDpi : NativeMethods.GetDpiForWindow(_windowHandle);
        ScreenBounds bounds = FlyoutPlacement.Calculate(
            new(info.Monitor.Left, info.Monitor.Top, info.Monitor.Width, info.Monitor.Height),
            new(info.Work.Left, info.Work.Top, info.Work.Width, info.Work.Height), dpi == 0 ? 1 : dpi / 96d);
        NativeMethods.SetWindowPos(
            _windowHandle,
            _isPinned ? IntPtr.Zero : new IntPtr(-1),
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_SHOW);
        _isVisible = true;
        NativeMethods.SetForegroundWindow(_windowHandle);
    }

    public void HidePanel()
    {
        ResetLogoClicks();
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_HIDE);
        }

        _isVisible = false;
    }

    public void AllowClose()
    {
        _allowClose = true;
        StopThemeTracking();
    }

    public void UpdateAppSnapshot(AppSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _activeEndpointKind = snapshot.ActiveController.Endpoint.Kind;
        UpdateSnapshot(RuntimeSnapshotAdapter.ToRuntimeSnapshot(
            snapshot,
            _runtime?.Snapshot.NetworkSwitch));
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ApplyTheme(_runtime?.Settings.Theme ?? "system");
        CoreStatus core = snapshot.Core;
        _updatingSnapshot = true;
        bool localController = _activeEndpointKind == EndpointKind.Local;
        bool coreBusy = core.State is CoreState.Validating
            or CoreState.Starting
            or CoreState.Stopping
            or CoreState.Restarting;
        CoreActionButton.IsEnabled = localController && !coreBusy;
        SystemProxySwitch.IsEnabled = localController
            && snapshot.SystemProxy is not (SystemProxyState.Enabling or SystemProxyState.Disabling);
        TunSwitch.IsEnabled = localController
            && snapshot.Tun is not (TunState.Enabling or TunState.Disabling);
        ToolTipService.SetToolTip(
            SystemProxySwitch,
            localController
                ? null
                : LocalizationService.Get("RemoteLocalNetworkActionUnavailable"));
        ToolTipService.SetToolTip(
            TunSwitch,
            localController
                ? null
                : LocalizationService.Get("RemoteLocalNetworkActionUnavailable"));
        bool coreRunning = core.State == CoreState.Running;
        RuleModeButton.IsEnabled = coreRunning;
        GlobalModeButton.IsEnabled = coreRunning;
        DirectModeButton.IsEnabled = coreRunning;
        RuleModeButton.IsChecked = coreRunning && core.Mode == ProxyMode.Rule;
        GlobalModeButton.IsChecked = coreRunning && core.Mode == ProxyMode.Global;
        DirectModeButton.IsChecked = coreRunning && core.Mode == ProxyMode.Direct;
        CoreStateText.Text = core.State switch
        {
            CoreState.Running => LocalizationService.Get("CoreStateRunning"),
            CoreState.Starting => LocalizationService.Get("CoreStateStarting"),
            CoreState.Stopping => LocalizationService.Get("CoreStateStopping"),
            CoreState.Restarting => LocalizationService.Get("CoreStateRestarting"),
            CoreState.Failed => LocalizationService.Get("CoreStateFailed"),
            CoreState.Missing => LocalizationService.Get("CoreStateMissing"),
            _ => LocalizationService.Get("CoreStateStopped")
        };
        CoreActionButton.Content = new FontIcon
        {
            Glyph = core.State == CoreState.Running ? "\uF305" : "\uE768",
            FontSize = 16
        };
        ToolTipService.SetToolTip(
            CoreActionButton,
            !localController
                ? LocalizationService.Get("RemoteCoreActionUnavailable")
                : core.State == CoreState.Running
                    ? LocalizationService.Get("ToolTipRestartCore")
                    : LocalizationService.Get("ToolTipStartCore"));
        StatusDot.Fill = new SolidColorBrush(core.State switch
        {
            CoreState.Running => Colors.Green,
            CoreState.Failed => Colors.Red,
            CoreState.Starting or CoreState.Stopping or CoreState.Restarting or CoreState.Validating => Colors.Orange,
            _ => Colors.Gray
        });
        string appVersion = typeof(MainWindow)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? "0.0.0-dev";
        CoreVersionText.Text = string.IsNullOrWhiteSpace(core.Version)
            ? LocalizationService.Format("CoreVersionIdleFormat", appVersion)
            : LocalizationService.Format("CoreVersionRunningFormat", core.Version, appVersion);
        bool dashboardAvailable = localController && coreRunning && _runtime?.DashboardAvailable == true;
        ControllerEndpointButton.Content = !localController
            ? LocalizationService.Get("ControllerRemoteNotAvailable")
            : coreRunning
                ? $"127.0.0.1:{_runtime?.Settings.ControllerPort ?? 9090}/ui/"
                : LocalizationService.Get("ControllerCoreNotRunning");
        ControllerEndpointButton.IsEnabled = localController && coreRunning;
        ToolTipService.SetToolTip(
            ControllerEndpointButton,
            !localController
                ? LocalizationService.Get("ControllerRemoteNotAvailable")
                : !coreRunning
                ? LocalizationService.Get("ToolTipControllerNeedsCore")
                : dashboardAvailable
                    ? LocalizationService.Get("ToolTipControllerOpenDashboard")
                    : LocalizationService.Get("ToolTipControllerMissingDashboard"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            ControllerEndpointButton,
            !localController
                ? LocalizationService.Get("ControllerRemoteNotAvailable")
                : dashboardAvailable
                    ? LocalizationService.Get("AutomationOpenDashboard")
                    : LocalizationService.Get("AutomationOpenOrCopyController"));
        ConnectionCountText.Text = core.ConnectionCount.ToString(CultureInfo.InvariantCulture);
        TrafficText.Text = core.TrafficAvailable
            ? $"↑ {FormatRate(core.UploadBytesPerSecond)}  ↓ {FormatRate(core.DownloadBytesPerSecond)}"
            : LocalizationService.Get("TrafficUnavailable");
        MemoryText.Text = core.MemoryAvailable ? FormatBytes(core.MemoryBytes) : LocalizationService.Get("MemoryUnavailable");
        SystemProxyStateText.Text = snapshot.SystemProxy switch
        {
            SystemProxyState.On => LocalizationService.Get("SwitchStateOn"),
            SystemProxyState.Enabling => LocalizationService.Get("SwitchStateEnabling"),
            SystemProxyState.Disabling => LocalizationService.Get("SwitchStateDisabling"),
            SystemProxyState.RestoreRequired => LocalizationService.Get("SwitchStateRestoreRequired"),
            SystemProxyState.Failed => LocalizationService.Get("SwitchStateFailed"),
            _ => LocalizationService.Get("SwitchStateOff")
        };
        SystemProxySwitch.IsOn = snapshot.SystemProxy == SystemProxyState.On;
        TunStateText.Text = snapshot.Tun switch
        {
            TunState.On => LocalizationService.Get("SwitchStateOn"),
            TunState.Enabling => LocalizationService.Get("SwitchStateEnabling"),
            TunState.Disabling => LocalizationService.Get("SwitchStateDisabling"),
            TunState.Unavailable => LocalizationService.Get("TunStateUnavailable"),
            TunState.Unknown => LocalizationService.Get("TunStateUnknown"),
            TunState.Failed => LocalizationService.Get("SwitchStateFailed"),
            _ => LocalizationService.Get("SwitchStateOff")
        };
        TunSwitch.IsOn = snapshot.Tun == TunState.On;
        _updatingSnapshot = false;
        ConfigurationText.Text = core.ConfigurationName ?? snapshot.Configurations.FirstOrDefault(c => c.IsActive)?.Name ?? LocalizationService.Get("ConfigImportPrompt");
        ErrorBanner.Message = snapshot.ErrorMessage ?? core.ErrorMessage ?? string.Empty;
        ErrorBanner.IsOpen = !string.IsNullOrEmpty(ErrorBanner.Message);
        DownloadText.Text = FormatRate(core.DownloadBytesPerSecond);
        UploadText.Text = FormatRate(core.UploadBytesPerSecond);
        TotalTrafficText.Text = core.TrafficAvailable
            ? LocalizationService.Format("TotalTrafficFormat", FormatBytes(core.UploadBytes), FormatBytes(core.DownloadBytes))
            : LocalizationService.Get("TotalTrafficUnavailable");
        if (core.TrafficAvailable && DateTime.UtcNow - _lastTrafficSample >= TimeSpan.FromSeconds(1))
        {
            _lastTrafficSample = DateTime.UtcNow;
            _trafficHistory.Enqueue((core.UploadBytesPerSecond, core.DownloadBytesPerSecond));
            while (_trafficHistory.Count > 60)
            {
                _trafficHistory.Dequeue();
            }

            DrawTraffic();
        }

        AppSettings? settings = _runtime?.Settings;
        if (settings is not null)
        {
            HttpEndpointButton.Content = $"HTTP {settings.HttpPort}";
            HttpEndpointButton.Tag = $"127.0.0.1:{settings.HttpPort}";
            MixedEndpointButton.Content = $"Mixed {settings.MixedPort}";
            MixedEndpointButton.Tag = $"127.0.0.1:{settings.MixedPort}";
        }

        ConfigurationsComboBox.SelectionChanged -= ConfigurationsComboBox_SelectionChanged;
        ConfigurationsComboBox.Items.Clear();
        foreach (ConfigurationProfile configuration in snapshot.Configurations)
        {
            ConfigurationsComboBox.Items.Add(new ComboBoxItem { Content = configuration.Name, Tag = configuration.Id });
            if (configuration.IsActive || configuration.Id == _selectedConfigurationId)
            {
                _selectedConfigurationId = configuration.Id;
            }
        }

        ComboBoxItem? selectedItem = ConfigurationsComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, _selectedConfigurationId, StringComparison.OrdinalIgnoreCase));
        ConfigurationsComboBox.SelectedItem = selectedItem;
        ConfigurationsComboBox.SelectionChanged += ConfigurationsComboBox_SelectionChanged;
        _proxyPage?.UpdateSnapshot(snapshot);
        _rulesPage?.UpdateSnapshot(snapshot);
        _connectionsPage?.UpdateSnapshot(snapshot);
        _logsPage?.UpdateSnapshot(snapshot);
        _settingsPage?.UpdateSnapshot(snapshot);
    }

    public void ShowError(string message)
    {
        ShowMessage(message, InfoBarSeverity.Error);
    }

    private void ShowMessage(string message, InfoBarSeverity severity)
    {
        ErrorBanner.Severity = severity;
        ErrorBanner.Message = ErrorSanitizer.Sanitize(message);
        ErrorBanner.IsOpen = true;
    }

    internal async Task ImportLocalConfigurationAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        FileOpenPicker picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        StorageFile file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await _runtime.ImportLocalConfigurationAsync(file.Path);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    internal async Task ImportSubscriptionAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        TextBox urlBox = new TextBox { PlaceholderText = "https://example.com/mihomo.yaml", MinWidth = 320 };
        TextBox nameBox = new TextBox { PlaceholderText = LocalizationService.Get("DialogOptionalNamePlaceholder"), Margin = new Thickness(0, 8, 0, 0) };
        StackPanel content = new StackPanel { Children = { new TextBlock { Text = LocalizationService.Get("DialogSubscriptionUrlLabel") }, urlBox, nameBox } };
        ContentDialog dialog = new ContentDialog
        {
            Title = LocalizationService.Get("DialogAddSubscriptionTitle"),
            Content = content,
            PrimaryButtonText = LocalizationService.Get("DialogAdd"),
            CloseButtonText = LocalizationService.Get("DialogCancel"),
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !Uri.TryCreate(urlBox.Text.Trim(), UriKind.Absolute, out Uri? uri)
            || uri is null
            || uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        try
        {
            await _runtime.ImportSubscriptionAsync(uri, string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim());
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private void ApplyFlyoutWindowStyle()
    {
        long extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        extendedStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        extendedStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));
        ApplyWindowMode();
    }

    private void ApplyWindowMode()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        long style = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_STYLE).ToInt64();
        long extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
        if (_isPinned)
        {
            style |= NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_THICKFRAME | NativeMethods.WS_MINIMIZEBOX;
            extendedStyle &= ~NativeMethods.WS_EX_TOOLWINDOW;
            extendedStyle |= NativeMethods.WS_EX_APPWINDOW;
        }
        else
        {
            style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU | NativeMethods.WS_THICKFRAME | NativeMethods.WS_MINIMIZEBOX);
            extendedStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            extendedStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        }

        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_STYLE, new IntPtr(style));
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE, new IntPtr(extendedStyle));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int corners = 2;
            int cornerResult = NativeMethods.DwmSetWindowAttribute(_windowHandle, 33, ref corners, sizeof(int));
            if (cornerResult < 0)
            {
                System.Diagnostics.Debug.WriteLine($"Rounded window corners unavailable: {cornerResult:X8}");
            }
        }
        NativeMethods.SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
    }

    private NativeMethods.Rect GetTrayRect()
    {
        if (_trayIcon?.TryGetIconRect(out NativeMethods.Rect rect) == true)
        {
            return rect;
        }

        NativeMethods.GetCursorPos(out NativeMethods.Point point);
        return new NativeMethods.Rect
        {
            Left = point.X - 8,
            Top = point.Y - 8,
            Right = point.X + 8,
            Bottom = point.Y + 8
        };
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        PinButton.Content = new FontIcon
        {
            Glyph = "\uE718",
            FontSize = 16
        };
        ToolTipService.SetToolTip(PinButton, _isPinned ? LocalizationService.Get("ToolTipUnpinWindow") : LocalizationService.Get("ToolTipPinWindow"));
        ApplyWindowMode();
        if (!_isPinned)
        {
            ShowPanel();
        }
    }

    private async void CoreActionButton_Click(object sender, RoutedEventArgs e) => await _app.ToggleCoreAsync();

    private async void SystemProxySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSnapshot || _runtime is null)
        {
            return;
        }

        if (_activeEndpointKind != EndpointKind.Local)
        {
            _updatingSnapshot = true;
            SystemProxySwitch.IsOn = _runtime.Snapshot.SystemProxy == SystemProxyState.On;
            _updatingSnapshot = false;
            return;
        }

        if (SystemProxySwitch.IsOn == (_runtime.Snapshot.SystemProxy == SystemProxyState.On))
        {
            return;
        }

        _updatingSnapshot = true;
        SystemProxySwitch.IsOn = _runtime.Snapshot.SystemProxy == SystemProxyState.On;
        _updatingSnapshot = false;
        await _app.ToggleSystemProxyAsync();
    }

    private async void TunSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSnapshot || _runtime is null)
        {
            return;
        }

        if (_activeEndpointKind != EndpointKind.Local)
        {
            _updatingSnapshot = true;
            TunSwitch.IsOn = _runtime.Snapshot.Tun == TunState.On;
            _updatingSnapshot = false;
            return;
        }

        if (TunSwitch.IsOn == (_runtime.Snapshot.Tun == TunState.On))
        {
            return;
        }

        _updatingSnapshot = true;
        TunSwitch.IsOn = _runtime.Snapshot.Tun == TunState.On;
        _updatingSnapshot = false;
        await _app.ToggleTunAsync();
    }

    private async void RuleModeButton_Click(object sender, RoutedEventArgs e) => await _app.SetModeAsync(ProxyMode.Rule);

    private async void GlobalModeButton_Click(object sender, RoutedEventArgs e) => await _app.SetModeAsync(ProxyMode.Global);

    private async void DirectModeButton_Click(object sender, RoutedEventArgs e) => await _app.SetModeAsync(ProxyMode.Direct);

    private async void ImportLocalButton_Click(object sender, RoutedEventArgs e) => await ImportLocalConfigurationAsync();

    private async void ImportSubscriptionButton_Click(object sender, RoutedEventArgs e) => await ImportSubscriptionAsync();

    private async void RefreshConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        ConfigurationProfile? selected = GetSelectedConfiguration();
        if (selected is not null && _runtime is not null)
        {
            try
            {
                await _runtime.ReloadConfigurationAsync(selected);
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
        }
    }

    private async void DeleteConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        ConfigurationProfile? selected = GetSelectedConfiguration();
        if (selected is not null && _runtime is not null)
        {
            ContentDialog dialog = new ContentDialog
            {
                Title = LocalizationService.Get("DialogDeleteConfigTitle"),
                Content = selected.IsActive
                    ? LocalizationService.Format("DialogDeleteConfigActiveFormat", selected.Name)
                    : LocalizationService.Format("DialogDeleteConfigFormat", selected.Name),
                PrimaryButtonText = LocalizationService.Get("DialogDelete"),
                CloseButtonText = LocalizationService.Get("DialogCancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                await _runtime.DeleteConfigurationAsync(selected);
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
        }
    }

    private async void ConfigurationsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfigurationsComboBox.SelectedItem is ComboBoxItem { Tag: string id } && _runtime is not null)
        {
            _selectedConfigurationId = id;
            try
            {
                await _runtime.SetActiveConfigurationAsync(id);
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
        }
    }

    private ConfigurationProfile? GetSelectedConfiguration()
    {
        string? id = (ConfigurationsComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return _runtime?.Snapshot.Configurations.FirstOrDefault(configuration => configuration.Id == id);
    }

    private void RulesButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_rulesPage, PanelPage.Rules);

    private void ConnectionsButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_connectionsPage, PanelPage.Connections);

    private void LogsButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_logsPage, PanelPage.Logs);

    private void ProxyPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_proxyPage, PanelPage.Proxy);

    private void RulesPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_rulesPage, PanelPage.Rules);

    private void ConnectionsPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_connectionsPage, PanelPage.Connections);

    private void LogsPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_logsPage, PanelPage.Logs);

    private void SettingsPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_settingsPage, PanelPage.Settings);

    private void QuitButton_Click(object sender, RoutedEventArgs e) => _app.RequestQuit();

    private void CopyEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string endpoint })
        {
            return;
        }

        DataPackage package = new DataPackage();
        package.SetText(endpoint);
        Clipboard.SetContent(package);
    }

    private async void ControllerEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _runtime.Snapshot.Core.State != CoreState.Running)
        {
            return;
        }

        Uri controllerUri = new Uri($"http://127.0.0.1:{_runtime.Settings.ControllerPort}/");
        if (!_runtime.DashboardAvailable)
        {
            DataPackage package = new DataPackage();
            package.SetText(controllerUri.ToString());
            Clipboard.SetContent(package);
            ShowMessage(LocalizationService.Get("MessageDashboardMissingCopied"), InfoBarSeverity.Informational);
            return;
        }

        Uri dashboardUri = new Uri(controllerUri, "ui/");
        if (!await Launcher.LaunchUriAsync(dashboardUri))
        {
            ShowError(LocalizationService.Get("ErrorOpenDashboard"));
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HidePanel();
    }

    private void NavigateTo(UIElement? page, PanelPage target)
    {
        bool isDashboard = page == _proxyPage;
        DashboardScrollViewer.Visibility = isDashboard ? Visibility.Visible : Visibility.Collapsed;
        OtherPageScrollViewer.Visibility = isDashboard ? Visibility.Collapsed : Visibility.Visible;
        if (page is not null && !isDashboard)
        {
            PageContent.Content = page;
        }

        ProxyPageButton.IsChecked = isDashboard;
        RulesPageButton.IsChecked = target == PanelPage.Rules;
        ConnectionsPageButton.IsChecked = target == PanelPage.Connections;
        LogsPageButton.IsChecked = target == PanelPage.Logs;
        SettingsPageButton.IsChecked = target == PanelPage.Settings;
        if (target is PanelPage.Rules or PanelPage.Settings)
        {
            _ = RefreshPageDataAsync();
        }
    }

    private async Task RefreshPageDataAsync()
    {
        if (_runtime is null
            || _runtime.Snapshot.Core.State != CoreState.Running
            || _pageRefreshInProgress)
        {
            return;
        }

        _pageRefreshInProgress = true;
        try
        {
            await _runtime.RefreshDataAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            _pageRefreshInProgress = false;
        }
    }


    private void TrafficGraph_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTraffic();

    private void DrawTraffic()
    {
        if (TrafficGraph is null || UploadLine is null || DownloadLine is null)
        {
            return;
        }

        (double Up, double Down)[] samples = _trafficHistory.ToArray();
        double width = Math.Max(1, TrafficGraph.ActualWidth);
        double peak = samples.Length == 0 ? 1 : Math.Max(1, samples.Max(s => Math.Max(s.Up, s.Down)));
        PointCollection upload = new PointCollection();
        PointCollection download = new PointCollection();
        for (int i = 0; i < 60; i++)
        {
            int index = i - (60 - samples.Length);
            (double Up, double Down) sample = index < 0 ? (Up: 0d, Down: 0d) : samples[index];
            double x = i * width / 59;
            double height = Math.Max(3, TrafficGraph.ActualHeight) - 2;
            upload.Add(new Windows.Foundation.Point(x, height - (height - 2) * Math.Max(0, sample.Up) / peak));
            download.Add(new Windows.Foundation.Point(x, height - (height - 2) * Math.Max(0, sample.Down) / peak));
        }
        UploadLine.Points = upload;
        DownloadLine.Points = download;
    }

    private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && !_isPinned)
        {
            HidePanel();
            e.Handled = true;
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        double value = bytesPerSecond;
        string unit = "B/s";
        if (value >= 1024)
        {
            value /= 1024;
            unit = "KB/s";
        }

        if (value >= 1024)
        {
            value /= 1024;
            unit = "MB/s";
        }

        return $"{value:0.#} {unit}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} KB";
        }

        return $"{bytes / 1024d / 1024d:0.#} MB";
    }
}
