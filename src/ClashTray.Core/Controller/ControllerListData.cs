using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record ControllerListData<T>(IReadOnlyList<T> Items, ControllerListSummary? Summary);
