using ClashTray.Core;
using ClashTray.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Globalization;
using Microsoft.UI.Xaml.Media;

namespace ClashTray.App;

public sealed partial class SettingsPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private readonly SettingsDraftController _draft = new();
    private SettingsEditModel _edit => _draft.Edit;
    private readonly SettingsPageCommands _commands;
    private bool _saving => _commands.Settings.IsBusy;
    private bool _updatingNetworkControls;
    private bool _applyingNetworkControl => _commands.Network.IsBusy;
    private bool _controllerWritable;
    private EndpointCapability _controllerCapabilities;

    public SettingsPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _commands = new(runtime);
        InitializeComponent();
        EndpointSection.Initialize(runtime, _commands);
        ProviderSection.Initialize(_commands);
        CoreUpdateSection.Initialize(_commands);
        NetworkRuleSection.Initialize(runtime, _commands);
        EndpointSection.StatusChanged += PresentSectionStatus;
        ProviderSection.StatusChanged += PresentSectionStatus;
        CoreUpdateSection.StatusChanged += PresentSectionStatus;
        NetworkRuleSection.StatusChanged += PresentSectionStatus;
        ProviderSection.BusyChanged += RefreshMaintenanceActions;
        CoreUpdateSection.BusyChanged += RefreshMaintenanceActions;
        RefreshMaintenanceActions();
        Loaded += (_, _) => _commands.Activate();
        Unloaded += (_, _) => _commands.Deactivate();
        LoadSettings(runtime.Settings);
        EndpointSection.UpdateEndpointList(runtime.Endpoints);
        UpdateNetworkConfigurationState(runtime.Snapshot);
    }

    internal void UpdateSnapshot(PanelSnapshotContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RuntimeSnapshot snapshot = context.Pages;
        _controllerWritable = context.ControllerWritable;
        _controllerCapabilities = context.ControllerCapabilities;
        LoadSettings(_runtime.Settings);
        UpdateNetworkConfigurationState(snapshot);
        ProviderSection.UpdateSnapshot(context);
        NetworkRuleSection.UpdateSnapshot(snapshot);
        EndpointSection.UpdateEndpointList(_runtime.Endpoints);
    }

    private void PresentSectionStatus(string? message) => StatusText.Text = message;

    private void RefreshMaintenanceActions()
    {
        ProviderSection.RefreshActions();
        CoreUpdateSection.RefreshActions();
    }

    private bool HasControllerCapability(EndpointCapability capability) =>
        _controllerWritable && (_controllerCapabilities & capability) == capability;

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) { return; }
        CaptureDraft();
        AppSettings current = _runtime.Settings;
        if (!_draft.TryPrepareSave(current, CultureInfo.CurrentCulture, out SettingsDraftSubmission? submission, out SettingsEditValidationError? error))
        {
            StatusText.Text = ValidationMessage(error!);
            return;
        }

        SaveSettingsButton.IsEnabled = false;
        SaveNetworkSettingsButton.IsEnabled = false;
        PageCommandResult result = await _commands.SaveAsync(submission!.Patch);
        CaptureDraft(); // Edits made while saving remain a new draft.
        if (result.Succeeded)
        {
            _draft.AcceptSaved(_runtime.Settings, submission);
            if (result.CanPresent)
            {
                PresentDraft();
                UpdateStartupStatus(_runtime.Settings);
                StatusText.Text = !submission.LanguageChanged
                    ? LocalizationService.Get("SettingsSaved")
                    : $"{LocalizationService.Get("SettingsSaved")} {LocalizationService.Get("LanguageRestartPrompt")}";
            }
        }
        else if (result.CanPresent)
        {
            StatusText.Text = LocalizationService.Format("SettingsSaveFailedFormat", result.Error ?? string.Empty);
        }
        SaveSettingsButton.IsEnabled = !_saving;
        SaveNetworkSettingsButton.IsEnabled = !_saving;
    }

    private static string ValidationMessage(SettingsEditValidationError error)
    {
        if (error.Field == SettingsNumberField.SubscriptionRefreshHours) { return LocalizationService.Get("ErrorInvalidRefreshHours"); }
        string? label = error.Field switch
        {
            SettingsNumberField.HttpPort => "HttpPortLabel",
            SettingsNumberField.SocksPort => "SocksPortLabel",
            SettingsNumberField.MixedPort => "MixedPortLabel",
            SettingsNumberField.ControllerPort => "ControllerPortLabel",
            _ => null
        };
        return label is null
            ? error.Detail ?? LocalizationService.Get("SettingsSaveFailedFormat")
            : LocalizationService.Format("ErrorPortRangeFormat", LocalizationService.Get(label));
    }
    private static ControllerPortConflictPolicy ParseControllerPortConflictPolicy(string? value) =>
        string.Equals(value, "fixed", StringComparison.OrdinalIgnoreCase)
            ? ControllerPortConflictPolicy.Fixed
            : ControllerPortConflictPolicy.AutomaticFallback;

    private async void StartWithAvailableControllerPortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _runtime.Snapshot.Core.State is CoreState.Running or CoreState.Starting or CoreState.Restarting)
        {
            return;
        }

        StartWithAvailableControllerPortButton.IsEnabled = false;
        try
        {
            CoreStartOperationResult result = await _runtime.StartCoreUsingAvailableControllerPortOnceAsync();
            CoreStartFeedback feedback = CoreStartFeedback.Create(result);
            string message = feedback.ConfirmedPort is int port
                ? LocalizationService.Format(feedback.ResourceKey, port)
                : LocalizationService.Get(feedback.ResourceKey);
            StatusText.Text = string.IsNullOrWhiteSpace(feedback.ErrorDetail)
                ? message
                : LocalizationService.Format("ControllerPortOneTimeStartDetailFormat", message, feedback.ErrorDetail);
        }
        catch (Exception exception)
        {
            StatusText.Text = LocalizationService.Format(
                "ControllerPortOneTimeStartFailedFormat",
                ErrorSanitizer.Sanitize(exception));
        }
        finally
        {
            StartWithAvailableControllerPortButton.IsEnabled =
                _runtime.Snapshot.Core.State is not (CoreState.Running or CoreState.Starting or CoreState.Restarting);
        }
    }

    private async void SystemProxySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingNetworkControls || _applyingNetworkControl)
        {
            return;
        }

        bool enabled = SystemProxySwitch.IsOn;
        if (enabled == (_runtime.Snapshot.SystemProxy == SystemProxyState.On))
        {
            return;
        }

        UpdateNetworkConfigurationState(_runtime.Snapshot);
        Task<PageCommandResult> pending = _commands.SetSystemProxyAsync(enabled);
        UpdateNetworkConfigurationState(_runtime.Snapshot);
        PresentNetworkResult(await pending, "SystemProxyChangeApplied");
        UpdateNetworkConfigurationState(_runtime.Snapshot);
    }

    private async void TunSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingNetworkControls || _applyingNetworkControl) { return; }
        bool enabled = TunSwitch.IsOn;
        if (enabled == (_runtime.Snapshot.Tun == TunState.On)) { return; }
        UpdateNetworkConfigurationState(_runtime.Snapshot);
        Task<PageCommandResult> pending = _commands.SetTunAsync(enabled);
        UpdateNetworkConfigurationState(_runtime.Snapshot);
        PresentNetworkResult(await pending, "TunChangeApplied");
        UpdateNetworkConfigurationState(_runtime.Snapshot);
    }

    private void PresentNetworkResult(PageCommandResult result, string successKey)
    {
        if (!result.CanPresent) { return; }
        StatusText.Text = result.Succeeded
            ? LocalizationService.Get(successKey)
            : LocalizationService.Format("NetworkControlChangeFailedFormat", result.Error ?? string.Empty);
    }

    private void LoadSettings(AppSettings settings)
    {
        if (StartWithWindowsSwitch is null) { return; }
        UpdateStartupStatus(settings);
        if (_edit.IsLoaded)
        {
            CaptureDraft();
            _edit.MergeExternal(settings);
        }
        else { _edit.Load(settings); }
        PresentDraft();
    }

    private void CaptureDraft()
    {
        if (!_edit.IsLoaded) { return; }
        AppSettings values = _edit.Values with
        {
            StartWithWindows = StartWithWindowsSwitch.IsOn,
            StartCoreAutomatically = StartCoreSwitch.IsOn,
            AllowLan = AllowLanSwitch.IsOn,
            Ipv6 = Ipv6Switch.IsOn,
            TcpConcurrent = TcpConcurrentSwitch.IsOn,
            DisconnectConnectionsAfterProxySwitch = DisconnectAfterProxySwitch.IsOn,
            ControllerPortConflictPolicy = ParseControllerPortConflictPolicy((ControllerPortPolicyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()),
            LogLevel = (LogLevelBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? _edit.Values.LogLevel,
            Theme = (ThemeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? _edit.Values.Theme,
            Language = (LanguageBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? _edit.Values.Language,
            TunStack = (TunStackBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? _edit.Values.TunStack,
            BypassList = BypassListBox.Text.Trim()
        };
        _edit.Capture(values, NumberControls().ToDictionary(pair => pair.Field, pair => ReadNumberInput(pair.Control)));
    }

    private IEnumerable<(SettingsNumberField Field, NumberBox Control)> NumberControls()
    {
        yield return (SettingsNumberField.HttpPort, HttpPortBox);
        yield return (SettingsNumberField.SocksPort, SocksPortBox);
        yield return (SettingsNumberField.MixedPort, MixedPortBox);
        yield return (SettingsNumberField.ControllerPort, ControllerPortBox);
        yield return (SettingsNumberField.SubscriptionRefreshHours, SubscriptionRefreshHoursBox);
    }

    private static SettingsNumberInput ReadNumberInput(NumberBox control)
    {
        // NumberBox.Value may lag the focused input. Read the visible template
        // TextBox before merging snapshots or saving; never force a focus change.
        string text = FindNumberTextBox(control)?.Text ?? control.Text;
        bool pending = !double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out double parsed)
            || parsed != control.Value;
        return new(control.Value, text, pending);
    }

    private static TextBox? FindNumberTextBox(DependencyObject parent, int depth = 0)
    {
        if (parent is TextBox textBox) { return textBox; }
        if (depth >= 16) { return null; }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            TextBox? found = FindNumberTextBox(VisualTreeHelper.GetChild(parent, index), depth + 1);
            if (found is not null) { return found; }
        }
        return null;
    }

    private void PresentDraft()
    {
        AppSettings settings = _edit.Values;
        SetSwitch(StartWithWindowsSwitch, settings.StartWithWindows);
        SetSwitch(StartCoreSwitch, settings.StartCoreAutomatically);
        SetSwitch(AllowLanSwitch, settings.AllowLan);
        SetSwitch(Ipv6Switch, settings.Ipv6);
        SetSwitch(TcpConcurrentSwitch, settings.TcpConcurrent);
        SetSwitch(DisconnectAfterProxySwitch, settings.DisconnectConnectionsAfterProxySwitch);
        foreach ((SettingsNumberField field, NumberBox control) in NumberControls())
        {
            SettingsNumberDraft number = _edit.Numbers[field];
            // Reassigning Value can destroy pending/invalid text and selection.
            if (!number.IsModified && control.Value != number.Input.Value) { control.Value = number.Input.Value; }
        }
        SetChoice(ControllerPortPolicyBox, settings.ControllerPortConflictPolicy == ControllerPortConflictPolicy.Fixed ? "fixed" : "automatic");
        if (BypassListBox.Text.Trim() != settings.BypassList) { BypassListBox.Text = settings.BypassList; }
        SetChoice(TunStackBox, settings.TunStack);
        SetChoice(LogLevelBox, settings.LogLevel);
        SetChoice(LanguageBox, settings.Language);
        ComboBoxItem? hiddenTheme = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == "nakhimov");
        if (settings.NakhimovUnlocked && hiddenTheme is null) { ThemeBox.Items.Add(new ComboBoxItem { Content = "Nakhimov", Tag = "nakhimov" }); }
        else if (!settings.NakhimovUnlocked && hiddenTheme is not null) { ThemeBox.Items.Remove(hiddenTheme); }
        SetChoice(ThemeBox, settings.Theme);
    }

    private static void SetSwitch(ToggleSwitch control, bool value)
    {
        if (control.IsOn != value) { control.IsOn = value; }
    }

    private static void SetChoice(ComboBox control, string value)
    {
        if ((control.SelectedItem as ComboBoxItem)?.Tag?.ToString() != value)
        {
            control.SelectedItem = control.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == value)
                ?? control.Items.FirstOrDefault();
        }
    }
    private void UpdateNetworkConfigurationState(RuntimeSnapshot snapshot)
    {
        if (SystemProxySwitch is null || TunSwitch is null)
        {
            return;
        }

        bool coreRunning = snapshot.Core.State == CoreState.Running;
        ActiveControllerAddressText.Text = _runtime.ActiveRuntimeBinding is { ControllerReady: true } binding
            ? LocalizationService.Format("ControllerActualAddressFormat", binding.ControllerPort)
            : LocalizationService.Get("ControllerActualAddressNotConfirmed");
        bool canControlSystemProxy = HasControllerCapability(EndpointCapability.ControlSystemProxy);
        bool canControlTun = HasControllerCapability(EndpointCapability.ControlTun);
        _updatingNetworkControls = true;
        try
        {
            SettingsNetworkPresentation presentation = SettingsNetworkPresentation.Create(
                snapshot, _saving, _applyingNetworkControl, canControlSystemProxy, canControlTun);
            StartWithAvailableControllerPortButton.IsEnabled = presentation.CanStartWithAvailableControllerPort;
            SystemProxyStateText.Text = LocalizationService.Get(presentation.SystemProxy.StateKey);
            SystemProxySwitch.IsOn = presentation.SystemProxy.IsOn;
            SystemProxySwitch.IsEnabled = presentation.SystemProxy.IsEnabled;
            TunStateText.Text = LocalizationService.Get(presentation.Tun.StateKey);
            TunSwitch.IsOn = presentation.Tun.IsOn;
            TunSwitch.IsEnabled = presentation.Tun.IsEnabled;
        }
        finally
        {
            _updatingNetworkControls = false;
        }

        ToolTipService.SetToolTip(
            SystemProxySwitch,
            GetNetworkControlUnavailableReason(coreRunning, canControlSystemProxy));
        ToolTipService.SetToolTip(
            TunSwitch,
            GetNetworkControlUnavailableReason(coreRunning, canControlTun));
    }

    private static string? GetNetworkControlUnavailableReason(bool coreRunning, bool hasCapability)
    {
        if (!hasCapability)
        {
            return LocalizationService.Get("RemoteLocalNetworkActionUnavailable");
        }

        return coreRunning
            ? null
            : LocalizationService.Get("NetworkControlNeedsCore");
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

}
