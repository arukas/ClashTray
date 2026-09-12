using ClashTray.Core;
using ClashTray.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class SettingsPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private AppSettings? _loadedSettings;
    private bool _saving;

    public SettingsPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        InitializeComponent();
        LoadSettings(runtime.Settings);
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        LoadSettings(_runtime.Settings);
        UpdateProviders(snapshot);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saving)
        {
            return;
        }

        if (!TryReadPort(HttpPortBox, "HTTP", out int httpPort)
            || !TryReadPort(SocksPortBox, "SOCKS", out int socksPort)
            || !TryReadPort(MixedPortBox, "Mixed", out int mixedPort)
            || !TryReadPort(ControllerPortBox, "控制器", out int controllerPort))
        {
            return;
        }

        if (!TryReadSubscriptionRefreshHours(out int subscriptionRefreshHours))
        {
            StatusText.Text = "订阅刷新间隔必须是 1 到 168 之间的整数小时。";
            return;
        }

        AppSettings current = _runtime.Settings;
        string logLevel = (LogLevelBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? current.LogLevel;
        string theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? current.Theme;
        _saving = true;
        SaveSettingsButton.IsEnabled = false;
        try
        {
            await _runtime.UpdateSettingsAsync(current with
            {
                StartWithWindows = StartWithWindowsSwitch.IsOn,
                StartCoreAutomatically = StartCoreSwitch.IsOn,
                AllowLan = AllowLanSwitch.IsOn,
                Ipv6 = Ipv6Switch.IsOn,
                TcpConcurrent = TcpConcurrentSwitch.IsOn,
                DisconnectConnectionsAfterProxySwitch = DisconnectAfterProxySwitch.IsOn,
                HttpPort = httpPort,
                SocksPort = socksPort,
                MixedPort = mixedPort,
                ControllerPort = controllerPort,
                LogLevel = logLevel,
                Theme = theme,
                BypassList = BypassListBox.Text.Trim(),
                SubscriptionRefreshHours = subscriptionRefreshHours
            }, reconcileStartup: true);
            UpdateStartupStatus(_runtime.Settings);
            StatusText.Text = "设置已保存；需要重启的核心参数会自动受控重启，系统代理会在核心健康后使用新端口。";
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"设置保存失败：{exception.Message}";
        }
        finally
        {
            _saving = false;
            SaveSettingsButton.IsEnabled = true;
        }
    }

    private async void ClearFakeIpButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.ClearFakeIpCacheAsync();
            StatusText.Text = "FakeIP 缓存已清理。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void ClearDnsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.ClearDnsCacheAsync();
            StatusText.Text = "DNS 缓存已清理。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void UpdateGeoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.UpdateGeoAsync();
            StatusText.Text = "Geo 数据库更新请求已发送。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private async void RefreshProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProvidersListView.SelectedItem is not ListViewItem { Tag: ValueTuple<ProviderStatus, bool> selected })
        {
            StatusText.Text = "先选择一个 Provider。";
            return;
        }

        try
        {
            await _runtime.RefreshProviderAsync(selected.Item1.Name, selected.Item2);
            StatusText.Text = $"已请求刷新 {selected.Item1.Name}。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void ProvidersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProvidersListView.SelectedItem is ListViewItem { Tag: ValueTuple<ProviderStatus, bool> selected })
        {
            StatusText.Text = selected.Item1.Error ?? $"{selected.Item1.Name} · {selected.Item1.Count} 项";
        }
    }

    private async void InstallCoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(CoreDownloadUriBox.Text.Trim(), UriKind.Absolute, out Uri? uri)
            || uri is null
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(CoreVersionBox.Text)
            || CoreSha256Box.Text.Trim().Length != 64
            || CoreSha256Box.Text.Trim().Any(character => !Uri.IsHexDigit(character)))
        {
            StatusText.Text = "请填写官方 HTTPS 下载地址、版本和 64 位 SHA-256。";
            return;
        }

        try
        {
            await _runtime.InstallCoreUpdateAsync(new CoreUpdateManifest(CoreVersionBox.Text.Trim(), uri, CoreSha256Box.Text.Trim()));
            StatusText.Text = "核心已完成校验并原子替换。";
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void LoadSettings(AppSettings settings)
    {
        if (StartWithWindowsSwitch is null)
        {
            return;
        }

        UpdateStartupStatus(settings);
        if (settings == _loadedSettings)
        {
            return;
        }

        // Merge external settings changes only into fields the user has not edited.
        // Do not reassign unchanged values: NumberBox may still contain uncommitted text.
        bool ShouldRefresh<T>(T displayed, Func<AppSettings, T> select) =>
            _loadedSettings is null ||
            (!EqualityComparer<T>.Default.Equals(select(_loadedSettings), select(settings))
                && EqualityComparer<T>.Default.Equals(displayed, select(_loadedSettings)));

        if (ShouldRefresh(StartWithWindowsSwitch.IsOn, value => value.StartWithWindows))
        {
            StartWithWindowsSwitch.IsOn = settings.StartWithWindows;
        }

        if (ShouldRefresh(StartCoreSwitch.IsOn, value => value.StartCoreAutomatically))
        {
            StartCoreSwitch.IsOn = settings.StartCoreAutomatically;
        }

        if (ShouldRefresh(AllowLanSwitch.IsOn, value => value.AllowLan))
        {
            AllowLanSwitch.IsOn = settings.AllowLan;
        }

        if (ShouldRefresh(Ipv6Switch.IsOn, value => value.Ipv6))
        {
            Ipv6Switch.IsOn = settings.Ipv6;
        }

        if (ShouldRefresh(TcpConcurrentSwitch.IsOn, value => value.TcpConcurrent))
        {
            TcpConcurrentSwitch.IsOn = settings.TcpConcurrent;
        }

        if (ShouldRefresh(DisconnectAfterProxySwitch.IsOn, value => value.DisconnectConnectionsAfterProxySwitch))
        {
            DisconnectAfterProxySwitch.IsOn = settings.DisconnectConnectionsAfterProxySwitch;
        }

        if (ShouldRefresh(SubscriptionRefreshHoursBox.Value, value => (double)value.SubscriptionRefreshHours))
        {
            SubscriptionRefreshHoursBox.Value = settings.SubscriptionRefreshHours;
        }

        if (ShouldRefresh(HttpPortBox.Value, value => (double)value.HttpPort))
        {
            HttpPortBox.Value = settings.HttpPort;
        }

        if (ShouldRefresh(SocksPortBox.Value, value => (double)value.SocksPort))
        {
            SocksPortBox.Value = settings.SocksPort;
        }

        if (ShouldRefresh(MixedPortBox.Value, value => (double)value.MixedPort))
        {
            MixedPortBox.Value = settings.MixedPort;
        }

        if (ShouldRefresh(ControllerPortBox.Value, value => (double)value.ControllerPort))
        {
            ControllerPortBox.Value = settings.ControllerPort;
        }

        if (ShouldRefresh(BypassListBox.Text, value => value.BypassList))
        {
            BypassListBox.Text = settings.BypassList;
        }

        if (ShouldRefresh((LogLevelBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), value => value.LogLevel))
        {
            LogLevelBox.SelectedItem = LogLevelBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == settings.LogLevel)
                ?? LogLevelBox.Items.FirstOrDefault();
        }

        bool refreshTheme = ShouldRefresh((ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), value => value.Theme);
        ComboBoxItem? hiddenTheme = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "nakhimov");
        if (settings.NakhimovUnlocked && hiddenTheme is null)
        {
            ThemeBox.Items.Add(new ComboBoxItem { Content = "Nakhimov", Tag = "nakhimov" });
        }
        else if (!settings.NakhimovUnlocked && hiddenTheme is not null)
        {
            ThemeBox.Items.Remove(hiddenTheme);
        }

        if (refreshTheme)
        {
            ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == settings.Theme)
                ?? ThemeBox.Items.FirstOrDefault();
        }

        _loadedSettings = settings;
    }

    private void UpdateStartupStatus(AppSettings settings)
    {
        StartupRegistrationStatus status = _runtime.GetStartupStatus();
        if (!string.IsNullOrWhiteSpace(status.Error))
        {
            StartupStatusText.Text = $"无法读取 Windows 启动状态：{status.Error}";
            return;
        }

        if (settings.StartWithWindows)
        {
            StartupStatusText.Text = status.IsDisabledByOperatingSystem
                ? "开机启动已注册，但 Windows 已禁用；请在任务管理器的“启动应用”中启用。"
                : status.RequiresRepair
                    ? "开机启动已打开，但启动项命令已变化；点击“保存设置”修复。"
                    : status.IsRegistered
                        ? "开机启动已注册。"
                        : "开机启动已打开，但启动项缺失；点击“保存设置”修复。";
            return;
        }

        StartupStatusText.Text = status.IsRegistered && !status.IsOwnedByClashTray
            ? "开机启动已关闭；检测到同名启动项，保存时会保留其他程序的命令。"
            : "开机启动已关闭。";
    }

    private bool TryReadSubscriptionRefreshHours(out int hours)
    {
        double value = SubscriptionRefreshHoursBox.Value;
        if (double.IsNaN(value)
            || double.IsInfinity(value)
            || value < 1
            || value > 168
            || value != Math.Truncate(value))
        {
            hours = 0;
            return false;
        }

        hours = (int)value;
        return true;
    }

    private bool TryReadPort(NumberBox box, string name, out int port)
    {
        double value = box.Value;
        if (double.IsNaN(value)
            || double.IsInfinity(value)
            || value < 1
            || value > 65535
            || value != Math.Truncate(value))
        {
            StatusText.Text = $"{name} 端口必须是 1 到 65535 之间的整数。";
            port = 0;
            return false;
        }

        port = (int)value;
        return true;
    }

    public void UpdateProviders(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (ProvidersListView is null)
        {
            return;
        }

        string? selectedName = (ProvidersListView.SelectedItem as ListViewItem)?.Tag is ValueTuple<ProviderStatus, bool> selected
            ? selected.Item1.Name
            : null;
        ProvidersListView.Items.Clear();
        foreach (ProviderStatus provider in snapshot.Providers)
        {
            ProvidersListView.Items.Add(CreateProviderItem(provider, rules: false));
        }

        foreach (ProviderStatus provider in snapshot.RuleProviders)
        {
            ProvidersListView.Items.Add(CreateProviderItem(provider, rules: true));
        }

        ListViewItem? restored = ProvidersListView.Items.OfType<ListViewItem>().FirstOrDefault(item =>
            item.Tag is ValueTuple<ProviderStatus, bool> itemData && itemData.Item1.Name == selectedName);
        ProvidersListView.SelectedItem = restored;
    }

    private static ListViewItem CreateProviderItem(ProviderStatus provider, bool rules) =>
        new()
        {
            Content = $"{(rules ? "规则" : "代理")} · {provider.Name} · {provider.Count} 项 · {provider.UpdatedAt:MM-dd HH:mm}",
            Tag = (provider, rules)
        };
}



