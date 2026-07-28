namespace EZDIT.Services;

public sealed class AppLogService
{
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
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}{Environment.NewLine}";
            await File.AppendAllTextAsync(Path.Combine(_directory, "EZDIT.log"), line);
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
}
