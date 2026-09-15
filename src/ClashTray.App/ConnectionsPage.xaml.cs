using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class ConnectionsPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private IReadOnlyList<ConnectionInfo> _connections = [];
    private bool _controllerWritable = true;

    public ConnectionsPage(ClashTrayRuntime runtime)
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
        UpdateActionButtons();
        if (ReferenceEquals(_connections, snapshot.Connections) && !interactivityChanged)
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
        UpdateActionButtons();
    }

    private void ConnectionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConnectionsListView.SelectedItem is not ListViewItem { Tag: ConnectionInfo connection })
        {
            DetailsText.Text = LocalizationService.Get("SelectConnectionHint");
            UpdateActionButtons();
            return;
        }

        DetailsText.Text = LocalizationService.Format("ConnectionDetailsFormat",
            connection.Network, connection.Chain, connection.Rule, connection.RulePayload, connection.UploadBytes, connection.DownloadBytes);
        UpdateActionButtons();
    }

    private async void CloseSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_controllerWritable)
        {
            return;
        }

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
        if (!_controllerWritable)
        {
            return;
        }

        try
        {
            await _runtime.CloseAllConnectionsAsync();
        }
        catch (Exception exception)
        {
            DetailsText.Text = LocalizationService.Format("CloseConnectionFailedFormat", ErrorSanitizer.Sanitize(exception));
        }
    }

    private void UpdateActionButtons()
    {
        if (ConnectionsListView is null)
        {
            return;
        }

        bool hasSelection = ConnectionsListView.SelectedItem is ListViewItem { Tag: ConnectionInfo };
        CloseSelectedButton.IsEnabled = _controllerWritable && hasSelection;
        CloseAllButton.IsEnabled = _controllerWritable && ConnectionsListView.Items.Count > 0;
        string? tooltip = _controllerWritable
            ? null
            : LocalizationService.Get("RemoteControllerReadOnly");
        ToolTipService.SetToolTip(CloseSelectedButton, tooltip);
        ToolTipService.SetToolTip(CloseAllButton, tooltip);
    }
}
