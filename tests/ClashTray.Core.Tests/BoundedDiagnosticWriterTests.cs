using System.Text;

namespace ClashTray.Core.Tests;

[TestClass]
public sealed class BoundedDiagnosticWriterTests
{
    private static readonly string[] SyntheticSecrets =
    [
        "alice",
        "url-password",
        "url-token",
        "auth-token",
        "account-password"
    ];

    [TestMethod]
    public void SyntheticUrlAuthorizationAndCredentialSecretsDoNotReachDiagnosticFiles()
    {
        string root = CreateRoot();
        string message =
            "Request https://alice:url-password@subscription.invalid/config?token=url-token failed; "
            + "Authorization: Bearer auth-token; password=account-password.";
        try
        {
            Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(root, new InvalidOperationException(message)));
            string content = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(root, "startup-error-*.log")
                    .Select(File.ReadAllText));

            foreach (string secret in SyntheticSecrets)
            {
                Assert.IsFalse(content.Contains(secret, StringComparison.Ordinal), $"Found secret: {secret}");
            }

            StringAssert.Contains(content, "InvalidOperationException", StringComparison.Ordinal);
            StringAssert.Contains(content, "[已隐藏]", StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void TruncationInsideSensitiveValuesNeverWritesTheirVisiblePrefixes()
    {
        string root = CreateRoot();
        const string marker = "SYNTHETIC_CREDENTIAL_MARKER_MUST_NEVER_REACH_DISK";
        string prefix = new string('x', 512 - 32);
        string[] messages =
        [
            prefix + " https://user:" + marker + new string('p', 2048) + "@subscription.invalid/config",
            prefix + " Authorization: Bearer " + marker,
            prefix + "?token=" + marker
        ];
        try
        {
            foreach (string message in messages)
            {
                Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(root, new InvalidOperationException(message)));
            }

            string content = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(root, "startup-error-*.log")
                    .Select(File.ReadAllText));

            Assert.IsFalse(content.Contains("SYNTHETIC_", StringComparison.Ordinal));
            Assert.IsTrue(content.Length <= BoundedDiagnosticWriter.MaximumFileCount * BoundedDiagnosticWriter.MaximumFileBytes);
            foreach (string file in Directory.EnumerateFiles(root, "startup-error-*.log"))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    Assert.IsTrue(Encoding.UTF8.GetByteCount(line) + 1 <= BoundedDiagnosticWriter.MaximumRecordBytes);
                }
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void SafeEarlierUrlsAndMixedSecretsCannotExposeLaterUrlPrefixesAtScanBoundary()
    {
        string root = CreateRoot();
        const int scanWindow = 512 + 256;
        const string urlSecret = "SYNTHETIC_URL_PASSWORD_FRAGMENT";
        const string authorizationSecret = "SYNTHETIC_AUTHORIZATION_TOKEN";
        const string querySecret = "SYNTHETIC_QUERY_TOKEN";
        string safeUrlPrefix = "https://safe.invalid/path ";
        string[] messages =
        [
            CreateBoundaryMessage(secretAtOffset: scanWindow - 1, includeMixedSecrets: false),
            CreateBoundaryMessage(secretAtOffset: scanWindow, includeMixedSecrets: true),
            CreateBoundaryMessage(secretAtOffset: scanWindow + 1, includeMixedSecrets: true)
        ];

        try
        {
            foreach (string message in messages)
            {
                Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(root, new InvalidOperationException(message)));
            }

            string content = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(root, "startup-error-*.log")
                    .Select(File.ReadAllText));

