using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class ProxyPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private RuntimeSnapshot _snapshot;
    private bool _updating;

    public ProxyPage(ClashTrayRuntime runtime)
    {
        _runtime = runtime;
        _snapshot = runtime.Snapshot;
        InitializeComponent();
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        _updating = true;
        try
        {
            var currentGroup = (GroupComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
            GroupComboBox.Items.Clear();
            foreach (var group in snapshot.ProxyGroups)
            {
                GroupComboBox.Items.Add(new ComboBoxItem { Content = group.Name, Tag = group.Name });
            }

            var groupItem = GroupComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == currentGroup)
                ?? GroupComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            GroupComboBox.SelectedItem = groupItem;
            PopulateNodes(groupItem?.Tag as string);
        }
        finally
        {
            _updating = false;
        }
    }

    private void GroupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating)
        {
            return;
        }

        PopulateNodes((GroupComboBox.SelectedItem as ComboBoxItem)?.Tag as string);
    }

    private async void NodeListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || NodeListView.SelectedItem is not ListViewItem { Tag: string proxy } || GroupComboBox.SelectedItem is not ComboBoxItem { Tag: string group })
        {
            return;
        }

        try
        {
            await _runtime.SelectProxyAsync(group, proxy);
        }
        catch (Exception exception)
        {
            DelayText.Text = exception.Message;
        }
    }

    private async void TestDelayButton_Click(object sender, RoutedEventArgs e)
    {
        if (NodeListView.SelectedItem is not ListViewItem { Tag: string proxy })
        {
            DelayText.Text = "先选择节点";
            return;
        }

        try
        {
            var delay = await _runtime.TestProxyDelayAsync(proxy);
            DelayText.Text = delay is null ? "测试失败" : $"{delay} ms";
        }
        catch (Exception exception)
        {
            DelayText.Text = exception.Message;
        }
    }

    private void PopulateNodes(string? groupName)
    {
        NodeListView.Items.Clear();
        var group = _snapshot.ProxyGroups.FirstOrDefault(item => item.Name == groupName);
        if (group is null)
        {
            return;
        }

        foreach (var node in group.Members)
        {
            NodeListView.Items.Add(new ListViewItem
            {
                Content = string.Equals(node, group.Current, StringComparison.OrdinalIgnoreCase) ? $"{node} · 当前" : node,
                Tag = node
            });
        }
    }
}
