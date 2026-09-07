using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace ClashTray.App;

public sealed partial class MainWindow : Window
{
    private const int PanelWidth = 420;
    private const int PanelHeight = 640;
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

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
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
        PageContent.Content = _proxyPage;
        ApplyTheme(runtime.Settings.Theme);
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

        var dpi = NativeMethods.GetDpiForWindow(_windowHandle);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        var width = (int)Math.Round(PanelWidth * scale);
        var height = (int)Math.Round(PanelHeight * scale);
        var trayRect = GetTrayRect();
        var workArea = GetWorkArea(trayRect);
        var x = trayRect.CenterX - width / 2;
        var y = trayRect.Top - height - 8;

        if (trayRect.Bottom <= workArea.Top + 100)
        {
            y = trayRect.Bottom + 8;
        }
        else if (trayRect.Left <= workArea.Left + 100)
        {
            x = trayRect.Right + 8;
            y = trayRect.CenterY - height / 2;
        }
        else if (trayRect.Right >= workArea.Right - 100)
        {
            x = trayRect.Left - width - 8;
            y = trayRect.CenterY - height / 2;
        }

        x = Math.Clamp(x, workArea.Left + 8, workArea.Right - width - 8);
        y = Math.Clamp(y, workArea.Top + 8, workArea.Bottom - height - 8);
        NativeMethods.SetWindowPos(
            _windowHandle,
            _isPinned ? IntPtr.Zero : new IntPtr(-1),
            x,
            y,
            width,
            height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(_windowHandle, _isPinned ? NativeMethods.SW_SHOW : NativeMethods.SW_SHOWNOACTIVATE);
        _isVisible = true;
    }

