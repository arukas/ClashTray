using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class ConnectionsPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private IReadOnlyList<ConnectionInfo> _connections = [];

    public ConnectionsPage(ClashTrayRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        _connections = snapshot.Connections;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (ConnectionsListView is null)
        {
            return;
        }

        string search = SearchBox.Text.Trim();
        string? sort = (SortBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        IEnumerable<ConnectionInfo> filtered = _connections.Where(connection => string.IsNullOrWhiteSpace(search)
            || $"{connection.Source} {connection.Destination} {connection.Rule} {connection.RulePayload} {connection.Chain}".Contains(search, StringComparison.OrdinalIgnoreCase));
        filtered = sort switch
        {
            "上传" => filtered.OrderByDescending(connection => connection.UploadBytes),
            "下载" => filtered.OrderByDescending(connection => connection.DownloadBytes),
            _ => filtered.OrderByDescending(connection => connection.StartTime)
        };
        ConnectionsListView.Items.Clear();
        foreach (ConnectionInfo connection in filtered)
        {
            ConnectionsListView.Items.Add(new ListViewItem
            {
                Content = $"{connection.Source} → {connection.Destination} · {connection.Rule} {connection.RulePayload}",
                Tag = connection
            });
        }
        EmptyListText.Visibility = ConnectionsListView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ConnectionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionsListView.SelectedItem is not ListViewItem { Tag: ConnectionInfo connection })
        {
            DetailsText.Text = "选择连接查看详情";
            return;
        }

        DetailsText.Text = $"{connection.Network} · {connection.Chain}\n规则：{connection.Rule} {connection.RulePayload}\n上传 {connection.UploadBytes} B · 下载 {connection.DownloadBytes} B";
    }

    private async void CloseSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsListView.SelectedItem is ListViewItem { Tag: ConnectionInfo connection })
        {
            try
            {
                await _runtime.CloseConnectionAsync(connection.Id);
            }
            catch (Exception exception)
            {
                DetailsText.Text = $"关闭连接失败：{exception.Message}";
            }
        }
    }

    private async void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _runtime.CloseAllConnectionsAsync();
        }
        catch (Exception exception)
        {
            DetailsText.Text = $"关闭连接失败：{exception.Message}";
        }
    }
}
