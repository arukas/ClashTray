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
    private bool _updatingThemeControls;
    private bool _pageRefreshInProgress;
    private bool _configurationSelectionInProgress;
    private PanelPage _activePage = PanelPage.Proxy;
    private RuntimeSnapshot? _latestDisplayedSnapshot;
    private EndpointKind _activeEndpointKind = EndpointKind.Local;
    private readonly Queue<(double Up, double Down)> _trafficHistory = new();
    private DateTime _lastTrafficSample;
    private EndpointSessionState _activeEndpointState = EndpointSessionState.Disconnected;
    private DateTimeOffset? _activeControllerLastConfirmedAt;
    private string _activeEndpointDisplayName = EndpointId.Local.Value;
    private EndpointCapability _activeEndpointCapabilities = EndpointCapabilityDefaults.Local;
    private ErrorCode _activeControllerErrorCode = ErrorCode.None;

    private bool ActiveControllerWritable =>
        _activeEndpointKind == EndpointKind.Local
        || _activeEndpointState == EndpointSessionState.Connected;

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
        _activeEndpointState = snapshot.ActiveController.State;
        _activeControllerLastConfirmedAt = snapshot.ActiveController.LastConfirmedAt;
        _activeEndpointDisplayName = snapshot.ActiveController.Endpoint.DisplayName;
        _activeEndpointCapabilities = snapshot.ActiveController.Capabilities;
        _activeControllerErrorCode = snapshot.ActiveController.ErrorCode;
        UpdateSnapshot(RuntimeSnapshotAdapter.ToRuntimeSnapshot(
            snapshot));
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _latestDisplayedSnapshot = snapshot;
        ApplyTheme(_runtime?.Settings.Theme ?? "system");
        CoreStatus core = snapshot.Core;
        bool localController = _activeEndpointKind == EndpointKind.Local;
        bool controllerWritable = localController
            || _activeEndpointState == EndpointSessionState.Connected;
        bool canSwitchMode = controllerWritable
            && HasActiveEndpointCapability(EndpointCapability.SwitchMode);
        bool coreBusy = core.State is CoreState.Validating
            or CoreState.Starting
            or CoreState.Stopping
            or CoreState.Restarting;
        CoreActionButton.IsEnabled = localController && !coreBusy;
        bool coreRunning = core.State == CoreState.Running;
        RuleModeButton.IsEnabled = coreRunning && canSwitchMode;
        GlobalModeButton.IsEnabled = coreRunning && canSwitchMode;
        DirectModeButton.IsEnabled = coreRunning && canSwitchMode;
        string? modeTooltip = canSwitchMode
            ? null
            : controllerWritable
                ? LocalizationService.Get("RemoteControllerCapabilityUnavailable")
                : LocalizationService.Get("RemoteControllerReadOnly");
        ToolTipService.SetToolTip(RuleModeButton, modeTooltip);
        ToolTipService.SetToolTip(GlobalModeButton, modeTooltip);
        ToolTipService.SetToolTip(DirectModeButton, modeTooltip);
        RuleModeButton.IsChecked = coreRunning && core.Mode == ProxyMode.Rule;
        GlobalModeButton.IsChecked = coreRunning && core.Mode == ProxyMode.Global;
        DirectModeButton.IsChecked = coreRunning && core.Mode == ProxyMode.Direct;
        CoreStateText.Text = localController
            ? FormatCoreState(core.State)
            : FormatEndpointState(_activeEndpointState);
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
        StatusDot.Fill = new SolidColorBrush(localController
            ? core.State switch
            {
                CoreState.Running => Colors.Green,
                CoreState.Failed => Colors.Red,
                CoreState.Starting or CoreState.Stopping or CoreState.Restarting or CoreState.Validating => Colors.Orange,
                _ => Colors.Gray
            }
            : _activeEndpointState switch
            {
                EndpointSessionState.Connected => Colors.Green,
                EndpointSessionState.Connecting or EndpointSessionState.Reconnecting => Colors.Orange,
                EndpointSessionState.AuthenticationFailed
                    or EndpointSessionState.CertificateFailed
                    or EndpointSessionState.Incompatible
                    or EndpointSessionState.Failed => Colors.Red,
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
            ? LocalizationService.Format(
                "ControllerRemoteFormat",
                _activeEndpointDisplayName)
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
        if (localController)
        {
            ControllerFreshnessText.Visibility = Visibility.Collapsed;
            ControllerFreshnessText.Text = string.Empty;
        }
        else
        {
            ControllerFreshnessText.Visibility = Visibility.Visible;
            ControllerFreshnessText.Text = _activeControllerLastConfirmedAt is DateTimeOffset confirmedAt
                ? LocalizationService.Format(
                    "ControllerLastConfirmedFormat",
                    confirmedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                : LocalizationService.Get("ControllerNoConfirmedData");
        }
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
        ConfigurationText.Text = core.ConfigurationName ?? snapshot.Configurations.FirstOrDefault(c => c.IsActive)?.Name ?? LocalizationService.Get("ConfigImportPrompt");
        ErrorBanner.Message = FormatDisplayedError(
            localController ? ErrorCode.None : _activeControllerErrorCode,
            snapshot.ErrorMessage ?? core.ErrorMessage);
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

        string? activeConfigurationId = snapshot.Configurations
            .FirstOrDefault(configuration => configuration.IsActive)
            ?.Id;
        bool configurationItemsChanged = ConfigurationsComboBox.Items.Count != snapshot.Configurations.Count;
        if (!configurationItemsChanged)
        {
            for (int index = 0; index < snapshot.Configurations.Count; index++)
            {
                ConfigurationProfile configuration = snapshot.Configurations[index];
                if (ConfigurationsComboBox.Items[index] is not ComboBoxItem item
                    || !string.Equals(item.Tag as string, configuration.Id, StringComparison.Ordinal)
                    || !string.Equals(item.Content as string, configuration.Name, StringComparison.Ordinal))
                {
                    configurationItemsChanged = true;
                    break;
                }
            }
        }

        if (configurationItemsChanged)
        {
            ConfigurationsComboBox.SelectionChanged -= ConfigurationsComboBox_SelectionChanged;
            ConfigurationsComboBox.Items.Clear();
            foreach (ConfigurationProfile configuration in snapshot.Configurations)
            {
                ConfigurationsComboBox.Items.Add(new ComboBoxItem
                {
                    Content = configuration.Name,
                    Tag = configuration.Id
                });
            }

            ConfigurationsComboBox.SelectionChanged += ConfigurationsComboBox_SelectionChanged;
        }

        if (!_configurationSelectionInProgress)
        {
            _selectedConfigurationId = activeConfigurationId
                ?? snapshot.Configurations.FirstOrDefault(configuration =>
                    string.Equals(configuration.Id, _selectedConfigurationId, StringComparison.OrdinalIgnoreCase))?.Id;
            ComboBoxItem? selectedItem = ConfigurationsComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, _selectedConfigurationId, StringComparison.OrdinalIgnoreCase));
            if (!ReferenceEquals(ConfigurationsComboBox.SelectedItem, selectedItem))
            {
                ConfigurationsComboBox.SelectionChanged -= ConfigurationsComboBox_SelectionChanged;
                ConfigurationsComboBox.SelectedItem = selectedItem;
                ConfigurationsComboBox.SelectionChanged += ConfigurationsComboBox_SelectionChanged;
            }
        }

        UpdateActivePage(snapshot, controllerWritable, _activeEndpointCapabilities);
    }

    private void UpdateActivePage(
        RuntimeSnapshot snapshot,
        bool? controllerWritable = null,
        EndpointCapability? capabilities = null)
    {
        bool writable = controllerWritable ?? ActiveControllerWritable;
        EndpointCapability activeCapabilities = capabilities ?? _activeEndpointCapabilities;
        switch (_activePage)
        {
            case PanelPage.Proxy:
                _proxyPage?.UpdateSnapshot(snapshot, writable, activeCapabilities);
                break;
            case PanelPage.Rules:
                _rulesPage?.UpdateSnapshot(snapshot, writable, activeCapabilities);
                break;
            case PanelPage.Connections:
                _connectionsPage?.UpdateSnapshot(snapshot, writable, activeCapabilities);
                break;
            case PanelPage.Logs:
                _logsPage?.UpdateSnapshot(snapshot, writable, activeCapabilities);
                break;
            case PanelPage.Settings:
                _settingsPage?.UpdateSnapshot(snapshot, writable, activeCapabilities);
                break;
        }
    }

    private bool HasActiveEndpointCapability(EndpointCapability capability) =>
        (_activeEndpointCapabilities & capability) == capability;

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

    private async void RuleModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ActiveControllerWritable)
        {
            return;
        }

        if (HasActiveEndpointCapability(EndpointCapability.SwitchMode))
        {
            await _app.SetModeAsync(ProxyMode.Rule);
        }
    }

    private async void GlobalModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ActiveControllerWritable)
        {
            return;
        }

        if (HasActiveEndpointCapability(EndpointCapability.SwitchMode))
        {
            await _app.SetModeAsync(ProxyMode.Global);
        }
    }

    private async void DirectModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ActiveControllerWritable)
        {
            return;
        }

        if (HasActiveEndpointCapability(EndpointCapability.SwitchMode))
        {
            await _app.SetModeAsync(ProxyMode.Direct);
        }
    }

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
            _configurationSelectionInProgress = true;
            try
            {
                await _runtime.SetActiveConfigurationAsync(id);
            }
            catch (Exception exception)
            {
                ShowError(exception.Message);
            }
            finally
            {
                _configurationSelectionInProgress = false;
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

    private async void QuitButton_Click(object sender, RoutedEventArgs e) => await _app.RequestQuitAsync();

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
            _appWindow?.Closing -= AppWindow_Closing;
            return;
        }

        args.Cancel = true;
        HidePanel();
    }

    private void NavigateTo(UIElement? page, PanelPage target)
    {
        _activePage = target;
        bool isDashboard = page == _proxyPage;
        bool isListPage = target is PanelPage.Connections or PanelPage.Logs;
        DashboardScrollViewer.Visibility = isDashboard ? Visibility.Visible : Visibility.Collapsed;
        OtherPageHost.Visibility = isDashboard ? Visibility.Collapsed : Visibility.Visible;
        OtherPageScrollViewer.Visibility = isDashboard || isListPage
            ? Visibility.Collapsed
            : Visibility.Visible;
        ListPageContent.Visibility = isListPage ? Visibility.Visible : Visibility.Collapsed;
        if (page is not null && !isDashboard)
        {
            if (isListPage)
            {
                ListPageContent.Content = page;
            }
            else
            {
                PageContent.Content = page;
            }
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

        if (_latestDisplayedSnapshot is not null)
        {
            // Keep the currently projected endpoint when navigating. Using
            // _runtime.Snapshot here would replace a remote projection with
            // the local device snapshot until the next remote refresh.
            UpdateActivePage(_latestDisplayedSnapshot, ActiveControllerWritable, _activeEndpointCapabilities);
        }
    }

    private async Task RefreshPageDataAsync()
    {
        if (_runtime is null || _pageRefreshInProgress)
        {
            return;
        }

        bool activeControllerConnected = _activeEndpointKind == EndpointKind.Remote
            ? _runtime.AppSnapshot.ActiveController.State == EndpointSessionState.Connected
            : _runtime.Snapshot.Core.State == CoreState.Running;
        if (!activeControllerConnected)
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

    private static string FormatCoreState(CoreState state) => state switch
    {
        CoreState.Running => LocalizationService.Get("CoreStateRunning"),
        CoreState.Starting => LocalizationService.Get("CoreStateStarting"),
        CoreState.Stopping => LocalizationService.Get("CoreStateStopping"),
        CoreState.Restarting => LocalizationService.Get("CoreStateRestarting"),
        CoreState.Failed => LocalizationService.Get("CoreStateFailed"),
        CoreState.Missing => LocalizationService.Get("CoreStateMissing"),
        _ => LocalizationService.Get("CoreStateStopped")
    };

    private static string FormatEndpointState(EndpointSessionState state) => state switch
    {
        EndpointSessionState.Connected => LocalizationService.Get("ControllerStateConnected"),
        EndpointSessionState.Connecting => LocalizationService.Get("ControllerStateConnecting"),
        EndpointSessionState.Reconnecting => LocalizationService.Get("ControllerStateReconnecting"),
        EndpointSessionState.AuthenticationFailed => LocalizationService.Get("ControllerStateAuthenticationFailed"),
        EndpointSessionState.CertificateFailed => LocalizationService.Get("ControllerStateCertificateFailed"),
        EndpointSessionState.Incompatible => LocalizationService.Get("ControllerStateIncompatible"),
        EndpointSessionState.Failed => LocalizationService.Get("ControllerStateFailed"),
        _ => LocalizationService.Get("ControllerStateDisconnected")
    };

    private static string FormatDisplayedError(ErrorCode errorCode, string? detail)
    {
        string? localized = errorCode switch
        {
            ErrorCode.EndpointAuthenticationFailed => LocalizationService.Get("ControllerErrorAuthentication"),
            ErrorCode.EndpointCertificateFailed => LocalizationService.Get("ControllerErrorCertificate"),
            ErrorCode.EndpointIncompatible => LocalizationService.Get("ControllerErrorIncompatible"),
            ErrorCode.EndpointStaleResult => LocalizationService.Get("ControllerErrorStale"),
            ErrorCode.EndpointTransportFailed => LocalizationService.Get("ControllerErrorTransport"),
            _ => null
        };
        if (localized is null)
        {
            return ErrorSanitizer.SanitizeNullable(detail) ?? string.Empty;
        }

        string? sanitizedDetail = ErrorSanitizer.SanitizeNullable(detail);
        return string.IsNullOrEmpty(sanitizedDetail)
            ? localized
            : LocalizationService.Format("ControllerErrorWithDetailFormat", localized, sanitizedDetail);
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
