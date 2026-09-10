using System.Diagnostics;
using System.IO.Compression;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using ClashTray.Core;

namespace ClashTray.Setup;

internal static class Program
{
    private const string PayloadResourceName = "ClashTray.Setup.Payload.zip";
    private const string SetupFileName = "ClashTray.Setup.exe";
    private const string AppDirectoryName = "App";
    private const string ServiceDirectoryName = "Service";
    private const string CoreDirectoryName = "Core";
    private const string StartMenuDirectoryName = "ClashTray";
    private const string UninstallRegistryPath = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ClashTray";
    private const string StartupRegistryPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string StartupValueName = "ClashTray";
    private const long MaxPayloadBytes = 512L * 1024 * 1024;
    private const long MaxEntryBytes = 256L * 1024 * 1024;

    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxYesNoCancel = 0x00000003;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxIconInformation = 0x00000040;
    private const int MessageBoxYes = 6;
    private const int MessageBoxNo = 7;
    private const int MessageBoxCancel = 2;

    [STAThread]
    private static int Main(string[] args)
    {
        SetupLog.Write($"安装器启动。参数：{string.Join(" ", args)}");
        try
        {
            if (args.Length > 0 && string.Equals(args[0], "--cleanup", StringComparison.OrdinalIgnoreCase))
            {
                SetupLog.Write("进入卸载清理流程。");
                return CleanupAfterParentExit(args);
            }

            if (args.Length > 0 && string.Equals(args[0], "--uninstall", StringComparison.OrdinalIgnoreCase))
            {
                SetupLog.Write("进入卸载流程。");
                return Uninstall();
            }

            return Install();
        }
        catch (OperationCanceledException)
        {
            SetupLog.Write("操作被取消。");
            return 1;
        }
        catch (Win32Exception exception)
        {
            SetupLog.Write($"Windows 服务操作失败：{exception}");
            ShowMessage(
                $"Windows 服务操作失败（错误代码 {exception.NativeErrorCode}）。\n\n详细日志：{SetupLog.FilePath}",
                "ClashTray 安装失败",
                MessageBoxOk | MessageBoxIconError);
            return 1;
        }
        catch (Exception exception)
        {
            SetupLog.Write($"安装器异常：{exception}");
            ShowMessage(
                $"{exception.Message}\n\n详细日志：{SetupLog.FilePath}",
                "ClashTray 安装失败",
                MessageBoxOk | MessageBoxIconError);
            return 1;
        }
    }

    private static int Install()
    {
        SetupLog.Write("开始安装或升级。");
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ClashTray 仅支持 Windows。");
        }

        if (IsDesktopAppRunning())
        {
            throw new InvalidOperationException("请先从托盘退出正在运行的 ClashTray，然后重新运行安装程序。");
        }

        string installRoot = InstallPaths.InstallRoot;
        string setupPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定安装程序路径。");
        SetupLog.Write($"当前安装器路径：{setupPath}");
        if (IsPathInside(installRoot, setupPath))
        {
            throw new InvalidOperationException("请从新的安装包位置运行安装程序，不要从当前安装目录内运行升级程序。");
        }

        string stagingRoot = CreateTemporaryDirectory("ClashTray-Setup");
        string newRoot = InstallPaths.ValidateGeneratedPath(installRoot + ".new-" + Guid.NewGuid().ToString("N"));
        string backupRoot = InstallPaths.ValidateGeneratedPath(installRoot + ".backup-" + Guid.NewGuid().ToString("N"));
        bool swapped = false;
        bool serviceInstalled = false;

