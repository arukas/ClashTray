using ClashTray.Contracts;
using ClashTray.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashTray.App;

public sealed partial class ProxyPage : UserControl
{
    private readonly ClashTrayRuntime _runtime;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _testingGroups = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private RuntimeSnapshot? _snapshot;
    private string? _signature;

    public ProxyPage(ClashTrayRuntime runtime)
    {
        _runtime = runtime;
        InitializeComponent();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            if (_snapshot is not null)
            {
                RenderGroups(_snapshot);
            }
        };
        Unloaded += (_, _) => _searchTimer.Stop();
    }

    public void UpdateSnapshot(RuntimeSnapshot snapshot)
    {
        _snapshot = snapshot;
        // Traffic updates should not recreate controls or disturb keyboard focus.
        string signature = System.Text.Json.JsonSerializer.Serialize(new { snapshot.ProxyGroups, snapshot.ProxyNodes, snapshot.Providers });
        EmptyTitle.Text = snapshot.Core.State == CoreState.Running ? "配置中没有代理组" : "还没有代理组";
        if (_signature == signature)
        {
            return;
        }

        _signature = signature;
        _expanded.IntersectWith(snapshot.ProxyGroups.Select(group => group.Name));
        RenderGroups(snapshot);
        RenderProviders(snapshot);
    }

    private void RenderGroups(RuntimeSnapshot snapshot)
    {
        GroupsPanel.Children.Clear();
        string query = NodeSearchBox.Text.Trim();
        bool searching = query.Length > 0;
        Dictionary<string, string> delays = snapshot.ProxyNodes.GroupBy(node => node.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Delay, StringComparer.Ordinal);
        foreach (ProxyGroup proxyGroup in snapshot.ProxyGroups)
        {
            delays[proxyGroup.Name] = proxyGroup.Delay;
        }

        EmptyState.Visibility = snapshot.ProxyGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NodeSearchBox.Visibility = snapshot.ProxyGroups.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (ProxyGroup group in snapshot.ProxyGroups)
        {
            bool groupMatches = group.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            string[] members = group.Members.Where(member => !searching || groupMatches ||
                member.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (searching && !groupMatches && members.Length == 0)
            {
                continue;
            }

            Grid header = new Grid { ColumnSpacing = 10 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = group.Name,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labels.Children.Add(new TextBlock
            {
                Text = group.Current ?? "尚未选择",
                FontSize = 12,
                Style = (Style)Application.Current.Resources["ClashTrayAccentTextStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            header.Children.Add(labels);
            FontIcon chevron = new FontIcon { FontSize = 10 };
            Grid.SetColumn(chevron, 1);
            header.Children.Add(chevron);

            delays.TryGetValue(group.Current ?? "", out string latency);
            TextBlock delayLabel = new TextBlock
            {
                Text = FormatDelay(latency),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Style = SecondaryTextStyle
            };
            Button test = new Button
            {
                Content = new FontIcon { Glyph = "\uE9D9", FontSize = 14 },
                Style = (Style)Application.Current.Resources["ClashTrayIconButtonStyle"],
                IsEnabled = snapshot.Core.State == CoreState.Running && group.Members.Count > 0 && !_testingGroups.Contains(group.Name)
            };
            ToolTipService.SetToolTip(test, "测试整组节点延迟");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(test, $"测试 {group.Name} 整组节点延迟");
            test.Click += async (_, _) =>
            {
                if (!_testingGroups.Add(group.Name))
                {
                    return;
                }

                test.IsEnabled = false;
                DelayText.Text = $"{group.Name} · 正在测试整组节点…";
                try
                {
                    IReadOnlyDictionary<string, int?> results = await _runtime.TestProxyGroupDelayAsync(group.Name);
                    int available = results.Count(result => result.Value > 0);
                    DelayText.Text = $"{group.Name} · 测速完成，{available} 个可用，{results.Count - available} 个未连通";
                }
                catch (Exception exception) { DelayText.Text = $"{group.Name} · 测速失败：{exception.Message}"; }
                finally
                {
                    _testingGroups.Remove(group.Name);
                    if (_snapshot is not null)
                    {
                        RenderGroups(_snapshot);
                    }
                }
            };

            StackPanel body = new StackPanel { Spacing = 4, Margin = new Thickness(4, 0, 4, 4) };
            ListView list = new ListView
            {
                SelectionMode = ListViewSelectionMode.Single,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsTabStop = false
            };
            // The dashboard owns scrolling. Disabling (not hiding) the inner
            // viewport lets wheel/touch/keyboard navigation use that one scroll host.
            ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(list, $"{group.Name} 节点");
            body.Children.Add(new TextBlock
            {
                Text = searching ? $"{members.Length} / {group.Members.Count} 个节点" : $"{group.Members.Count} 个节点",
                FontSize = 11,
                Style = SecondaryTextStyle,
                Margin = new Thickness(10, 3, 10, 2)
            });
            body.Children.Add(list);
            bool populated = false;
            bool selectionPending = false;
            void Populate()
            {
                if (populated)
                {
                    return;
                }

                populated = true;
                foreach (string member in members)
                {
                    bool selected = member == group.Current;
                    Grid content = new Grid { ColumnSpacing = 10 };
                    content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                    content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    content.Children.Add(new FontIcon
                    {
                        Glyph = "\uE73E",
                        Opacity = selected ? 1 : 0,
                        Style = (Style)Application.Current.Resources["ClashTraySelectionIconStyle"]
                    });
                    TextBlock label = new TextBlock
                    {
                        Text = member,
                        FontSize = 13,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal
                    };
                    Grid nameRow = new Grid { ColumnSpacing = 6 };
                    nameRow.ColumnDefinitions.Add(new ColumnDefinition());
                    nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    nameRow.Children.Add(label);
                    if (selected)
                    {
                        TextBlock currentLabel = new TextBlock
                        {
                            Text = "当前",
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center,
                            Style = (Style)Application.Current.Resources["ClashTrayAccentTextStyle"]
                        };
                        Grid.SetColumn(currentLabel, 1);
                        nameRow.Children.Add(currentLabel);
                    }
                    Grid.SetColumn(nameRow, 1);
                    content.Children.Add(nameRow);
                    delays.TryGetValue(member, out string delay);
                    TextBlock detail = new TextBlock
                    {
                        Text = FormatDelay(delay),
                        FontSize = 11,
                        Style = SecondaryTextStyle,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(detail, 2);
                    content.Children.Add(detail);
                    ListViewItem item = new ListViewItem
                    {
                        Content = content,
                        Tag = member,
                        MinHeight = 38,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(10, 6, 10, 6),
                        CornerRadius = new CornerRadius(6)
                    };
                    ToolTipService.SetToolTip(item, member);
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, $"{member}，{detail.Text}{(selected ? "，当前节点" : "")}");
                    list.Items.Add(item);
                }
                list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(item => (string?)item.Tag == group.Current);
            }
            list.SelectionChanged += async (_, _) =>
            {
                if (selectionPending || list.SelectedItem is not ListViewItem { Tag: string name } || name == group.Current)
                {
                    return;
                }

                selectionPending = true;
                // Keep the confirmed selection visible while the core applies the request.
                list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(item => (string?)item.Tag == group.Current);
                list.IsEnabled = false;
                try { await _runtime.SelectProxyAsync(group.Name, name); }
                catch (Exception exception) { DelayText.Text = exception.Message; }
                finally { selectionPending = false; list.IsEnabled = true; }
            };

            StackPanel container = new StackPanel();
            Grid row = new Grid { ColumnSpacing = 4, Padding = new Thickness(2, 2, 6, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button expand = new Button
            {
                Content = header,
                Style = (Style)Application.Current.Resources["ClashTrayRowButtonStyle"],
                MinHeight = 52,
                Padding = new Thickness(10, 6, 8, 6)
            };
            ToolTipService.SetToolTip(expand, $"{group.Name} · {group.Current ?? "尚未选择"}");
            void ShowExpanded(bool expanded)
            {
                if (expanded)
                {
                    Populate();
                }

                body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                chevron.Glyph = expanded ? "\uE70E" : "\uE70D";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(expand,
                    $"{(expanded ? "收起" : "展开")} {group.Name}，当前 {group.Current}");
            }
            ShowExpanded(searching || _expanded.Contains(group.Name));
            expand.Click += (_, _) =>
            {
                bool expanded = body.Visibility != Visibility.Visible;
                if (expanded)
                {
                    _expanded.Add(group.Name);
                }
                else
                {
                    _expanded.Remove(group.Name);
                }

                ShowExpanded(expanded);
            };
            row.Children.Add(expand);
            Grid.SetColumn(delayLabel, 1);
            row.Children.Add(delayLabel);
            Grid.SetColumn(test, 2);
            row.Children.Add(test);
            container.Children.Add(row);
            container.Children.Add(body);
            GroupsPanel.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["ClashTrayGroupCardStyle"],
                Child = container
            });
        }
        NodeCountText.Text = searching ? $"{GroupsPanel.Children.Count} / {snapshot.ProxyGroups.Count} 组" : $"{snapshot.ProxyGroups.Count} 组";
        NoResultsText.Visibility = searching && GroupsPanel.Children.Count == 0 && snapshot.ProxyGroups.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderProviders(RuntimeSnapshot snapshot)
    {
        ProvidersPanel.Children.Clear();
        ProviderSection.Visibility = snapshot.Providers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProviderTitle.Text = $"代理提供者 · {snapshot.Providers.Count}";
        foreach (ProviderStatus provider in snapshot.Providers)
        {
            Grid row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel labels = new StackPanel { Spacing = 2 };
            labels.Children.Add(new TextBlock { Text = provider.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock { Text = provider.Error ?? $"{provider.Count} 项 · {provider.UpdatedAt:MM-dd HH:mm}", Style = SecondaryTextStyle, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(labels);
            Button refresh = new Button { Content = new FontIcon { Glyph = "\uE72C", FontSize = 15 }, Style = (Style)Application.Current.Resources["ClashTrayIconButtonStyle"] };
            ToolTipService.SetToolTip(refresh, $"刷新 {provider.Name}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(refresh, $"刷新代理提供者 {provider.Name}");
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

    private static string FormatDelay(string? delay) =>
        string.IsNullOrWhiteSpace(delay) || delay == "—" ? "未测速" :
        int.TryParse(delay, out int value) ? value > 0 ? $"{value} ms" : "超时" : delay;

    private Style SecondaryTextStyle => (Style)Resources["ProxySecondaryText"];

    private void NodeSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ToggleProviders_Click(object sender, RoutedEventArgs e) =>
        ProvidersPanel.Visibility = ProvidersPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
}
