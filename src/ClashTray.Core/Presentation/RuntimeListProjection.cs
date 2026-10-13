using ClashTray.Contracts;

namespace ClashTray.Core;

public static class RuntimeListProjection
{
    public static IReadOnlyList<ConnectionInfo> FilterAndSortConnections(
        IReadOnlyList<ConnectionInfo> connections,
        string? search,
        string? sort)
    {
        ArgumentNullException.ThrowIfNull(connections);
        string query = search?.Trim() ?? string.Empty;
        IEnumerable<ConnectionInfo> filtered = connections.Where(connection =>
            string.IsNullOrWhiteSpace(query)
            || $"{connection.Source} {connection.Destination} {connection.Rule} {connection.RulePayload} {connection.Chain}"
                .Contains(query, StringComparison.OrdinalIgnoreCase));
        return (sort switch
        {
            "upload" => filtered.OrderByDescending(connection => connection.UploadBytes),
            "download" => filtered.OrderByDescending(connection => connection.DownloadBytes),
            _ => filtered.OrderByDescending(connection => connection.StartTime)
        }).ToArray();
    }

    public static IReadOnlyList<LogEntry> FilterLogs(
        IReadOnlyList<LogEntry> logs,
        string? search,
        string? level,
        string? source)
    {
        ArgumentNullException.ThrowIfNull(logs);
        string query = search?.Trim() ?? string.Empty;
        return logs.Where(log =>
                (string.IsNullOrWhiteSpace(query)
                    || log.Message.Contains(query, StringComparison.OrdinalIgnoreCase))
                && (level is "all" or null
                    || string.Equals(log.Level, level, StringComparison.OrdinalIgnoreCase))
                && (source is "all" or null
                    || string.Equals(log.Source, source, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}