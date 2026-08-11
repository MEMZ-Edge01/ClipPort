using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ClipPort.Updater;

/// <summary>
/// 便携版更新器：
/// 由 ClipPort 主程序启动（通常从 %LOCALAPPDATA% 中的副本运行），
/// 等待主进程退出后，用下载好的 zip 整体替换应用目录，再重新启动主程序。
/// 采用"整目录换名 + staging 移入"的方式，避免逐个文件覆盖时
/// 主程序 exe 或原生 dll 被占用导致半更新状态。
/// </summary>
internal static class Program
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipPort",
        "Updates");
    private static readonly string LogPath = Path.Combine(LogDirectory, "updater.log");

    private const int MainProcessWaitSeconds = 120;
    private const int MessageBoxErrorIcon = 0x10; // MB_ICONERROR

    private static int Main(string[] arguments)
    {
        string? sourcePath = null;
        string? targetDirectory = null;
        string? mainExecutable = null;
        int? mainProcessId = null;
        bool restartAfterUpdate = false;

        for (int index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--source":
                    sourcePath = NextArgument(arguments, ref index, "--source");
                    break;
                case "--target":
                    targetDirectory = NextArgument(arguments, ref index, "--target");
                    break;
                case "--main-exe":
                    mainExecutable = NextArgument(arguments, ref index, "--main-exe");
                    break;
                case "--wait-pid":
                    if (index + 1 < arguments.Length &&
                        int.TryParse(arguments[index + 1], out int parsedPid))
                    {
                        mainProcessId = parsedPid;
                        index++;
                    }
                    break;
                case "--restart":
                    restartAfterUpdate = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(targetDirectory) ||
            string.IsNullOrWhiteSpace(mainExecutable))
        {
            ShowError("更新器参数不完整。");
            return 1;
        }

        try
        {
            Log("Update started.");
            RunUpdate(
                sourcePath,
                targetDirectory,
                mainExecutable,
                mainProcessId,
                restartAfterUpdate);
            Log("Update completed.");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Update failed: {ex}");
            ShowError($"更新失败：{ex.Message}");
            return 1;
        }
    }

    private static string NextArgument(string[] arguments, ref int index, string option)
    {
        if (index + 1 >= arguments.Length)
        {
            throw new ArgumentException($"缺少 {option} 参数的值。");
        }
        index++;
        return arguments[index];
    }

    private static void RunUpdate(
        string sourcePath,
        string targetDirectory,
        string mainExecutable,
        int? mainProcessId,
        bool restartAfterUpdate)
    {
        string fullTarget = Path.GetFullPath(targetDirectory);
        if (!File.Exists(sourcePath))
        {
            throw new IOException($"找不到更新包：{sourcePath}");
        }
        if (!Directory.Exists(fullTarget))
        {
            throw new IOException($"找不到应用目录：{fullTarget}");
        }

        // 等待旧主进程完全退出，避免替换 ClipPort.exe 时被占用。
        if (mainProcessId is int pid)
        {
            WaitForMainProcessExit(pid);
        }

        // staging 与应用目录放在同一卷，保证最后的 Directory.Move 是纯改名，
        // 不会因为跨磁盘复制而中途失败。
        string parentDirectory = Path.GetDirectoryName(fullTarget) ?? fullTarget;
        string stagingDirectory = Path.Combine(
            parentDirectory,
            $".clipport-update-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            Log($"Extracting {sourcePath} to {stagingDirectory}");
            ZipFile.ExtractToDirectory(sourcePath, stagingDirectory);

            string newExecutable = Path.Combine(stagingDirectory, mainExecutable);
            if (!File.Exists(newExecutable))
            {
                throw new IOException($"更新包内缺少 {mainExecutable}，已取消更新。");
            }

            string backupDirectory =
                $"{fullTarget}.update-backup-{Guid.NewGuid():N}";
            try
            {
                // 正在运行的更新器是从 %LOCALAPPDATA% 副本启动的，因此
                // 整个应用目录（包括旧更新器）都可以被重命名和替换。
                Directory.Move(fullTarget, backupDirectory);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "应用目录中的文件正被其他程序占用（例如文件资源管理器）。" +
                    "请关闭 ClipPort 和资源管理器窗口后重试。",
                    ex);
            }

            try
            {
                Directory.Move(stagingDirectory, fullTarget);
            }
            catch
            {
                // 回滚：把旧目录放回原位，保留 staging 供排查。
                try
                {
                    Directory.Move(backupDirectory, fullTarget);
                }
                catch (Exception rollbackEx)
                {
                    Log($"Rollback failed: {rollbackEx}");
                }
                throw;
            }

            // 旧目录尽量删除；个别文件（例如 Explorer 加载的组件 dll）
            // 删除失败不影响新版本运行，留到下次更新时再清理。
            TryDeleteDirectory(backupDirectory);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }

        if (restartAfterUpdate)
        {
            string executablePath = Path.Combine(fullTarget, mainExecutable);
            Log($"Restarting {executablePath}");
            Process.Start(new ProcessStartInfo(executablePath)
            {
                WorkingDirectory = fullTarget,
                UseShellExecute = true
            });
        }
    }

    private static void WaitForMainProcessExit(int processId)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(MainProcessWaitSeconds))
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                // 进程已不存在，说明主程序已经退出。
                return;
            }
            Thread.Sleep(200);
        }
        throw new TimeoutException(
            $"等待 ClipPort 退出超时（{MainProcessWaitSeconds} 秒）。");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            Log($"Directory cleanup skipped ({path}): {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能阻塞更新本身。
        }
    }

    private static void ShowError(string message)
    {
        try
        {
            MessageBoxW(
                IntPtr.Zero,
                message,
                "ClipPort 更新",
                MessageBoxErrorIcon);
        }
        catch
        {
            // 弹窗失败时仅写日志。
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(
        IntPtr owner,
        string text,
        string caption,
        uint type);
}
