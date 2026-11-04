using ClashTray.Core;
using ClashTray.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace ClashTray.App;

public sealed partial class NetworkRuleSettingsSection : UserControl
{
    private ClashTrayRuntime _runtime = null!;
    private SettingsPageCommands _commands = null!;
    internal event Action<string?>? StatusChanged;
    private string? Status { set => StatusChanged?.Invoke(value); }
    private bool _savingNetworkSwitch => _commands.NetworkRules.IsBusy;
    private NetworkSwitchRuleSet? _loadedNetworkRules;
    private RuntimeSnapshot? _lastSnapshot;
    private string _configurationIdsSignature = string.Empty;
    private readonly List<NetworkRuleEditorRow> _networkRuleRows = [];
    public NetworkRuleSettingsSection() => InitializeComponent();

    internal void Initialize(ClashTrayRuntime runtime, SettingsPageCommands commands)
    {
        _runtime = runtime;
        _commands = commands;

    }

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
            ContentDialogResult confirmationResult;
            try
            {
                confirmationResult = await confirmation.ShowAsync();
            }
            catch (Exception exception)
            {
                NetworkSwitchEnabledSwitch.IsOn = current.AutomaticSwitchingEnabled;
                NetworkSwitchStateText.Text = ErrorSanitizer.Sanitize(exception);
                return;
            }

            if (confirmationResult != ContentDialogResult.Primary)
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
        SaveNetworkSwitchButton.IsEnabled = false;
        PageCommandResult result = await _commands.SaveNetworkRulesAsync(next);
        if (result.CanPresent)
        {
            if (result.Succeeded) { _loadedNetworkRules = next; }
            NetworkSwitchStateText.Text = result.Succeeded
                ? LocalizationService.Get("NetworkSwitchSaved") : result.Error;
        }
        SaveNetworkSwitchButton.IsEnabled = !_savingNetworkSwitch;
    }

    private async void ResumeNetworkSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        PageCommandResult result = await _commands.ResumeNetworkRulesAsync();
        if (result.CanPresent)
        {
            NetworkSwitchStateText.Text = result.Succeeded
                ? LocalizationService.Get("NetworkSwitchResumeRequested") : result.Error;
        }
    }

    private async void OpenNetworkPermissionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool launched = await Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-location"));
            NetworkSwitchStateText.Text = LocalizationService.Get(
                launched
                    ? "NetworkPermissionSettingsOpened"
                    : "NetworkPermissionSettingsUnavailable");
        }
        catch
        {
            NetworkSwitchStateText.Text = LocalizationService.Get("NetworkPermissionSettingsUnavailable");
        }
    }

    private void AddNetworkRuleButton_Click(object sender, RoutedEventArgs e)
    {
        RuntimeSnapshot snapshot = _lastSnapshot ?? _runtime.Snapshot;
        if (_networkRuleRows.Count >= 128)
        {
            Status = LocalizationService.Get("NetworkSwitchRuleLimit");
            return;
        }

        string targetConfigurationId = snapshot.Configurations.Count > 0
            ? snapshot.Configurations[0].Id
            : _runtime.NetworkSwitchRules.DefaultConfigurationId ?? string.Empty;
        AddNetworkRuleRow(
            new NetworkSwitchRule(Guid.NewGuid().ToString("N"), string.Empty, targetConfigurationId),
            snapshot.Configurations);
    }

    internal void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        if (NetworkSwitchEnabledSwitch is null)
        {
            return;
        }

        _lastSnapshot = snapshot;
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
        OpenNetworkPermissionButton.Visibility = snapshot.NetworkSwitch?.State == NetworkSwitchState.PermissionRequired
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveNetworkSwitchButton.IsEnabled = !_savingNetworkSwitch;
        ResumeNetworkSwitchButton.IsEnabled = !_savingNetworkSwitch
            && snapshot.NetworkSwitch?.LastDecision?.State == NetworkSwitchState.ManualOverride;
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
                Status = LocalizationService.Get("NetworkSwitchSsidRequired");
                rules = [];
                return false;
            }

            string? configurationId = (row.ConfigurationBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (string.IsNullOrWhiteSpace(configurationId))
            {
                Status = LocalizationService.Get("NetworkSwitchTargetRequired");
                rules = [];
                return false;
            }

            bool enabled = row.EnabledBox.IsChecked == true;
            if (enabled && !enabledSsids.Add(ssid))
            {
                Status = LocalizationService.Get("NetworkSwitchDuplicateSsid");
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
            return AppendNetworkSwitchErrorDetail(state, status.ErrorCode);
        }

        string network = status.Context?.CurrentSsid
            ?? LocalizationService.Get("NetworkSwitchNoNetwork");
        return AppendNetworkSwitchErrorDetail(
            LocalizationService.Format("NetworkSwitchStatusFormat", state, network),
            status.ErrorCode);
    }

    private static string AppendNetworkSwitchErrorDetail(string status, ErrorCode errorCode)
    {
        string? detail = errorCode switch
        {
            ErrorCode.NetworkRuleTargetMissing => LocalizationService.Get("NetworkSwitchErrorTargetMissing"),
            ErrorCode.ConfigurationSwitchFailed => LocalizationService.Get("NetworkSwitchErrorExecution"),
            _ => null
        };

        return detail is null
            ? status
            : LocalizationService.Format("NetworkSwitchStatusErrorFormat", status, detail);
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


}