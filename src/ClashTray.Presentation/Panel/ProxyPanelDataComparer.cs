using ClashTray.Contracts;

namespace ClashTray.App;

internal static class ProxyPanelDataComparer
{
    public static bool Equals(RuntimeSnapshot? previous, RuntimeSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (ReferenceEquals(previous, current))
        {
            return true;
        }

        return previous is not null
            && SequenceEqual(previous.ProxyGroups, current.ProxyGroups, GroupEquals)
            && SequenceEqual(previous.ProxyNodes, current.ProxyNodes, NodeEquals)
            && SequenceEqual(previous.Providers, current.Providers, ProviderEquals);
    }

    private static bool GroupEquals(ProxyGroup previous, ProxyGroup current) =>
        previous.Name == current.Name
        && previous.Type == current.Type
        && previous.Current == current.Current
        && previous.Delay == current.Delay
        && SequenceEqual(previous.Members, current.Members, StringEquals);

    private static bool NodeEquals(ProxyNode previous, ProxyNode current) =>
        previous.Name == current.Name
        && previous.Type == current.Type
        && previous.Delay == current.Delay
        && previous.IsCurrent == current.IsCurrent
        && SequenceEqual(previous.Providers, current.Providers, StringEquals);

    private static bool ProviderEquals(ProviderStatus previous, ProviderStatus current) =>
        previous.Equals(current);

    private static bool StringEquals(string previous, string current) =>
        string.Equals(previous, current, StringComparison.Ordinal);

    private static bool SequenceEqual<T>(
        IReadOnlyList<T> previous,
        IReadOnlyList<T> current,
        Func<T, T, bool> equals)
    {
        if (ReferenceEquals(previous, current))
        {
            return true;
        }

        if (previous.Count != current.Count)
        {
            return false;
        }

        for (int index = 0; index < previous.Count; index++)
        {
            if (!equals(previous[index], current[index]))
            {
                return false;
            }
        }

        return true;
    }
}
