using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class RulesPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private IReadOnlyList<RuleInfo> _rules = [];
    private bool _refreshing;
    private bool _controllerWritable = true;

    public RulesPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        InitializeComponent();
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        UpdateSnapshot(snapshot, controllerWritable: true);
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot, bool controllerWritable)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool interactivityChanged = _controllerWritable != controllerWritable;
        _controllerWritable = controllerWritable;
        RefreshRulesButton.IsEnabled = _controllerWritable && !_refreshing;
        ToolTipService.SetToolTip(
            RefreshRulesButton,
            _controllerWritable
                ? null
                : LocalizationService.Get("RemoteControllerReadOnly"));
        if (ReferenceEquals(_rules, snapshot.Rules) && !interactivityChanged)
        {
            return;
        }

        _rules = snapshot.Rules;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private async void RefreshRulesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing || !_controllerWritable)
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
            RefreshRulesButton.IsEnabled = _controllerWritable;
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
        RulesListView.Items.Clear();
        foreach (RuleInfo rule in _rules.Where(rule =>
                     (string.IsNullOrWhiteSpace(search) || $"{rule.Type} {rule.Payload} {rule.Proxy}".Contains(search, StringComparison.OrdinalIgnoreCase))
                     && (filter is "all" or null
                         || filter == "domain" && rule.Type.Contains("DOMAIN", StringComparison.OrdinalIgnoreCase)
                         || filter == "ip" && rule.Type.Contains("IP", StringComparison.OrdinalIgnoreCase))))
        {
            RulesListView.Items.Add(new ListViewItem { Content = $"{rule.Type}  {rule.Payload}  → {rule.Proxy}" });
        }
        EmptyListText.Visibility = RulesListView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
