using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class RulesPage : UserControl, IDisposable
{
    private readonly ClashTrayRuntime _runtime;
    private IReadOnlyList<RuleInfo> _rules = [];
    private ControllerListSummary? _listSummary;
    private readonly DebouncedAction _searchDebounce;
    private IReadOnlyList<(RuleInfo Rule, string SearchText, string DisplayText)> _ruleRows = [];
    private bool _refreshing;
    private bool _controllerWritable = true;
    private EndpointCapability _controllerCapabilities = EndpointCapabilityDefaults.Local;

    public RulesPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _searchDebounce = new(action => DispatcherQueue.TryEnqueue(() => action()), ApplyFilter);
        Unloaded += (_, _) => _searchDebounce.Cancel();
        Loaded += (_, _) => ApplyFilter();
        InitializeComponent();
    }

    public void Dispose() { _searchDebounce.Dispose(); GC.SuppressFinalize(this); }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        UpdateSnapshot(snapshot, controllerWritable: true, EndpointCapabilityDefaults.Local);
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot, bool controllerWritable)
    {
        UpdateSnapshot(
            snapshot,
            controllerWritable,
            controllerWritable ? EndpointCapabilityDefaults.Local : EndpointCapability.None);
    }

    public void UpdateSnapshot(
        RuntimeSnapshot snapshot,
        bool controllerWritable,
        EndpointCapability capabilities)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool interactivityChanged = _controllerWritable != controllerWritable
            || _controllerCapabilities != capabilities;
        _controllerWritable = controllerWritable;
        _controllerCapabilities = capabilities;
        RefreshRulesButton.IsEnabled = CanRefreshRules && !_refreshing;
        ToolTipService.SetToolTip(
            RefreshRulesButton,
            GetRefreshTooltip());
        if (ReferenceEquals(_rules, snapshot.Rules) && Equals(_listSummary, snapshot.RulesSummary) && !interactivityChanged)
        {
            return;
        }

        _rules = snapshot.Rules;
        _ruleRows = _rules.Select(rule => (rule, $"{rule.Type} {rule.Payload} {rule.Proxy}", $"{rule.Type}  {rule.Payload}  → {rule.Proxy}")).ToArray();
        _listSummary = snapshot.RulesSummary;
        if (!_searchDebounce.IsPending) { ApplyFilter(); }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _searchDebounce.Schedule();

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { _searchDebounce.Cancel(); ApplyFilter(); }

    private async void RefreshRulesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || !CanRefreshRules)
        {
            return;
        }

        _refreshing = true;
        RefreshRulesButton.IsEnabled = false;
        try
        {
            await _runtime.RefreshDataAsync();
            StatusText.Text = LocalizationService.Get("RulesRefreshed");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = LocalizationService.Format("RefreshFailedFormat", ErrorSanitizer.Sanitize(exception));
        }
        finally
        {
            _refreshing = false;
            RefreshRulesButton.IsEnabled = CanRefreshRules;
        }
    }

    private void ApplyFilter()
    {
        if (RulesListView is null)
        {
            return;
        }

        string search = SearchBox.Text.Trim();
        string? filter = (FilterBox.SelectedItem as ComboBoxItem)?.Tag as string;
        RulesListView.ItemsSource = _ruleRows.Where(row =>
                     (string.IsNullOrWhiteSpace(search) || row.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase))
                     && (filter is "all" or null
                         || filter == "domain" && row.Rule.Type.Contains("DOMAIN", StringComparison.OrdinalIgnoreCase)
                         || filter == "ip" && row.Rule.Type.Contains("IP", StringComparison.OrdinalIgnoreCase)))
            .Select(row => row.DisplayText).ToArray();
        EmptyListText.Visibility = RulesListView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListSummaryText.Text = LocalizationService.Format("ControllerListCountFormat", _listSummary?.ReportedCount ?? _rules.Count, _rules.Count, RulesListView.Items.Count)
            + (_listSummary?.IsTruncated == true ? LocalizationService.Get("ControllerListTruncated") : string.Empty);
    }

    private bool CanRefreshRules =>
        _controllerWritable
        && (_controllerCapabilities & (EndpointCapability.ObserveRules | EndpointCapability.ObserveProviders))
            == (EndpointCapability.ObserveRules | EndpointCapability.ObserveProviders);

    private string? GetRefreshTooltip()
    {
        if (CanRefreshRules)
        {
            return null;
        }

        return !_controllerWritable
            ? LocalizationService.Get("RemoteControllerReadOnly")
            : LocalizationService.Get("RemoteControllerCapabilityUnavailable");
    }
}
