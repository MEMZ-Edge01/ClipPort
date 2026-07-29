using System.Text.Json;
using System.Text.Json.Serialization;
using EZDIT.Models;

namespace EZDIT.Services;

public sealed class JobHistoryService
{
    private readonly string _dataDirectory;
    private readonly string _historyPath;
    private readonly string _defaultReportsDirectory;
    private volatile string _reportsDirectory;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public JobHistoryService(string? dataDirectory = null, string? reportsDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EZDIT");
        _historyPath = Path.Combine(_dataDirectory, "history.json");
        _defaultReportsDirectory = Path.Combine(_dataDirectory, "Reports");
        _reportsDirectory = string.IsNullOrWhiteSpace(reportsDirectory)
            ? _defaultReportsDirectory
            : reportsDirectory;
    }

    public void SetReportsDirectory(string? reportsDirectory) =>
        _reportsDirectory = string.IsNullOrWhiteSpace(reportsDirectory)
            ? _defaultReportsDirectory
            : reportsDirectory;

    public async Task<List<JobHistoryItem>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_historyPath))
        {
            return [];
        }

        try
        {
            string json = await File.ReadAllTextAsync(_historyPath, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                BackupCorruptHistory();
                return [];
            }

            var items = new List<JobHistoryItem>();
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    JobHistoryItem? item = element.Deserialize<JobHistoryItem>(_jsonOptions);
                    if (item is null || !Enum.IsDefined(item.Status))
                    {
                        continue;
                    }

                    Normalize(item);
                    items.Add(item);
                }
                catch (JsonException)
                {
                    // Preserve every valid record even when one entry is malformed.
                }
            }
            return items;
        }
        catch (JsonException)
        {
            BackupCorruptHistory();
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        string reportsDirectory = _reportsDirectory;
        Directory.CreateDirectory(reportsDirectory);
        string fileName = SafeReportName(jobId);
        string path = Path.Combine(reportsDirectory, fileName);
        await File.WriteAllTextAsync(path, report, cancellationToken);
        return path;
    }

    public async Task<string?> ReadReportAsync(string? reportReference, CancellationToken cancellationToken = default)
    {
        string? path = ResolveReportPath(reportReference);
        return path is not null
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : null;
    }

    public string? ResolveReportPath(string? reportReference)
    {
        if (string.IsNullOrWhiteSpace(reportReference))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(reportReference) && File.Exists(reportReference))
        {
            return Path.GetFullPath(reportReference);
        }

        string fileName = Path.GetFileName(reportReference);
        string reportsDirectory = _reportsDirectory;
        string path = Path.Combine(reportsDirectory, fileName);
        if (!File.Exists(path) && !string.Equals(
                reportsDirectory, _defaultReportsDirectory, StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(_defaultReportsDirectory, fileName);
        }
        return File.Exists(path) ? path : null;
    }

    public Task DeleteReportAsync(string? reportReference)
    {
        if (!string.IsNullOrWhiteSpace(reportReference))
        {
            if (Path.IsPathFullyQualified(reportReference))
            {
                TryDelete(Path.GetFullPath(reportReference));
            }

            string fileName = Path.GetFileName(reportReference);
            string reportsDirectory = _reportsDirectory;
            TryDelete(Path.Combine(reportsDirectory, fileName));
            if (!string.Equals(reportsDirectory, _defaultReportsDirectory, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(Path.Combine(_defaultReportsDirectory, fileName));
            }
        }
        return Task.CompletedTask;
    }

    private static string SafeReportName(string jobId) => $"{Path.GetFileName(jobId)}.txt";

    private static void Normalize(JobHistoryItem item)
    {
        item.DisplayName ??= string.Empty;
        item.SourcePath ??= string.Empty;
        item.DestinationPath ??= string.Empty;
        item.FailedFiles ??= [];
        item.DuplicateFiles ??= [];
        item.DuplicateDecisions ??= new Dictionary<string, ExistingFilePolicy>(
            StringComparer.OrdinalIgnoreCase);
        item.DestinationFiles ??= new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
    }

    private void BackupCorruptHistory()
    {
        try
        {
            string backupPath = Path.Combine(
                _dataDirectory,
                $"history.corrupt-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(_historyPath, backupPath, overwrite: false);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Loading still returns safely; never destroy the original file.
        }
    }

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
