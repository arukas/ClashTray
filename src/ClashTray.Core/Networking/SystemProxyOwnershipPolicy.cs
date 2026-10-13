using System.Diagnostics.CodeAnalysis;

namespace ClashTray.Core;

/// <summary>
/// Compares the complete set of proxy values ClashTray owns after enabling System Proxy.
/// The legacy two-field ownership file is handled conservatively during restoration.
/// </summary>
public static class SystemProxyOwnershipPolicy
{
    public static ProxyOwnershipState Create(ProxyRegistryState appliedState)
    {
        ArgumentNullException.ThrowIfNull(appliedState);
        if (string.IsNullOrWhiteSpace(appliedState.ProxyServer))
        {
            throw new ArgumentException("An owned proxy state must include a proxy server.", nameof(appliedState));
        }

        return new ProxyOwnershipState(
            appliedState.ProxyServer,
            appliedState.ProxyOverride,
            appliedState.AutoConfigUrl,
            appliedState.AutoDetect);
    }

    public static bool IsOwnedByClashTray(
        ProxyRegistryState currentState,
        ProxyOwnershipState ownership)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(ownership);

        bool basicValuesMatch = currentState.ProxyEnable == 1
            && string.Equals(currentState.ProxyServer, ownership.ProxyServer, StringComparison.OrdinalIgnoreCase)
            && string.Equals(currentState.ProxyOverride ?? string.Empty, ownership.ProxyOverride ?? string.Empty, StringComparison.Ordinal);
        if (!basicValuesMatch)
        {
            return false;
        }

        // Files written by older builds did not capture AutoConfigURL/AutoDetect. They
        // remain eligible for state detection, but restoration performs an extra check
        // against the original backup in CanRestore.
        return ownership.AutoDetect is null
            || currentState.AutoDetect == ownership.AutoDetect.Value
                && string.Equals(currentState.AutoConfigUrl, ownership.AutoConfigUrl, StringComparison.Ordinal);
    }

    public static bool CanRestore(
        ProxyRegistryState currentState,
        ProxyRegistryState backupState,
        ProxyOwnershipState ownership)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(backupState);
        ArgumentNullException.ThrowIfNull(ownership);

        // Older builds wrote ownership after all proxy values had been applied. Keep
        // their strict check because they cannot prove which values were part of a
        // partially completed transition.
        if (ownership.AutoDetect is null)
        {
            return IsOwnedByClashTray(currentState, ownership)
                && currentState.AutoDetect == backupState.AutoDetect
                && string.Equals(currentState.AutoConfigUrl, backupState.AutoConfigUrl, StringComparison.Ordinal);
        }

        // New ownership metadata is written before registry mutation. Each changed
        // value must still be either the original value or ClashTray's intended value;
        // any third value belongs to another application and blocks restoration.
        return ownership.AutoDetect == backupState.AutoDetect
            && string.Equals(ownership.AutoConfigUrl, backupState.AutoConfigUrl, StringComparison.Ordinal)
            && currentState.AutoDetect == backupState.AutoDetect
            && string.Equals(currentState.AutoConfigUrl, backupState.AutoConfigUrl, StringComparison.Ordinal)
            && (currentState.ProxyEnable == backupState.ProxyEnable || currentState.ProxyEnable == 1)
            && Matches(currentState.ProxyServer, backupState.ProxyServer, ownership.ProxyServer, StringComparison.OrdinalIgnoreCase)
            && Matches(currentState.ProxyOverride, backupState.ProxyOverride, ownership.ProxyOverride, StringComparison.Ordinal);
    }

    private static bool Matches(
        string? current,
        string? original,
        string? intended,
        StringComparison comparison) =>
        string.Equals(current, original, comparison)
        || string.Equals(current, intended, comparison);
}

[SuppressMessage(
    "Design",
    "CA1054:URI-like parameters should not be strings",
    Justification = "The value is persisted in the Windows Internet Settings registry as an opaque user value.")]
[SuppressMessage(
    "Design",
    "CA1056:URI-like properties should not be strings",
    Justification = "The value is persisted in the Windows Internet Settings registry as an opaque user value.")]
public sealed record ProxyOwnershipState(
    string ProxyServer,
    string? ProxyOverride,
    string? AutoConfigUrl = null,
    int? AutoDetect = null);