            Assert.IsFalse(content.Contains("SYNTHETIC_URL_PASSWORD", StringComparison.Ordinal));
            Assert.IsFalse(content.Contains(authorizationSecret, StringComparison.Ordinal));
            Assert.IsFalse(content.Contains(querySecret, StringComparison.Ordinal));
            StringAssert.Contains(content, "safe.invalid", StringComparison.Ordinal);
            Assert.IsTrue(content.Length <= BoundedDiagnosticWriter.MaximumFileCount * BoundedDiagnosticWriter.MaximumFileBytes);
            foreach (string file in Directory.EnumerateFiles(root, "startup-error-*.log"))
            {
                Assert.IsTrue(new FileInfo(file).Length <= BoundedDiagnosticWriter.MaximumFileBytes);
                foreach (string line in File.ReadAllLines(file))
                {
                    Assert.IsTrue(Encoding.UTF8.GetByteCount(line) + 1 <= BoundedDiagnosticWriter.MaximumRecordBytes);
                }
            }
        }
        finally
        {
            DeleteRoot(root);
        }

        string CreateBoundaryMessage(int secretAtOffset, bool includeMixedSecrets)
        {
            string mixed = includeMixedSecrets
                ? $"Authorization: Bearer {authorizationSecret} https://second-safe.invalid/x?token={querySecret} "
                : string.Empty;
            string secondSafeUrl = includeMixedSecrets ? string.Empty : "https://second-safe.invalid/x ";
            string beforeCredentialUrl = safeUrlPrefix + mixed + secondSafeUrl;
            int credentialUrlStart = beforeCredentialUrl.Length + 220;
            string filler = new string('x', credentialUrlStart - beforeCredentialUrl.Length - 1);
            string credentialPrefix = "https://user:" + urlSecret;
            int paddingLength = secretAtOffset - credentialUrlStart - credentialPrefix.Length;
            Assert.IsTrue(paddingLength >= 0);
            return beforeCredentialUrl
                + filler
                + " "
                + credentialPrefix
                + new string('p', paddingLength)
                + "@subscription.invalid/config";
        }
    }
    [TestMethod]
    public void NestedAndUnicodeExceptionsAreSingleLineAndWithinEncodedRecordLimit()
    {
        string root = CreateRoot();
        Exception nested = new InvalidOperationException(
            new string('外', 12_000),
            new HttpRequestException("inner https://inner-user:inner-password@host.invalid/?token=inner-token"));
        try
        {
            Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(root, nested));
            string path = Directory.EnumerateFiles(root, "startup-error-*.log").Single();
            byte[] content = File.ReadAllBytes(path);
            Assert.IsTrue(content.Length <= BoundedDiagnosticWriter.MaximumRecordBytes);
            Assert.AreEqual(1, content.Count(value => value == (byte)'\n'));
            Assert.IsFalse(Encoding.UTF8.GetString(content).Contains("inner-password", StringComparison.Ordinal));
            Assert.IsFalse(Encoding.UTF8.GetString(content).Contains("inner-token", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ConcurrentWritesRotateWithinThreeOneMiBFilesAndPreserveEveryRecordBoundary()
    {
        string root = CreateRoot();
        AggregateException largeException = new(
            Enumerable.Range(0, 8)
                .Select(index => new InvalidOperationException(
                    $"{index}:{new string('界', 1200)}"))
                .ToArray());
        try
        {
            Task<bool>[] writes = Enumerable.Range(0, 200)
                .Select(index => Task.Run(() => BoundedDiagnosticWriter
                    .TryWriteException(root, largeException)))
                .ToArray();
            bool[] results = await Task.WhenAll(writes);

            Assert.IsTrue(results.All(result => result));
            FileInfo[] files = Directory.EnumerateFiles(root, "startup-error-*.log")
                .Select(path => new FileInfo(path))
                .ToArray();
            Assert.AreEqual(BoundedDiagnosticWriter.MaximumFileCount, files.Length);
            Assert.IsTrue(files.All(file => file.Length <= BoundedDiagnosticWriter.MaximumFileBytes));
            Assert.IsTrue(files.Sum(file => file.Length)
                <= (long)BoundedDiagnosticWriter.MaximumFileCount * BoundedDiagnosticWriter.MaximumFileBytes);

            foreach (FileInfo file in files)
            {
                byte[] bytes = await File.ReadAllBytesAsync(file.FullName);
                int lineStart = 0;
                for (int index = 0; index < bytes.Length; index++)
                {
                    if (bytes[index] == (byte)'\n')
                    {
                        Assert.IsTrue(index + 1 - lineStart <= BoundedDiagnosticWriter.MaximumRecordBytes);
                        lineStart = index + 1;
                    }
                }

                Assert.AreEqual(0, bytes.Length - lineStart);
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void IncompleteTrailingRecordIsRemovedBeforeNextAppend()
    {
        string root = CreateRoot();
        try
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "startup-error-0.log");
            File.WriteAllText(path, "previous complete record" + Environment.NewLine + "partial");

            Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(root, new InvalidOperationException("next record")));

            string[] lines = File.ReadAllLines(path);
            Assert.AreEqual(2, lines.Length);
            Assert.AreEqual("previous complete record", lines[0]);
            StringAssert.Contains(lines[1], "InvalidOperationException", StringComparison.Ordinal);
            Assert.IsFalse(lines.Any(line => line.Contains("partial", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void LockedFileInvalidDirectoryAndSanitizerFailureDoNotEscapeWriter()
    {
        string root = CreateRoot();
        InvalidOperationException exception = new("test failure");
        try
        {
            Directory.CreateDirectory(root);
            string filePath = Path.Combine(root, "not-a-directory");
            File.WriteAllText(filePath, "occupied");
            Assert.IsFalse(BoundedDiagnosticWriter.TryWriteException(filePath, exception));

            string lockedDirectory = Path.Combine(root, "locked");
            Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(lockedDirectory, exception));
            string newestFile = Path.Combine(lockedDirectory, "startup-error-0.log");
            using FileStream held = new(
                newestFile,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Assert.IsFalse(BoundedDiagnosticWriter.TryWriteException(lockedDirectory, exception));

            Assert.IsFalse(BoundedDiagnosticWriter.TryWriteException(
                Path.Combine(root, "sanitizer-failure"),
                new ThrowingMessageException("simulated diagnostic sanitizer failure")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }


    [TestMethod]
    public void DiagnosticsIncludeBoundedApplicationVersionAndDistinctMethodLocations()
    {
        string root = CreateRoot();
        try
        {
            Exception first = CaptureAtFirstCallSite();
            Exception second = CaptureAtSecondCallSite();
            Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(root, first));
            Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(root, second));

            string content = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(root, "startup-error-*.log")
                    .Select(File.ReadAllText));

            StringAssert.Contains(content, "app-version=", StringComparison.Ordinal);
            StringAssert.Contains(content, nameof(ThrowFromFirstCallSite), StringComparison.Ordinal);
            StringAssert.Contains(content, nameof(ThrowFromSecondCallSite), StringComparison.Ordinal);
            Assert.IsTrue(content.Length < (BoundedDiagnosticWriter.MaximumFileCount * BoundedDiagnosticWriter.MaximumFileBytes));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public void AggregateExceptionTraversalStopsAtTheConfiguredDetailBound()
    {
        string root = CreateRoot();
        AggregateException exception = new(
            Enumerable.Range(0, 10_000)
                .Select(index => new InvalidOperationException($"aggregate-child-{index}")));
        try
        {
            Assert.IsTrue(BoundedDiagnosticWriter.TryWriteException(root, exception));
            byte[] bytes = File.ReadAllBytes(Directory.EnumerateFiles(root, "startup-error-*.log").Single());
            string content = Encoding.UTF8.GetString(bytes);

            Assert.IsTrue(bytes.Length <= BoundedDiagnosticWriter.MaximumRecordBytes);
            StringAssert.Contains(content, "aggregate-child-0", StringComparison.Ordinal);
            Assert.IsFalse(content.Contains("aggregate-child-9999", StringComparison.Ordinal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static InvalidOperationException CaptureAtFirstCallSite()
    {
        try
        {
            ThrowFromFirstCallSite();
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the first diagnostic exception to be thrown.");
    }

    private static InvalidOperationException CaptureAtSecondCallSite()
    {
        try
        {
            ThrowFromSecondCallSite();
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Expected the second diagnostic exception to be thrown.");
    }

    private static void ThrowFromFirstCallSite() =>
        throw new InvalidOperationException("same diagnostic message");

    private static void ThrowFromSecondCallSite() =>
        throw new InvalidOperationException("same diagnostic message");

    private static string CreateRoot() =>
        Path.Combine(Path.GetTempPath(), "ClashTrayTests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ThrowingMessageException : Exception
    {
        public ThrowingMessageException() { }

        public ThrowingMessageException(string message) : base(message) { }

        public ThrowingMessageException(string message, Exception innerException) : base(message, innerException) { }
        public override string Message =>
            throw new InvalidOperationException("simulated diagnostic sanitizer failure");
    }
}