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

    public LogsPage(ClashTrayRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
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

        var search = SearchBox.Text.Trim();
        var level = (LevelBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var source = (SourceBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        LogsListView.Items.Clear();
        foreach (var log in _logs.Where(log =>
                     (string.IsNullOrWhiteSpace(search) || log.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                     && (level is "全部" or null || string.Equals(log.Level, level, StringComparison.OrdinalIgnoreCase))
                     && (source is "全部来源" or null || string.Equals(log.Source, source, StringComparison.OrdinalIgnoreCase))))
        {
            var folded = log.RepeatCount > 1 ? $" ×{log.RepeatCount}" : string.Empty;
            LogsListView.Items.Add(new ListViewItem { Content = $"{log.Timestamp:HH:mm:ss} [{log.Source}/{log.Level}] {log.Message}{folded}", Tag = log });
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var item in LogsListView.Items.OfType<ListViewItem>())
        {
            text.AppendLine(item.Content?.ToString());
        }

        var package = new DataPackage();
        package.SetText(text.ToString());
        Clipboard.SetContent(package);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _runtime.ClearLogs();
}
