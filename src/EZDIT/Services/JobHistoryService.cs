using System.Text.Json;
using System.Text.Json.Serialization;
using EZDIT.Models;

namespace EZDIT.Services;

public sealed class JobHistoryService
{
    private readonly string _dataDirectory;
    private readonly string _historyPath;
    private readonly string _reportsDirectory;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public JobHistoryService(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZDIT");
        _historyPath = Path.Combine(_dataDirectory, "history.json");
        _reportsDirectory = Path.Combine(_dataDirectory, "Reports");
    }

    public async Task<List<JobHistoryItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(_historyPath);
            return await JsonSerializer.DeserializeAsync<List<JobHistoryItem>>(stream, _jsonOptions, cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<JobHistoryItem> items, CancellationToken cancellationToken = default)
    {
        List<JobHistoryItem> snapshot = items.ToList();
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            string temporaryPath = _historyPath + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await JsonSerializer.SerializeAsync(stream, snapshot, _jsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temporaryPath, _historyPath, true);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public async Task<string> SaveReportAsync(string jobId, string report, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_reportsDirectory);
        string fileName = SafeReportName(jobId);
        await File.WriteAllTextAsync(Path.Combine(_reportsDirectory, fileName), report, cancellationToken);
        return fileName;
    }

    public async Task<string?> ReadReportAsync(string? fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string path = Path.Combine(_reportsDirectory, Path.GetFileName(fileName));
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
    }

    public Task DeleteReportAsync(string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            TryDelete(Path.Combine(_reportsDirectory, Path.GetFileName(fileName)));
        }
        return Task.CompletedTask;
    }

    private static string SafeReportName(string jobId) => $"{Path.GetFileName(jobId)}.txt";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // History cleanup must never affect copied media.
        }
    }
}