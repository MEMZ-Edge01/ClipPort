using System.Diagnostics;
using ClipPort.Models;

namespace ClipPort.Services;

internal sealed record SourceFile(string FullPath, string RelativePath, long Length);

internal sealed record SourceScanResult(
    List<SourceFile> Files,
    List<string> Directories,
    List<string> Errors,
    long TotalBytes);

internal readonly record struct SourceEntry(
    string FullPath,
    FileAttributes Attributes,
    long Length,
    Exception? Error = null);

internal interface ISourceEntryProvider
{
    IEnumerable<SourceEntry> EnumerateDirectory(string directory);
}

internal sealed class FileSystemSourceEntryProvider : ISourceEntryProvider
{
    public IEnumerable<SourceEntry> EnumerateDirectory(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0
        };

        foreach (FileSystemInfo entry in new DirectoryInfo(directory)
                     .EnumerateFileSystemInfos("*", options))
        {
            yield return ReadEntry(entry);
        }
    }

    private static SourceEntry ReadEntry(FileSystemInfo entry)
    {
        try
        {
            FileAttributes attributes = entry.Attributes;
            long length = entry is FileInfo file ? file.Length : 0;
            return new SourceEntry(entry.FullName, attributes, length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preserve streaming enumeration when metadata for one entry is unreadable.
            return new SourceEntry(entry.FullName, 0, 0, ex);
        }
    }
}

internal sealed class SourceScanner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly ISourceEntryProvider _entryProvider;

    public SourceScanner(ISourceEntryProvider? entryProvider = null)
    {
        _entryProvider = entryProvider ?? new FileSystemSourceEntryProvider();
    }

    public Task<SourceScanResult> ScanAsync(
        string sourceRoot,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => Scan(
                sourceRoot,
                progress,
                waitWhilePaused,
                cancellationToken),
            cancellationToken);

    private SourceScanResult Scan(
        string sourceRoot,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        string normalizedSource = Path.GetFullPath(sourceRoot);
        var files = new List<SourceFile>();
        var directories = new List<string>();
        var errors = new List<string>();
        var pending = new Stack<string>();
        var watch = Stopwatch.StartNew();
        TimeSpan lastReport = TimeSpan.Zero;
        long totalBytes = 0;
        int scannedDirectories = 0;
        pending.Push(normalizedSource);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            waitWhilePaused(cancellationToken).GetAwaiter().GetResult();
            string current = pending.Pop();
            scannedDirectories++;
            try
            {
                foreach (SourceEntry entry in _entryProvider.EnumerateDirectory(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    waitWhilePaused(cancellationToken).GetAwaiter().GetResult();
                    if (entry.Error is Exception entryError)
                    {
                        errors.Add(ResourceService.Format(
                            "Format.CannotReadPath",
                            Path.GetRelativePath(normalizedSource, entry.FullPath),
                            entryError.Message));
                        continue;
                    }
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    string relativePath = Path.GetRelativePath(normalizedSource, entry.FullPath);
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Add(relativePath);
                        pending.Push(entry.FullPath);
                    }
                    else
                    {
                        files.Add(new SourceFile(entry.FullPath, relativePath, entry.Length));
                        totalBytes = checked(totalBytes + entry.Length);
                    }

                    if (watch.Elapsed - lastReport >= ProgressInterval)
                    {
                        Report(relativePath);
                        lastReport = watch.Elapsed;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(ResourceService.Format(
                    "Format.CannotScanDirectory",
                    Path.GetRelativePath(normalizedSource, current),
                    ex.Message));
            }
        }

        Report(string.Empty);
        return new SourceScanResult(files, directories, errors, totalBytes);

        void Report(string currentPath) =>
            progress.Report(new CopyProgressInfo(
                CopyPhase.Scanning,
                totalBytes,
                totalBytes,
                files.Count,
                files.Count,
                currentPath,
                0,
                watch.Elapsed)
            {
                SuccessfulBytes = 0,
                SuccessfulFiles = 0,
                IsTotalKnown = false,
                ScannedDirectories = scannedDirectories
            });
    }
}
