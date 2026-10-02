using System.Text.Json;
using System.Text.Json.Nodes;
using ClashTray.Contracts;

namespace ClashTray.Core;

internal sealed record SettingsRecoveryRecord(int SchemaVersion, AppSettings Previous, AppSettings Attempted)
{
    // Schema 1 already contains enough information to infer the exact write
    // set. Unchanged fields are not owned by this recovery transaction.
    public AppSettings RestoreOnto(AppSettings current)
    {
        JsonObject before = JsonSerializer.SerializeToNode(Previous)!.AsObject();
        JsonObject attempted = JsonSerializer.SerializeToNode(Attempted)!.AsObject();
        JsonObject merged = JsonSerializer.SerializeToNode(current)!.AsObject();
        foreach ((string field, JsonNode? previousValue) in before)
        {
            JsonNode? attemptedValue = attempted[field];
            if (JsonNode.DeepEquals(previousValue, attemptedValue)) { continue; }
            JsonNode? currentValue = merged[field];
            if (!JsonNode.DeepEquals(currentValue, previousValue) && !JsonNode.DeepEquals(currentValue, attemptedValue))
            {
                throw new InvalidOperationException($"持久化设置的 {field} 已被其他入口更改；保留恢复记录，请检查设置后重试。");
            }
            merged[field] = previousValue?.DeepClone();
        }
        AppSettings restored = merged.Deserialize<AppSettings>() ?? throw new InvalidDataException("设置恢复合并失败。");
        SettingsValidator.Validate(restored);
        return restored;
    }
}
internal sealed record SettingsRestorationResult(bool Completed, Exception? Failure = null);
internal sealed record NetworkDisableIntent(int SchemaVersion = 1, bool SystemProxyOff = false, bool TunOff = false)
{
    public bool IsEmpty => !SystemProxyOff && !TunOff;
    public NetworkDisableIntent Merge(NetworkDisableIntent other) => this with
    {
        SystemProxyOff = SystemProxyOff || other.SystemProxyOff,
        TunOff = TunOff || other.TunOff
    };
    public AppSettings Apply(AppSettings settings) => settings with
    {
        SystemProxyEnabled = settings.SystemProxyEnabled && !SystemProxyOff,
        TunEnabled = settings.TunEnabled && !TunOff
    };
}

// One record per exclusive settings operation. AppSettings contains preferences,
// never controller secrets, subscription URLs or endpoint credentials.
internal sealed class SettingsRecoveryJournal(AppPaths paths)
{
    private const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public bool Exists
    {
        get
        {
            try { _ = File.GetAttributes(paths.SettingsRecoveryFile); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
    }

    public async Task PrepareAsync(AppSettings previous, AppSettings attempted, CancellationToken cancellationToken)
    {
        if (Exists)
        {
            throw new InvalidOperationException("设置恢复未完成，请先恢复后重试。");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new SettingsRecoveryRecord(1, previous, attempted), Options);
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("设置恢复记录超过大小限制。");
        }
        await AtomicFile.WriteBytesAsync(paths.SettingsRecoveryFile, bytes, cancellationToken).ConfigureAwait(false);
        WindowsPathSecurity.ProtectRuntimeFile(paths.SettingsRecoveryFile);
    }

    public async Task<SettingsRecoveryRecord?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!Exists) { return null; }
        await using FileStream stream = new(paths.SettingsRecoveryFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        byte[] bytes = new byte[MaximumBytes + 1];
        int count = 0;
        while (count < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) { break; }
            count += read;
        }
        if (count > MaximumBytes) { throw new InvalidDataException("设置恢复记录超过大小限制。"); }
        SettingsRecoveryRecord record = JsonSerializer.Deserialize<SettingsRecoveryRecord>(bytes.AsSpan(0, count), Options)
            ?? throw new InvalidDataException("设置恢复记录无效。");
        if (record.SchemaVersion != 1) { throw new InvalidDataException("设置恢复记录版本不兼容。"); }
        SettingsValidator.Validate(record.Previous);
        SettingsValidator.Validate(record.Attempted);
        return record;
    }

    public void Complete() => File.Delete(paths.SettingsRecoveryFile);

    // A separate, off-only companion is necessary when the older settings
    // record itself cannot be read or replaced. It never authorizes an enable.
    // It is owned by this same recovery mechanism, not a second transaction.
    public async Task<NetworkDisableIntent> ReadNetworkDisableIntentAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(paths.SettingsNetworkOffFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length > 1024) { throw new InvalidDataException("网络关闭意图记录超过大小限制。"); }
            NetworkDisableIntent intent = await JsonSerializer.DeserializeAsync<NetworkDisableIntent>(stream, Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("网络关闭意图记录无效。");
            if (intent.SchemaVersion != 1) { throw new InvalidDataException("网络关闭意图记录版本不兼容。"); }
            return intent;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
    }

    public async Task WriteNetworkDisableIntentAsync(NetworkDisableIntent intent, CancellationToken cancellationToken)
    {
        if (intent.IsEmpty) { File.Delete(paths.SettingsNetworkOffFile); return; }
        await AtomicFile.WriteBytesAsync(paths.SettingsNetworkOffFile, JsonSerializer.SerializeToUtf8Bytes(intent, Options), cancellationToken).ConfigureAwait(false);
        WindowsPathSecurity.ProtectRuntimeFile(paths.SettingsNetworkOffFile);
    }
}
