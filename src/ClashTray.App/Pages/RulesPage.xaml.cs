using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class RulesPage : UserControl, IDisposable
{
    private readonly ClashTrayRuntime _runtime;
    private readonly StableRowReconciler<RuleRowIdentity, RuleProjection, RuleRowViewModel> _rows;
    private IReadOnlyList<RuleInfo> _rules = [];
    private IReadOnlyList<RuleProjection> _ruleRows = [];
    private ControllerListSummary? _listSummary;
    private readonly DebouncedAction _searchDebounce;
    private string _controllerIdentity = EndpointId.Local.Value;
    private bool _refreshing;
    private bool _controllerWritable;
    private EndpointCapability _controllerCapabilities;
    private long _viewRevision;

    public RulesPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _searchDebounce = new(action => DispatcherQueue.TryEnqueue(() => action()), ApplyFilter);
        Unloaded += (_, _) => _searchDebounce.Cancel();
        Loaded += (_, _) => ApplyFilter();
        _rows = new(projection => new RuleRowIdentity(_controllerIdentity, projection.SourceIndex));
        InitializeComponent();
        RulesListView.ItemsSource = _rows.Rows;
    }

    public void Dispose() { _searchDebounce.Dispose(); GC.SuppressFinalize(this); }

    internal void UpdateSnapshot(PanelSnapshotContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RuntimeSnapshot snapshot = context.Pages;
        bool controllerWritable = context.ControllerWritable;
        EndpointCapability capabilities = context.ControllerCapabilities;
        string controllerIdentity = context.ControllerIdentity;
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerIdentity);
        bool controllerChanged = !string.Equals(_controllerIdentity, controllerIdentity, StringComparison.Ordinal);
        bool interactivityChanged = _controllerWritable != controllerWritable
            || _controllerCapabilities != capabilities;
        if (controllerChanged)
        {
            _searchDebounce.Cancel();
        }

        _controllerIdentity = controllerIdentity;
        _controllerWritable = controllerWritable;
        _controllerCapabilities = capabilities;
        RefreshRulesButton.IsEnabled = CanRefreshRules && !_refreshing;
        ToolTipService.SetToolTip(
            RefreshRulesButton,
            GetRefreshTooltip());
        if (ReferenceEquals(_rules, snapshot.Rules)
            && Equals(_listSummary, snapshot.RulesSummary)
            && !interactivityChanged
            && !controllerChanged)
        {
            return;
        }

        _rules = snapshot.Rules;
        _ruleRows = _rules.Select((rule, index) => new RuleProjection(index, rule)).ToArray();
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
        IReadOnlyList<RuleProjection> filtered = _ruleRows.Where(row =>
                (string.IsNullOrWhiteSpace(search) || row.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase))
                && (filter is "all" or null
                    || filter == "domain" && row.Rule.Type.Contains("DOMAIN", StringComparison.OrdinalIgnoreCase)
                    || filter == "ip" && row.Rule.Type.Contains("IP", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        long revision = ++_viewRevision;
        ListViewUpdateState viewState = ListViewUpdateState.Capture(RulesListView);
        StableRowReconcileResult reconciliation = _rows.Reconcile(
            _ruleRows,
            filtered.Select(projection => new RuleRowIdentity(_controllerIdentity, projection.SourceIndex)).ToArray(),
            projection => new RuleRowViewModel(projection),
            (row, projection) => row.Update(projection));
        if (reconciliation.ViewReset) { viewState.Restore(RulesListView, () => revision == _viewRevision); }
        EmptyListText.Visibility = _rows.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListSummaryText.Text = LocalizationService.Format("ControllerListCountFormat", _listSummary?.ReportedCount ?? _rules.Count, _rules.Count, _rows.Rows.Count)
            + (_listSummary?.IsTruncated == true ? LocalizationService.Get("ControllerListTruncated") : string.Empty);
    }

    private bool CanRefreshRules => EndpointCapabilityPolicy.Allows(
        _controllerWritable,
        _controllerCapabilities,
        EndpointCapability.ObserveRules | EndpointCapability.ObserveProviders);

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
