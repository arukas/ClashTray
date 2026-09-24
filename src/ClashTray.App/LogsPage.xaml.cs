using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ClashTray.App;

public sealed partial class LogsPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private readonly StableRowReconciler<LogRowIdentity, LogEntry, LogRowViewModel> _rows;
    private IReadOnlyList<LogEntry> _logs = [];
    private string _controllerIdentity = EndpointId.Local.Value;
    private bool _controllerWritable = true;

    public LogsPage(ClashTrayRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _rows = new(log => new LogRowIdentity(_controllerIdentity, log.Sequence));
        InitializeComponent();
        LogsListView.ItemsSource = _rows.Rows;
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
        _controllerIdentity = controllerIdentity;
        bool interactivityChanged = _controllerWritable != controllerWritable;
        _controllerWritable = controllerWritable;
        ClearLogsButton.IsEnabled = _controllerWritable;
        ToolTipService.SetToolTip(
            ClearLogsButton,
            _controllerWritable
                ? null
                : LocalizationService.Get("RemoteControllerReadOnly"));
        if (ReferenceEquals(_logs, snapshot.Logs) && !interactivityChanged && !controllerChanged)
        {
            return;
        }

        _logs = snapshot.Logs;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void LevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (LogsListView is null)
        {
            return;
        }

        string? level = (LevelBox.SelectedItem as ComboBoxItem)?.Tag as string;
        string? source = (SourceBox.SelectedItem as ComboBoxItem)?.Tag as string;
        IReadOnlyList<LogEntry> filtered = RuntimeListProjection.FilterLogs(
            _logs,
            SearchBox.Text,
            level,
            source);
        _rows.Reconcile(
            _logs,
            filtered.Select(log => new LogRowIdentity(_controllerIdentity, log.Sequence)).ToArray(),
            log => new LogRowViewModel(log),
            (row, log) => row.Update(log));
        EmptyListText.Visibility = _rows.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        StringBuilder text = new();
        foreach (LogRowViewModel row in _rows.Rows)
        {
            text.AppendLine(row.DisplayText);
        }

        DataPackage package = new();
        package.SetText(text.ToString());
        Clipboard.SetContent(package);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controllerWritable)
        {
            _runtime.ClearLogs();
        }
    }
}

internal readonly record struct LogRowIdentity(string ControllerIdentity, long Sequence);

public sealed class LogRowViewModel : INotifyPropertyChanged
{
    public LogRowViewModel(LogEntry log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Log = log;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Sequence => Log.Sequence;

    public LogEntry Log { get; private set; }

    public string DisplayText
    {
        get
        {
            string folded = Log.RepeatCount > 1 ? $" ×{Log.RepeatCount}" : string.Empty;
            return $"{Log.Timestamp:HH:mm:ss} [{Log.Source}/{Log.Level}] {Log.Message}{folded}";
        }
    }

    internal bool Update(LogEntry log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (Log == log)
        {
            return false;
        }

        Log = log;
        OnPropertyChanged(nameof(Log));
        OnPropertyChanged(nameof(DisplayText));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}