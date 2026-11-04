using ClashTray.Core;
using ClashTray.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class ProviderSettingsSection : UserControl
{
    private SettingsPageCommands _commands = null!;
    internal event Action<string?>? StatusChanged;
    private string? Status { set => StatusChanged?.Invoke(value); }
    private IReadOnlyList<ProviderStatus>? _providers;
    private IReadOnlyList<ProviderStatus>? _ruleProviders;
    private bool _controllerWritable;
    private EndpointCapability _controllerCapabilities;
    private EndpointCommandTarget? _expectedCommandTarget;
    private string? _controllerIdentity;
    public ProviderSettingsSection() => InitializeComponent();

    internal void Initialize(SettingsPageCommands commands)
    {
        _commands = commands;
    }

    internal event Action? BusyChanged;

    internal void UpdateSnapshot(PanelSnapshotContext context)
    {
        _controllerWritable = context.ControllerWritable;
        _controllerCapabilities = context.ControllerCapabilities;
        _expectedCommandTarget = context.CommandTarget;
        if (_controllerIdentity != context.ControllerIdentity)
        {
            ProvidersListView.SelectedItem = null;
            _providers = null;
            _ruleProviders = null;
            _controllerIdentity = context.ControllerIdentity;
        }
        UpdateProviders(context.Pages);
        RefreshActions();
    }
    private async void ClearFakeIpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasControllerCapability(EndpointCapability.ClearCache)) { return; }
        EndpointCommandTarget? target = _expectedCommandTarget;
        await PresentMaintenanceAsync(() => _commands.ClearFakeIpAsync(target), LocalizationService.Get("FakeIpCleared"));
    }

    private async void ClearDnsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasControllerCapability(EndpointCapability.ClearCache)) { return; }
        EndpointCommandTarget? target = _expectedCommandTarget;
        await PresentMaintenanceAsync(() => _commands.ClearDnsAsync(target), LocalizationService.Get("DnsCleared"));
    }

    private async void UpdateGeoButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasControllerCapability(EndpointCapability.UpdateGeo)) { return; }
        EndpointCommandTarget? target = _expectedCommandTarget;
        await PresentMaintenanceAsync(() => _commands.UpdateGeoAsync(target), LocalizationService.Get("GeoUpdateSent"));
    }

    private async void RefreshProviderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasControllerCapability(EndpointCapability.RefreshProvider)) { return; }
        if (ProvidersListView.SelectedItem is not ProviderSettingsRow selected)
        {
            Status = LocalizationService.Get("SelectProviderFirst");
            return;
        }
        EndpointCommandTarget? target = _expectedCommandTarget;
        await PresentMaintenanceAsync(
            () => _commands.RefreshProviderAsync(selected.Provider.Name, selected.IsRuleProvider, target),
            LocalizationService.Format("ProviderRefreshRequestedFormat", selected.Provider.Name));
    }

    private async Task PresentMaintenanceAsync(Func<Task<PageCommandResult>> command, string success)
    {
        Task<PageCommandResult> pending = command();
        BusyChanged?.Invoke();
        RefreshActions();
        PageCommandResult result = await pending;
        BusyChanged?.Invoke();
        RefreshActions();
        if (result.CanPresent) { Status = result.Succeeded ? success : result.Error; }
    }
    private void ProvidersListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshActions();
        if (ProvidersListView.SelectedItem is ProviderSettingsRow selected)
        {
            Status = ErrorSanitizer.Sanitize(
                selected.Provider.Error ?? LocalizationService.Format("ProviderStatusSummaryFormat", selected.Provider.Name, selected.Provider.Count));
        }
    }

    internal void RefreshActions()
    {
        if (ClearDnsButton is null)
        {
            return;
        }

        bool canClearCache = HasControllerCapability(EndpointCapability.ClearCache);
        bool canUpdateGeo = HasControllerCapability(EndpointCapability.UpdateGeo);
        ClearDnsButton.IsEnabled = canClearCache && !_commands.Maintenance.IsBusy;
        ClearFakeIpButton.IsEnabled = canClearCache && !_commands.Maintenance.IsBusy;
        UpdateGeoButton.IsEnabled = canUpdateGeo && !_commands.Maintenance.IsBusy;
        bool providerSelected = ProvidersListView.SelectedItem is ProviderSettingsRow;
        bool canRefreshProvider = HasControllerCapability(EndpointCapability.RefreshProvider);
        RefreshProviderButton.IsEnabled = canRefreshProvider && providerSelected && !_commands.Maintenance.IsBusy;
        string? cacheTooltip = canClearCache
            ? null
            : !_controllerWritable
                ? LocalizationService.Get("RemoteControllerReadOnly")
                : LocalizationService.Get("RemoteControllerCapabilityUnavailable");
        string? geoTooltip = canUpdateGeo
            ? null
            : !_controllerWritable
                ? LocalizationService.Get("RemoteControllerReadOnly")
                : LocalizationService.Get("RemoteControllerCapabilityUnavailable");
        string? providerTooltip = canRefreshProvider
            ? null
            : !_controllerWritable
                ? LocalizationService.Get("RemoteControllerReadOnly")
                : LocalizationService.Get("RemoteControllerCapabilityUnavailable");
        ToolTipService.SetToolTip(ClearDnsButton, cacheTooltip);
        ToolTipService.SetToolTip(ClearFakeIpButton, cacheTooltip);
        ToolTipService.SetToolTip(UpdateGeoButton, geoTooltip);
        ToolTipService.SetToolTip(RefreshProviderButton, providerTooltip);
    }

    private bool HasControllerCapability(EndpointCapability capability) =>
        _controllerWritable && (_controllerCapabilities & capability) == capability;

    private void UpdateProviders(RuntimeSnapshot snapshot)
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
        ProviderSettingsRow? selected = ProvidersListView.SelectedItem as ProviderSettingsRow;
        ProvidersListView.Items.Clear();
        foreach (ProviderStatus provider in snapshot.Providers)
        {
            ProvidersListView.Items.Add(new ProviderSettingsRow(provider, false));
        }

        foreach (ProviderStatus provider in snapshot.RuleProviders)
        {
            ProvidersListView.Items.Add(new ProviderSettingsRow(provider, true));
        }

        ProviderSettingsRow? restored = ProvidersListView.Items.OfType<ProviderSettingsRow>().FirstOrDefault(item =>
            item.Provider.Name == selected?.Provider.Name && item.IsRuleProvider == selected?.IsRuleProvider);
        ProvidersListView.SelectedItem = restored;
        RefreshActions();
    }
}