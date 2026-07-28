using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using EZDIT.Models;

namespace EZDIT.Services;

public sealed class FileCopyService
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const int PipelineBufferCount = 4;

    public Task<CopyResult> CopyAndVerifyAsync(
        string sourceRoot,
        string destinationRoot,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken) =>
        CopyAndVerifyAsync(
            sourceRoot,
            destinationRoot,
            new CopyOptions(),
            progress,
            new Progress<DuplicateFileConflict>(_ => { }),
            (conflicts, _) => Task.FromResult<IReadOnlyDictionary<string, ExistingFilePolicy>>(
                conflicts.ToDictionary(item => item.RelativePath, _ => ExistingFilePolicy.Skip, StringComparer.OrdinalIgnoreCase)),
            waitWhilePaused,
            cancellationToken);

    public Task<CopyResult> CopyAndVerifyAsync(
        string sourceRoot,
        string destinationRoot,
        CopyOptions options,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken) =>
        CopyAndVerifyAsync(
            sourceRoot,
            destinationRoot,
            options,
            progress,
            new Progress<DuplicateFileConflict>(_ => { }),
            (conflicts, _) => Task.FromResult<IReadOnlyDictionary<string, ExistingFilePolicy>>(
                conflicts.ToDictionary(item => item.RelativePath, _ => ExistingFilePolicy.Skip, StringComparer.OrdinalIgnoreCase)),
            waitWhilePaused,
            cancellationToken);

    public async Task<CopyResult> CopyAndVerifyAsync(
        string sourceRoot,
        string destinationRoot,
        CopyOptions options,
        IProgress<CopyProgressInfo> progress,
        IProgress<DuplicateFileConflict> duplicateProgress,
        Func<IReadOnlyList<DuplicateFileConflict>, CancellationToken, Task<IReadOnlyDictionary<string, ExistingFilePolicy>>> resolveDuplicates,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        if (options.SkipCopy)
        {
            options = options with { VerifyFiles = true };
        }

        progress.Report(new CopyProgressInfo(CopyPhase.Scanning, 0, 0, 0, 0, string.Empty, 0, TimeSpan.Zero));

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        ScanResult scan = await Task.Run(() =>
        {
            var files = Directory.EnumerateFiles(sourceRoot, "*", enumerationOptions)
                .Select(path => new SourceFile(path, Path.GetRelativePath(sourceRoot, path), new FileInfo(path).Length))
                .ToList();
            var directories = Directory.EnumerateDirectories(sourceRoot, "*", enumerationOptions)
                .Select(path => Path.GetRelativePath(sourceRoot, path))
                .ToList();
            return new ScanResult(files, directories);
        }, cancellationToken);

        List<SourceFile> files = scan.Files;
        long totalBytes = files.Sum(file => file.Length);
        if (!options.SkipCopy)
        {
            Directory.CreateDirectory(destinationRoot);
            EnsureDestinationCapacity(destinationRoot, totalBytes);
            foreach (string relativeDirectory in scan.Directories)
            {
                Directory.CreateDirectory(Path.Combine(destinationRoot, relativeDirectory));
            }
        }

        var immediateFiles = new List<SourceFile>(files.Count);
        var duplicateFiles = new List<SourceFile>();
        var detectedConflicts = new List<DuplicateFileConflict>();
        foreach (SourceFile file in files)
        {
            if (options.SkipCopy)
            {
                immediateFiles.Add(file);
                continue;
            }

            string destinationPath = Path.Combine(destinationRoot, file.RelativePath);
            if (File.Exists(destinationPath))
            {
                var conflict = new DuplicateFileConflict(
                    file.RelativePath, file.FullPath, destinationPath, file.Length);
                detectedConflicts.Add(conflict);
                duplicateProgress.Report(conflict);
                if (options.ExistingFilePolicy == ExistingFilePolicy.Ask)
                {
                    duplicateFiles.Add(file);
                    continue;
                }
            }
            immediateFiles.Add(file);
        }

        var destinationPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var failedFiles = new List<FileOperationFailure>();
        var verifications = new List<FileVerificationResult>(files.Count);
        var copyWatch = new Stopwatch();
        var verifyWatch = new Stopwatch();
        long copiedBytes = 0;
        int copiedFiles = 0;
        long verifiedBytes = 0;
        int verifiedFiles = 0;
        long lastReportTicks = 0;

        async Task CopyGroupAsync(IReadOnlyList<SourceFile> group, ExistingFilePolicy policy)
        {
            copyWatch.Start();
            foreach (SourceFile file in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await waitWhilePaused(cancellationToken);

                string destinationPath = Path.Combine(destinationRoot, file.RelativePath);
                if (File.Exists(destinationPath))
                {
                    if (policy == ExistingFilePolicy.Skip)
                    {
                        destinationPaths[file.RelativePath] = destinationPath;
                        copiedBytes += file.Length;
                        copiedFiles++;
                        ReportCopyProgress(file);
                        continue;
                    }

                    if (policy == ExistingFilePolicy.CreateCopy)
                    {
                        destinationPath = GetUniqueDestinationPath(destinationPath);
                    }
                }

                string partialPath = destinationPath + ".ezdit-partial";
                long fileReportedBytes = 0;
                try
                {
                    string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    await CopyFileAsync(
                        file.FullPath,
                        partialPath,
                        options.UseFastCopyAlgorithm,
                        waitWhilePaused,
                        bytesWritten =>
                        {
                            fileReportedBytes += bytesWritten;
                            copiedBytes += bytesWritten;
                            long now = copyWatch.ElapsedTicks;
                            if (now - lastReportTicks >= Stopwatch.Frequency / 10)
                            {
                                ReportCopyProgress(file);
                                lastReportTicks = now;
                            }
                        },
                        cancellationToken);

                    File.Move(partialPath, destinationPath, true);
                    File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(file.FullPath));
                    destinationPaths[file.RelativePath] = destinationPath;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDeletePartialFile(partialPath);
                    copiedBytes += Math.Max(0, file.Length - fileReportedBytes);
                    string error = $"\u65E0\u6CD5\u62F7\u8D1D {file.RelativePath}\uFF1A{ex.Message}";
                    errors.Add(error);
                    failedFiles.Add(new FileOperationFailure(
                        file.RelativePath, file.FullPath, destinationPath, file.Length,
                        FileOperationStage.Copying, error));
                }
                catch
                {
                    TryDeletePartialFile(partialPath);
                    throw;
                }

                copiedFiles++;
                ReportCopyProgress(file);
            }
            copyWatch.Stop();
        }

        void ReportCopyProgress(SourceFile file) =>
            progress.Report(new CopyProgressInfo(
                CopyPhase.Copying, totalBytes, copiedBytes, files.Count, copiedFiles,
                file.RelativePath, copiedBytes / Math.Max(copyWatch.Elapsed.TotalSeconds, 0.001), copyWatch.Elapsed));

        async Task VerifyGroupAsync(IReadOnlyList<SourceFile> group)
        {
            if (!options.VerifyFiles)
            {
                return;
            }

            verifyWatch.Start();
            foreach (SourceFile file in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await waitWhilePaused(cancellationToken);

                if (!destinationPaths.TryGetValue(file.RelativePath, out string? destinationPath))
                {
                    verifiedBytes += file.Length;
                    verifiedFiles++;
                    progress.Report(new CopyProgressInfo(
                        CopyPhase.Verifying, totalBytes, verifiedBytes, files.Count, verifiedFiles,
                        file.RelativePath, verifiedBytes / Math.Max(verifyWatch.Elapsed.TotalSeconds, 0.001), verifyWatch.Elapsed));
                    continue;
                }
                try
                {
                    byte[] sourceHash;
                    byte[] destinationHash;
                    if (options.UseFastCopyAlgorithm)
                    {
                        byte[][] hashes = await Task.WhenAll(
                            ComputeHashAsync(file.FullPath, waitWhilePaused, cancellationToken),
                            ComputeHashAsync(destinationPath, waitWhilePaused, cancellationToken));
                        sourceHash = hashes[0];
                        destinationHash = hashes[1];
                    }
                    else
                    {
                        sourceHash = await ComputeHashAsync(file.FullPath, waitWhilePaused, cancellationToken);
                        destinationHash = await ComputeHashAsync(destinationPath, waitWhilePaused, cancellationToken);
                    }
                    bool isMatch = CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
                    verifications.Add(new FileVerificationResult(
                        file.RelativePath, file.Length, Convert.ToHexString(sourceHash),
                        Convert.ToHexString(destinationHash), isMatch, null));
                    if (!isMatch)
                    {
                        errors.Add($"校验不一致：{file.RelativePath}");
                        failedFiles.Add(new FileOperationFailure(
                            file.RelativePath, file.FullPath, destinationPath, file.Length,
                            FileOperationStage.Verifying, errors[^1]));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string error = $"无法校验 {file.RelativePath}：{ex.Message}";
                    errors.Add(error);
                    failedFiles.Add(new FileOperationFailure(
                        file.RelativePath, file.FullPath, destinationPath, file.Length,
                        FileOperationStage.Verifying, error));
                    verifications.Add(new FileVerificationResult(
                        file.RelativePath, file.Length, string.Empty, string.Empty, false, error));
                }

                verifiedBytes += file.Length;
                verifiedFiles++;
                progress.Report(new CopyProgressInfo(
                    CopyPhase.Verifying, totalBytes, verifiedBytes, files.Count, verifiedFiles,
                    file.RelativePath, verifiedBytes / Math.Max(verifyWatch.Elapsed.TotalSeconds, 0.001), verifyWatch.Elapsed));
            }
            verifyWatch.Stop();
        }

        if (options.SkipCopy)
        {
            foreach (SourceFile file in files)
            {
                destinationPaths[file.RelativePath] = Path.Combine(destinationRoot, file.RelativePath);
            }
            await VerifyGroupAsync(files);
        }
        else
        {
            ExistingFilePolicy initialPolicy = options.ExistingFilePolicy == ExistingFilePolicy.Ask
                ? ExistingFilePolicy.Overwrite
                : options.ExistingFilePolicy;
            await CopyGroupAsync(immediateFiles, initialPolicy);
            await VerifyGroupAsync(immediateFiles);

            if (duplicateFiles.Count > 0)
            {
                progress.Report(new CopyProgressInfo(
                    CopyPhase.WaitingForDuplicateDecision, totalBytes, copiedBytes, files.Count, copiedFiles,
                    string.Empty, 0, copyWatch.Elapsed + verifyWatch.Elapsed));
                IReadOnlyList<DuplicateFileConflict> conflicts = duplicateFiles
                    .Select(file => new DuplicateFileConflict(
                        file.RelativePath, file.FullPath,
                        Path.Combine(destinationRoot, file.RelativePath), file.Length))
                    .ToList();
                IReadOnlyDictionary<string, ExistingFilePolicy> decisions =
                    await resolveDuplicates(conflicts, cancellationToken);
                foreach (SourceFile duplicate in duplicateFiles)
                {
                    ExistingFilePolicy decision = decisions.TryGetValue(duplicate.RelativePath, out ExistingFilePolicy selected)
                        ? selected
                        : ExistingFilePolicy.Skip;
                    if (decision == ExistingFilePolicy.Ask)
                    {
                        decision = ExistingFilePolicy.Skip;
                    }
                    await CopyGroupAsync([duplicate], decision);
                    await VerifyGroupAsync([duplicate]);
                }
            }
        }

        progress.Report(new CopyProgressInfo(
            CopyPhase.Completed,
            totalBytes, totalBytes, files.Count, files.Count, string.Empty, 0,
            copyWatch.Elapsed + verifyWatch.Elapsed));

        return new CopyResult(
            errors.Count == 0, files.Count, totalBytes, copyWatch.Elapsed, verifyWatch.Elapsed,
            options.VerifyFiles, detectedConflicts, verifications, failedFiles, errors);
    }

    public async Task<FileRetryResult> RetryFailedFilesAsync(
        IReadOnlyList<FileOperationFailure> failures,
        CopyOptions options,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        var remaining = new List<FileOperationFailure>();
        long totalBytes = failures.Sum(item => item.Length);
        long processedBytes = 0;
        int processedFiles = 0;

        if (options.SkipCopy)
        {
            var verifyWatch = Stopwatch.StartNew();
            foreach (FileOperationFailure failure in failures)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await waitWhilePaused(cancellationToken);
                try
                {
                    byte[] sourceHash = await ComputeHashAsync(
                        failure.SourcePath, waitWhilePaused, cancellationToken);
                    byte[] destinationHash = await ComputeHashAsync(
                        failure.DestinationPath, waitWhilePaused, cancellationToken);
                    if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                    {
                        string error = $"\u6821\u9A8C\u4E0D\u4E00\u81F4\uFF1A{failure.RelativePath}";
                        remaining.Add(failure with { Stage = FileOperationStage.Verifying, Error = error });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string error = $"\u65E0\u6CD5\u6821\u9A8C {failure.RelativePath}\uFF1A{ex.Message}";
                    remaining.Add(failure with { Stage = FileOperationStage.Verifying, Error = error });
                }

                processedBytes += failure.Length;
                processedFiles++;
                progress.Report(new CopyProgressInfo(
                    CopyPhase.Verifying, totalBytes, processedBytes, failures.Count, processedFiles,
                    failure.RelativePath,
                    processedBytes / Math.Max(verifyWatch.Elapsed.TotalSeconds, 0.001),
                    verifyWatch.Elapsed));
            }
            verifyWatch.Stop();
            return new FileRetryResult(remaining, TimeSpan.Zero, verifyWatch.Elapsed);
        }

        var copyWatch = Stopwatch.StartNew();

        foreach (FileOperationFailure failure in failures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await waitWhilePaused(cancellationToken);
            string partialPath = failure.DestinationPath + ".ezdit-partial";
            bool copied = false;
            try
            {
                string? destinationDirectory = Path.GetDirectoryName(failure.DestinationPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                await CopyFileAsync(
                    failure.SourcePath,
                    partialPath,
                    options.UseFastCopyAlgorithm,
                    waitWhilePaused,
                    _ => { },
                    cancellationToken);
                File.Move(partialPath, failure.DestinationPath, true);
                File.SetLastWriteTimeUtc(failure.DestinationPath, File.GetLastWriteTimeUtc(failure.SourcePath));
                copied = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeletePartialFile(partialPath);
                string error = $"\u65E0\u6CD5\u62F7\u8D1D {failure.RelativePath}\uFF1A{ex.Message}";
                remaining.Add(failure with { Stage = FileOperationStage.Copying, Error = error });
            }

            processedBytes += failure.Length;
            processedFiles++;
            progress.Report(new CopyProgressInfo(
                CopyPhase.Copying, totalBytes, processedBytes, failures.Count, processedFiles,
                failure.RelativePath, processedBytes / Math.Max(copyWatch.Elapsed.TotalSeconds, 0.001), copyWatch.Elapsed));

            if (!copied || !options.VerifyFiles)
            {
                continue;
            }

            try
            {
                byte[] sourceHash = await ComputeHashAsync(failure.SourcePath, waitWhilePaused, cancellationToken);
                byte[] destinationHash = await ComputeHashAsync(failure.DestinationPath, waitWhilePaused, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                {
                    string error = $"\u6821\u9A8C\u4E0D\u4E00\u81F4\uFF1A{failure.RelativePath}";
                    remaining.Add(failure with { Stage = FileOperationStage.Verifying, Error = error });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string error = $"\u65E0\u6CD5\u6821\u9A8C {failure.RelativePath}\uFF1A{ex.Message}";
                remaining.Add(failure with { Stage = FileOperationStage.Verifying, Error = error });
            }
        }

        copyWatch.Stop();
        return new FileRetryResult(remaining, copyWatch.Elapsed, TimeSpan.Zero);
    }

    public Task<FileRetryResult> OverwriteVerificationMismatchesAsync(
        IReadOnlyList<FileOperationFailure> failures,
        CopyOptions options,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        if (failures.Count == 0 || failures.Any(failure => !failure.IsVerificationMismatch))
        {
            throw new ArgumentException(
                "\u53EA\u80FD\u8986\u76D6\u6821\u9A8C\u4E0D\u4E00\u81F4\u7684\u6587\u4EF6\u3002",
                nameof(failures));
        }

        return RetryFailedFilesAsync(
            failures,
            options with { SkipCopy = false, VerifyFiles = true },
            progress,
            waitWhilePaused,
            cancellationToken);
    }

    private static Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        bool useFastCopyAlgorithm,
        Func<CancellationToken, Task> waitWhilePaused,
        Action<int> reportBytesWritten,
        CancellationToken cancellationToken) =>
        useFastCopyAlgorithm && NativeCopyEngine.IsAvailable
            ? NativeCopyEngine.CopyFileAsync(
                sourcePath, destinationPath, waitWhilePaused, reportBytesWritten, cancellationToken)
            : useFastCopyAlgorithm
                ? CopyFilePipelinedAsync(
                    sourcePath, destinationPath, waitWhilePaused, reportBytesWritten, cancellationToken)
                : CopyFileSequentialAsync(
                    sourcePath, destinationPath, waitWhilePaused, reportBytesWritten, cancellationToken);

    private static async Task CopyFileSequentialAsync(
        string sourcePath,
        string destinationPath,
        Func<CancellationToken, Task> waitWhilePaused,
        Action<int> reportBytesWritten,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await source.ReadAsync(
                buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                await waitWhilePaused(cancellationToken);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                reportBytesWritten(read);
            }
            await destination.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyFilePipelinedAsync(
        string sourcePath,
        string destinationPath,
        Func<CancellationToken, Task> waitWhilePaused,
        Action<int> reportBytesWritten,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<CopyBuffer>(
            new BoundedChannelOptions(PipelineBufferCount)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        using var pipelineCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken pipelineToken = pipelineCancellation.Token;

        async Task ReadSourceAsync()
        {
            try
            {
                await using var source = new FileStream(
                    sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    await waitWhilePaused(pipelineToken);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                    bool handedOff = false;
                    try
                    {
                        int read = await source.ReadAsync(
                            buffer.AsMemory(0, BufferSize), pipelineToken);
                        if (read == 0)
                        {
                            break;
                        }

                        await channel.Writer.WriteAsync(
                            new CopyBuffer(buffer, read), pipelineToken);
                        handedOff = true;
                    }
                    finally
                    {
                        if (!handedOff)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
                pipelineCancellation.Cancel();
                throw;
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }

        async Task WriteDestinationAsync()
        {
            try
            {
                await using var destination = new FileStream(
                    destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await foreach (CopyBuffer block in channel.Reader.ReadAllAsync(pipelineToken))
                {
                    try
                    {
                        await waitWhilePaused(pipelineToken);
                        await destination.WriteAsync(
                            block.Buffer.AsMemory(0, block.Count), pipelineToken);
                        reportBytesWritten(block.Count);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(block.Buffer);
                    }
                }
                await destination.FlushAsync(pipelineToken);
            }
            catch
            {
                pipelineCancellation.Cancel();
                throw;
            }
        }

        Task readTask = ReadSourceAsync();
        Task writeTask = WriteDestinationAsync();
        try
        {
            await Task.WhenAll(readTask, writeTask);
        }
        finally
        {
            pipelineCancellation.Cancel();
            while (channel.Reader.TryRead(out CopyBuffer? block))
            {
                if (block is not null)
                    ArrayPool<byte>.Shared.Return(block.Buffer);
            }
        }
    }

    private static async Task<byte[]> ComputeHashAsync(
        string path,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                await waitWhilePaused(cancellationToken);
                hash.AppendData(buffer, 0, read);
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureDestinationCapacity(string destinationRoot, long requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(
                    $"目标磁盘空间不足：需要 {FormatBytes(requiredBytes)}，可用 {FormatBytes(drive.AvailableFreeSpace)}。");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Some network providers do not expose free-space information; writes still perform normal IO checks.
        }
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:F0} {units[unit]}" : $"{bytes:F2} {units[unit]}";
    }

    private static string GetUniqueDestinationPath(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int index = 1; ; index++)
        {
            string candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !File.Exists(candidate + ".ezdit-partial"))
            {
                return candidate;
            }
        }
    }

    private static void TryDeletePartialFile(string path)
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
            // Keep the original copy error; an orphaned partial file is safe to remove later.
        }
    }

    private sealed record CopyBuffer(byte[] Buffer, int Count);
    private sealed record ScanResult(List<SourceFile> Files, List<string> Directories);
    private sealed record SourceFile(string FullPath, string RelativePath, long Length);
}
