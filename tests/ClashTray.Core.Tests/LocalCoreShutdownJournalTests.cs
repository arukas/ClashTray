using System.Diagnostics;
using System.Text.Json;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class LocalCoreShutdownJournalTests
{
    [TestMethod]
    public async Task MalformedRecoveryRecordsFailClosedWithoutCallingRecoveryOrDeletingRecord()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        paths.EnsureDirectories();
        LocalCoreShutdownJournal journal = new(paths, _ => true);
        string executablePath = paths.ManagedCoreExecutable;
        long startTimeUtcTicks = DateTime.UtcNow.Ticks;
        string[] malformedRecords =
        [
            """{"version":1,"identity":null}""",
            """{"version":1}""",
            JsonSerializer.Serialize(new
            {
                version = 2,
                identity = new { processId = 42, startTimeUtcTicks, executablePath }
            }),
            JsonSerializer.Serialize(new
            {
                version = 1,
                identity = new { processId = 0, startTimeUtcTicks, executablePath }
            }),
            JsonSerializer.Serialize(new
            {
                version = 1,
                identity = new { processId = 42, startTimeUtcTicks = 0, executablePath }
            }),
            JsonSerializer.Serialize(new
            {
                version = 1,
                identity = new { processId = 42, startTimeUtcTicks }
            }),
            JsonSerializer.Serialize(new
            {
                version = 1,
                identity = new { processId = 42, startTimeUtcTicks, executablePath = string.Empty }
            })
        ];
        int recoveryCalls = 0;

        try
        {
            foreach (string malformedRecord in malformedRecords)
            {
                await File.WriteAllTextAsync(paths.LocalCoreShutdownFile, malformedRecord);

                LocalCoreShutdownJournalResult result = await journal.RecoverPendingStopAsync(
                    (_, _) =>
                    {
                        recoveryCalls++;
                        return Task.FromResult(new LocalCoreShutdownJournalResult(true, true));
                    });

                Assert.IsFalse(result.Succeeded, malformedRecord);
                Assert.IsTrue(result.RecordFound, malformedRecord);
                Assert.IsFalse(string.IsNullOrWhiteSpace(result.Detail), malformedRecord);
                Assert.AreEqual(0, recoveryCalls, malformedRecord);
                Assert.AreEqual(malformedRecord, await File.ReadAllTextAsync(paths.LocalCoreShutdownFile));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RecoveryStopsOnlyExactPidStartTimeAndExecutableMatch()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        LocalCoreShutdownJournal journal = new(paths, _ => true);
        Process child = StartTestChild();
        try
        {
            LocalCoreProcessIdentity identity = await CaptureIdentityAsync(child);
            LocalCoreShutdownJournalResult saved = await journal.SavePendingStopAsync(identity);
            Assert.IsTrue(saved.Succeeded, saved.Detail);
            StringAssert.Contains(await File.ReadAllTextAsync(paths.LocalCoreShutdownFile), "\"version\":1", StringComparison.Ordinal);

            LocalCoreShutdownJournalResult recovered = await journal.RecoverPendingStopAsync();

            Assert.IsTrue(recovered.Succeeded, recovered.Detail);
            StringAssert.Contains(recovered.Detail, "停止与恢复记录身份完全匹配", StringComparison.Ordinal);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.IsTrue(child.HasExited);
            Assert.IsFalse(File.Exists(paths.LocalCoreShutdownFile));
        }
        finally
        {
            StopTestChild(child);
            child.Dispose();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RecoveryPreservesProcessWhenPidWasReusedForAnotherImage()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        LocalCoreShutdownJournal journal = new(paths, _ => true);
        Process child = StartTestChild();
        try
        {
            LocalCoreProcessIdentity identity = (await CaptureIdentityAsync(child)) with
            {
                ExecutablePath = Path.Combine(root, "different.exe")
            };
            Assert.IsTrue((await journal.SavePendingStopAsync(identity)).Succeeded);

            LocalCoreShutdownJournalResult recovered = await journal.RecoverPendingStopAsync();

            Assert.IsTrue(recovered.Succeeded, recovered.Detail);
            Assert.IsFalse(child.HasExited, "A process whose image does not match the journal must be preserved.");
            Assert.IsFalse(File.Exists(paths.LocalCoreShutdownFile));
        }
        finally
        {
            StopTestChild(child);
            child.Dispose();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RecoveryRefusesProcessOutsideManagedMihomoPath()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        LocalCoreShutdownJournal testWriter = new(paths, _ => true);
        LocalCoreShutdownJournal productionReader = new(paths);
        Process child = StartTestChild();
        try
        {
            LocalCoreProcessIdentity identity = await CaptureIdentityAsync(child);
            Assert.IsTrue((await testWriter.SavePendingStopAsync(identity)).Succeeded);

            LocalCoreShutdownJournalResult recovered = await productionReader.RecoverPendingStopAsync();

            Assert.IsFalse(recovered.Succeeded);
            Assert.IsFalse(child.HasExited, "An unmanaged image must not be stopped from a recovery record.");
            Assert.IsTrue(File.Exists(paths.LocalCoreShutdownFile), "An untrusted record remains for explicit recovery.");
        }
        finally
        {
            StopTestChild(child);
            child.Dispose();
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RecoveryPromotesCompleteStagedRecordAfterInterruptedAtomicReplace()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        LocalCoreShutdownJournal journal = new(paths);
        LocalCoreProcessIdentity identity = new(
            42_424,
            DateTime.UtcNow.Ticks,
            paths.ManagedCoreExecutable);
        string temporaryPath = paths.LocalCoreShutdownFile + ".tmp";
        Directory.CreateDirectory(paths.LocalRoot);
        string stagedContent = JsonSerializer.Serialize(new
        {
            version = 1,
            identity = new
            {
                processId = identity.ProcessId,
                startTimeUtcTicks = identity.StartTimeUtcTicks,
                executablePath = identity.ExecutablePath
            }
        });
        await File.WriteAllTextAsync(temporaryPath, stagedContent);
        LocalCoreProcessIdentity? recoveredIdentity = null;

        try
        {
            LocalCoreShutdownJournalResult recovered = await journal.RecoverPendingStopAsync(
                (pending, _) =>
                {
                    recoveredIdentity = pending;
                    return Task.FromResult(new LocalCoreShutdownJournalResult(true, true, "simulated owned process stopped"));
                });

            Assert.IsTrue(recovered.Succeeded, recovered.Detail);
            Assert.AreEqual(identity, recoveredIdentity);
            Assert.IsFalse(File.Exists(paths.LocalCoreShutdownFile));
            Assert.IsFalse(File.Exists(temporaryPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RecoveryFailsClosedWhenMainAndStagedRecordsBothExist()
    {
        string root = CreateRoot();
        AppPaths paths = new(Path.Combine(root, "local"), Path.Combine(root, "program"));
        LocalCoreShutdownJournal journal = new(paths);
        Directory.CreateDirectory(paths.LocalRoot);
        await File.WriteAllTextAsync(paths.LocalCoreShutdownFile, "{}");
        await File.WriteAllTextAsync(paths.LocalCoreShutdownFile + ".tmp", "{}");
        bool recoveryCalled = false;

        try
        {
            LocalCoreShutdownJournalResult recovered = await journal.RecoverPendingStopAsync(
                (_, _) =>
                {
                    recoveryCalled = true;
                    return Task.FromResult(new LocalCoreShutdownJournalResult(true, true));
                });

            Assert.IsFalse(recovered.Succeeded);
            Assert.IsFalse(recoveryCalled);
            Assert.IsTrue(File.Exists(paths.LocalCoreShutdownFile));
            Assert.IsTrue(File.Exists(paths.LocalCoreShutdownFile + ".tmp"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static Process StartTestChild()
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            Arguments = "-t 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start isolated shutdown test child.");
        return child;
    }

    private static async Task<LocalCoreProcessIdentity> CaptureIdentityAsync(Process process)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            process.Refresh();
            if (process.HasExited)
            {
                break;
            }

            try
            {
                string? imagePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(imagePath))
                {
                    return new LocalCoreProcessIdentity(
                        process.Id,
                        process.StartTime.ToUniversalTime().Ticks,
                        Path.GetFullPath(imagePath));
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException("Test child image path is unavailable.");
    }

    private static void StopTestChild(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
