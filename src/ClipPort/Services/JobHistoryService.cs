using System.Text.Json;
using System.Text.Json.Serialization;
using ClipPort.Models;

namespace ClipPort.Services;

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
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipPort");
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
        item.FailedFiles = item.FailedFiles
            .OfType<FileOperationFailure>()
            .Select(NormalizeLegacyFailureReason)
            .ToList();
        item.DuplicateFiles ??= [];
        item.DuplicateDecisions ??= new Dictionary<string, ExistingFilePolicy>(
            StringComparer.OrdinalIgnoreCase);
        item.DestinationFiles ??= new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var copySamples = NormalizeThroughputSamples(
            item.CopyByteSpeedSamples,
            item.CopyItemSpeedSamples,
            item.CopyThroughputProgressSamples);
        item.CopyByteSpeedSamples = copySamples.ByteRates;
        item.CopyItemSpeedSamples = copySamples.ItemRates;
        item.CopyThroughputProgressSamples = copySamples.ProgressPositions;

        var verifySamples = NormalizeThroughputSamples(
            item.VerifyByteSpeedSamples,
            item.VerifyItemSpeedSamples,
            item.VerifyThroughputProgressSamples);
        item.VerifyByteSpeedSamples = verifySamples.ByteRates;
        item.VerifyItemSpeedSamples = verifySamples.ItemRates;
        item.VerifyThroughputProgressSamples = verifySamples.ProgressPositions;
    }

    private static List<double> NormalizeSpeedSamples(List<double>? samples) =>
        (samples ?? [])
        .Where(value => double.IsFinite(value) && value >= 0)
        .TakeLast(CopyThroughputSampler.DefaultCapacity)
        .ToList();

    private static (
        List<double> ByteRates,
        List<double> ItemRates,
        List<double> ProgressPositions) NormalizeThroughputSamples(
            List<double>? byteRates,
            List<double>? itemRates,
            List<double>? progressPositions)
    {
        List<double> normalizedByteRates = NormalizeSpeedSamples(byteRates);
        List<double> normalizedItemRates = NormalizeSpeedSamples(itemRates);
        int sampleCount = Math.Min(normalizedByteRates.Count, normalizedItemRates.Count);
        normalizedByteRates = normalizedByteRates.TakeLast(sampleCount).ToList();
        normalizedItemRates = normalizedItemRates.TakeLast(sampleCount).ToList();

        List<double> normalizedProgress = (progressPositions ?? [])
            .Where(double.IsFinite)
            .Select(value => Math.Clamp(value, 0, 1))
            .TakeLast(sampleCount)
            .ToList();
        if (normalizedProgress.Count != sampleCount)
        {
            // Older histories have no progress positions and are rendered by
            // the legacy full-width fallback instead of inventing timestamps.
            normalizedProgress.Clear();
        }
        else
        {
            for (int index = 1; index < normalizedProgress.Count; index++)
            {
                normalizedProgress[index] = Math.Max(
                    normalizedProgress[index - 1],
                    normalizedProgress[index]);
            }
        }

        return (normalizedByteRates, normalizedItemRates, normalizedProgress);
    }

    private static FileOperationFailure NormalizeLegacyFailureReason(
        FileOperationFailure failure)
    {
        if (failure.Reason != FileOperationFailureReason.Unknown)
        {
            return failure;
        }

        FileOperationFailureReason reason = failure.Stage switch
        {
            FileOperationStage.Copying => FileOperationFailureReason.CopyIo,
            FileOperationStage.Verifying when IsLegacyVerificationMismatch(failure.Error) =>
                FileOperationFailureReason.VerificationMismatch,
            FileOperationStage.Verifying => FileOperationFailureReason.VerificationIo,
            _ => FileOperationFailureReason.Unknown
        };
        return failure with { Reason = reason };
    }

    private static bool IsLegacyVerificationMismatch(string? error)
    {
        // These are migration signatures from builds that persisted localized
        // error text before FileOperationFailureReason was added.
        return error?.StartsWith(
                   "\u6821\u9A8C\u4E0D\u4E00\u81F4\uFF1A",
                   StringComparison.Ordinal) == true ||
               error?.StartsWith(
                   "Verification mismatch:",
                   StringComparison.Ordinal) == true;
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
