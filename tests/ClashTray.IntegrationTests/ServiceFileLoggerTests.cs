using ClashTray.Testing;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashTray.Contracts;
using ClashTray.Core;
using ClashTray.Service;
using Microsoft.Extensions.Logging;

namespace ClashTray.IntegrationTests;

[TestClass]
public sealed class ServiceFileLoggerTests
{
    [TestMethod]
    [TestCategory("RequiresWindowsAcl")]
    public void CoreUpdatePermissionPolicyPreservesTheManagedUserReadOnlyBoundary()
    {
        string root = TestFixtureDirectory.Create();
        TestFixtureDirectory.Run(root, () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string sid = identity.User!.Value;
            WindowsCoreUpdatePermissionPolicy policy = new();
            Directory.CreateDirectory(paths.CoreRoot);
            File.WriteAllText(paths.ManagedCoreExecutable, "fixture executable");
            File.WriteAllText(paths.ManagedCoreMetadata, "fixture metadata");
            policy.PrepareDirectories(paths, sid);
            policy.ProtectInstalledFiles(paths, sid, protectDirectory: true);
            foreach (string path in new[] { paths.ManagedCoreExecutable, paths.ManagedCoreMetadata })
            {
                FileSecurity security = new FileInfo(path).GetAccessControl();
                Assert.IsTrue(security.AreAccessRulesProtected);
                FileSystemAccessRule[] rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(rule => rule.IdentityReference.Value == sid).ToArray();
                Assert.HasCount(1, rules);
                Assert.AreEqual(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rules[0].FileSystemRights);
            }
        });
    }

    [TestMethod]
    public async Task ServiceEventIsPersistedToLogFile()
    {
        string root = CreateTempRoot();
        await TestFixtureDirectory.RunAsync(root, async () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            string logDirectory = Path.Combine(root, "logs");
            using (RollingFileLoggerProvider provider = new(logDirectory))
            using (ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider)))
            {
                await using ServiceRuntimeController controller = new(paths, loggerFactory: factory);
                ServiceResponse response = await controller.HandleAsync(
                    new ServiceRequest(Guid.NewGuid(), ServiceCommand.GetStatus),
                    CancellationToken.None);
                Assert.IsFalse(response.Succeeded);
            }

            string[] files = Directory.GetFiles(logDirectory, "service-*.log");
            Assert.HasCount(1, files);
            string content = await File.ReadAllTextAsync(files[0]);
            StringAssert.Contains(content, "unsupported protocol version", StringComparison.Ordinal);
            StringAssert.Contains(content, "Warning", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void RollingFileLoggerBoundsTotalLogVolume()
    {
        string root = CreateTempRoot();
        TestFixtureDirectory.Run(root, () =>
        {
            const long maxFileBytes = 4096;
            const int maxRetainedFiles = 3;
            using (RollingFileLoggerProvider provider = new(
                       root,
                       maxFileBytes: maxFileBytes,
                       maxRetainedFiles: maxRetainedFiles))
            {
                ILogger logger = provider.CreateLogger("BoundTest");
                for (int index = 0; index < 400; index++)
                {
                    logger.LogInformation("bounded log entry {Index} with some padding payload", index);
                }
            }

            string[] files = Directory.GetFiles(root, "service-*.log");
            Assert.IsTrue(files.Length is >= 2 and <= maxRetainedFiles, $"expected 2..{maxRetainedFiles} files, got {files.Length}");
            foreach (string file in files)
            {
                // One entry may straddle the rotation boundary.
                Assert.IsTrue(new FileInfo(file).Length < maxFileBytes + 4096, $"{file} exceeded the size cap");
            }
        });
    }

    [TestMethod]
    public void RollingFileLoggerRedactsSecrets()
    {
        string root = CreateTempRoot();
        TestFixtureDirectory.Run(root, () =>
        {
            using (RollingFileLoggerProvider provider = new(root))
            {
                ILogger logger = provider.CreateLogger("RedactionTest");
                logger.LogWarning(
                    "failure with Authorization: Bearer topsecretvalue123 and https://example.com/sub?token=querysecret456");
            }

            string[] files = Directory.GetFiles(root, "service-*.log");
            Assert.HasCount(1, files);
            string content = File.ReadAllText(files[0]);
            Assert.IsFalse(content.Contains("topsecretvalue123", StringComparison.Ordinal), "authorization header leaked");
            Assert.IsFalse(content.Contains("querysecret456", StringComparison.Ordinal), "query secret leaked");
            StringAssert.Contains(content, "[已隐藏]", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    [TestCategory("RequiresWindowsAcl")]
    public void ServiceLogDirectoryIsAdministratorsWriteUsersReadOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("ACL assertions are Windows-only.");
            return;
        }

        string root = CreateTempRoot();
        TestFixtureDirectory.Run(root, () =>
        {
            AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
            paths.EnsureProgramDataDirectories();

            DirectoryInfo logsDirectory = new DirectoryInfo(paths.ServiceLogsRoot);
            Assert.IsTrue(logsDirectory.Exists);
            DirectorySecurity security = logsDirectory.GetAccessControl();
            Assert.IsTrue(security.AreAccessRulesProtected, "log directory must not inherit parent ACLs");

            AuthorizationRuleCollection rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: false,
                typeof(SecurityIdentifier));
            Assert.IsTrue(HasRule(rules, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl));
            Assert.IsTrue(HasRule(rules, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl));
            Assert.IsTrue(HasRule(rules, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute));
            Assert.IsFalse(HasRule(rules, WellKnownSidType.BuiltinUsersSid, FileSystemRights.Write), "users must not write service logs");
        });
    }

    private static bool HasRule(
        AuthorizationRuleCollection rules,
        WellKnownSidType sidType,
        FileSystemRights requiredRights)
    {
        SecurityIdentifier sid = new SecurityIdentifier(sidType, null);
        foreach (AuthorizationRule rule in rules)
        {
            if (rule is FileSystemAccessRule accessRule
                && accessRule.AccessControlType == AccessControlType.Allow
                && accessRule.IdentityReference.Equals(sid)
                && (accessRule.FileSystemRights & requiredRights) == requiredRights)
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateTempRoot()
    {
        string root = TestFixtureDirectory.Create();
        Directory.CreateDirectory(root);
        return root;
    }

}
