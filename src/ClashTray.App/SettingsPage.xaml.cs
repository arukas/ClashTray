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
    private bool _savingNetworkSwitch;
    private IReadOnlyList<ProviderStatus>? _providers;
    private IReadOnlyList<ProviderStatus>? _ruleProviders;
    private NetworkSwitchRuleSet? _loadedNetworkRules;
    private RuntimeSnapshot? _lastSnapshot;
    private string _configurationIdsSignature = string.Empty;
    private string _endpointSignature = string.Empty;
    private bool _updatingEndpointControls;
    private readonly List<NetworkRuleEditorRow> _networkRuleRows = [];

    public SettingsPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        InitializeComponent();
        LoadSettings(runtime.Settings);
        UpdateEndpointList(runtime.Endpoints);
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _lastSnapshot = snapshot;
        LoadSettings(_runtime.Settings);
        UpdateProviders(snapshot);
        UpdateNetworkSwitch(snapshot);
        UpdateEndpointList(_runtime.Endpoints);
    }

    private async void SaveEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EndpointNameBox.Text)
            || string.IsNullOrWhiteSpace(EndpointUriBox.Text))
        {
            StatusText.Text = LocalizationService.Get("EndpointFieldsRequired");
            return;
        }

        bool explicitHttp = string.Equals(
            (EndpointTransportBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            "http-explicit",
            StringComparison.Ordinal);
        if (explicitHttp && EndpointHttpRiskCheckBox.IsChecked != true)
        {
            StatusText.Text = LocalizationService.Get("EndpointHttpRiskRequired");
            return;
        }

        if (!Uri.TryCreate(EndpointUriBox.Text.Trim(), UriKind.Absolute, out Uri? endpointUri)
            || endpointUri is null)
        {
            StatusText.Text = LocalizationService.Get("EndpointUriInvalid");
            return;
        }

        try
        {
            EndpointDescriptor descriptor = EndpointUriNormalizer.CreateRemoteDescriptor(
                new EndpointId($"remote-{Guid.NewGuid():N}"),
                EndpointNameBox.Text.Trim(),
                endpointUri,
                allowExplicitHttp: explicitHttp);
            EndpointRecord record = new(
                descriptor,
                InsecureHttpAcknowledgedAtUtc: explicitHttp ? DateTimeOffset.UtcNow : null);
            await _runtime.SaveRemoteEndpointAsync(record);
            UpdateEndpointList(_runtime.Endpoints);
            ClearEndpointEditor();
            StatusText.Text = LocalizationService.Get("EndpointSaved");
        }
        catch (Exception exception)
        {
            StatusText.Text = ErrorSanitizer.Sanitize(exception);
        }
    }

    private async void RemoveEndpointButton_Click(object sender, RoutedEventArgs e)
    {
        if (EndpointListView.SelectedItem is not ListViewItem { Tag: EndpointDescriptor endpoint }
            || endpoint.Kind != EndpointKind.Remote)
        {
            StatusText.Text = LocalizationService.Get("EndpointSelectRemote");
            return;
        }

        try
        {
            EndpointRemovalResult result = await _runtime.RemoveRemoteEndpointAsync(endpoint.Id);
            UpdateEndpointList(_runtime.Endpoints);
            ClearEndpointEditor();
            StatusText.Text = result.Removed
                ? LocalizationService.Get("EndpointRemoved")
                : LocalizationService.Get("EndpointNotFound");
        }
        catch (Exception exception)
        {
            StatusText.Text = ErrorSanitizer.Sanitize(exception);
        }
    }

    private void EndpointListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEndpointControls)
        {
            return;
        }

        if (EndpointListView.SelectedItem is ListViewItem { Tag: EndpointDescriptor endpoint }
            && endpoint.Kind == EndpointKind.Remote)
        {
            EndpointNameBox.Text = endpoint.DisplayName;
            EndpointUriBox.Text = endpoint.BaseUri.AbsoluteUri.TrimEnd('/');
            EndpointTransportBox.SelectedItem = EndpointTransportBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    endpoint.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed
                        ? "http-explicit"
                        : "https-system",
                    StringComparison.Ordinal));
            EndpointHttpRiskCheckBox.IsChecked = endpoint.Security == EndpointTransportSecurity.HttpExplicitlyConfirmed;
        }

        UpdateEndpointRemoveButton();
    }

    private void EndpointTransportBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EndpointHttpRiskCheckBox is null)
        {
            return;
        }

        bool explicitHttp = string.Equals(
            (EndpointTransportBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            "http-explicit",
            StringComparison.Ordinal);
        EndpointHttpRiskCheckBox.IsEnabled = explicitHttp;
        if (!explicitHttp)
        {
            EndpointHttpRiskCheckBox.IsChecked = false;
        }
    }

    private void UpdateEndpointList(IReadOnlyList<EndpointDescriptor> endpoints)
    {
        if (EndpointListView is null)
        {
            return;
        }

        string signature = string.Join(
            '\u001F',
            endpoints.Select(endpoint => $"{endpoint.Id.Value}\u001E{endpoint.DisplayName}\u001E{endpoint.BaseUri.AbsoluteUri}\u001E{endpoint.Security}\u001E{endpoint.IsEnabled}"));
        if (string.Equals(_endpointSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        EndpointId? selectedId = (EndpointListView.SelectedItem as ListViewItem)?.Tag is EndpointDescriptor selected
            ? selected.Id
            : null;
        _updatingEndpointControls = true;
        try
        {
            EndpointListView.Items.Clear();
            foreach (EndpointDescriptor endpoint in endpoints)
            {
                StackPanel content = new() { Spacing = 1 };
                content.Children.Add(new TextBlock
                {
                    Text = endpoint.DisplayName,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                });
                content.Children.Add(new TextBlock
                {
                    Text = endpoint.Kind == EndpointKind.Local
                        ? LocalizationService.Get("EndpointLocalSummary")
                        : $"{endpoint.BaseUri.AbsoluteUri} · {FormatEndpointSecurity(endpoint.Security)}",
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });
                EndpointListView.Items.Add(new ListViewItem
                {
                    Content = content,
                    Tag = endpoint,
                    IsEnabled = endpoint.Kind == EndpointKind.Remote
                });
            }

            if (selectedId is EndpointId previousId)
            {
                EndpointListView.SelectedItem = EndpointListView.Items
                    .OfType<ListViewItem>()
                    .FirstOrDefault(item => item.Tag is EndpointDescriptor endpoint && endpoint.Id == previousId);
            }
        }
        finally
        {
            _updatingEndpointControls = false;
        }

        _endpointSignature = signature;
        UpdateEndpointRemoveButton();
    }

    private void ClearEndpointEditor()
    {
        _updatingEndpointControls = true;
        try
        {
            EndpointListView.SelectedIndex = -1;
            EndpointNameBox.Text = string.Empty;
            EndpointUriBox.Text = string.Empty;
            EndpointTransportBox.SelectedIndex = 0;
            EndpointHttpRiskCheckBox.IsChecked = false;
        }
        finally
        {
            _updatingEndpointControls = false;
        }

        RemoveEndpointButton.IsEnabled = false;
    }

    private void UpdateEndpointRemoveButton()
    {
        RemoveEndpointButton.IsEnabled = EndpointListView.SelectedItem is ListViewItem
        {
            Tag: EndpointDescriptor { Kind: EndpointKind.Remote }
        };
    }

    private static string FormatEndpointSecurity(EndpointTransportSecurity security) => security switch
    {
        EndpointTransportSecurity.HttpsSystemTrust => LocalizationService.Get("EndpointSecurityHttpsSystemTrust"),
        EndpointTransportSecurity.HttpsCustomCertificate => LocalizationService.Get("EndpointSecurityHttpsCustomCa"),
        EndpointTransportSecurity.HttpExplicitlyConfirmed => LocalizationService.Get("EndpointSecurityHttpConfirmed"),
        _ => LocalizationService.Get("EndpointSecurityLoopback")
    };

    private async void SaveNetworkSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_savingNetworkSwitch)
        {
            return;
        }

        NetworkSwitchRuleSet current = _runtime.NetworkSwitchRules;
        bool enabled = NetworkSwitchEnabledSwitch.IsOn;
        if (enabled && !current.AutomaticSwitchingEnabled)
        {
            ContentDialog confirmation = new()
            {
                XamlRoot = XamlRoot,
                Title = LocalizationService.Get("NetworkSwitchEnableConfirmTitle"),
                Content = LocalizationService.Get("NetworkSwitchEnableConfirmContent"),
                PrimaryButtonText = LocalizationService.Get("NetworkSwitchEnableConfirmPrimary"),
                CloseButtonText = LocalizationService.Get("DialogCancel")
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
            {
                NetworkSwitchEnabledSwitch.IsOn = current.AutomaticSwitchingEnabled;
                return;
            }
        }

        if (!TryReadNetworkRules(out IReadOnlyList<NetworkSwitchRule> editedRules))
        {
            return;
        }

        string? defaultConfigurationId = (NetworkSwitchDefaultConfigurationBox.SelectedItem as ComboBoxItem)?.Tag as string;
        NetworkSwitchRuleSet next = new(enabled, defaultConfigurationId, editedRules);
        _savingNetworkSwitch = true;
        SaveNetworkSwitchButton.IsEnabled = false;
        try
        {
            await _runtime.UpdateNetworkSwitchRulesAsync(next);
            _loadedNetworkRules = next;
            NetworkSwitchStateText.Text = LocalizationService.Get("NetworkSwitchSaved");
        }
        catch (Exception exception)
        {
            NetworkSwitchStateText.Text = ErrorSanitizer.Sanitize(exception);
        }
        finally
        {
            _savingNetworkSwitch = false;
            SaveNetworkSwitchButton.IsEnabled = true;
        }
    }

    private void ResumeNetworkSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _runtime.ClearNetworkSwitchManualOverride();
            NetworkSwitchStateText.Text = LocalizationService.Get("NetworkSwitchResumeRequested");
        }
        catch (Exception exception)
        {
            NetworkSwitchStateText.Text = ErrorSanitizer.Sanitize(exception);
        }
    }

    private void AddNetworkRuleButton_Click(object sender, RoutedEventArgs e)
    {
        RuntimeSnapshot snapshot = _lastSnapshot ?? _runtime.Snapshot;
        if (_networkRuleRows.Count >= 128)
        {
            StatusText.Text = LocalizationService.Get("NetworkSwitchRuleLimit");
            return;
        }

        string targetConfigurationId = snapshot.Configurations.Count > 0
            ? snapshot.Configurations[0].Id
            : _runtime.NetworkSwitchRules.DefaultConfigurationId ?? string.Empty;
        AddNetworkRuleRow(
            new NetworkSwitchRule(Guid.NewGuid().ToString("N"), string.Empty, targetConfigurationId),
            snapshot.Configurations);
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

    private void UpdateNetworkSwitch(RuntimeSnapshot snapshot)
    {
        if (NetworkSwitchEnabledSwitch is null)
        {
            return;
        }

        NetworkSwitchRuleSet rules = _runtime.NetworkSwitchRules;
        if (_loadedNetworkRules is null || !NetworkSwitchRulesEqual(_loadedNetworkRules, rules))
        {
            NetworkSwitchEnabledSwitch.IsOn = rules.AutomaticSwitchingEnabled;
            PopulateDefaultConfigurations(snapshot.Configurations, rules.DefaultConfigurationId);
            RenderNetworkRules(snapshot.Configurations, rules.Rules);
            _loadedNetworkRules = rules;
        }

        string configurationIdsSignature = string.Join(
            '\u001F',
            snapshot.Configurations.Select(configuration => configuration.Id));
        if (!string.Equals(_configurationIdsSignature, configurationIdsSignature, StringComparison.Ordinal))
        {
            string? selectedDefault = (NetworkSwitchDefaultConfigurationBox.SelectedItem as ComboBoxItem)?.Tag as string;
            PopulateDefaultConfigurations(snapshot.Configurations, selectedDefault ?? rules.DefaultConfigurationId);
            foreach (NetworkRuleEditorRow row in _networkRuleRows)
            {
                string? selectedTarget = (row.ConfigurationBox.SelectedItem as ComboBoxItem)?.Tag as string;
                PopulateRuleConfigurationBox(row.ConfigurationBox, snapshot.Configurations, selectedTarget);
            }

            _configurationIdsSignature = configurationIdsSignature;
        }

        NetworkSwitchStateText.Text = FormatNetworkSwitchStatus(snapshot.NetworkSwitch);
        ResumeNetworkSwitchButton.IsEnabled = snapshot.NetworkSwitch?.LastDecision?.State == NetworkSwitchState.ManualOverride;
    }

    private void PopulateDefaultConfigurations(
        IReadOnlyList<ConfigurationProfile> configurations,
        string? selectedConfigurationId)
    {
        NetworkSwitchDefaultConfigurationBox.Items.Clear();
        NetworkSwitchDefaultConfigurationBox.Items.Add(new ComboBoxItem
        {
            Content = LocalizationService.Get("NetworkSwitchNoDefault")
        });

        foreach (ConfigurationProfile configuration in configurations)
        {
            NetworkSwitchDefaultConfigurationBox.Items.Add(new ComboBoxItem
            {
                Content = configuration.Name,
                Tag = configuration.Id
            });
        }

        if (!string.IsNullOrWhiteSpace(selectedConfigurationId)
            && !configurations.Any(configuration =>
                string.Equals(configuration.Id, selectedConfigurationId, StringComparison.OrdinalIgnoreCase)))
        {
            NetworkSwitchDefaultConfigurationBox.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Format("NetworkSwitchMissingConfigurationFormat", selectedConfigurationId),
                Tag = selectedConfigurationId
            });
        }

        NetworkSwitchDefaultConfigurationBox.SelectedItem = NetworkSwitchDefaultConfigurationBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, selectedConfigurationId, StringComparison.OrdinalIgnoreCase))
            ?? NetworkSwitchDefaultConfigurationBox.Items.FirstOrDefault();
    }

    private void RenderNetworkRules(
        IReadOnlyList<ConfigurationProfile> configurations,
        IReadOnlyList<NetworkSwitchRule> rules)
    {
        NetworkSwitchRulesListView.Items.Clear();
        _networkRuleRows.Clear();
        foreach (NetworkSwitchRule rule in rules)
        {
            AddNetworkRuleRow(rule, configurations);
        }
    }

    private NetworkRuleEditorRow AddNetworkRuleRow(
        NetworkSwitchRule rule,
        IReadOnlyList<ConfigurationProfile> configurations)
    {
        TextBox ssidBox = new()
        {
            Header = LocalizationService.Get("NetworkSwitchSsidHeader"),
            PlaceholderText = LocalizationService.Get("NetworkSwitchSsidPlaceholder"),
            Text = rule.Ssid,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ComboBox configurationBox = new()
        {
            Header = LocalizationService.Get("NetworkSwitchTargetHeader"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        PopulateRuleConfigurationBox(configurationBox, configurations, rule.ConfigurationId);
        CheckBox enabledBox = new()
        {
            Content = LocalizationService.Get("NetworkSwitchRuleEnabled"),
            IsChecked = rule.Enabled
        };
        Button removeButton = new()
        {
            Content = LocalizationService.Get("NetworkSwitchRemoveRule"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Grid fields = new() { ColumnSpacing = 6 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fields.Children.Add(ssidBox);
        fields.Children.Add(configurationBox);
        fields.Children.Add(removeButton);
        Grid.SetColumn(configurationBox, 1);
        Grid.SetColumn(removeButton, 2);

        StackPanel content = new() { Spacing = 4 };
        content.Children.Add(fields);
        content.Children.Add(enabledBox);
        NetworkRuleEditorRow editorRow = new(rule.RuleId, ssidBox, configurationBox, enabledBox);
        ListViewItem item = new() { Content = content, Tag = editorRow };
        editorRow.Item = item;
        removeButton.Click += (_, _) => RemoveNetworkRuleRow(editorRow);
        _networkRuleRows.Add(editorRow);
        NetworkSwitchRulesListView.Items.Add(item);
        return editorRow;
    }

    private static void PopulateRuleConfigurationBox(
        ComboBox box,
        IReadOnlyList<ConfigurationProfile> configurations,
        string? selectedConfigurationId)
    {
        box.Items.Clear();
        foreach (ConfigurationProfile configuration in configurations)
        {
            box.Items.Add(new ComboBoxItem
            {
                Content = configuration.Name,
                Tag = configuration.Id
            });
        }

        if (!string.IsNullOrWhiteSpace(selectedConfigurationId)
            && !configurations.Any(configuration =>
                string.Equals(configuration.Id, selectedConfigurationId, StringComparison.OrdinalIgnoreCase)))
        {
            box.Items.Add(new ComboBoxItem
            {
                Content = LocalizationService.Format("NetworkSwitchMissingConfigurationFormat", selectedConfigurationId),
                Tag = selectedConfigurationId
            });
        }

        box.SelectedItem = box.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, selectedConfigurationId, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveNetworkRuleRow(NetworkRuleEditorRow row)
    {
        if (row.Item is not null)
        {
            NetworkSwitchRulesListView.Items.Remove(row.Item);
        }

        _networkRuleRows.Remove(row);
    }

    private bool TryReadNetworkRules(out IReadOnlyList<NetworkSwitchRule> rules)
    {
        List<NetworkSwitchRule> editedRules = [];
        HashSet<string> enabledSsids = new(StringComparer.Ordinal);
        foreach (NetworkRuleEditorRow row in _networkRuleRows)
        {
            string ssid = row.SsidBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(ssid))
            {
                StatusText.Text = LocalizationService.Get("NetworkSwitchSsidRequired");
                rules = [];
                return false;
            }

            string? configurationId = (row.ConfigurationBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(configurationId))
            {
                StatusText.Text = LocalizationService.Get("NetworkSwitchTargetRequired");
                rules = [];
                return false;
            }

            bool enabled = row.EnabledBox.IsChecked == true;
            if (enabled && !enabledSsids.Add(ssid))
            {
                StatusText.Text = LocalizationService.Get("NetworkSwitchDuplicateSsid");
                rules = [];
                return false;
            }

            editedRules.Add(new NetworkSwitchRule(row.RuleId, ssid, configurationId, enabled));
        }

        rules = editedRules;
        return true;
    }

    private static bool NetworkSwitchRulesEqual(NetworkSwitchRuleSet left, NetworkSwitchRuleSet right)
    {
        if (left.AutomaticSwitchingEnabled != right.AutomaticSwitchingEnabled
            || !string.Equals(left.DefaultConfigurationId, right.DefaultConfigurationId, StringComparison.OrdinalIgnoreCase)
            || left.Rules.Count != right.Rules.Count)
        {
            return false;
        }

        return left.Rules.SequenceEqual(right.Rules);
    }

    private static string FormatNetworkSwitchStatus(NetworkSwitchStatus? status)
    {
        if (status is null)
        {
            return LocalizationService.Get("NetworkSwitchUnavailable");
        }

        string state = status.State switch
        {
            NetworkSwitchState.Disabled => LocalizationService.Get("NetworkSwitchStateDisabled"),
            NetworkSwitchState.WaitingForNetwork => LocalizationService.Get("NetworkSwitchStateWaiting"),
            NetworkSwitchState.PermissionRequired => LocalizationService.Get("NetworkSwitchStatePermission"),
            NetworkSwitchState.AmbiguousNetwork => LocalizationService.Get("NetworkSwitchStateAmbiguous"),
            NetworkSwitchState.Evaluating => LocalizationService.Get("NetworkSwitchStateEvaluating"),
            NetworkSwitchState.Switching => LocalizationService.Get("NetworkSwitchStateSwitching"),
            NetworkSwitchState.ManualOverride => LocalizationService.Get("NetworkSwitchStateManual"),
            NetworkSwitchState.CoolingDown => LocalizationService.Get("NetworkSwitchStateCooling"),
            NetworkSwitchState.Failed => LocalizationService.Get("NetworkSwitchStateFailed"),
            _ => LocalizationService.Get("NetworkSwitchUnavailable")
        };
        if (!status.Available)
        {
            return state;
        }

        string network = status.Context?.CurrentSsid
            ?? LocalizationService.Get("NetworkSwitchNoNetwork");
        return LocalizationService.Format("NetworkSwitchStatusFormat", state, network);
    }

    private sealed class NetworkRuleEditorRow(
        string ruleId,
        TextBox ssidBox,
        ComboBox configurationBox,
        CheckBox enabledBox)
    {
        public string RuleId { get; } = ruleId;

        public TextBox SsidBox { get; } = ssidBox;

        public ComboBox ConfigurationBox { get; } = configurationBox;

        public CheckBox EnabledBox { get; } = enabledBox;

        public ListViewItem? Item { get; set; }
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



