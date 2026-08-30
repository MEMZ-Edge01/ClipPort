using System.Text.Json;
using ClipPort.Models;
using ClipPort.Services;

namespace ClipPort.FnOS.Persistence;

public sealed class JsonPartialFileJournal : IPartialFileJournal
{
    private readonly object _sync = new();
    private readonly string _journalPath;
    private readonly HashSet<string> _paths = new(PathSemantics.Comparer);

    public JsonPartialFileJournal(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _journalPath = Path.Combine(dataDirectory, "partial-files.json");
        Load();
    }

    public void CleanupTrackedFiles()
    {
        lock (_sync)
        {
            foreach (string path in _paths.ToArray())
            {
                if (!IsOwnedPartialPath(path))
                {
                    _paths.Remove(path);
                    continue;
                }

                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    _paths.Remove(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep the entry so the next safe startup can retry it.
                }
            }
            Persist();
        }
    }

    public void Track(string path)
    {
        string normalized = Path.GetFullPath(path);
        if (!IsOwnedPartialPath(normalized))
        {
            throw new InvalidOperationException("Only ClipPort partial files can be journaled.");
        }

        lock (_sync)
        {
            if (_paths.Add(normalized))
            {
                Persist();
            }
        }
    }

    public void Untrack(string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return;
        }

        lock (_sync)
        {
            if (!_paths.Remove(normalized))
            {
                return;
            }

            try
            {
                Persist();
            }
            catch (IOException)
            {
                // A stale entry is safe: startup cleanup checks the exact path.
                _paths.Add(normalized);
            }
            catch (UnauthorizedAccessException)
            {
                _paths.Add(normalized);
            }
        }
    }

    internal IReadOnlyCollection<string> Snapshot()
    {
        lock (_sync)
        {
            return _paths.ToArray();
        }
    }

    private void Load()
    {
        if (!File.Exists(_journalPath))
        {
            return;
        }

        try
        {
            string[] paths = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(_journalPath)) ?? [];
            foreach (string path in paths.Where(IsOwnedPartialPath))
            {
                _paths.Add(Path.GetFullPath(path));
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Ignore a corrupt journal rather than broadening cleanup scope.
        }
    }

    private void Persist()
    {
        string temporaryPath = _journalPath + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(_paths.Order(PathSemantics.Comparer)));
        File.Move(temporaryPath, _journalPath, overwrite: true);
    }

    private static bool IsOwnedPartialPath(string path) =>
        Path.IsPathFullyQualified(path) &&
        path.EndsWith(".clipport-partial", StringComparison.Ordinal);
}
