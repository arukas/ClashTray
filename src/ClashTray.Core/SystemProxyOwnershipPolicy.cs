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

        if (!IsOwnedByClashTray(currentState, ownership))
        {
            return false;
        }

        // A legacy ownership file cannot prove that AutoConfigURL or AutoDetect were
        // untouched. Since enabling ClashTray never changes either value, only restore
        // when they still equal the original backup.
        return ownership.AutoDetect is not null
            || currentState.AutoDetect == backupState.AutoDetect
                && string.Equals(currentState.AutoConfigUrl, backupState.AutoConfigUrl, StringComparison.Ordinal);
    }
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
