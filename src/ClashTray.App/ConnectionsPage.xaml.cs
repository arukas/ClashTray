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
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        InitializeComponent();
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (ReferenceEquals(_connections, snapshot.Connections))
        {
            return;
        }

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
        string? sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string;
        IEnumerable<ConnectionInfo> filtered = _connections.Where(connection => string.IsNullOrWhiteSpace(search)
            || $"{connection.Source} {connection.Destination} {connection.Rule} {connection.RulePayload} {connection.Chain}".Contains(search, StringComparison.OrdinalIgnoreCase));
        filtered = sort switch
        {
            "upload" => filtered.OrderByDescending(connection => connection.UploadBytes),
            "download" => filtered.OrderByDescending(connection => connection.DownloadBytes),
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
            DetailsText.Text = LocalizationService.Get("SelectConnectionHint");
            return;
        }

        DetailsText.Text = LocalizationService.Format("ConnectionDetailsFormat",
            connection.Network, connection.Chain, connection.Rule, connection.RulePayload, connection.UploadBytes, connection.DownloadBytes);
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
                DetailsText.Text = LocalizationService.Format("CloseConnectionFailedFormat", ErrorSanitizer.Sanitize(exception));
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
            DetailsText.Text = LocalizationService.Format("CloseConnectionFailedFormat", ErrorSanitizer.Sanitize(exception));
        }
    }
}
