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
    private IReadOnlyList<ProviderStatus>? _providers;
    private IReadOnlyList<ProviderStatus>? _ruleProviders;

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
            || !TryReadPort(ControllerPortBox, LocalizationService.Get("ControllerPortLabel"), out int controllerPort))
        {
            return;
        }

        if (!TryReadSubscriptionRefreshHours(out int subscriptionRefreshHours))
        {
            StatusText.Text = LocalizationService.Get("ErrorInvalidRefreshHours");
            return;
        }

        AppSettings current = _runtime.Settings;
        string logLevel = (LogLevelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? current.LogLevel;
        string theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? current.Theme;
        string language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? current.Language;
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
                Language = language,
                BypassList = BypassListBox.Text.Trim(),
                SubscriptionRefreshHours = subscriptionRefreshHours
            }, reconcileStartup: true);
            UpdateStartupStatus(_runtime.Settings);
            // The language override is applied before the first window exists, so
            // changing it only takes effect on the next app start; the core keeps running.
            StatusText.Text = string.Equals(language, current.Language, StringComparison.OrdinalIgnoreCase)
                ? LocalizationService.Get("SettingsSaved")
                : $"{LocalizationService.Get("SettingsSaved")} {LocalizationService.Get("LanguageRestartPrompt")}";
        }
        catch (ArgumentException exception)
        {
            StatusText.Text = ErrorSanitizer.Sanitize(exception);
        }
        catch (Exception exception)
        {
            StatusText.Text = LocalizationService.Format("SettingsSaveFailedFormat", ErrorSanitizer.Sanitize(exception));
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
            StatusText.Text = LocalizationService.Get("FakeIpCleared");
        }
        catch (Exception exception)
        {
            StatusText.Text = ErrorSanitizer.Sanitize(exception);
        }
    }

    private async void ClearDnsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.ClearDnsCacheAsync();
            StatusText.Text = LocalizationService.Get("DnsCleared");
        }
        catch (Exception exception)
        {
            StatusText.Text = ErrorSanitizer.Sanitize(exception);
        }
    }

    private async void UpdateGeoButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.UpdateGeoAsync();
            StatusText.Text = LocalizationService.Get("GeoUpdateSent");
        }
        catch (Exception exception)
        {
            StatusText.Text = ErrorSanitizer.Sanitize(exception);
        }
    }

    private async void RefreshProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProvidersListView.SelectedItem is not ListViewItem { Tag: ValueTuple<ProviderStatus, bool> selected })
        {
            StatusText.Text = LocalizationService.Get("SelectProviderFirst");
            return;
        }

        try
        {
            await _runtime.RefreshProviderAsync(selected.Item1.Name, selected.Item2);
            StatusText.Text = LocalizationService.Format("ProviderRefreshRequestedFormat", selected.Item1.Name);
        }
        catch (Exception exception)
        {
            StatusText.Text = ErrorSanitizer.Sanitize(exception);
        }
    }

    private void ProvidersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProvidersListView.SelectedItem is ListViewItem { Tag: ValueTuple<ProviderStatus, bool> selected })
        {
            StatusText.Text = ErrorSanitizer.Sanitize(
                selected.Item1.Error ?? LocalizationService.Format("ProviderStatusSummaryFormat", selected.Item1.Name, selected.Item1.Count));
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
            StatusText.Text = LocalizationService.Get("CoreUpdateFieldsRequired");
            return;
        }

        try
        {
            await _runtime.InstallCoreUpdateAsync(new CoreUpdateManifest(CoreVersionBox.Text.Trim(), uri, CoreSha256Box.Text.Trim()));
            StatusText.Text = LocalizationService.Get("CoreUpdateDone");
        }
        catch (Exception exception)
        {
            StatusText.Text = ErrorSanitizer.Sanitize(exception);
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

        if (ShouldRefresh((LogLevelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), value => value.LogLevel))
        {
            LogLevelBox.SelectedItem = LogLevelBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == settings.LogLevel)
                ?? LogLevelBox.Items.FirstOrDefault();
        }

        if (ShouldRefresh((LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), value => value.Language))
        {
            LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == settings.Language)
                ?? LanguageBox.Items.FirstOrDefault();
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
            StartupStatusText.Text = LocalizationService.Format("StartupStatusReadFailedFormat", ErrorSanitizer.Sanitize(status.Error));
            return;
        }

        if (settings.StartWithWindows)
        {
            StartupStatusText.Text = status.IsDisabledByOperatingSystem
                ? LocalizationService.Get("StartupRegisteredButDisabled")
                : status.RequiresRepair
                    ? LocalizationService.Get("StartupNeedsRepair")
                    : status.IsRegistered
                        ? LocalizationService.Get("StartupRegistered")
                        : LocalizationService.Get("StartupMissing");
            return;
        }

        StartupStatusText.Text = status.IsRegistered && !status.IsOwnedByClashTray
            ? LocalizationService.Get("StartupOffForeignItem")
            : LocalizationService.Get("StartupOff");
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
            StatusText.Text = LocalizationService.Format("ErrorPortRangeFormat", name);
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

        if (ReferenceEquals(_providers, snapshot.Providers)
            && ReferenceEquals(_ruleProviders, snapshot.RuleProviders))
        {
            return;
        }

        _providers = snapshot.Providers;
        _ruleProviders = snapshot.RuleProviders;
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
            Content = LocalizationService.Format("ProviderListItemFormat",
                LocalizationService.Get(rules ? "ProviderItemKindRule" : "ProviderItemKindProxy"),
                provider.Name,
                provider.Count,
                provider.UpdatedAt?.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty),
            Tag = (provider, rules)
        };
}



