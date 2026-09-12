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

    public RulesPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        InitializeComponent();
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _rules = snapshot.Rules;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private async void RefreshRulesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        RefreshRulesButton.IsEnabled = false;
        try
        {
            await _runtime.RefreshDataAsync();
            StatusText.Text = "规则和 Provider 状态已刷新。";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText.Text = $"刷新失败：{ErrorSanitizer.Sanitize(exception)}";
        }
        finally
        {
            _refreshing = false;
            RefreshRulesButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        if (RulesListView is null)
        {
            return;
        }

        string search = SearchBox.Text.Trim();
        string? filter = (FilterBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        RulesListView.Items.Clear();
        foreach (RuleInfo rule in _rules.Where(rule =>
                     (string.IsNullOrWhiteSpace(search) || $"{rule.Type} {rule.Payload} {rule.Proxy}".Contains(search, StringComparison.OrdinalIgnoreCase))
                     && (filter is "全部" or null
                         || filter == "域名" && rule.Type.Contains("DOMAIN", StringComparison.OrdinalIgnoreCase)
                         || filter == "IP" && rule.Type.Contains("IP", StringComparison.OrdinalIgnoreCase))))
        {
            RulesListView.Items.Add(new ListViewItem { Content = $"{rule.Type}  {rule.Payload}  → {rule.Proxy}" });
        }
        EmptyListText.Visibility = RulesListView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
