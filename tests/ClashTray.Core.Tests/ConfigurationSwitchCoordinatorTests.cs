using ClashTray.Contracts;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class ConfigurationSwitchCoordinatorTests
{
    [TestMethod]
    public async Task SuccessfulSwitchCommitsAndRemovesJournal()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationProfile candidate = CreateProfile("new");
        FakeSwitchOperations operations = new FakeSwitchOperations(candidate);
        await using ConfigurationSwitchCoordinator coordinator =
            new ConfigurationSwitchCoordinator(new ConfigurationSwitchJournalStore(paths));

        try
        {
            ConfigurationSwitchResult result = await coordinator.ExecuteAsync(
                ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.Manual, candidate.Id),
                operations);

            Assert.AreEqual(ConfigurationSwitchOutcome.Committed, result.Outcome);
            Assert.AreEqual(candidate.Id, operations.CurrentConfigurationId);
            Assert.IsTrue(operations.Applied);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
            Assert.IsTrue(operations.Stages.Contains(ConfigurationSwitchStage.CandidateValidated));
            Assert.AreEqual(ConfigurationSwitchStage.Committed, result.Stage);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task FailedSwitchRollsBackAndRemovesJournal()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationProfile candidate = CreateProfile("new");
        FakeSwitchOperations operations = new FakeSwitchOperations(candidate) { FailApply = true };
        await using ConfigurationSwitchCoordinator coordinator =
            new ConfigurationSwitchCoordinator(new ConfigurationSwitchJournalStore(paths));

        try
        {
            ConfigurationSwitchResult result = await coordinator.ExecuteAsync(
                ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.NetworkRule, candidate.Id),
                operations);

            Assert.AreEqual(ConfigurationSwitchOutcome.RolledBack, result.Outcome);
            Assert.AreEqual(ErrorCode.ConfigurationSwitchFailed, result.ErrorCode);
            Assert.AreEqual("old", operations.CurrentConfigurationId);
            Assert.IsTrue(operations.RolledBack);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RollbackFailureLeavesJournalAtRollbackFailedStage()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationProfile candidate = CreateProfile("new");
        FakeSwitchOperations operations = new FakeSwitchOperations(candidate)
        {
            FailApply = true,
            FailRollback = true
        };
        await using ConfigurationSwitchCoordinator coordinator =
            new ConfigurationSwitchCoordinator(new ConfigurationSwitchJournalStore(paths));

        try
        {
            ConfigurationSwitchResult result = await coordinator.ExecuteAsync(
                ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.NetworkDefault, candidate.Id),
                operations);

            Assert.AreEqual(ConfigurationSwitchOutcome.RollbackFailed, result.Outcome);
            Assert.AreEqual(ErrorCode.ConfigurationSwitchRollbackFailed, result.ErrorCode);
            Assert.IsTrue(File.Exists(paths.ConfigurationSwitchJournalFile));
            ConfigurationSwitchJournalLoadResult loaded =
                await new ConfigurationSwitchJournalStore(paths).LoadAsync();
            Assert.AreEqual(ConfigurationSwitchStage.RollbackFailed, loaded.Journal!.Stage);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PendingJournalFromAnotherOperationRejectsNewSwitch()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationProfile candidate = CreateProfile("new");
        FakeSwitchOperations operations = new FakeSwitchOperations(candidate);
        ConfigurationSwitchJournalStore journalStore = new ConfigurationSwitchJournalStore(paths);
        await journalStore.SaveAsync(ConfigurationSwitchJournal.Create(
            Guid.NewGuid(),
            ConfigurationSwitchSource.Recovery,
            "old",
            candidate.Id,
            previousCoreWasRunning: false,
            previousSystemProxyPreference: false,
            previousSystemProxyState: SystemProxyState.Off,
            previousTunPreference: false,
            previousTunState: TunState.Off,
            previousControllerGeneration: 1).WithStage(ConfigurationSwitchStage.RollbackFailed));
        await using ConfigurationSwitchCoordinator coordinator =
            new ConfigurationSwitchCoordinator(journalStore);

        try
        {
            ConfigurationSwitchResult result = await coordinator.ExecuteAsync(
                ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.Manual, candidate.Id),
                operations);

            Assert.AreEqual(ConfigurationSwitchOutcome.Rejected, result.Outcome);
            Assert.AreEqual(ErrorCode.ConfigurationSwitchRecoveryRequired, result.ErrorCode);
            Assert.IsFalse(operations.Applied);
            Assert.IsTrue(File.Exists(paths.ConfigurationSwitchJournalFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task PreparedJournalForSameOperationPreservesContentBackupId()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationProfile candidate = CreateProfile("new");
        FakeSwitchOperations operations = new FakeSwitchOperations(candidate)
        {
            FailApply = true,
            FailRollback = true
        };
        Guid operationId = Guid.NewGuid();
        Guid backupId = Guid.NewGuid();
        ConfigurationSwitchJournalStore journalStore = new ConfigurationSwitchJournalStore(paths);
        await journalStore.SaveAsync(ConfigurationSwitchJournal.Create(
            operationId,
            ConfigurationSwitchSource.SubscriptionRefresh,
            "old",
            candidate.Id,
            previousCoreWasRunning: false,
            previousSystemProxyPreference: false,
            previousSystemProxyState: SystemProxyState.Off,
            previousTunPreference: false,
            previousTunState: TunState.Off,
            previousControllerGeneration: 1)
            .WithContentBackup(backupId));
        await using ConfigurationSwitchCoordinator coordinator =
            new ConfigurationSwitchCoordinator(journalStore);

        try
        {
            ConfigurationSwitchResult result = await coordinator.ExecuteAsync(
                new ConfigurationSwitchRequest(
                    operationId,
                    ConfigurationSwitchSource.SubscriptionRefresh,
                    candidate.Id),
                operations);

            Assert.AreEqual(ConfigurationSwitchOutcome.RollbackFailed, result.Outcome);
            ConfigurationSwitchJournalLoadResult loaded = await journalStore.LoadAsync();
            Assert.AreEqual(backupId, loaded.Journal!.ContentBackupId);
            Assert.AreEqual(ConfigurationSwitchStage.RollbackFailed, loaded.Journal.Stage);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InvalidCandidateDoesNotCreateJournalOrApply()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationProfile candidate = CreateProfile("new");
        FakeSwitchOperations operations = new FakeSwitchOperations(candidate)
        {
            FailValidation = true
        };
        await using ConfigurationSwitchCoordinator coordinator =
            new ConfigurationSwitchCoordinator(new ConfigurationSwitchJournalStore(paths));

        try
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                coordinator.ExecuteAsync(
                    ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.Manual, candidate.Id),
                    operations));

            Assert.IsFalse(operations.Applied);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task SelectingCurrentConfigurationIsNoOp()
    {
        string root = CreateRoot();
        AppPaths paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "program"));
        ConfigurationProfile candidate = CreateProfile("old");
        FakeSwitchOperations operations = new FakeSwitchOperations(candidate);
        await using ConfigurationSwitchCoordinator coordinator =
            new ConfigurationSwitchCoordinator(new ConfigurationSwitchJournalStore(paths));

        try
        {
            ConfigurationSwitchResult result = await coordinator.ExecuteAsync(
                ConfigurationSwitchRequest.Create(ConfigurationSwitchSource.Manual, candidate.Id),
                operations);

            Assert.AreEqual(ConfigurationSwitchOutcome.NoOp, result.Outcome);
            Assert.IsFalse(operations.Applied);
            Assert.IsFalse(File.Exists(paths.ConfigurationSwitchJournalFile));
        }
        finally
        {
            DeleteRoot(root);
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

    private static ConfigurationProfile CreateProfile(string id) =>
        new(id, id, $"{id}.yaml", null, null, false);

    private sealed class FakeSwitchOperations : IConfigurationSwitchOperations
    {
        private readonly ConfigurationProfile _candidate;

        public FakeSwitchOperations(ConfigurationProfile candidate)
        {
            _candidate = candidate;
        }

        public string? CurrentConfigurationId { get; private set; } = "old";

        public bool Applied { get; private set; }

        public bool RolledBack { get; private set; }

        public bool FailApply { get; init; }

        public bool FailRollback { get; init; }

        public bool FailValidation { get; init; }

        public List<ConfigurationSwitchStage> Stages { get; } = [];

        public Task<ConfigurationProfile?> ResolveCandidateAsync(
            string id,
            CancellationToken cancellationToken) =>
            Task.FromResult<ConfigurationProfile?>(
                string.Equals(id, _candidate.Id, StringComparison.OrdinalIgnoreCase)
                    ? _candidate
                    : null);

        public Task ValidateCandidateAsync(
            ConfigurationProfile candidate,
            CancellationToken cancellationToken)
        {
            if (FailValidation)
            {
                throw new InvalidDataException("candidate rejected");
            }

            return Task.CompletedTask;
        }

        public Task<ConfigurationSwitchRuntimeState> CaptureStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new ConfigurationSwitchRuntimeState(
                "old",
                false,
                false,
                SystemProxyState.Off,
                false,
                TunState.Off,
                4));

        public async Task ApplyAsync(
            ConfigurationSwitchContext context,
            CancellationToken cancellationToken)
        {
            Applied = true;
            Stages.Add(context.Journal.Stage);
            await context.SetStageAsync(ConfigurationSwitchStage.RuntimePromoted, cancellationToken);
            if (FailApply)
            {
                throw new InvalidOperationException("candidate failed");
            }

            CurrentConfigurationId = _candidate.Id;
        }

        public async Task RollbackAsync(
            ConfigurationSwitchContext context,
            Exception failure,
            CancellationToken cancellationToken)
        {
            if (FailRollback)
            {
                throw new InvalidOperationException("rollback failed");
            }

            RolledBack = true;
            CurrentConfigurationId = context.PreviousState.ActiveConfigurationId;
            await Task.CompletedTask;
        }
    }
}
