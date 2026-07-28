namespace EZDIT.Services;

public sealed class AppLogService
{
    private const long MaxLogSizeBytes = 5 * 1024 * 1024; // 5 MiB
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private string _directory;

    public AppLogService(string directory)
    {
        _directory = directory;
    }

    public void SetDirectory(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _directory = directory;
        }
    }

    public async Task WriteAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await _writeGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            string logPath = Path.Combine(_directory, "EZDIT.log");
            RotateIfNeeded(logPath);
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(logPath, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never interrupt a copy task.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static void RotateIfNeeded(string logPath)
    {
        try
        {
            FileInfo info = new(logPath);
            if (!info.Exists || info.Length < MaxLogSizeBytes)
            {
                return;
            }

            string archive = Path.Combine(info.DirectoryName!, "EZDIT.old.log");
            File.Move(logPath, archive, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort rotation; logging continues if rotation fails.
        }
    }
}
