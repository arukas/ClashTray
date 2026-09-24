using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class ConnectionsPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private readonly StableRowReconciler<ConnectionRowIdentity, ConnectionInfo, ConnectionRowViewModel> _rows;
    private IReadOnlyList<ConnectionInfo> _connections = [];
    private string _controllerIdentity = EndpointId.Local.Value;
    private bool _controllerWritable = true;
    private EndpointCapability _controllerCapabilities = EndpointCapabilityDefaults.Local;
    private string? _selectedConnectionId;
    private bool _synchronizingSelection;

    public ConnectionsPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _rows = new(connection => new ConnectionRowIdentity(_controllerIdentity, connection.Id));
        InitializeComponent();
        ConnectionsListView.ItemsSource = _rows.Rows;
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        UpdateSnapshot(snapshot, controllerWritable: true, EndpointCapabilityDefaults.Local, EndpointId.Local.Value);
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot, bool controllerWritable)
    {
        UpdateSnapshot(
            snapshot,
            controllerWritable,
            controllerWritable ? EndpointCapabilityDefaults.Local : EndpointCapability.None,
            EndpointId.Local.Value);
    }

    public void UpdateSnapshot(
        RuntimeSnapshot snapshot,
        bool controllerWritable,
        EndpointCapability capabilities) =>
        UpdateSnapshot(snapshot, controllerWritable, capabilities, EndpointId.Local.Value);

    public void UpdateSnapshot(
        RuntimeSnapshot snapshot,
        bool controllerWritable,
        EndpointCapability capabilities,
        string controllerIdentity)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(controllerIdentity);
        bool controllerChanged = !string.Equals(
            _controllerIdentity,
            controllerIdentity,
            StringComparison.Ordinal);
        bool interactivityChanged = _controllerWritable != controllerWritable
            || _controllerCapabilities != capabilities;
        _controllerIdentity = controllerIdentity;
        if (controllerChanged)
        {
            _selectedConnectionId = null;
        }

        _controllerWritable = controllerWritable;
        _controllerCapabilities = capabilities;
        UpdateActionButtons();
        if (ReferenceEquals(_connections, snapshot.Connections)
            && !interactivityChanged
            && !controllerChanged)
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

        string? sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string;
        IReadOnlyList<ConnectionInfo> filtered = RuntimeListProjection.FilterAndSortConnections(
            _connections,
            SearchBox.Text,
            sort);
        _synchronizingSelection = true;
        ConnectionRowViewModel? selectedRow;
        try
        {
            _rows.Reconcile(
                _connections,
                filtered.Select(connection => new ConnectionRowIdentity(_controllerIdentity, connection.Id)).ToArray(),
                connection => new ConnectionRowViewModel(connection),
                (row, connection) => row.Update(connection));

            selectedRow = _selectedConnectionId is null
                ? null
                : _rows.Rows.FirstOrDefault(row => string.Equals(
                    row.Id,
                    _selectedConnectionId,
                    StringComparison.Ordinal));
            ConnectionsListView.SelectedItem = selectedRow;
            if (selectedRow is null)
            {
                _selectedConnectionId = null;
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }

        EmptyListText.Visibility = _rows.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectedDetails();
        UpdateActionButtons();
    }

    private void ConnectionsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection)
        {
            return;
        }

        _selectedConnectionId = (ConnectionsListView.SelectedItem as ConnectionRowViewModel)?.Id;
        UpdateSelectedDetails();
        UpdateActionButtons();
    }

    private void UpdateSelectedDetails()
    {
        if (ConnectionsListView.SelectedItem is not ConnectionRowViewModel row)
        {
            DetailsText.Text = LocalizationService.Get("SelectConnectionHint");
            return;
        }

        ConnectionInfo connection = row.Connection;
        DetailsText.Text = LocalizationService.Format(
            "ConnectionDetailsFormat",
            connection.Network,
            connection.Chain,
            connection.Rule,
            connection.RulePayload,
            connection.UploadBytes,
            connection.DownloadBytes);
    }

    private async void CloseSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasCapability(EndpointCapability.CloseConnection))
        {
            return;
        }

        if (ConnectionsListView.SelectedItem is ConnectionRowViewModel row)
        {
            try
            {
                await _runtime.CloseConnectionAsync(row.Id);
            }
            catch (Exception exception)
            {
                DetailsText.Text = LocalizationService.Format(
                    "CloseConnectionFailedFormat",
                    ErrorSanitizer.Sanitize(exception));
            }
        }
    }

    private async void CloseAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HasCapability(EndpointCapability.CloseConnection))
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

        bool hasSelection = ConnectionsListView.SelectedItem is ConnectionRowViewModel;
        bool canCloseConnections = HasCapability(EndpointCapability.CloseConnection);
        CloseSelectedButton.IsEnabled = canCloseConnections && hasSelection;
        CloseAllButton.IsEnabled = canCloseConnections && ConnectionsListView.Items.Count > 0;
        string? tooltip = canCloseConnections
            ? null
            : !_controllerWritable
                ? LocalizationService.Get("RemoteControllerReadOnly")
                : LocalizationService.Get("RemoteControllerCapabilityUnavailable");
        ToolTipService.SetToolTip(CloseSelectedButton, tooltip);
        ToolTipService.SetToolTip(CloseAllButton, tooltip);
    }

    private bool HasCapability(EndpointCapability capability) =>
        _controllerWritable && (_controllerCapabilities & capability) == capability;
}

internal readonly record struct ConnectionRowIdentity(string ControllerIdentity, string ConnectionId);

public sealed class ConnectionRowViewModel : INotifyPropertyChanged
{
    public ConnectionRowViewModel(ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => Connection.Id;

    public ConnectionInfo Connection { get; private set; }

    public string DisplayText =>
        $"{Connection.Source} → {Connection.Destination} · {Connection.Rule} {Connection.RulePayload}";

    internal bool Update(ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (Connection == connection)
        {
            return false;
        }

        Connection = connection;
        OnPropertyChanged(nameof(Connection));
        OnPropertyChanged(nameof(DisplayText));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}