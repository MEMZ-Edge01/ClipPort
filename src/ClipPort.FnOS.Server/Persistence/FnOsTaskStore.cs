using System.Text.Json;
using System.Text.Json.Serialization;
using ClipPort.FnOS.Contracts;

namespace ClipPort.FnOS.Persistence;

public sealed class FnOsTaskStore
{
    private const int HistoryLimit = 200;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public FnOsTaskStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "tasks.json");
    }

    public async Task<List<FnOsTaskRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(_path);
            List<FnOsTaskRecord> records = await JsonSerializer.DeserializeAsync<List<FnOsTaskRecord>>(
                stream,
                _options,
                cancellationToken) ?? [];
            foreach (FnOsTaskRecord record in records.Where(IsActive))
            {
                record.Status = FnOsTaskStatus.Interrupted;
                record.FinishedAt = DateTimeOffset.UtcNow;
                record.Errors.Add("The fnOS application stopped before this task completed.");
            }
            return records.Take(HistoryLimit).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(
        IReadOnlyCollection<FnOsTaskRecord> records,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string temporaryPath = _path + ".tmp";
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        records.Take(HistoryLimit),
                        _options,
                        cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                File.Move(temporaryPath, _path, overwrite: true);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsActive(FnOsTaskRecord record) => record.Status is
        FnOsTaskStatus.Queued or
        FnOsTaskStatus.Running or
        FnOsTaskStatus.Paused or
        FnOsTaskStatus.AwaitingDuplicateDecision or
        FnOsTaskStatus.AwaitingFailureDecision;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
