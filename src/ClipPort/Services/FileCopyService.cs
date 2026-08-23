using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using ClipPort.Models;

namespace ClipPort.Services;

public sealed class FileCopyService
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const int PipelineBufferCount = 4;
    private readonly SourceScanner _sourceScanner;
    private static readonly IDisposable NoopExecutionLease = new NoopDisposable();

    public FileCopyService()
        : this(new SourceScanner())
    {
    }

    internal FileCopyService(SourceScanner sourceScanner)
    {
        _sourceScanner = sourceScanner;
    }

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
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<IDisposable>>? executionLeaseFactory = null)
    {
        options = options with
        {
            VerificationAlgorithm = VerificationAlgorithms.Normalize(options.VerificationAlgorithm),
            VerificationExecutionMode = Enum.IsDefined(options.VerificationExecutionMode)
                ? options.VerificationExecutionMode
                : VerificationExecutionMode.AfterCopy
        };

        if (options.SkipCopy)
        {
            // Skip-copy mode implies verification; there is nothing to skip
            // when the user only wants to verify existing destination files.
            options = options with
            {
                VerifyFiles = true,
                VerificationExecutionMode = VerificationExecutionMode.AfterCopy
            };
        }

        progress.Report(new CopyProgressInfo(
            CopyPhase.Scanning,
            0,
            0,
            0,
            0,
            string.Empty,
            0,
            TimeSpan.Zero)
        {
            IsTotalKnown = false
        });

        SourceScanResult scan;
        using (await AcquireExecutionLeaseAsync(
                   executionLeaseFactory,
                   cancellationToken).ConfigureAwait(false))
        {
            scan = await _sourceScanner.ScanAsync(
                sourceRoot,
                progress,
                waitWhilePaused,
                cancellationToken).ConfigureAwait(false);
        }

        List<SourceFile> files = scan.Files;
        long totalBytes = scan.TotalBytes;
        var errors = new List<string>(scan.Errors);
        var warnings = new List<string>();
        var failedFiles = new List<FileOperationFailure>();
        var immediateFiles = new List<SourceFile>(files.Count);
        var duplicateFiles = new List<SourceFile>();
        var detectedConflicts = new List<DuplicateFileConflict>();
        using (await AcquireExecutionLeaseAsync(
                   executionLeaseFactory,
                   cancellationToken).ConfigureAwait(false))
        {
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
        long sequentialVerifyBytes = 0;
        int processedVerifyFiles = 0;
        long verifiedBytes = 0;
        int verifiedFiles = 0;
        long lastReportTicks = 0;
        TimeSpan verificationElapsedBase = TimeSpan.Zero;
        bool opportunisticVerification = options.VerifyFiles &&
            !options.SkipCopy &&
            options.VerificationExecutionMode == VerificationExecutionMode.OpportunisticDuringCopy;
        var deferredVerificationItems = new List<VerificationWorkItem>();
        using BackgroundVerificationWorker? backgroundVerifier = opportunisticVerification
            ? new BackgroundVerificationWorker(
                options.VerificationAlgorithm,
                totalBytes,
                files.Count,
                progress,
                waitWhilePaused,
                executionLeaseFactory,
                cancellationToken)
            : null;

        void QueueVerification(SourceFile file, string destinationPath)
        {
            if (!opportunisticVerification)
            {
                return;
            }

            var item = new VerificationWorkItem(file, destinationPath);
            if (backgroundVerifier?.TryQueue(item) != true)
            {
                // Never make copying wait for verification. A full queue, or a
                // platform that cannot lower I/O priority, falls back to the tail.
                deferredVerificationItems.Add(item);
            }
        }

        async Task CopyGroupAsync(IReadOnlyList<SourceFile> group, ExistingFilePolicy policy)
        {
            copyWatch.Start();
            foreach (SourceFile file in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await waitWhilePaused(cancellationToken);
                using IDisposable executionLease = await AcquireExecutionLeaseAsync(
                    executionLeaseFactory,
                    cancellationToken).ConfigureAwait(false);

                string destinationPath = Path.Combine(destinationRoot, file.RelativePath);
                if (File.Exists(destinationPath))
                {
                    if (policy == ExistingFilePolicy.Skip)
                    {
                        destinationPaths[file.RelativePath] = destinationPath;
                        QueueVerification(file, destinationPath);
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
                        QueueVerification(file, destinationPath);
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
                    QueueVerification(file, committedPath);
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
                using IDisposable executionLease = await AcquireExecutionLeaseAsync(
                    executionLeaseFactory,
                    cancellationToken).ConfigureAwait(false);

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
                            ComputeHashAsync(file.FullPath, options.VerificationAlgorithm, waitWhilePaused, cancellationToken),
                            ComputeHashAsync(destinationPath, options.VerificationAlgorithm, waitWhilePaused, cancellationToken));
                        sourceHash = hashes[0];
                        destinationHash = hashes[1];
                    }
                    else
                    {
                        sourceHash = await ComputeHashAsync(file.FullPath, options.VerificationAlgorithm, waitWhilePaused, cancellationToken);
                        destinationHash = await ComputeHashAsync(destinationPath, options.VerificationAlgorithm, waitWhilePaused, cancellationToken);
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
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
                sequentialVerifyBytes += file.Length;
                processedVerifyFiles++;
                ReportVerifyProgress(file);
            }
            verifyWatch.Stop();
        }

        async Task VerifyDeferredItemsAsync(IReadOnlyList<VerificationWorkItem> items)
        {
            foreach (VerificationWorkItem item in items)
            {
                destinationPaths[item.Source.RelativePath] = item.DestinationPath;
            }
            await VerifyGroupAsync(items.Select(item => item.Source).ToArray());
        }

        void ApplyBackgroundOutcome(BackgroundVerificationOutcome outcome)
        {
            SourceFile file = outcome.WorkItem.Source;
            verifications.Add(outcome.Verification);
            processedVerifyBytes += file.Length;
            processedVerifyFiles++;
            if (outcome.Failure is FileOperationFailure failure)
            {
                errors.Add(failure.Error);
                failedFiles.Add(failure);
            }
            else
            {
                verifiedBytes += file.Length;
                verifiedFiles++;
            }
        }

        async Task CompleteOpportunisticVerificationAsync()
        {
            if (!opportunisticVerification || backgroundVerifier is null)
            {
                return;
            }

            BackgroundVerificationSummary summary = await backgroundVerifier.CompleteAsync();
            verificationElapsedBase = summary.UsedBackgroundIoPriority
                ? summary.Duration
                : copyWatch.Elapsed;
            foreach (BackgroundVerificationOutcome outcome in summary.Outcomes)
            {
                ApplyBackgroundOutcome(outcome);
            }
            await VerifyDeferredItemsAsync(deferredVerificationItems);
        }

        void ReportVerifyProgress(SourceFile file) =>
            progress.Report(new CopyProgressInfo(
                CopyPhase.Verifying,
                totalBytes,
                processedVerifyBytes,
                files.Count,
                processedVerifyFiles,
                file.RelativePath,
                sequentialVerifyBytes / Math.Max(verifyWatch.Elapsed.TotalSeconds, 0.001),
                verificationElapsedBase + verifyWatch.Elapsed)
            {
                SuccessfulBytes = verifiedBytes,
                SuccessfulFiles = verifiedFiles
            });

        if (options.SkipCopy)
        {
            using (await AcquireExecutionLeaseAsync(
                       executionLeaseFactory,
                       cancellationToken).ConfigureAwait(false))
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
            }
            await VerifyGroupAsync(files);
        }
        else
        {
            ExistingFilePolicy initialPolicy = options.ExistingFilePolicy == ExistingFilePolicy.Ask
                ? ExistingFilePolicy.Overwrite
                : options.ExistingFilePolicy;
            await CopyGroupAsync(immediateFiles, initialPolicy);
            if (!opportunisticVerification)
            {
                await VerifyGroupAsync(immediateFiles);
            }

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
                    if (!opportunisticVerification)
                    {
                        await VerifyGroupAsync([duplicate]);
                    }
                }
            }
            await CompleteOpportunisticVerificationAsync();
        }

        TimeSpan verificationDuration = opportunisticVerification
            ? verificationElapsedBase + verifyWatch.Elapsed
            : verifyWatch.Elapsed;
        TimeSpan completedElapsed = opportunisticVerification
            ? TimeSpan.FromSeconds(Math.Max(
                copyWatch.Elapsed.TotalSeconds,
                verificationDuration.TotalSeconds))
            : copyWatch.Elapsed + verifyWatch.Elapsed;
        progress.Report(new CopyProgressInfo(
            CopyPhase.Completed,
            totalBytes,
            totalBytes,
            files.Count,
            files.Count,
            string.Empty,
            0,
            completedElapsed)
        {
            SuccessfulBytes = options.VerifyFiles ? verifiedBytes : copiedBytes,
            SuccessfulFiles = options.VerifyFiles ? verifiedFiles : copiedFiles
        });

        return new CopyResult(
            errors.Count == 0,
            files.Count,
            totalBytes,
            copyWatch.Elapsed,
            verificationDuration,
            options.VerifyFiles, detectedConflicts, verifications, failedFiles, errors)
        {
            CopiedBytes = copiedBytes,
            CopiedFiles = copiedFiles,
            VerifiedBytes = verifiedBytes,
            VerifiedFileCount = verifiedFiles,
            DestinationPaths = destinationPaths,
            Warnings = warnings,
            VerificationAlgorithm = options.VerificationAlgorithm
        };
    }

    public Task<FileRetryResult> RetryFailedFilesAsync(
        IReadOnlyList<FileOperationFailure> failures,
        CopyOptions options,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<IDisposable>>? executionLeaseFactory = null) =>
        Task.Run(
            () => RetryFailedFilesCoreAsync(
                failures,
                options,
                overwriteVerificationMismatches: false,
                progress,
                waitWhilePaused,
                executionLeaseFactory,
                cancellationToken),
            cancellationToken);

    public Task<FileRetryResult> OverwriteVerificationMismatchesAsync(
        IReadOnlyList<FileOperationFailure> failures,
        CopyOptions options,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<IDisposable>>? executionLeaseFactory = null)
    {
        if (failures.Count == 0 || failures.Any(failure => !failure.IsVerificationMismatch))
        {
            throw new ArgumentException(
                ResourceService.GetString("Error.OnlyVerificationMismatchCanOverwrite"),
                nameof(failures));
        }

        return Task.Run(
            () => RetryFailedFilesCoreAsync(
                failures,
                options with { SkipCopy = false, VerifyFiles = true },
                overwriteVerificationMismatches: true,
                progress,
                waitWhilePaused,
                executionLeaseFactory,
                cancellationToken),
            cancellationToken);
    }

    private static async Task<FileRetryResult> RetryFailedFilesCoreAsync(
        IReadOnlyList<FileOperationFailure> failures,
        CopyOptions options,
        bool overwriteVerificationMismatches,
        IProgress<CopyProgressInfo> progress,
        Func<CancellationToken, Task> waitWhilePaused,
        Func<CancellationToken, ValueTask<IDisposable>>? executionLeaseFactory,
        CancellationToken cancellationToken)
    {
        options = options with
        {
            VerificationAlgorithm = VerificationAlgorithms.Normalize(options.VerificationAlgorithm)
        };

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

        using (await AcquireExecutionLeaseAsync(
                   executionLeaseFactory,
                   cancellationToken).ConfigureAwait(false))
        {
            foreach (IGrouping<string, FileOperationFailure> destinationVolume in failures
                         .Where(failure => failure.Stage == FileOperationStage.Copying ||
                                           overwriteVerificationMismatches)
                         .GroupBy(
                             failure => Path.GetPathRoot(Path.GetFullPath(failure.DestinationPath)) ??
                                        failure.DestinationPath,
                             StringComparer.OrdinalIgnoreCase))
            {
                // Retrying several files on one volume must not re-query free space for each file.
                EnsureDestinationCapacity(
                    destinationVolume.Key,
                    destinationVolume.Sum(failure => failure.Length));
            }
        }

        foreach (FileOperationFailure failure in failures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await waitWhilePaused(cancellationToken);
            using IDisposable executionLease = await AcquireExecutionLeaseAsync(
                executionLeaseFactory,
                cancellationToken).ConfigureAwait(false);

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
                byte[] sourceHash = await ComputeHashAsync(failure.SourcePath, options.VerificationAlgorithm, waitWhilePaused, cancellationToken);
                byte[] destinationHash = await ComputeHashAsync(failure.DestinationPath, options.VerificationAlgorithm, waitWhilePaused, cancellationToken);
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
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
        VerificationAlgorithmKind algorithm,
        Func<CancellationToken, Task> waitWhilePaused,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        VerificationAlgorithmKind normalizedAlgorithm = VerificationAlgorithms.Normalize(algorithm);
        XxHash64? xxHash = normalizedAlgorithm == VerificationAlgorithmKind.XxHash64
            ? new XxHash64()
            : null;
        using IncrementalHash? cryptographicHash = xxHash is null
            ? IncrementalHash.CreateHash(GetCryptographicHashName(normalizedAlgorithm))
            : null;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
            {
                await waitWhilePaused(cancellationToken);
                if (xxHash is not null)
                {
                    xxHash.Append(buffer.AsSpan(0, read));
                }
                else
                {
                    cryptographicHash!.AppendData(buffer, 0, read);
                }
            }
            return xxHash?.GetHashAndReset() ?? cryptographicHash!.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static HashAlgorithmName GetCryptographicHashName(
        VerificationAlgorithmKind algorithm) =>
        algorithm switch
        {
            VerificationAlgorithmKind.Sha256 => HashAlgorithmName.SHA256,
            VerificationAlgorithmKind.Sha512 => HashAlgorithmName.SHA512,
            VerificationAlgorithmKind.Sha1 => HashAlgorithmName.SHA1,
            VerificationAlgorithmKind.Md5 => HashAlgorithmName.MD5,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null)
        };

    private static ValueTask<IDisposable> AcquireExecutionLeaseAsync(
        Func<CancellationToken, ValueTask<IDisposable>>? executionLeaseFactory,
        CancellationToken cancellationToken) =>
        executionLeaseFactory?.Invoke(cancellationToken) ??
        ValueTask.FromResult(NoopExecutionLease);

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
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
        $"{destinationPath}.{Guid.NewGuid():N}.clipport-partial";

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
}