    public void HidePanel()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_windowHandle, NativeMethods.SW_HIDE);
        }

        _isVisible = false;
    }

    public void AllowClose() => _allowClose = true;

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        ApplyTheme(_runtime?.Settings.Theme ?? "system");
        var core = snapshot.Core;
        CoreStateText.Text = core.State switch
        {
            CoreState.Running => "运行中",
            CoreState.Starting => "启动中",
            CoreState.Stopping => "停止中",
            CoreState.Restarting => "重启中",
            CoreState.Failed => "异常",
            CoreState.Missing => "未安装",
            _ => "已停止"
        };
        CoreActionButton.Content = core.State == CoreState.Running ? "重启" : "启动";
        CoreVersionText.Text = string.IsNullOrWhiteSpace(core.Version) ? "版本未知" : $"Mihomo {core.Version}";
        TrafficText.Text = $"↑ {FormatRate(core.UploadBytesPerSecond)} · ↓ {FormatRate(core.DownloadBytesPerSecond)} · {core.ConnectionCount} 个连接 · 内存 {FormatBytes(core.MemoryBytes)}";
        SystemProxyStateText.Text = snapshot.SystemProxy switch
        {
            SystemProxyState.On => "已开启",
            SystemProxyState.Enabling => "开启中",
            SystemProxyState.Disabling => "关闭中",
            SystemProxyState.RestoreRequired => "需要恢复",
            SystemProxyState.Failed => "操作失败",
            _ => "已关闭"
        };
        TunStateText.Text = snapshot.Tun switch
        {
            TunState.On => "已开启",
            TunState.Enabling => "开启中",
            TunState.Disabling => "关闭中",
            TunState.Unavailable => "服务未安装",
            TunState.Failed => "操作失败",
            _ => "已关闭"
        };
        ConfigurationText.Text = core.ConfigurationName is null
            ? snapshot.ErrorMessage ?? "尚未导入配置"
            : $"{core.ConfigurationName} · {core.Mode switch { ProxyMode.Global => "全局", ProxyMode.Direct => "直连", _ => "规则" }}模式";

        ConfigurationsComboBox.SelectionChanged -= ConfigurationsComboBox_SelectionChanged;
        ConfigurationsComboBox.Items.Clear();
        foreach (var configuration in snapshot.Configurations)
        {
            ConfigurationsComboBox.Items.Add(new ComboBoxItem { Content = configuration.Name, Tag = configuration.Id });
            if (configuration.IsActive || configuration.Id == _selectedConfigurationId)
            {
                _selectedConfigurationId = configuration.Id;
            }
        }

        var selectedItem = ConfigurationsComboBox.Items
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
        ConfigurationText.Text = message;
    }

    private void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme.ToLowerInvariant() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    internal async Task ImportLocalConfigurationAsync()
    {
        if (_runtime is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var file = await picker.PickSingleFileAsync();
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

        var urlBox = new TextBox { PlaceholderText = "https://example.com/mihomo.yaml", MinWidth = 320 };
        var nameBox = new TextBox { PlaceholderText = "可选名称", Margin = new Thickness(0, 8, 0, 0) };
        var content = new StackPanel { Children = { new TextBlock { Text = "订阅地址" }, urlBox, nameBox } };
        var dialog = new ContentDialog
        {
            Title = "添加订阅",
            Content = content,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !Uri.TryCreate(urlBox.Text.Trim(), UriKind.Absolute, out var uri)
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
        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
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

        var style = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_STYLE).ToInt64();
        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GWL_EXSTYLE).ToInt64();
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
        if (_trayIcon?.TryGetIconRect(out var rect) == true)
        {
            return rect;
        }

        NativeMethods.GetCursorPos(out var point);
        return new NativeMethods.Rect
        {
            Left = point.X - 8,
            Top = point.Y - 8,
            Right = point.X + 8,
            Bottom = point.Y + 8
        };
    }

    private static NativeMethods.Rect GetWorkArea(NativeMethods.Rect anchor)
    {
        var monitor = NativeMethods.MonitorFromRect(ref anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
            Device = new int[32]
        };
        return NativeMethods.GetMonitorInfo(monitor, ref info) ? info.Work : new NativeMethods.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        PinButton.Content = _isPinned ? "取消固定" : "固定";
        ApplyWindowMode();
    }

    private async void CoreActionButton_Click(object sender, RoutedEventArgs e) => await _app.ToggleCoreAsync();

    private async void SystemProxyButton_Click(object sender, RoutedEventArgs e) => await _app.ToggleSystemProxyAsync();

    private async void TunButton_Click(object sender, RoutedEventArgs e) => await _app.ToggleTunAsync();

    private async void RuleModeButton_Click(object sender, RoutedEventArgs e) => await _app.SetModeAsync(ProxyMode.Rule);

    private async void GlobalModeButton_Click(object sender, RoutedEventArgs e) => await _app.SetModeAsync(ProxyMode.Global);

    private async void DirectModeButton_Click(object sender, RoutedEventArgs e) => await _app.SetModeAsync(ProxyMode.Direct);

    private async void ImportLocalButton_Click(object sender, RoutedEventArgs e) => await ImportLocalConfigurationAsync();

    private async void ImportSubscriptionButton_Click(object sender, RoutedEventArgs e) => await ImportSubscriptionAsync();

    private async void RefreshConfigurationButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedConfiguration();
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
        var selected = GetSelectedConfiguration();
        if (selected is not null && _runtime is not null)
        {
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
        var id = (ConfigurationsComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return _runtime?.Snapshot.Configurations.FirstOrDefault(configuration => configuration.Id == id);
    }

    private void RulesButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_rulesPage, "规则");

    private void ConnectionsButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_connectionsPage, "连接");

    private void LogsButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_logsPage, "日志");

    private void ProxyPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_proxyPage, "代理");

    private void RulesPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_rulesPage, "规则");

    private void ConnectionsPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_connectionsPage, "连接");

    private void LogsPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_logsPage, "日志");

    private void SettingsPageButton_Click(object sender, RoutedEventArgs e) => NavigateTo(_settingsPage, "设置");

    private void QuitButton_Click(object sender, RoutedEventArgs e) => _app.RequestQuit();

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        HidePanel();
    }

    private void NavigateTo(UIElement? page, string title)
    {
        if (page is not null)
        {
            PageContent.Content = page;
            ConfigurationText.Text = title;
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        var value = bytesPerSecond;
        var unit = "B/s";
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