        try
        {
            SetupLog.Write($"创建临时目录：{stagingRoot}");
            ExtractPayload(stagingRoot);
            SetupLog.Write("安装包内容解压完成。");
            CopyDirectory(Path.Combine(stagingRoot, AppDirectoryName), Path.Combine(newRoot, AppDirectoryName));
            CopyDirectory(Path.Combine(stagingRoot, ServiceDirectoryName), Path.Combine(newRoot, ServiceDirectoryName));
            File.Copy(setupPath, Path.Combine(newRoot, SetupFileName), overwrite: true);
            SetupLog.Write("应用、服务和安装器文件复制完成。");
            InstallBundledCore(stagingRoot);
            SetupLog.Write("Mihomo 核心文件处理完成。");

            WindowsServiceManager.RemoveOwnedService();
            SetupLog.Write("旧 ClashTray 服务处理完成。");
            if (Directory.Exists(installRoot))
            {
                Directory.Move(installRoot, backupRoot);
            }

            Directory.Move(newRoot, installRoot);
            swapped = true;
            SetupLog.Write("安装目录替换完成。");

            string servicePath = Path.Combine(installRoot, ServiceDirectoryName, "ClashTray.Service.exe");
            string userSid = WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("无法确定当前 Windows 用户。");
            WindowsServiceManager.InstallOrUpdate(servicePath, userSid);
            serviceInstalled = true;
            SetupLog.Write("新 ClashTray 服务安装并启动完成。");
            WriteUninstallRegistration(installRoot);
            CreateStartMenuShortcut(installRoot);
            SetupLog.Write("卸载信息和开始菜单快捷方式写入完成。");
            DeleteDirectoryIfExists(backupRoot);
            StartInstalledApp(installRoot);
            SetupLog.Write("桌面程序启动请求已发出，安装完成。");

            ShowMessage(
                "ClashTray 已安装完成，托盘程序已启动。桌面程序会以普通用户权限运行。",
                "ClashTray",
                MessageBoxOk | MessageBoxIconInformation);
            return 0;
        }
        catch (Exception exception)
        {
            SetupLog.Write($"安装流程失败，开始回滚：{exception}");
            if (serviceInstalled || swapped)
            {
                try
                {
                    WindowsServiceManager.RemoveOwnedService();
                }
                catch (Exception rollbackException)
                {
                    SetupLog.Write($"回滚时删除服务失败：{rollbackException}");
                }
            }

            if (swapped)
            {
                DeleteDirectoryIfExists(installRoot);
                if (Directory.Exists(backupRoot))
                {
                    Directory.Move(backupRoot, installRoot);
                }
            }

            throw;
        }
        finally
        {
            DeleteDirectoryIfExists(stagingRoot);
            DeleteDirectoryIfExists(newRoot);
            if (!swapped)
            {
                DeleteDirectoryIfExists(backupRoot);
            }

            SetupLog.Write("安装流程清理完成。");
        }
    }

    private static int Uninstall()
    {
        if (IsDesktopAppRunning())
        {
            throw new InvalidOperationException("请先从托盘退出正在运行的 ClashTray，然后重新运行卸载程序。");
        }

        int dataChoice = ShowMessage(
            "是否保留 ClashTray 的配置、订阅和日志？\n\n选择“是”保留数据，选择“否”删除数据，选择“取消”停止卸载。",
            "卸载 ClashTray",
            MessageBoxYesNoCancel | MessageBoxIconInformation);
        if (dataChoice == MessageBoxCancel)
        {
            return 0;
        }

        WindowsServiceManager.RemoveOwnedService();
        SystemProxyRecovery.RestoreOwnedStatesForLoadedUsers();
        RemoveCurrentUserStartupEntry();
        RemoveStartMenuShortcut();
        RemoveUninstallRegistration();

        if (dataChoice == MessageBoxNo)
        {
            DeleteDirectoryIfExists(InstallPaths.LocalDataRoot);
            DeleteDirectoryIfExists(InstallPaths.ProgramDataRoot);
        }

        string installRoot = InstallPaths.InstallRoot;
        string? currentSetupPath = Environment.ProcessPath;
        if (currentSetupPath is not null && IsPathInside(installRoot, currentSetupPath))
        {
            string cleanupPath = Path.Combine(Path.GetTempPath(), $"ClashTray-Cleanup-{Guid.NewGuid():N}.exe");
            File.Copy(currentSetupPath, cleanupPath, overwrite: true);
            Process? cleanup = Process.Start(new ProcessStartInfo
            {
                FileName = cleanupPath,
                Arguments = $"--cleanup {QuoteArgument(installRoot)} {Environment.ProcessId}",
                WorkingDirectory = Path.GetDirectoryName(cleanupPath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (cleanup is null)
            {
                throw new InvalidOperationException("无法启动卸载清理进程。");
            }
        }
        else
        {
            DeleteDirectoryIfExists(installRoot);
        }

        ShowMessage(
            dataChoice == MessageBoxYes ? "ClashTray 已卸载，用户数据已保留。" : "ClashTray 已卸载，用户数据已删除。",
            "ClashTray",
            MessageBoxOk | MessageBoxIconInformation);
        return 0;
    }

    private static int CleanupAfterParentExit(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out int parentProcessId))
        {
            return 1;
        }

        string installRoot = InstallPaths.ValidateInstallRoot(args[1]);
        for (int attempt = 0; attempt < 100 && IsProcessRunning(parentProcessId); attempt++)
        {
            Thread.Sleep(100);
        }

        DeleteDirectoryIfExists(installRoot);
        TryDeleteFile(Environment.ProcessPath);
        return 0;
    }

    private static void ExtractPayload(string stagingRoot)
    {
        using Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("安装程序未包含应用文件。请重新运行 Build-EXE.ps1 生成安装包。");
        using ZipArchive archive = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: false);
        long totalBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            string relativePath = NormalizeEntryPath(entry.FullName);
            if (!relativePath.StartsWith(AppDirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !relativePath.StartsWith(ServiceDirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !relativePath.StartsWith(CoreDirectoryName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"安装包包含不允许的路径：{entry.FullName}");
            }

            if (entry.Length < 0 || entry.Length > MaxEntryBytes || totalBytes > MaxPayloadBytes - entry.Length)
            {
                throw new InvalidDataException("安装包内容超过安全大小限制。");
            }

            totalBytes += entry.Length;
            string targetPath = GetSafeChildPath(stagingRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using Stream input = entry.Open();
            using FileStream output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
            input.CopyTo(output);
        }

        RequirePayloadFile(stagingRoot, AppDirectoryName, "ClashTray.App.exe");
        RequirePayloadFile(stagingRoot, ServiceDirectoryName, "ClashTray.Service.exe");
        string coreRoot = Path.Combine(stagingRoot, CoreDirectoryName);
        if (Directory.Exists(coreRoot))
        {
            RequirePayloadFile(stagingRoot, CoreDirectoryName, "mihomo.exe");
            RequirePayloadFile(stagingRoot, CoreDirectoryName, "Mihomo-LICENSE.txt");
            RequirePayloadFile(stagingRoot, CoreDirectoryName, "Mihomo-Release.txt");
        }
    }

    private static void InstallBundledCore(string stagingRoot)
    {
        string sourceRoot = Path.Combine(stagingRoot, CoreDirectoryName);
        if (!File.Exists(Path.Combine(sourceRoot, "mihomo.exe")))
        {
            // NoCore and Framework packages intentionally leave the core to the
            // verified in-app updater. Preserve an existing installed core on
            // upgrades and keep a fresh install usable for configuration work.
            return;
        }

        string targetRoot = InstallPaths.ProgramDataCoreRoot;
        string targetCore = Path.Combine(targetRoot, "mihomo.exe");
        string targetLicense = Path.Combine(targetRoot, "Mihomo-LICENSE.txt");
        string targetRelease = Path.Combine(targetRoot, "Mihomo-Release.txt");

        Directory.CreateDirectory(targetRoot);
        if (!File.Exists(targetCore))
        {
            string candidate = targetCore + ".new-" + Guid.NewGuid().ToString("N");
            File.Copy(Path.Combine(sourceRoot, "mihomo.exe"), candidate, overwrite: false);
            File.Move(candidate, targetCore);
        }

        if (!File.Exists(targetLicense))
        {
            File.Copy(Path.Combine(sourceRoot, "Mihomo-LICENSE.txt"), targetLicense, overwrite: false);
        }

        if (!File.Exists(targetRelease))
        {
            File.Copy(Path.Combine(sourceRoot, "Mihomo-Release.txt"), targetRelease, overwrite: false);
        }
    }

    private static void StartInstalledApp(string installRoot)
    {
        string appPath = Path.Combine(installRoot, AppDirectoryName, "ClashTray.App.exe");
        if (!File.Exists(appPath))
        {
            throw new FileNotFoundException("安装完成后找不到 ClashTray 桌面程序。", appPath);
        }

        // The installer is elevated. Ask the normal Explorer shell to launch the
        // desktop process so it does not inherit the administrator token.
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = QuoteArgument(appPath),
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(appPath)!
        });
    }

    private static string NormalizeEntryPath(string path)
    {
        string normalized = path.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException("安装包包含绝对路径。");
        }

        string[] parts = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("安装包包含非法路径。");
        }

        return Path.Combine(parts);
    }

    private static string GetSafeChildPath(string root, string relativePath)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("安装包路径越过了临时目录。");
        }

        return fullPath;
    }

    private static void RequirePayloadFile(string root, string directory, string fileName)
    {
        string path = Path.Combine(root, directory, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"安装包缺少 {directory}/{fileName}。");
        }
    }

    private static void CopyDirectory(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"安装包缺少目录：{sourceRoot}");
        }

        Directory.CreateDirectory(targetRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            string targetFile = Path.Combine(targetRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: false);
        }
    }

    private static void WriteUninstallRegistration(string installRoot)
    {
        using RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallRegistryPath, writable: true)
            ?? throw new InvalidOperationException("无法写入 Windows 卸载注册表项。");
        string setupPath = Path.Combine(installRoot, SetupFileName);
        key.SetValue("DisplayName", "ClashTray", RegistryValueKind.String);
        key.SetValue("DisplayVersion", GetSetupVersion(), RegistryValueKind.String);
        key.SetValue("Publisher", "ClashTray Project", RegistryValueKind.String);
        key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
        key.SetValue("DisplayIcon", setupPath, RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{setupPath}\" --uninstall", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void RemoveUninstallRegistration()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
            "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
            writable: true);
        key?.DeleteSubKeyTree("ClashTray", throwOnMissingSubKey: false);
    }

    private static void CreateStartMenuShortcut(string installRoot)
    {
        string appPath = Path.Combine(installRoot, AppDirectoryName, "ClashTray.App.exe");
        string shortcutDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), StartMenuDirectoryName);
        Directory.CreateDirectory(shortcutDirectory);
        string shortcutPath = Path.Combine(shortcutDirectory, "ClashTray.lnk");

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows 快捷方式组件不可用。");
        object shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建 Windows 快捷方式组件。");
        object? shortcutObject = null;
        try
        {
            shortcutObject = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [shortcutPath]);
            dynamic shortcut = shortcutObject!;
            shortcut.TargetPath = appPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(appPath)!;
            shortcut.Description = "ClashTray Mihomo 控制中心";
            shortcut.IconLocation = $"{appPath},0";
            shortcut.Save();
        }
        finally
        {
            if (shortcutObject is not null && Marshal.IsComObject(shortcutObject))
            {
                Marshal.FinalReleaseComObject(shortcutObject);
            }

            if (Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void RemoveStartMenuShortcut()
    {
        string shortcutDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), StartMenuDirectoryName);
        TryDeleteFile(Path.Combine(shortcutDirectory, "ClashTray.lnk"));
        try
        {
            if (Directory.Exists(shortcutDirectory) && !Directory.EnumerateFileSystemEntries(shortcutDirectory).Any())
            {
                Directory.Delete(shortcutDirectory);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RemoveCurrentUserStartupEntry()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
        key?.DeleteValue(StartupValueName, throwOnMissingValue: false);
    }

    private static bool IsDesktopAppRunning()
    {
        foreach (Process process in Process.GetProcessesByName("ClashTray.App"))
        {
            process.Dispose();
            return true;
        }

        return false;
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                Thread.Sleep(150);
            }
        }

        throw new IOException($"无法删除目录：{path}");
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsPathInside(string root, string path)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static string GetSetupVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private static int ShowMessage(string text, string caption, uint type) =>
        MessageBox(IntPtr.Zero, text, caption, type);

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);

    private static class InstallPaths
    {
        public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClashTray");

        public static string LocalDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashTray");

        public static string ProgramDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClashTray");

        public static string ProgramDataCoreRoot => Path.Combine(ProgramDataRoot, "core");

        public static string ValidateGeneratedPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string programFiles = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullPath, Path.TrimEndingDirectorySeparator(programFiles), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安装路径不在 Program Files 范围内。");
            }

            return fullPath;
        }

        public static string ValidateInstallRoot(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(fullPath, Path.GetFullPath(InstallRoot), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("卸载清理路径不是 ClashTray 安装目录。");
            }

            return fullPath;
        }
    }
}
