using System.Security.Cryptography;
using System.Text.Json;

namespace ClashTray.Core;

public enum NetworkRuleStoreLoadStatus
{
    FirstRun,
    Loaded,
    Recovered,
    ReadFailed
}

public sealed record NetworkRuleStoreLoadResult(
    ClashTray.Contracts.NetworkSwitchRuleSet Rules,
    NetworkRuleStoreLoadStatus Status,
    bool WasQuarantined,
    string? Message);

public sealed class NetworkRuleStore
{
    private const int CurrentSchemaVersion = 1;
    private const int MaxStoreBytes = 512 * 1024;
    private const int MaxRules = 128;
    private const int MaxSsidCharacters = 256;
    private const int MaxProtectedSsidCharacters = 4096;
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public NetworkRuleStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _paths.EnsureDirectories();
    }

    public async Task<NetworkRuleStoreLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.NetworkRulesFile))
        {
            return new NetworkRuleStoreLoadResult(
                new ClashTray.Contracts.NetworkSwitchRuleSet(false, null, []),
                NetworkRuleStoreLoadStatus.FirstRun,
                false,
                null);
        }

        try
        {
            if (new FileInfo(_paths.NetworkRulesFile).Length > MaxStoreBytes)
            {
                throw new InvalidDataException("网络规则文件超过支持的大小限制。");
            }

            await using FileStream stream = File.OpenRead(_paths.NetworkRulesFile);
            PersistedNetworkRuleSet persisted = await JsonSerializer.DeserializeAsync<PersistedNetworkRuleSet>(
                    stream,
                    _options,
                    cancellationToken)
                ?? throw new InvalidDataException("网络规则文件为空。");
            ClashTray.Contracts.NetworkSwitchRuleSet rules = ToDomain(persisted);
            return new NetworkRuleStoreLoadResult(
                rules,
                NetworkRuleStoreLoadStatus.Loaded,
                false,
                null);
        }
        catch (JsonException)
        {
            return QuarantineCorrupt("网络规则文件格式无效，原文件已隔离。");
        }
        catch (CryptographicException)
        {
            return QuarantineCorrupt("网络规则中的受保护数据无效，原文件已隔离。");
        }
        catch (InvalidDataException)
        {
            return QuarantineCorrupt("网络规则文件无效，原文件已隔离。");
        }
        catch (ArgumentException)
        {
            return QuarantineCorrupt("网络规则内容无效，原文件已隔离。");
        }
        catch (UnauthorizedAccessException)
        {
            return new NetworkRuleStoreLoadResult(
                new ClashTray.Contracts.NetworkSwitchRuleSet(false, null, []),
                NetworkRuleStoreLoadStatus.ReadFailed,
                false,
                "网络规则文件无权读取，已保留原文件；自动切换已停用。请检查权限后重试。" );
        }
        catch (IOException)
        {
            return new NetworkRuleStoreLoadResult(
                new ClashTray.Contracts.NetworkSwitchRuleSet(false, null, []),
                NetworkRuleStoreLoadStatus.ReadFailed,
                false,
                "网络规则文件无法读取，已保留原文件；自动切换已停用。请检查磁盘或文件权限。" );
        }
    }

    public async Task SaveAsync(
        ClashTray.Contracts.NetworkSwitchRuleSet rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ValidateDomain(rules);
        PersistedNetworkRuleSet persisted = new(
            CurrentSchemaVersion,
            rules.AutomaticSwitchingEnabled,
            rules.DefaultConfigurationId,
            rules.Rules.Select(rule => new PersistedNetworkRule(
                rule.RuleId,
                WindowsDataProtection.ProtectString(rule.Ssid),
                rule.ConfigurationId,
                rule.Enabled,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)).ToArray());
        await AtomicFile.WriteJsonAsync(
            _paths.NetworkRulesFile,
            persisted,
            _options,
            cancellationToken);
    }

    private static ClashTray.Contracts.NetworkSwitchRuleSet ToDomain(PersistedNetworkRuleSet persisted)
    {
        if (persisted.SchemaVersion != CurrentSchemaVersion
            || persisted.Rules is null
            || persisted.Rules.Count > MaxRules)
        {
            throw new InvalidDataException("网络规则 schema 或数量无效。");
        }

        ClashTray.Contracts.NetworkSwitchRule[] rules = persisted.Rules
            .Select(rule => new ClashTray.Contracts.NetworkSwitchRule(
                rule.RuleId,
                UnprotectSsid(rule.ProtectedSsid),
                rule.ConfigurationId,
                rule.Enabled))
            .ToArray();
        ClashTray.Contracts.NetworkSwitchRuleSet result = new(
            persisted.AutomaticSwitchingEnabled,
            persisted.DefaultConfigurationId,
            rules);
        ValidateDomain(result);
        return result;
    }

    private static string UnprotectSsid(string protectedSsid)
    {
        if (string.IsNullOrWhiteSpace(protectedSsid)
            || protectedSsid.Length > MaxProtectedSsidCharacters)
        {
            throw new InvalidDataException("网络规则中的 SSID 保护数据无效。");
        }

        return WindowsDataProtection.UnprotectString(protectedSsid);
    }

    private static void ValidateDomain(ClashTray.Contracts.NetworkSwitchRuleSet rules)
    {
        if (rules.Rules is null || rules.Rules.Count > MaxRules)
        {
            throw new ArgumentException("网络规则数量超过限制。", nameof(rules));
        }

        if (rules.DefaultConfigurationId is not null
            && string.IsNullOrWhiteSpace(rules.DefaultConfigurationId))
        {
            throw new ArgumentException("默认配置 ID 不能为空白。", nameof(rules));
        }

        HashSet<string> ruleIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> enabledSsids = new(StringComparer.Ordinal);
        foreach (ClashTray.Contracts.NetworkSwitchRule rule in rules.Rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.RuleId);
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.Ssid);
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.ConfigurationId);
            if (rule.Ssid.Length > MaxSsidCharacters)
            {
                throw new ArgumentException("SSID 长度超过限制。", nameof(rules));
            }

            if (!ruleIds.Add(rule.RuleId))
            {
                throw new ArgumentException("规则 ID 不能重复。", nameof(rules));
            }

            if (rule.Enabled && !enabledSsids.Add(rule.Ssid))
            {
                throw new ArgumentException("启用的 SSID 规则不能重复。", nameof(rules));
            }
        }
    }

    private NetworkRuleStoreLoadResult QuarantineCorrupt(string message)
    {
        string quarantinePath =
            $"{_paths.NetworkRulesFile}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        try
        {
            File.Move(_paths.NetworkRulesFile, quarantinePath);
            return new NetworkRuleStoreLoadResult(
                new ClashTray.Contracts.NetworkSwitchRuleSet(false, null, []),
                NetworkRuleStoreLoadStatus.Recovered,
                true,
                message);
        }
        catch (UnauthorizedAccessException)
        {
            return new NetworkRuleStoreLoadResult(
                new ClashTray.Contracts.NetworkSwitchRuleSet(false, null, []),
                NetworkRuleStoreLoadStatus.ReadFailed,
                false,
                "网络规则损坏但无权隔离，已停用自动切换；原文件未覆盖。" );
        }
        catch (IOException)
        {
            return new NetworkRuleStoreLoadResult(
                new ClashTray.Contracts.NetworkSwitchRuleSet(false, null, []),
                NetworkRuleStoreLoadStatus.ReadFailed,
                false,
                "网络规则损坏且无法隔离，已停用自动切换；原文件未覆盖。" );
        }
    }

    private sealed record PersistedNetworkRuleSet(
        int SchemaVersion,
        bool AutomaticSwitchingEnabled,
        string? DefaultConfigurationId,
        IReadOnlyList<PersistedNetworkRule>? Rules);

    private sealed record PersistedNetworkRule(
        string RuleId,
        string ProtectedSsid,
        string ConfigurationId,
        bool Enabled,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);
}
