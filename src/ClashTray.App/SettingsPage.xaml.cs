using ClashTray.Core;
using ClashTray.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class SettingsPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;

    public SettingsPage(ClashTrayRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        LoadSettings(runtime.Settings);
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        LoadSettings(_runtime.Settings);
        UpdateProviders(snapshot);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var current = _runtime.Settings;
        var logLevel = (LogLevelBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? current.LogLevel;
        var theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? current.Theme;
        await _runtime.UpdateSettingsAsync(current with
        {
            StartWithWindows = StartWithWindowsSwitch.IsOn,
            StartCoreAutomatically = StartCoreSwitch.IsOn,
            AllowLan = AllowLanSwitch.IsOn,
            Ipv6 = Ipv6Switch.IsOn,
            TcpConcurrent = TcpConcurrentSwitch.IsOn,
            HttpPort = (int)HttpPortBox.Value,
            SocksPort = (int)SocksPortBox.Value,
            MixedPort = (int)MixedPortBox.Value,
            ControllerPort = (int)ControllerPortBox.Value,
            LogLevel = logLevel,
            Theme = theme,
            BypassList = BypassListBox.Text.Trim()
        });
        StatusText.Text = "设置已保存；核心重启后端口配置生效。";
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
        if (!Uri.TryCreate(CoreDownloadUriBox.Text.Trim(), UriKind.Absolute, out var uri)
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

        StartWithWindowsSwitch.IsOn = settings.StartWithWindows;
        StartCoreSwitch.IsOn = settings.StartCoreAutomatically;
        AllowLanSwitch.IsOn = settings.AllowLan;
        Ipv6Switch.IsOn = settings.Ipv6;
        TcpConcurrentSwitch.IsOn = settings.TcpConcurrent;
        HttpPortBox.Value = settings.HttpPort;
        SocksPortBox.Value = settings.SocksPort;
        MixedPortBox.Value = settings.MixedPort;
        ControllerPortBox.Value = settings.ControllerPort;
        BypassListBox.Text = settings.BypassList;
        LogLevelBox.SelectedItem = LogLevelBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == settings.LogLevel)
            ?? LogLevelBox.Items.FirstOrDefault();
        ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == settings.Theme)
            ?? ThemeBox.Items.FirstOrDefault();
    }

    public void UpdateProviders(RuntimeSnapshot snapshot)
    {
        if (ProvidersListView is null)
        {
            return;
        }

        var selectedName = (ProvidersListView.SelectedItem as ListViewItem)?.Tag is ValueTuple<ProviderStatus, bool> selected
            ? selected.Item1.Name
            : null;
        ProvidersListView.Items.Clear();
        foreach (var provider in snapshot.Providers)
        {
            ProvidersListView.Items.Add(CreateProviderItem(provider, rules: false));
        }

        foreach (var provider in snapshot.RuleProviders)
        {
            ProvidersListView.Items.Add(CreateProviderItem(provider, rules: true));
        }

        var restored = ProvidersListView.Items.OfType<ListViewItem>().FirstOrDefault(item =>
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
