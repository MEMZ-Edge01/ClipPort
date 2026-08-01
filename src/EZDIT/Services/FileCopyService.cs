using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
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
            // Skip-copy mode implies verification; there is nothing to skip
            // when the user only wants to verify existing destination files.
            options = options with { VerifyFiles = true };
        }

        progress.Report(new CopyProgressInfo(CopyPhase.Scanning, 0, 0, 0, 0, string.Empty, 0, TimeSpan.Zero));

        ScanResult scan = await ScanSourceAsync(
            sourceRoot,
            waitWhilePaused,
            cancellationToken);

        List<SourceFile> files = scan.Files;
        long totalBytes = files.Sum(file => file.Length);
        var errors = new List<string>(scan.Errors);
        var warnings = new List<string>();
        var failedFiles = new List<FileOperationFailure>();
        if (!options.SkipCopy)
        {
            PathSafety.EnsureDestinationDoesNotTraverseReparsePoint(destinationRoot);
            EnsureDestinationCapacity(destinationRoot, totalBytes);
            Directory.CreateDirectory(destinationRoot);
            foreach (string relativeDirectory in scan.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await waitWhilePaused(cancellationToken);
                string destinationDirectory = Path.Combine(destinationRoot, relativeDirectory);
                try
                {
                    PathSafety.EnsureDestinationDoesNotTraverseReparsePoint(destinationDirectory);
                    Directory.CreateDirectory(destinationDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(ResourceService.Format(
                        "Format.CannotCreateDirectory",
                        relativeDirectory,
                        ex.Message));
                }
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
        var verifications = new List<FileVerificationResult>(files.Count);
        var copyWatch = new Stopwatch();
        var verifyWatch = new Stopwatch();
        long processedCopyBytes = 0;
        int processedCopyFiles = 0;
        long copiedBytes = 0;
        int copiedFiles = 0;
        long transferredCopyBytes = 0;
        long processedVerifyBytes = 0;
        int processedVerifyFiles = 0;
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
                        processedCopyBytes += file.Length;
                        processedCopyFiles++;
                        ReportCopyProgress(file);
                        continue;
                    }

                    if (policy == ExistingFilePolicy.CreateCopy)
                    {
                        destinationPath = GetUniqueDestinationPath(destinationPath);
                    }
                }

                string partialPath = CreatePartialPath(destinationPath);
                long fileReportedBytes = 0;
                try
                {
                    string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDirectory))
                    {
                        PathSafety.EnsureDestinationDoesNotTraverseReparsePoint(destinationDirectory);
                        Directory.CreateDirectory(destinationDirectory);
                    }
                    EnsureDestinationCapacity(destinationRoot, file.Length);

                    await CopyFileAsync(
                        file.FullPath,
                        partialPath,
                        options.UseFastCopyAlgorithm,
                        waitWhilePaused,
                        bytesWritten =>
                        {
                            fileReportedBytes += bytesWritten;
                            Interlocked.Add(ref transferredCopyBytes, bytesWritten);
                            long now = copyWatch.ElapsedTicks;
                            if (now - lastReportTicks >= Stopwatch.Frequency / 10)
                            {
                                ReportCopyProgress(file, Math.Min(fileReportedBytes, file.Length));
                                lastReportTicks = now;
                            }
                        },
                        cancellationToken);

                    string? committedPath = CommitPartialFile(
                        partialPath,
                        destinationPath,
                        policy);
                    if (committedPath is null)
                    {
                        destinationPaths[file.RelativePath] = destinationPath;
                        processedCopyBytes += file.Length;
                        processedCopyFiles++;
                        ReportCopyProgress(file);
                        continue;
                    }

                    destinationPath = committedPath;
                    destinationPaths[file.RelativePath] = committedPath;
                    copiedBytes += file.Length;
                    copiedFiles++;
                    TryPreserveLastWriteTime(file, committedPath, warnings);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDeletePartialFile(partialPath);
                    processedCopyBytes += file.Length;
                    processedCopyFiles++;
                    string error = ResourceService.Format(
                        "Format.CannotCopyFile",
                        file.RelativePath,
                        ex.Message);
                    errors.Add(error);
                    failedFiles.Add(new FileOperationFailure(
                        file.RelativePath, file.FullPath, destinationPath, file.Length,
                        FileOperationStage.Copying, error, FileOperationFailureReason.CopyIo));
                    ReportCopyProgress(file);
                    continue;
                }
                catch
                {
                    TryDeletePartialFile(partialPath);
                    throw;
                }

                processedCopyBytes += file.Length;
                processedCopyFiles++;
                ReportCopyProgress(file);
            }
            copyWatch.Stop();
        }

        void ReportCopyProgress(SourceFile file, long inFlightBytes = 0) =>
            progress.Report(new CopyProgressInfo(
                CopyPhase.Copying,
                totalBytes,
                Math.Min(totalBytes, processedCopyBytes + inFlightBytes),
                files.Count,
                processedCopyFiles,
                file.RelativePath,
                Interlocked.Read(ref transferredCopyBytes) / Math.Max(copyWatch.Elapsed.TotalSeconds, 0.001),
                copyWatch.Elapsed)
            {
                SuccessfulBytes = copiedBytes,
                SuccessfulFiles = copiedFiles
            });

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
                    processedVerifyBytes += file.Length;
                    processedVerifyFiles++;
                    ReportVerifyProgress(file);
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
                        errors.Add(ResourceService.Format(
                            "Format.VerificationMismatch",
                            file.RelativePath));
                        failedFiles.Add(new FileOperationFailure(
                            file.RelativePath, file.FullPath, destinationPath, file.Length,
                            FileOperationStage.Verifying,
                            errors[^1],
                            FileOperationFailureReason.VerificationMismatch));
                    }
                    else
                    {
                        verifiedBytes += file.Length;
                        verifiedFiles++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    string error = ResourceService.Format(
                        "Format.CannotVerifyFile",
                        file.RelativePath,
                        ex.Message);
                    errors.Add(error);
                    failedFiles.Add(new FileOperationFailure(
                        file.RelativePath, file.FullPath, destinationPath, file.Length,
                        FileOperationStage.Verifying,
                        error,
                        FileOperationFailureReason.VerificationIo));
                    verifications.Add(new FileVerificationResult(
                        file.RelativePath, file.Length, string.Empty, string.Empty, false, error));
                }

                processedVerifyBytes += file.Length;
                processedVerifyFiles++;
                ReportVerifyProgress(file);
            }
            verifyWatch.Stop();
        }

        void ReportVerifyProgress(SourceFile file) =>
            progress.Report(new CopyProgressInfo(
                CopyPhase.Verifying,
                totalBytes,
                processedVerifyBytes,
                files.Count,
                processedVerifyFiles,
                file.RelativePath,
                processedVerifyBytes / Math.Max(verifyWatch.Elapsed.TotalSeconds, 0.001),
                verifyWatch.Elapsed)
            {
                SuccessfulBytes = verifiedBytes,
                SuccessfulFiles = verifiedFiles
            });

        if (options.SkipCopy)
        {
            foreach (SourceFile file in files)
            {
                string destinationPath =
                    options.DestinationPaths?.TryGetValue(
                        file.RelativePath,
                        out string? mappedPath) == true
                        ? mappedPath
                        : Path.Combine(destinationRoot, file.RelativePath);
                try
                {
                    if (!PathSafety.IsSameOrDescendantPath(destinationPath, destinationRoot))
                    {
                        throw new IOException(ResourceService.Format(
                            "Format.InvalidDestinationMapping",
                            file.RelativePath));
                    }
                    PathSafety.EnsureDestinationDoesNotTraverseReparsePoint(destinationPath);
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    string error = ResourceService.Format(
                        "Format.CannotVerifyFile",
                        file.RelativePath,
                        ex.Message);
                    errors.Add(error);
                    failedFiles.Add(new FileOperationFailure(
                        file.RelativePath,
                        file.FullPath,
                        destinationPath,
                        file.Length,
                        FileOperationStage.Verifying,
                        error,
                        FileOperationFailureReason.VerificationIo));
                    continue;
                }
                destinationPaths[file.RelativePath] = destinationPath;
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
                    CopyPhase.WaitingForDuplicateDecision,
                    totalBytes,
                    processedCopyBytes,
                    files.Count,
                    processedCopyFiles,
                    string.Empty,
                    0,
                    copyWatch.Elapsed + verifyWatch.Elapsed)
                {
                    SuccessfulBytes = copiedBytes,
                    SuccessfulFiles = copiedFiles
                });
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
            totalBytes,
            totalBytes,
            files.Count,
            files.Count,
            string.Empty,
            0,
            copyWatch.Elapsed + verifyWatch.Elapsed)
        {
            SuccessfulBytes = options.VerifyFiles ? verifiedBytes : copiedBytes,
            SuccessfulFiles = options.VerifyFiles ? verifiedFiles : copiedFiles
        });

        return new CopyResult(
            errors.Count == 0, files.Count, totalBytes, copyWatch.Elapsed, verifyWatch.Elapsed,
            options.VerifyFiles, detectedConflicts, verifications, failedFiles, errors)
        {
            CopiedBytes = copiedBytes,
            CopiedFiles = copiedFiles,
            VerifiedBytes = verifiedBytes,
            VerifiedFileCount = verifiedFiles,
            DestinationPaths = destinationPaths,
            Warnings = warnings
        };
    }

    public async Task<FileRetryResult> RetryFailedFilesAsync(
        IReadOnlyList<FileOperationFailure> failures,
        CopyOptions options,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken) =>
        await RetryFailedFilesCoreAsync(
            failures,
            options,
            overwriteVerificationMismatches: false,
            progress,
            waitWhilePaused,
            cancellationToken);

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
                ResourceService.GetString("Error.OnlyVerificationMismatchCanOverwrite"),
                nameof(failures));
        }

        return RetryFailedFilesCoreAsync(
            failures,
            options with { SkipCopy = false, VerifyFiles = true },
            overwriteVerificationMismatches: true,
            progress,
            waitWhilePaused,
            cancellationToken);
    }

    private static async Task<FileRetryResult> RetryFailedFilesCoreAsync(
        IReadOnlyList<FileOperationFailure> failures,
        CopyOptions options,
        bool overwriteVerificationMismatches,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        var remaining = new List<FileOperationFailure>();
        var verificationResults = new List<FileVerificationResult>();
        var warnings = new List<string>();
        var destinationPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = failures.Sum(item => item.Length);
        long processedCopyBytes = 0;
        int processedCopyFiles = 0;
        long copiedBytes = 0;
        int copiedFiles = 0;
        long transferredBytes = 0;
        long processedVerifyBytes = 0;
        int processedVerifyFiles = 0;
        long verifiedBytes = 0;
        int verifiedFiles = 0;
        var copyWatch = new Stopwatch();
        var verifyWatch = new Stopwatch();

        foreach (FileOperationFailure failure in failures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await waitWhilePaused(cancellationToken);

            bool shouldCopy = failure.Stage == FileOperationStage.Copying ||
                              overwriteVerificationMismatches;
            if (!shouldCopy)
            {
                await VerifyFailureAsync(failure);
                continue;
            }

            copyWatch.Start();
            string partialPath = CreatePartialPath(failure.DestinationPath);
            bool copySucceeded = false;
            long fileReportedBytes = 0;
            try
            {
                string? destinationDirectory = Path.GetDirectoryName(failure.DestinationPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    PathSafety.EnsureDestinationDoesNotTraverseReparsePoint(destinationDirectory);
                    Directory.CreateDirectory(destinationDirectory);
                    EnsureDestinationCapacity(destinationDirectory, failure.Length);
                }

                long lastRetryReportTicks = copyWatch.ElapsedTicks;
                await CopyFileAsync(
                    failure.SourcePath,
                    partialPath,
                    options.UseFastCopyAlgorithm,
                    waitWhilePaused,
                    written =>
                    {
                        fileReportedBytes += written;
                        transferredBytes += written;
                        long now = copyWatch.ElapsedTicks;
                        if (now - lastRetryReportTicks >= Stopwatch.Frequency / 10)
                        {
                            progress.Report(new CopyProgressInfo(
                                CopyPhase.Copying,
                                totalBytes,
                                Math.Min(
                                    totalBytes,
                                    processedCopyBytes + Math.Min(fileReportedBytes, failure.Length)),
                                failures.Count,
                                processedCopyFiles,
                                failure.RelativePath,
                                transferredBytes / Math.Max(copyWatch.Elapsed.TotalSeconds, 0.001),
                                copyWatch.Elapsed)
                            {
                                SuccessfulBytes = copiedBytes,
                                SuccessfulFiles = copiedFiles
                            });
                            lastRetryReportTicks = now;
                        }
                    },
                    cancellationToken);
                File.Move(partialPath, failure.DestinationPath, true);
                TryPreserveLastWriteTime(
                    new SourceFile(failure.SourcePath, failure.RelativePath, failure.Length),
                    failure.DestinationPath,
                    warnings);
                destinationPaths[failure.RelativePath] = failure.DestinationPath;
                copiedBytes += failure.Length;
                copiedFiles++;
                copySucceeded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeletePartialFile(partialPath);
                string error = ResourceService.Format(
                    "Format.CannotCopyFile",
                    failure.RelativePath,
                    ex.Message);
                remaining.Add(failure with
                {
                    Stage = FileOperationStage.Copying,
                    Error = error,
                    Reason = FileOperationFailureReason.CopyIo
                });
            }
            catch
            {
                TryDeletePartialFile(partialPath);
                throw;
            }

            processedCopyBytes += failure.Length;
            processedCopyFiles++;
            progress.Report(new CopyProgressInfo(
                CopyPhase.Copying,
                totalBytes,
                processedCopyBytes,
                failures.Count,
                processedCopyFiles,
                failure.RelativePath,
                transferredBytes / Math.Max(copyWatch.Elapsed.TotalSeconds, 0.001),
                copyWatch.Elapsed)
            {
                SuccessfulBytes = copiedBytes,
                SuccessfulFiles = copiedFiles
            });
            copyWatch.Stop();

            if (!copySucceeded || !options.VerifyFiles)
            {
                continue;
            }

            await VerifyFailureAsync(failure);
        }

        return new FileRetryResult(
            remaining,
            copyWatch.Elapsed,
            verifyWatch.Elapsed)
        {
            CopiedBytes = copiedBytes,
            CopiedFiles = copiedFiles,
            VerificationResults = verificationResults,
            DestinationPaths = destinationPaths,
            Warnings = warnings
        };

        async Task VerifyFailureAsync(FileOperationFailure failure)
        {
            verifyWatch.Start();
            try
            {
                byte[] sourceHash = await ComputeHashAsync(failure.SourcePath, waitWhilePaused, cancellationToken);
                byte[] destinationHash = await ComputeHashAsync(failure.DestinationPath, waitWhilePaused, cancellationToken);
                bool isMatch = CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
                string? error = isMatch
                    ? null
                    : ResourceService.Format(
                        "Format.VerificationMismatch",
                        failure.RelativePath);
                verificationResults.Add(new FileVerificationResult(
                    failure.RelativePath,
                    failure.Length,
                    Convert.ToHexString(sourceHash),
                    Convert.ToHexString(destinationHash),
                    isMatch,
                    error));
                if (!isMatch)
                {
                    remaining.Add(failure with
                    {
                        Stage = FileOperationStage.Verifying,
                        Error = error!,
                        Reason = FileOperationFailureReason.VerificationMismatch
                    });
                }
                else
                {
                    verifiedBytes += failure.Length;
                    verifiedFiles++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string error = ResourceService.Format(
                    "Format.CannotVerifyFile",
                    failure.RelativePath,
                    ex.Message);
                remaining.Add(failure with
                {
                    Stage = FileOperationStage.Verifying,
                    Error = error,
                    Reason = FileOperationFailureReason.VerificationIo
                });
                verificationResults.Add(new FileVerificationResult(
                    failure.RelativePath,
                    failure.Length,
                    string.Empty,
                    string.Empty,
                    false,
                    error));
            }

            processedVerifyBytes += failure.Length;
            processedVerifyFiles++;
            progress.Report(new CopyProgressInfo(
                CopyPhase.Verifying,
                totalBytes,
                processedVerifyBytes,
                failures.Count,
                processedVerifyFiles,
                failure.RelativePath,
                processedVerifyBytes / Math.Max(verifyWatch.Elapsed.TotalSeconds, 0.001),
                verifyWatch.Elapsed)
            {
                SuccessfulBytes = verifiedBytes,
                SuccessfulFiles = verifiedFiles
            });
            verifyWatch.Stop();
        }
    }

    private static Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        bool useFastCopyAlgorithm,
        Func<CancellationToken, Task> waitWhilePaused,
        Action<long> reportBytesWritten,
        CancellationToken cancellationToken)
    {
        if (useFastCopyAlgorithm && NativeCopyEngine.IsAvailable)
        {
            Action<CancellationToken> syncWait = ct =>
            {
                // The native callback runs on a C++ worker thread
                // which cannot safely run async state machines.
                // We wait synchronously; the delegate is always a
                // fast polling loop (Task.Delay / TCS check) that
                // never requires the UI thread.
                waitWhilePaused(ct).GetAwaiter().GetResult();
            };
            return NativeCopyEngine.CopyFileAsync(
                sourcePath, destinationPath, syncWait, reportBytesWritten, cancellationToken);
        }

        return useFastCopyAlgorithm
            ? CopyFilePipelinedAsync(
                sourcePath, destinationPath, waitWhilePaused, reportBytesWritten, cancellationToken)
            : CopyFileSequentialAsync(
                sourcePath, destinationPath, waitWhilePaused, reportBytesWritten, cancellationToken);
    }

    private static async Task CopyFileSequentialAsync(
        string sourcePath,
        string destinationPath,
        Func<CancellationToken, Task> waitWhilePaused,
        Action<long> reportBytesWritten,
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
        Action<long> reportBytesWritten,
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

    private static async Task<ScanResult> ScanSourceAsync(
        string sourceRoot,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        string normalizedSource = Path.GetFullPath(sourceRoot);
        var files = new List<SourceFile>();
        var directories = new List<string>();
        var errors = new List<string>();
        var pending = new Stack<string>();
        pending.Push(normalizedSource);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await waitWhilePaused(cancellationToken);
            string current = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(ResourceService.Format(
                    "Format.CannotScanDirectory",
                    Path.GetRelativePath(normalizedSource, current),
                    ex.Message));
                continue;
            }

            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await waitWhilePaused(cancellationToken);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(ResourceService.Format(
                        "Format.CannotReadPath",
                        Path.GetRelativePath(normalizedSource, entry),
                        ex.Message));
                    continue;
                }

                // Never follow links while scanning removable media.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string relativePath = Path.GetRelativePath(normalizedSource, entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Add(relativePath);
                    pending.Push(entry);
                    continue;
                }

                try
                {
                    files.Add(new SourceFile(
                        entry,
                        relativePath,
                        new FileInfo(entry).Length));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(ResourceService.Format(
                        "Format.CannotReadPath",
                        relativePath,
                        ex.Message));
                }
            }
        }

        return new ScanResult(files, directories, errors);
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
                    ResourceService.Format(
                        "Format.DestinationCapacityInsufficient",
                        DisplayFormatting.FormatBytes(requiredBytes),
                        DisplayFormatting.FormatBytes(drive.AvailableFreeSpace)));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Some network providers do not expose free-space information; writes still perform normal IO checks.
        }
    }

    private static string CreatePartialPath(string destinationPath) =>
        $"{destinationPath}.{Guid.NewGuid():N}.ezdit-partial";

    private static string? CommitPartialFile(
        string partialPath,
        string destinationPath,
        ExistingFilePolicy policy)
    {
        if (policy == ExistingFilePolicy.Overwrite)
        {
            File.Move(partialPath, destinationPath, true);
            return destinationPath;
        }

        if (policy == ExistingFilePolicy.Skip)
        {
            try
            {
                File.Move(partialPath, destinationPath, false);
                return destinationPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(destinationPath))
                {
                    TryDeletePartialFile(partialPath);
                    return null;
                }
                throw;
            }
        }

        string candidate = destinationPath;
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            if (attempt > 0 || File.Exists(candidate))
            {
                candidate = GetUniqueDestinationPath(destinationPath, attempt + 1);
            }

            try
            {
                File.Move(partialPath, candidate, false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Another process won this name; keep the completed partial
                // and atomically try the next candidate.
            }
        }

        throw new IOException(ResourceService.Format(
            "Format.CannotCreateUniqueCopy",
            destinationPath));
    }

    private static void TryPreserveLastWriteTime(
        SourceFile source,
        string destinationPath,
        List<string>? warnings)
    {
        try
        {
            File.SetLastWriteTimeUtc(
                destinationPath,
                File.GetLastWriteTimeUtc(source.FullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings?.Add(ResourceService.Format(
                "Format.CannotPreserveTimestamp",
                source.RelativePath,
                ex.Message));
        }
    }

    /// <summary>
    /// Returns a unique destination path by appending a numeric suffix.
    /// This is a best-effort helper: the existence check and the subsequent
    /// <see cref="File.Move(string,string,bool)"/> are not atomic.  Callers
    /// such as <see cref="CommitPartialFile"/> handle the TOCTOU window with
    /// a retry loop around the final move.
    /// </summary>
    private static string GetUniqueDestinationPath(string path, int startIndex = 1)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        int firstIndex = Math.Max(1, startIndex);
        for (int index = firstIndex; index <= 10_000; index++)
        {
            string candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(ResourceService.Format(
            "Format.CannotCreateUniqueCopy",
            path));
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
    private sealed record ScanResult(
        List<SourceFile> Files,
        List<string> Directories,
        List<string> Errors);
    private sealed record SourceFile(string FullPath, string RelativePath, long Length);
}
