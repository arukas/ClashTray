using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Snapshot list reconciliation helpers: incoming controller data replaces the
/// published list only when an element actually changed, so an unchanged poll
/// neither allocates a new snapshot graph nor notifies subscribers.
/// </summary>
internal static class SnapshotDataComparer
{
    public static IReadOnlyList<T> ReuseIfEqual<T>(
        IReadOnlyList<T> previous,
        IReadOnlyList<T> current,
        Func<T, T, bool> equals)
    {
        if (previous.Count != current.Count)
        {
            return current;
        }

        for (int index = 0; index < previous.Count; index++)
        {
            if (!equals(previous[index], current[index]))
            {
                return current;
            }
        }

        return previous;
    }

    public static bool ProxyGroupsEqual(ProxyGroup left, ProxyGroup right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
        && string.Equals(left.Current, right.Current, StringComparison.Ordinal)
        && string.Equals(left.Delay, right.Delay, StringComparison.Ordinal)
        && left.Members.SequenceEqual(right.Members, StringComparer.Ordinal);

    public static bool ProxyNodesEqual(ProxyNode left, ProxyNode right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
        && string.Equals(left.Delay, right.Delay, StringComparison.Ordinal)
        && left.IsCurrent == right.IsCurrent
        && left.Providers.SequenceEqual(right.Providers, StringComparer.Ordinal);
}
