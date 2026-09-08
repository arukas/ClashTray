using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClashTray.App;

public sealed partial class ProxyPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private string? _signature;

    public ProxyPage(ClashTrayRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        // Metrics change every second; don't rebuild the node controls or lose
        // keyboard focus while only the traffic counters are changing.
        var signature = System.Text.Json.JsonSerializer.Serialize(new { snapshot.ProxyGroups, snapshot.ProxyNodes, snapshot.Providers });
        EmptyTitle.Text = snapshot.Core.State == CoreState.Running ? "配置中没有代理组" : "还没有代理组";
        if (_signature == signature) return;
        _signature = signature;
        GroupsPanel.Children.Clear();
        NodeCountText.Text = $"{snapshot.ProxyGroups.Count} 组";
        EmptyState.Visibility = snapshot.ProxyGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var group in snapshot.ProxyGroups)
        {
            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { Glyph = "\uE8F1", FontSize = 17, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(icon);
            var labels = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.Children.Add(new TextBlock { Text = group.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var currentLabel = new TextBlock { Text = group.Current ?? "尚未选择", FontSize = 12, Style = SecondaryTextStyle, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(currentLabel, 1);
            labels.Children.Add(currentLabel);
            Grid.SetColumn(labels, 1);
            header.Children.Add(labels);
            var latency = snapshot.ProxyNodes.FirstOrDefault(n => n.Name == group.Current)?.Delay;
            var delayLabel = new TextBlock { Text = string.IsNullOrWhiteSpace(latency) ? "—" : $"{latency} ms", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Style = SecondaryTextStyle };
            Grid.SetColumn(delayLabel, 2);
            header.Children.Add(delayLabel);
            var chevron = new FontIcon { Glyph = _expanded.Contains(group.Name) ? "\uE70E" : "\uE70D", FontSize = 10 };
            Grid.SetColumn(chevron, 3);
            header.Children.Add(chevron);
            var test = new Button { Content = new FontIcon { Glyph = "\uE9D9", FontSize = 15 }, Style = (Style)Application.Current.Resources["ClashTrayIconButtonStyle"] };
            test.Width = 28;
            test.Height = 28;
            ToolTipService.SetToolTip(test, "测试当前节点延迟");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(test, $"测试 {group.Name} 当前节点延迟");
            test.Click += async (_, _) =>
            {
                if (group.Current is null) return;
                test.IsEnabled = false;
                try
                {
                    var delay = await _runtime.TestProxyDelayAsync(group.Current);
                    delayLabel.Text = delay is null ? "超时" : $"{delay} ms";
                    DelayText.Text = $"{group.Current} · {delayLabel.Text}";
                }
                catch (Exception exception) { DelayText.Text = exception.Message; }
                finally { test.IsEnabled = true; }
            };
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 240, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            foreach (var member in group.Members)
            {
                list.Items.Add(new ListViewItem { Content = member, Tag = member, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 6, 8, 6) });
            }
            list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(item => (string?)item.Tag == group.Current);
            list.SelectionChanged += async (_, _) =>
            {
                if (list.SelectedItem is not ListViewItem { Tag: string name } || name == group.Current) return;
                list.IsEnabled = false;
                try { await _runtime.SelectProxyAsync(group.Name, name); }
                catch (Exception exception)
                {
                    DelayText.Text = exception.Message;
                    list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(item => (string?)item.Tag == group.Current);
                }
                finally { list.IsEnabled = true; }
            };
            var container = new StackPanel();
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var expand = new Button { Content = header, Style = (Style)Application.Current.Resources["ClashTrayRowButtonStyle"], MinHeight = 34, Padding = new Thickness(6, 4, 4, 4) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expand, $"展开或收起 {group.Name}，当前 {group.Current}");
            list.Visibility = _expanded.Contains(group.Name) ? Visibility.Visible : Visibility.Collapsed;
            expand.Click += (_, _) =>
            {
                if (!_expanded.Add(group.Name)) _expanded.Remove(group.Name);
                list.Visibility = _expanded.Contains(group.Name) ? Visibility.Visible : Visibility.Collapsed;
                chevron.Glyph = _expanded.Contains(group.Name) ? "\uE70E" : "\uE70D";
            };
            row.Children.Add(expand);
            Grid.SetColumn(test, 1);
            row.Children.Add(test);
            container.Children.Add(row);
            container.Children.Add(list);
            GroupsPanel.Children.Add(container);
        }

        ProvidersPanel.Children.Clear();
        ProviderSection.Visibility = snapshot.Providers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProviderTitle.Text = $"代理提供者 · {snapshot.Providers.Count}";
        foreach (var provider in snapshot.Providers)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { Spacing = 2 };
            labels.Children.Add(new TextBlock { Text = provider.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            labels.Children.Add(new TextBlock { Text = provider.Error ?? $"{provider.Count} 项 · {provider.UpdatedAt:MM-dd HH:mm}", Style = SecondaryTextStyle, FontSize = 12 });
            row.Children.Add(labels);
            var refresh = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = 15 }, Style = (Style)Application.Current.Resources["ClashTrayIconButtonStyle"] };
            ToolTipService.SetToolTip(refresh, $"刷新 {provider.Name}");
            refresh.Click += async (_, _) =>
            {
                refresh.IsEnabled = false;
                try { await _runtime.RefreshProviderAsync(provider.Name, false); }
                catch (Exception exception) { DelayText.Text = exception.Message; }
                finally { refresh.IsEnabled = true; }
            };
            Grid.SetColumn(refresh, 1);
            row.Children.Add(refresh);
            ProvidersPanel.Children.Add(row);
        }
    }

    private Style SecondaryTextStyle => (Style)Resources["ProxySecondaryText"];

    private void ToggleProviders_Click(object sender, RoutedEventArgs e) =>
        ProvidersPanel.Visibility = ProvidersPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
}
