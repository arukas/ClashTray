namespace ClashTray.Core.Tests;

[TestClass]
public sealed class SystemProxyOwnershipPolicyTests
{
    [TestMethod]
    public void CreatedOwnershipCapturesAllValuesClashTrayLeavesApplied()
    {
        ProxyRegistryState applied = new(
            ProxyEnable: 1,
            ProxyServer: "127.0.0.1:7890",
            ProxyOverride: "localhost;127.*",
            AutoConfigUrl: "https://pac.example.test/proxy.pac",
            AutoDetect: 1);

        ProxyOwnershipState ownership = SystemProxyOwnershipPolicy.Create(applied);

        Assert.AreEqual(applied.ProxyServer, ownership.ProxyServer);
        Assert.AreEqual(applied.ProxyOverride, ownership.ProxyOverride);
        Assert.AreEqual(applied.AutoConfigUrl, ownership.AutoConfigUrl);
        Assert.AreEqual(applied.AutoDetect, ownership.AutoDetect);
    }

    [TestMethod]
    public void OwnershipRejectsCompetingChangesToAnyOwnedValue()
    {
        ProxyRegistryState applied = CreateAppliedState();
        ProxyOwnershipState ownership = SystemProxyOwnershipPolicy.Create(applied);

        Assert.IsTrue(SystemProxyOwnershipPolicy.IsOwnedByClashTray(applied, ownership));
        Assert.IsFalse(SystemProxyOwnershipPolicy.IsOwnedByClashTray(
            applied with { ProxyServer = "other:8080" },
            ownership));
        Assert.IsFalse(SystemProxyOwnershipPolicy.IsOwnedByClashTray(
            applied with { ProxyOverride = "other" },
            ownership));
        Assert.IsFalse(SystemProxyOwnershipPolicy.IsOwnedByClashTray(
            applied with { AutoConfigUrl = "https://other.example.test/pac" },
            ownership));
        Assert.IsFalse(SystemProxyOwnershipPolicy.IsOwnedByClashTray(
            applied with { AutoDetect = 0 },
            ownership));
    }

    [TestMethod]
    public void InterruptedPartialApplyCanRestoreOnlyWhenEveryValueMatchesOriginalOrIntendedState()
    {
        ProxyRegistryState backup = new(
            ProxyEnable: 0,
            ProxyServer: null,
            ProxyOverride: null,
            AutoConfigUrl: "https://pac.example.test/original.pac",
            AutoDetect: 1);
        ProxyRegistryState intended = backup with
        {
            ProxyEnable = 1,
            ProxyServer = "127.0.0.1:7890",
            ProxyOverride = "localhost;127.*"
        };
        ProxyOwnershipState ownership = SystemProxyOwnershipPolicy.Create(intended);
        ProxyRegistryState partial = backup with
        {
            ProxyEnable = intended.ProxyEnable,
            ProxyServer = intended.ProxyServer
        };

        Assert.IsTrue(SystemProxyOwnershipPolicy.CanRestore(partial, backup, ownership));
        Assert.IsFalse(SystemProxyOwnershipPolicy.CanRestore(
            partial with { ProxyOverride = "proxy-from-another-app" },
            backup,
            ownership));
    }

    [TestMethod]
    public void DisabledOrExternallyDisabledProxyIsNotOwned()
    {
        ProxyRegistryState applied = CreateAppliedState();
        ProxyOwnershipState ownership = SystemProxyOwnershipPolicy.Create(applied);

        Assert.IsFalse(SystemProxyOwnershipPolicy.IsOwnedByClashTray(
            applied with { ProxyEnable = 0 },
            ownership));
    }

    [TestMethod]
    public void LegacyOwnershipCanRestoreOnlyWhenUntouchedValuesMatchBackup()
    {
        ProxyRegistryState backup = new(0, null, null, "https://pac.example.test/original.pac", 1);
        ProxyRegistryState current = backup with
        {
            ProxyEnable = 1,
            ProxyServer = "127.0.0.1:7890",
            ProxyOverride = "localhost"
        };
        ProxyOwnershipState legacyOwnership = new(current.ProxyServer!, current.ProxyOverride);

        Assert.IsTrue(SystemProxyOwnershipPolicy.CanRestore(current, backup, legacyOwnership));
        Assert.IsFalse(SystemProxyOwnershipPolicy.CanRestore(
            current with { AutoDetect = 0 },
            backup,
            legacyOwnership));
        Assert.IsFalse(SystemProxyOwnershipPolicy.CanRestore(
            current with { AutoConfigUrl = "https://other.example.test/pac" },
            backup,
            legacyOwnership));
    }

    private static ProxyRegistryState CreateAppliedState() =>
        new(
            ProxyEnable: 1,
            ProxyServer: "127.0.0.1:7890",
            ProxyOverride: "localhost",
            AutoConfigUrl: "https://pac.example.test/proxy.pac",
            AutoDetect: 1);
}
