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