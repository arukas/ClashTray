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
    private IReadOnlyList<LogEntry> _logs = [];
    private bool _controllerWritable = true;

    public LogsPage(ClashTrayRuntime runtime)
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
        ClearLogsButton.IsEnabled = _controllerWritable;
        ToolTipService.SetToolTip(
            ClearLogsButton,
            _controllerWritable
                ? null
                : LocalizationService.Get("RemoteControllerReadOnly"));
        if (ReferenceEquals(_logs, snapshot.Logs) && !interactivityChanged)
        {
            return;
        }

        _logs = snapshot.Logs;
        ApplyFilter();
    }

    public void UpdateSnapshot(
        RuntimeSnapshot snapshot,
        bool controllerWritable,
        EndpointCapability capabilities) =>
        UpdateSnapshot(snapshot, controllerWritable);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void LevelBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (LogsListView is null)
        {
            return;
        }

        string search = SearchBox.Text.Trim();
        string? level = (LevelBox.SelectedItem as ComboBoxItem)?.Tag as string;
        string? source = (SourceBox.SelectedItem as ComboBoxItem)?.Tag as string;
        LogsListView.Items.Clear();
        foreach (LogEntry log in _logs.Where(log =>
                     (string.IsNullOrWhiteSpace(search) || log.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                     && (level is "all" or null || string.Equals(log.Level, level, StringComparison.OrdinalIgnoreCase))
                     && (source is "all" or null || string.Equals(log.Source, source, StringComparison.OrdinalIgnoreCase))))
        {
            string folded = log.RepeatCount > 1 ? $" ×{log.RepeatCount}" : string.Empty;
            LogsListView.Items.Add(new ListViewItem { Content = $"{log.Timestamp:HH:mm:ss} [{log.Source}/{log.Level}] {log.Message}{folded}", Tag = log });
        }
        EmptyListText.Visibility = LogsListView.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        StringBuilder text = new StringBuilder();
        foreach (ListViewItem item in LogsListView.Items.OfType<ListViewItem>())
        {
            text.AppendLine(item.Content?.ToString());
        }

        DataPackage package = new DataPackage();
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
