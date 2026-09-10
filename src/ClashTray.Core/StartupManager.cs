using Microsoft.Win32;
using System.Security;

namespace ClashTray.Core;

public sealed record StartupRegistrationStatus(
    bool IsRegistered,
    bool IsDisabledByOperatingSystem,
    string? Command,
    string? Error = null,
    bool IsOwnedByClashTray = false)
{
    public bool IsEnabled => IsOwnedByClashTray && !IsDisabledByOperatingSystem;

    public bool RequiresRepair => IsRegistered && !IsOwnedByClashTray;
}

internal sealed record StartupRegistrationChange(
    bool Changed,
    string ExpectedCommand,
    bool ExpectedAfterExists,
    bool PreviousValueExists,
    object? PreviousValue,
    RegistryValueKind PreviousValueKind);

internal interface IStartupRegistration
{
    public StartupRegistrationStatus GetStatus();

    public StartupRegistrationChange Ensure(bool enabled, string? executablePath);

    public void Rollback(StartupRegistrationChange change);
}

internal interface IStartupRegistry
{
    public RegistryValueSnapshot ReadRunValue();

    public bool IsStartupApprovedDisabled();

    public void WriteRunValue(object value, RegistryValueKind kind);

    public void DeleteRunValue();
}

internal sealed record RegistryValueSnapshot(object? Value, RegistryValueKind Kind, bool Exists)
{
    public static RegistryValueSnapshot Missing { get; } =
        new(null, RegistryValueKind.String, Exists: false);
}

public sealed class StartupManager : IStartupRegistration
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string StartupApprovedRunKeyPath =
        "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";
    private const string ValueName = "ClashTray";
    private readonly IStartupRegistry _registry;

    public StartupManager()
        : this(new WindowsStartupRegistry())
    {
    }

    internal StartupManager(IStartupRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public static bool IsEnabled() => new StartupManager().GetStatus().IsEnabled;

    public static void SetEnabled(bool enabled, string executablePath) =>
        new StartupManager().Ensure(enabled, executablePath);

    public StartupRegistrationStatus GetStatus()
    {
        try
        {
            RegistryValueSnapshot runValue = _registry.ReadRunValue();
            string? command = runValue.Value as string;
            bool registered = command is not null && !string.IsNullOrWhiteSpace(command);
            string? expectedCommand = TryBuildCommand(Environment.ProcessPath);
            bool owned = registered
                && expectedCommand is not null
                && string.Equals(command, expectedCommand, StringComparison.Ordinal);
            return new StartupRegistrationStatus(
                registered,
                _registry.IsStartupApprovedDisabled(),
                command,
                registered && expectedCommand is null
                    ? "无法确定 ClashTray 的真实可执行文件路径。"
                    : null,
                owned);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new StartupRegistrationStatus(false, false, null, exception.Message);
        }
    }

    internal StartupRegistrationChange Ensure(bool enabled, string? executablePath)
    {
        string expectedCommand = enabled
            ? BuildCommand(executablePath)
            : string.IsNullOrWhiteSpace(executablePath) ? string.Empty : BuildCommand(executablePath);
        RegistryValueSnapshot current = _registry.ReadRunValue();

        if (enabled)
        {
            if (string.Equals(current.Value as string, expectedCommand, StringComparison.Ordinal))
            {
                return NoChange(expectedCommand, current);
            }

            _registry.WriteRunValue(expectedCommand, RegistryValueKind.String);
            return new StartupRegistrationChange(
                Changed: true,
                ExpectedCommand: expectedCommand,
                ExpectedAfterExists: true,
                PreviousValueExists: current.Exists,
                PreviousValue: current.Value,
                PreviousValueKind: current.Kind);
        }

        // Removing a startup entry is safe only when it is the exact command
        // ClashTray owns. A command replaced by another application is kept.
        if (string.IsNullOrWhiteSpace(expectedCommand)
            || !string.Equals(current.Value as string, expectedCommand, StringComparison.Ordinal))
        {
            return NoChange(expectedCommand, current);
        }

        _registry.DeleteRunValue();
        return new StartupRegistrationChange(
            Changed: true,
            ExpectedCommand: expectedCommand,
            ExpectedAfterExists: false,
            PreviousValueExists: current.Exists,
            PreviousValue: current.Value,
            PreviousValueKind: current.Kind);
    }


    void IStartupRegistration.Rollback(StartupRegistrationChange change)
    {
        Rollback(change);
    }

    internal void Rollback(StartupRegistrationChange change)
    {
        if (!change.Changed)
        {
            return;
        }

        RegistryValueSnapshot current = _registry.ReadRunValue();
        bool stillOwned = change.ExpectedAfterExists
            ? current.Value is string command
                && string.Equals(command, change.ExpectedCommand, StringComparison.Ordinal)
            : !current.Exists;
        if (!stillOwned)
        {
            // Another process changed the value after our write. Preserve its
            // command instead of undoing an unrelated startup registration.
            return;
        }

        if (!change.PreviousValueExists)
        {
            _registry.DeleteRunValue();
            return;
        }

        if (change.PreviousValue is null)
        {
            throw new InvalidDataException("The previous ClashTray startup value cannot be restored.");
        }

        _registry.WriteRunValue(change.PreviousValue, change.PreviousValueKind);
    }

    StartupRegistrationStatus IStartupRegistration.GetStatus() => GetStatus();

    StartupRegistrationChange IStartupRegistration.Ensure(bool enabled, string? executablePath) =>
        Ensure(enabled, executablePath);

    internal static string BuildCommand(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || !Path.IsPathFullyQualified(executablePath)
            || !File.Exists(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "无法确定 ClashTray 的真实可执行文件路径，未修改 Windows 启动项。请从已安装目录启动应用。");
        }

        return $"\"{Path.GetFullPath(executablePath)}\" --startup";
    }

    private static string? TryBuildCommand(string? executablePath)
    {
        try
        {
            return BuildCommand(executablePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static StartupRegistrationChange NoChange(string expectedCommand, RegistryValueSnapshot current) =>
        new(
            Changed: false,
            ExpectedCommand: expectedCommand,
            ExpectedAfterExists: current.Exists,
            PreviousValueExists: current.Exists,
            PreviousValue: current.Value,
            PreviousValueKind: current.Kind);

    private sealed class WindowsStartupRegistry : IStartupRegistry
    {
        public RegistryValueSnapshot ReadRunValue()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null || !key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase))
            {
                return RegistryValueSnapshot.Missing;
            }

            object? value = key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return new RegistryValueSnapshot(value, key.GetValueKind(ValueName), Exists: true);
        }

        public bool IsStartupApprovedDisabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKeyPath, writable: false);
            object? value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value is byte[] bytes
                && bytes.Length > 0
                && bytes[0] is 0x03 or 0x07;
        }

        public void WriteRunValue(object value, RegistryValueKind kind)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("Windows startup registry key is unavailable.");
            key.SetValue(ValueName, value, kind);
        }

        public void DeleteRunValue()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

internal sealed class InMemoryStartupRegistry : IStartupRegistry
{
    public RegistryValueSnapshot Current { get; set; } = RegistryValueSnapshot.Missing;

    public bool StartupApprovedDisabled { get; set; }

    public RegistryValueSnapshot ReadRunValue() => Current;

    public bool IsStartupApprovedDisabled() => StartupApprovedDisabled;

    public void WriteRunValue(object value, RegistryValueKind kind) =>
        Current = new RegistryValueSnapshot(value, kind, Exists: true);

    public void DeleteRunValue() => Current = RegistryValueSnapshot.Missing;
}


