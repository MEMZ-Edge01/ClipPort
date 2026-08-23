using System.Collections.ObjectModel;
using System.Globalization;
using ClipPort.Models;
using ClipPort.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ClipPort;

public sealed partial class MainWindow
{
    private readonly CopyJobScheduler _jobScheduler = new();
    private readonly Dictionary<string, CopyJobRuntime> _jobRuntimes = new(StringComparer.Ordinal);
    private bool _shutdownInProgress;
    private bool _shutdownCompleted;

    private async void ConcurrentStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfigureNewTaskAsync())
        {
            await EnqueueConfiguredJobAsync();
        }
    }

    private async void ConcurrentNewJobButton_Click(object sender, RoutedEventArgs e)
    {
        PrepareConcurrentNewJobView();
        if (await ConfigureNewTaskAsync())
        {
            await EnqueueConfiguredJobAsync();
        }
    }

    private async Task EnqueueConfiguredJobAsync()
    {
        if (_sourcePath is null || _destinationPath is null)
        {
            return;
        }

        if (!EnqueueJob(
                _sourcePath,
                _destinationPath,
                _copyOptions,
                PriorityExecutionToggle.IsOn,
                PreventSleepToggle.IsOn,
                displayName: null,
                out string? enqueueError))
        {
            if (_shutdownInProgress || _shutdownCompleted)
            {
                return;
            }
            await ShowMessageAsync(
                "Error.CannotStartOverlappingTask",
                enqueueError!);
        }
    }

    private bool EnqueueJob(
        string sourcePath,
        string destinationPath,
        CopyOptions options,
        bool isPriority,
        bool preventSleep,
        string? displayName,
        out string? error)
    {
        // Enqueue and closing callbacks run on the UI thread. Closing this gate
        // before taking the runtime snapshot prevents late tasks from escaping it.
        if (_shutdownInProgress || _shutdownCompleted)
        {
            error = null;
            return false;
        }

        if (TryFindPathConflict(
                sourcePath,
                destinationPath,
                options.SkipCopy,
                out JobHistoryItem? conflictingJob))
        {
            error = ResourceService.Format(
                "Format.TaskPathConflict",
                conflictingJob?.DisplayName ?? string.Empty);
            return false;
        }

        var job = new JobHistoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? GetDisplayName(sourcePath)
                : displayName,
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            StartedAt = DateTimeOffset.Now,
            Status = JobStatus.Queued,
            CopyEnabled = !options.SkipCopy,
            VerificationEnabled = options.VerifyFiles,
            VerificationAlgorithm = options.VerificationAlgorithm,
            VerificationExecutionMode = options.VerificationExecutionMode,
            UseFastCopyAlgorithm = options.UseFastCopyAlgorithm,
            IsPriority = isPriority,
            PreventSleep = preventSleep,
            IsAcknowledged = false,
            DestinationFiles = options.DestinationPaths is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(
                    options.DestinationPaths,
                    StringComparer.OrdinalIgnoreCase),
        };
        var runtime = new CopyJobRuntime(job, options);
        CopyJobScheduler.CopyJobScheduleRegistration? registration = null;
        try
        {
            registration = _jobScheduler.Register(isPriority);
            _jobRuntimes.Add(job.Id, runtime);
            runtime.ScheduleRegistration = registration;
            UpdateSleepPreventionState();
            _history.Insert(0, job);
            TrimHistory();
            UpdateHistoryEmptyState();
            _selectedJob = job;
            RefreshHistoryItem(job);
            NewJobsList.SelectedItem = job;
            ShowRuntimeJob(runtime);
            _ = SaveHistorySafeAsync();
            runtime.ExecutionTask = RunCopyJobAsync(runtime);
            RefreshSelectedRuntime();
            error = null;
            return true;
        }
        catch
        {
            // If anything after Register fails, the priority gate must be
            // released so ordinary jobs are not permanently blocked.
            registration?.Dispose();
            _jobRuntimes.Remove(job.Id);
            throw;
        }
    }

    private bool TryFindPathConflict(
        string sourcePath,
        string destinationPath,
        bool skipCopy,
        out JobHistoryItem? conflictingJob)
    {
        foreach (CopyJobRuntime existing in _jobRuntimes.Values)
        {
            bool newWrites = !skipCopy;
            bool existingWrites = !existing.Options.SkipCopy;
            bool conflicts =
                newWrites &&
                (PathSafety.PathsOverlap(destinationPath, existing.Job.SourcePath) ||
                 PathSafety.PathsOverlap(destinationPath, existing.Job.DestinationPath)) ||
                existingWrites &&
                (PathSafety.PathsOverlap(existing.Job.DestinationPath, sourcePath) ||
                 PathSafety.PathsOverlap(existing.Job.DestinationPath, destinationPath));
            if (conflicts)
            {
                conflictingJob = existing.Job;
                return true;
            }
        }

        conflictingJob = null;
        return false;
    }

    private async Task RunCopyJobAsync(CopyJobRuntime runtime)
    {
        try
        {
            await WaitForQueueTurnAsync(runtime, runtime.Cancellation.Token);
            RefreshSchedulingPresentation();
            runtime.Job.Status = JobStatus.Running;
            RefreshHistoryItem(runtime.Job);
            await SaveHistorySafeAsync();
            RefreshSelectedRuntime();

            var progress = new Progress<CopyProgressInfo>(info => UpdateJobProgress(runtime, info));
            var duplicateProgress = new Progress<DuplicateFileConflict>(conflict =>
                RecordJobDuplicateConflict(runtime, conflict));
            CopyResult result = await _copyService.CopyAndVerifyAsync(
                runtime.Job.SourcePath,
                runtime.Job.DestinationPath,
                runtime.Options,
                progress,
                duplicateProgress,
                (conflicts, token) => InvokeOnUiThreadAsync(
                    () => WaitForJobDuplicateChoicesAsync(runtime, conflicts, token)),
                token => WaitWhilePausedAsync(runtime, token),
                runtime.Cancellation.Token,
                token => _jobScheduler.AcquireExecutionLeaseAsync(
                    runtime.ScheduleRegistration!,
                    token));

            runtime.Result = result;
            await ResolveFailedFilesAsync(runtime, result.FailedFiles, runtime.Cancellation.Token);
            CopyResult finalResult = runtime.Result ?? result;
            bool completedSuccessfully = finalResult.Success;
            await FinalizeJobAsync(
                runtime,
                completedSuccessfully ? JobStatus.Completed : JobStatus.CompletedWithErrors,
                finalResult,
                completedSuccessfully ? null : finalResult.Errors.FirstOrDefault());
        }
        catch (OperationCanceledException)
        {
            await FinalizeJobAsync(
                runtime,
                JobStatus.Cancelled,
                null,
                ResourceService.GetString("Error.TaskCancelledKept"));
        }
        catch (Exception ex)
        {
            await FinalizeJobAsync(runtime, JobStatus.Failed, null, ex.Message);
            if (ReferenceEquals(_selectedJob, runtime.Job))
            {
                await ShowMessageAsync("Error.TaskExecutionFailed", ex.Message);
            }
        }
        finally
        {
            runtime.ScheduleRegistration?.Dispose();
            runtime.ScheduleRegistration = null;
            runtime.Cancellation.Dispose();
            _jobRuntimes.Remove(runtime.Job.Id);
            RefreshSchedulingPresentation();
            bool historyTrimmed = TrimHistory();
            UpdateSleepPreventionState();
            RefreshSelectedRuntime();
            if (historyTrimmed)
            {
                await SaveHistorySafeAsync();
            }
        }
    }

    private async Task ResolveFailedFilesAsync(
        CopyJobRuntime runtime,
        IReadOnlyList<FileOperationFailure> failures,
        CancellationToken cancellationToken)
    {
        runtime.FailedFileChoices.Clear();
        foreach (FileOperationFailure failure in failures)
        {
            runtime.FailedFileChoices.Add(new FailedFileChoice(failure));
        }

        TimeSpan retryCopyDuration = TimeSpan.Zero;
        TimeSpan retryVerifyDuration = TimeSpan.Zero;
        long retryCopiedBytes = 0;
        int retryCopiedFiles = 0;
        var retryVerifications = new Dictionary<string, FileVerificationResult>(
            StringComparer.OrdinalIgnoreCase);
        var retryDestinationPaths = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var retryWarnings = new List<string>();
        while (runtime.FailedFileChoices.Count > 0)
        {
            runtime.IsAwaitingFailureDecision = true;
            var actionSource = new TaskCompletionSource<FailureResolutionAction>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.FailureActionSource = actionSource;
            RefreshSelectedRuntime();

            FailureResolutionAction action;
            using (cancellationToken.Register(() =>
                actionSource.TrySetCanceled(cancellationToken)))
            {
                action = await actionSource.Task;
            }
            if (ReferenceEquals(runtime.FailureActionSource, actionSource))
            {
                runtime.FailureActionSource = null;
            }
            runtime.IsAwaitingFailureDecision = false;

            List<FailedFileChoice> actedChoices = runtime.FailedFileChoices
                .Where(choice => action.Failures.Contains(choice.Failure))
                .ToList();

            if (action.Mode is FailureResolutionMode.Retry or FailureResolutionMode.Overwrite)
            {
                runtime.IsRetryingFailures = true;
                runtime.ActiveFailureAction = action.Mode;
                var retryProgress = new Progress<CopyProgressInfo>(info =>
                {
                    CopyPhase? previousRetryPhase = runtime.RetryProgress?.Phase;
                    runtime.RetryProgress = info;
                    EndThroughputInterval(runtime, previousRetryPhase, info.Phase);
                    SampleThroughput(runtime, info);
                    RefreshSelectedRuntime();
                });
                FileRetryResult retryResult = action.Mode == FailureResolutionMode.Overwrite
                    ? await _copyService.OverwriteVerificationMismatchesAsync(
                        action.Failures,
                        runtime.Options,
                        retryProgress,
                        token => WaitWhilePausedAsync(runtime, token),
                        cancellationToken,
                        token => _jobScheduler.AcquireExecutionLeaseAsync(
                            runtime.ScheduleRegistration!,
                            token))
                    : await _copyService.RetryFailedFilesAsync(
                        action.Failures,
                        runtime.Options,
                        retryProgress,
                        token => WaitWhilePausedAsync(runtime, token),
                        cancellationToken,
                        token => _jobScheduler.AcquireExecutionLeaseAsync(
                            runtime.ScheduleRegistration!,
                            token));
                EndThroughputInterval(runtime, runtime.RetryProgress?.Phase, null);
                retryCopyDuration += retryResult.CopyDuration;
                retryVerifyDuration += retryResult.VerifyDuration;
                retryCopiedBytes += retryResult.CopiedBytes;
                retryCopiedFiles += retryResult.CopiedFiles;
                foreach (FileVerificationResult verification in retryResult.VerificationResults)
                {
                    retryVerifications[verification.RelativePath] = verification;
                }
                foreach (var (relativePath, destinationPath) in retryResult.DestinationPaths)
                {
                    retryDestinationPaths[relativePath] = destinationPath;
                }
                retryWarnings.AddRange(retryResult.Warnings);
                foreach (FailedFileChoice choice in actedChoices)
                {
                    runtime.FailedFileChoices.Remove(choice);
                }
                foreach (FileOperationFailure remaining in retryResult.FailedFiles)
                {
                    runtime.FailedFileChoices.Add(new FailedFileChoice(remaining));
                }
                runtime.IsRetryingFailures = false;
                runtime.ActiveFailureAction = null;
                runtime.RetryProgress = null;
            }
            else
            {
                runtime.SkippedFailures.AddRange(action.Failures);
                foreach (FailedFileChoice choice in actedChoices)
                {
                    runtime.FailedFileChoices.Remove(choice);
                }
            }
        }

        runtime.IsAwaitingFailureDecision = false;
        if (runtime.Result is CopyResult original)
        {
            FileOperationFailure[] skipped = runtime.SkippedFailures.ToArray();
            HashSet<string> fileFailureErrors = original.FailedFiles
                .Select(item => item.Error)
                .ToHashSet(StringComparer.Ordinal);
            string[] unresolvedErrors = original.Errors
                .Where(error => !fileFailureErrors.Contains(error))
                .Concat(skipped.Select(item => item.Error))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var verifications = original.VerifiedFiles
                .ToDictionary(
                    item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase);
            foreach (var (relativePath, verification) in retryVerifications)
            {
                verifications[relativePath] = verification;
            }
            var destinationPaths = new Dictionary<string, string>(
                original.DestinationPaths,
                StringComparer.OrdinalIgnoreCase);
            foreach (var (relativePath, destinationPath) in retryDestinationPaths)
            {
                destinationPaths[relativePath] = destinationPath;
            }
            FileVerificationResult[] finalVerifications = verifications.Values.ToArray();
            runtime.Result = original with
            {
                Success = unresolvedErrors.Length == 0,
                CopyDuration = original.CopyDuration + retryCopyDuration,
                VerifyDuration = original.VerifyDuration + retryVerifyDuration,
                FailedFiles = skipped,
                Errors = unresolvedErrors,
                CopiedBytes = Math.Min(
                    original.TotalBytes,
                    original.CopiedBytes + retryCopiedBytes),
                CopiedFiles = Math.Min(
                    original.FileCount,
                    original.CopiedFiles + retryCopiedFiles),
                VerifiedBytes = finalVerifications
                    .Where(item => item.IsMatch)
                    .Sum(item => item.Length),
                VerifiedFileCount = finalVerifications.Count(item => item.IsMatch),
                VerifiedFiles = finalVerifications,
                DestinationPaths = destinationPaths,
                Warnings = original.Warnings
                    .Concat(retryWarnings)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }
    }

    private async Task WaitForQueueTurnAsync(
        CopyJobRuntime runtime,
        CancellationToken cancellationToken)
    {
        // 每个任务只在开始时申请一次队列执行权；后续检查点负责确认安全交接。
        await _jobScheduler.WaitForTurnAsync(
            runtime.ScheduleRegistration!,
            cancellationToken);
    }

    private async Task WaitWhilePausedAsync(
        CopyJobRuntime runtime,
        CancellationToken cancellationToken)
    {
        await _jobScheduler.WaitForExecutionAsync(
            runtime.ScheduleRegistration!,
            cancellationToken);
    }

    private bool IsRuntimePaused(CopyJobRuntime runtime) =>
        runtime.IsPaused ||
        runtime.Job.Status == JobStatus.Running &&
        runtime.ScheduleRegistration is not null &&
        !_jobScheduler.CanExecute(runtime.ScheduleRegistration);

    private bool IsRuntimeAutomaticallyPaused(CopyJobRuntime runtime) =>
        runtime.ScheduleRegistration is not null &&
        _jobScheduler.IsPreempted(runtime.ScheduleRegistration);

    private void UpdateJobProgress(CopyJobRuntime runtime, CopyProgressInfo info)
    {
        CopyPhase? previousPhase = runtime.LastProgress?.Phase;
        runtime.LastProgress = info;
        if (runtime.Options.VerificationExecutionMode == VerificationExecutionMode.AfterCopy)
        {
            EndThroughputInterval(runtime, previousPhase, info.Phase);
        }
        else if (info.Phase == CopyPhase.Completed)
        {
            EndThroughputInterval(runtime, CopyPhase.Copying, null);
            EndThroughputInterval(runtime, CopyPhase.Verifying, null);
        }
        SampleThroughput(runtime, info);
        runtime.Job.TotalBytes = info.TotalBytes;
        runtime.Job.FileCount = info.TotalFiles;
        switch (info.Phase)
        {
            case CopyPhase.Scanning:
                runtime.LastScanProgress = info;
                break;
            case CopyPhase.Copying:
                runtime.LastCopyProgress = info;
                runtime.ProcessedCopyBytes = info.ProcessedBytes;
                runtime.ProcessedCopyFiles = info.ProcessedFiles;
                runtime.CopiedBytes = info.SuccessfulBytes;
                runtime.CopiedFiles = info.SuccessfulFiles;
                runtime.CopyElapsed = info.Elapsed;
                runtime.Job.CopiedBytes = info.SuccessfulBytes;
                runtime.Job.CopiedFiles = info.SuccessfulFiles;
                runtime.Job.CopySeconds = info.Elapsed.TotalSeconds;
                break;
            case CopyPhase.Verifying:
                runtime.LastVerifyProgress = info;
                runtime.ProcessedVerifyBytes = info.ProcessedBytes;
                runtime.ProcessedVerifyFiles = info.ProcessedFiles;
                runtime.VerifiedBytes = info.SuccessfulBytes;
                runtime.VerifiedFiles = info.SuccessfulFiles;
                runtime.VerifyElapsed = info.Elapsed;
                runtime.Job.VerifiedFiles = info.SuccessfulFiles;
                runtime.Job.VerifySeconds = info.Elapsed.TotalSeconds;
                break;
            case CopyPhase.Completed:
                runtime.ProcessedCopyBytes = runtime.Options.SkipCopy
                    ? runtime.ProcessedCopyBytes
                    : info.TotalBytes;
                runtime.ProcessedCopyFiles = runtime.Options.SkipCopy
                    ? runtime.ProcessedCopyFiles
                    : info.TotalFiles;
                runtime.ProcessedVerifyBytes = runtime.Options.VerifyFiles
                    ? info.TotalBytes
                    : runtime.ProcessedVerifyBytes;
                runtime.ProcessedVerifyFiles = runtime.Options.VerifyFiles
                    ? info.TotalFiles
                    : runtime.ProcessedVerifyFiles;
                break;
        }

        RefreshHistoryItem(runtime.Job);
        if (ReferenceEquals(_selectedJob, runtime.Job))
        {
            ShowRuntimeJob(runtime);
        }
    }

    private static void SampleThroughput(CopyJobRuntime runtime, CopyProgressInfo info)
    {
        if (info.Phase == CopyPhase.Verifying && !info.IsPhaseActive)
        {
            runtime.VerifyThroughputSampler.TryAppendIdleSample(
                runtime.Job.VerifyByteSpeedSamples,
                runtime.Job.VerifyItemSpeedSamples,
                runtime.Job.VerifyThroughputProgressSamples);
            return;
        }

        runtime.CopyThroughputSampler.TrySample(
            info,
            runtime.Job.CopyByteSpeedSamples,
            runtime.Job.CopyItemSpeedSamples,
            runtime.Job.CopyThroughputProgressSamples);
        runtime.VerifyThroughputSampler.TrySample(
            info,
            runtime.Job.VerifyByteSpeedSamples,
            runtime.Job.VerifyItemSpeedSamples,
            runtime.Job.VerifyThroughputProgressSamples);
    }

    private static void EndThroughputInterval(
        CopyJobRuntime runtime,
        CopyPhase? previousPhase,
        CopyPhase? nextPhase)
    {
        if (previousPhase == nextPhase)
        {
            return;
        }

        if (previousPhase == CopyPhase.Copying)
        {
            runtime.CopyThroughputSampler.TryAppendIdleSample(
                runtime.Job.CopyByteSpeedSamples,
                runtime.Job.CopyItemSpeedSamples,
                runtime.Job.CopyThroughputProgressSamples);
        }
        else if (previousPhase == CopyPhase.Verifying)
        {
            runtime.VerifyThroughputSampler.TryAppendIdleSample(
                runtime.Job.VerifyByteSpeedSamples,
                runtime.Job.VerifyItemSpeedSamples,
                runtime.Job.VerifyThroughputProgressSamples);
        }
    }

    private void RecordJobDuplicateConflict(CopyJobRuntime runtime, DuplicateFileConflict conflict)
    {
        if (runtime.DuplicateChoices.Any(item =>
            string.Equals(item.RelativePath, conflict.RelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ExistingFilePolicy? initialDecision = runtime.Options.ExistingFilePolicy == ExistingFilePolicy.Ask
            ? null
            : runtime.Options.ExistingFilePolicy;
        runtime.DuplicateChoices.Add(new DuplicateConflictChoice(conflict, initialDecision, initialDecision is null));
        runtime.Job.DuplicateFiles.Add(conflict);
        if (initialDecision is ExistingFilePolicy decision)
        {
            runtime.Job.DuplicateDecisions[conflict.RelativePath] = decision;
        }
        if (ReferenceEquals(_selectedJob, runtime.Job))
        {
            ShowRuntimeDuplicateChoices(runtime);
        }
    }

    private async Task<IReadOnlyDictionary<string, ExistingFilePolicy>> WaitForJobDuplicateChoicesAsync(
        CopyJobRuntime runtime,
        IReadOnlyList<DuplicateFileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        foreach (DuplicateFileConflict conflict in conflicts)
        {
            RecordJobDuplicateConflict(runtime, conflict);
        }
        runtime.DuplicateDecisionSource = new TaskCompletionSource<IReadOnlyDictionary<string, ExistingFilePolicy>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (DuplicateConflictChoice choice in runtime.DuplicateChoices)
        {
            choice.SetCanChoose(true);
        }
        RefreshSelectedRuntime();

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            runtime.DuplicateDecisionSource?.TrySetCanceled(cancellationToken));
        try
        {
            return await runtime.DuplicateDecisionSource.Task;
        }
        finally
        {
            runtime.DuplicateDecisionSource = null;
        }
    }

    private async Task FinalizeJobAsync(
        CopyJobRuntime runtime,
        JobStatus status,
        CopyResult? result,
        string? error)
    {
        JobHistoryItem job = runtime.Job;
        job.Status = status;
        job.FinishedAt = DateTimeOffset.Now;
        job.TotalBytes = result?.TotalBytes ?? runtime.LastProgress?.TotalBytes ?? job.TotalBytes;
        job.FileCount = result?.FileCount ?? runtime.LastProgress?.TotalFiles ?? job.FileCount;
        job.CopiedBytes = result is not null && !runtime.Options.SkipCopy
            ? result.CopiedBytes
            : runtime.CopiedBytes;
        job.CopiedFiles = result is not null && !runtime.Options.SkipCopy
            ? result.CopiedFiles
            : runtime.CopiedFiles;
        job.VerifiedFiles = result?.VerifiedFileCount ?? runtime.VerifiedFiles;
        job.CopySeconds = result?.CopyDuration.TotalSeconds ?? runtime.CopyElapsed.TotalSeconds;
        job.VerifySeconds = result?.VerifyDuration.TotalSeconds ?? runtime.VerifyElapsed.TotalSeconds;
        job.VerificationEnabled = result?.VerificationPerformed ?? runtime.Options.VerifyFiles;
        job.VerificationAlgorithm = result?.VerificationAlgorithm ?? runtime.Options.VerificationAlgorithm;
        job.ErrorMessage = error;

        if (result is not null)
        {
            job.DuplicateFiles = result.DuplicateFiles.ToList();
            job.FailedFiles = result.FailedFiles.ToList();
            job.DestinationFiles = new Dictionary<string, string>(
                result.DestinationPaths,
                StringComparer.OrdinalIgnoreCase);
            foreach (DuplicateFileConflict conflict in result.DuplicateFiles)
            {
                if (!job.DuplicateDecisions.ContainsKey(conflict.RelativePath))
                {
                    job.DuplicateDecisions[conflict.RelativePath] =
                        runtime.Options.ExistingFilePolicy == ExistingFilePolicy.Ask
                            ? ExistingFilePolicy.Skip
                            : runtime.Options.ExistingFilePolicy;
                }
            }
        }

        runtime.Report = result is not null ? BuildReport(result, job) : BuildIncompleteReport(job);
        try
        {
            job.ReportPath = await _historyService.SaveReportAsync(job.Id, runtime.Report);
            job.ReportFileName = Path.GetFileName(job.ReportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(_selectedJob, job))
            {
                LogText.Text = ResourceService.Format("Format.TaskReportSaveFailed", ex.Message);
            }
        }

        RefreshHistoryItem(job);
        await SaveHistorySafeAsync();
    }

    private void ConcurrentHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isMultiSelectMode)
        {
            if (!_isChangingMultiSelectMode)
            {
                UpdateBatchSelectionUi();
            }
            return;
        }
        if (HistoryList.SelectedItem is not JobHistoryItem item)
        {
            return;
        }
        NewJobsList.SelectedItem = null;
        _selectedJob = item;
        if (_jobRuntimes.TryGetValue(item.Id, out CopyJobRuntime? runtime) && runtime is not null)
        {
            ShowRuntimeJob(runtime);
        }
        else
        {
            ShowHistoryJob(item);
        }
    }

    private void ShowRuntimeJob(CopyJobRuntime runtime)
    {
        JobHistoryItem job = runtime.Job;
        CopyProgressInfo? info = runtime.LastProgress;
        CopyProgressInfo? scanInfo = runtime.LastScanProgress;
        CopyProgressInfo? copyInfo = runtime.LastCopyProgress;
        CopyProgressInfo? verifyInfo = runtime.LastVerifyProgress;
        HeroNameText.Text = job.DisplayName;
        HeroPriorityBadge.Visibility = job.IsPriority ? Visibility.Visible : Visibility.Collapsed;
        SourcePathText.Text = job.SourcePath;
        DestinationPathText.Text = job.DestinationPath;
        TotalSizeText.Text = info is null ? "--" : FormatBytes(info.TotalBytes);
        TotalCountText.Text = info?.TotalFiles.ToString("N0", CultureInfo.InvariantCulture) ?? "--";
        StartTimeText.Text = job.StartedAt.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        EndTimeText.Text = job.FinishedAt?.ToString(
            "MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture) ?? "--";
        TimeSpan activeElapsed = runtime.Options.VerificationExecutionMode ==
                                 VerificationExecutionMode.OpportunisticDuringCopy
            ? TimeSpan.FromSeconds(Math.Max(
                runtime.CopyElapsed.TotalSeconds,
                runtime.VerifyElapsed.TotalSeconds))
            : runtime.CopyElapsed + runtime.VerifyElapsed;
        if (info?.Phase == CopyPhase.Scanning)
        {
            activeElapsed = scanInfo?.Elapsed ?? TimeSpan.Zero;
        }
        DurationText.Text = FormatDuration(activeElapsed);
        bool copyStillActive = copyInfo is not null &&
            (copyInfo.ProcessedBytes < copyInfo.TotalBytes ||
             copyInfo.ProcessedFiles < copyInfo.TotalFiles);
        string? currentFile = copyStillActive
            ? copyInfo?.CurrentFile
            : verifyInfo?.CurrentFile ?? scanInfo?.CurrentFile ?? info?.CurrentFile;
        CurrentFileText.Text = string.IsNullOrWhiteSpace(currentFile)
            ? $"{job.SourcePath} → {job.DestinationPath}"
            : currentFile;

        int totalFiles = info?.TotalFiles ?? job.FileCount;
        long totalBytes = info?.TotalBytes ?? job.TotalBytes;
        double copyPercent = GetJobPercent(
            totalBytes,
            totalFiles,
            runtime.ProcessedCopyBytes,
            runtime.ProcessedCopyFiles);
        double verifyPercent = runtime.Options.VerifyFiles
            ? GetJobPercent(
                totalBytes,
                totalFiles,
                runtime.ProcessedVerifyBytes,
                runtime.ProcessedVerifyFiles)
            : 100;
        double overall = runtime.Options.SkipCopy
            ? verifyPercent
            : runtime.Options.VerifyFiles
            ? copyPercent * 0.8 + verifyPercent * 0.2
            : copyPercent;

        CopyProgress.Value = copyPercent;
        VerifyProgress.Value = verifyPercent;
        OverallProgress.Value = overall;
        bool scanning = info?.Phase == CopyPhase.Scanning && !info.IsTotalKnown;
        OverallProgress.IsIndeterminate = scanning;
        CopyProgressRow.Visibility = runtime.Options.SkipCopy
            ? Visibility.Collapsed
            : Visibility.Visible;
        VerifyProgressRow.Visibility = runtime.Options.VerifyFiles
            ? Visibility.Visible
            : Visibility.Collapsed;
        PercentText.Text = scanning
            ? ResourceService.GetString("Status.Scanning")
            : $"{overall:F2}%";
        bool automaticallyPaused = IsRuntimeAutomaticallyPaused(runtime);
        bool runtimePaused = runtime.IsPaused || automaticallyPaused;
        CopySpeedText.Text = !runtimePaused && copyStillActive && copyInfo is not null
            ? $"{FormatBytes(copyInfo.BytesPerSecond)}/s"
            : "--";
        bool verificationActive = !runtimePaused &&
            verifyInfo is { IsPhaseActive: true } &&
            runtime.ProcessedVerifyFiles < totalFiles;
        VerifySpeedText.Text = runtime.Options.VerifyFiles
            ? $"{FormatBytes(verificationActive ? verifyInfo!.BytesPerSecond : 0)}/s"
            : "--";
        UpdateThroughputCharts(
            job.CopyByteSpeedSamples,
            job.CopyItemSpeedSamples,
            job.CopyThroughputProgressSamples,
            job.VerifyByteSpeedSamples,
            job.VerifyItemSpeedSamples,
            job.VerifyThroughputProgressSamples);
        CopyTimeText.Text = FormatDuration(runtime.CopyElapsed);
        VerifyTimeText.Text = FormatDuration(runtime.VerifyElapsed);
        CopyCountText.Text = runtime.Options.SkipCopy ? "--" : $"{runtime.CopiedFiles}/{totalFiles}";
        VerifyCountText.Text = $"{runtime.VerifiedFiles}/{totalFiles}";
        bool copyDone = runtime.Options.SkipCopy ||
            totalFiles > 0 && runtime.ProcessedCopyFiles >= totalFiles ||
            totalFiles == 0 && info?.Phase == CopyPhase.Completed;
        bool verifyDone = runtime.Options.VerifyFiles &&
            (totalFiles > 0 && runtime.ProcessedVerifyFiles >= totalFiles ||
             totalFiles == 0 && info?.Phase == CopyPhase.Completed);
        CopyProgress.Visibility = copyDone ? Visibility.Collapsed : Visibility.Visible;
        CopyCompletedBadge.Visibility = copyDone ? Visibility.Visible : Visibility.Collapsed;
        CopyCompletedText.Text = runtime.Options.SkipCopy
            ? ResourceService.GetString("Common.Disabled")
            : runtime.FailedFileChoices.Count > 0
                ? ResourceService.GetString("Common.Processed")
                : ResourceService.GetString("Common.Completed");
        VerifyProgress.Visibility = verifyDone || !runtime.Options.VerifyFiles ? Visibility.Collapsed : Visibility.Visible;
        VerifyCompletedBadge.Visibility = verifyDone || !runtime.Options.VerifyFiles ? Visibility.Visible : Visibility.Collapsed;
        VerifyCompletedText.Text = !runtime.Options.VerifyFiles
            ? ResourceService.GetString("Common.Disabled")
            : runtime.FailedFileChoices.Count > 0
                ? ResourceService.GetString("Common.Processed")
                : ResourceService.GetString("Common.Completed");

        CompletionIcon.Visibility = Visibility.Collapsed;
        PercentText.Visibility = Visibility.Visible;
        StatusText.FontSize = 15;
        StatusText.Foreground = (SolidColorBrush)Application.Current.Resources["MutedTextBrush"];
        StartButton.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = Visibility.Collapsed;
        StartVerificationButton.Visibility = Visibility.Collapsed;
        ExportReportButton.Visibility = Visibility.Collapsed;
        RestartJobButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        PauseButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        PauseText.Text = runtimePaused ? ResourceService.GetString("Button.Resume") : ResourceService.GetString("Button.Pause");
        PauseIcon.Glyph = runtimePaused ? "\uE768" : "\uE769";
        NewJobButton.IsEnabled = !_isMultiSelectMode;
        SourcePickerButton.IsEnabled = false;
        DestinationPickerButton.IsEnabled = false;
        HistoryList.IsEnabled = true;
        NewJobsList.IsEnabled = true;

        if (runtime.IsRetryingFailures)
        {
            bool overwriting = runtime.ActiveFailureAction == FailureResolutionMode.Overwrite;
            StatusText.Text = overwriting
                ? ResourceService.GetString("Status.OverwritingMismatched")
                : ResourceService.GetString("Status.RetryingFailedFiles");
            PhaseText.Text = overwriting
                ? ResourceService.GetString("Info.ReverifyAfterOverwrite")
                : ResourceService.GetString("Info.OnlySelectedFailures");
            CurrentFileText.Text = runtime.RetryProgress?.CurrentFile ?? job.SourcePath;
            LogText.Text = overwriting
                ? ResourceService.GetString("Info.OverwriteAndReverifyDesc")
                : ResourceService.GetString("Info.StillFailedRemain");
        }
        else if (runtime.IsAwaitingFailureDecision)
        {
            StatusText.Text = ResourceService.GetString("Status.WaitingFailedFiles");
            PhaseText.Text = ResourceService.GetString("Info.ChooseRetryOrSkip");
            CurrentFileText.Text = ResourceService.Format("Format.NFilesAwaitingAction", runtime.FailedFileChoices.Count.ToString("N0"));
            LogText.Text = ResourceService.GetString("Info.OtherFilesProcessed");
        }
        else if (automaticallyPaused)
        {
            StatusText.Text = ResourceService.GetString("Status.PausedByPriority");
            PhaseText.Text = ResourceService.GetString("Info.WaitingForPriorityTask");
            LogText.Text = ResourceService.GetString("Info.PriorityPauseAutomaticResume");
        }
        else if (runtime.IsPaused)
        {
            StatusText.Text = ResourceService.GetString("Status.Paused");
            PhaseText.Text = ResourceService.GetString("Info.WaitingToResume");
            LogText.Text = ResourceService.GetString("Info.TaskPaused");
        }
        else if (job.Status == JobStatus.Queued)
        {
            StatusText.Text = ResourceService.GetString("Status.Queued");
            PhaseText.Text = ResourceService.GetString("Status.TaskStarting");
            int waitingAhead = _jobScheduler.GetWaitingPosition(
                runtime.ScheduleRegistration!);
            LogText.Text = waitingAhead > 0
                ? ResourceService.Format(
                    job.IsPriority
                        ? "Info.PriorityTaskQueuedAhead"
                        : "Info.TaskQueuedAhead",
                    waitingAhead.ToString(CultureInfo.InvariantCulture))
                : ResourceService.GetString(
                    job.IsPriority
                        ? "Info.PriorityTaskQueued"
                        : "Info.TaskQueued");
        }
        else
        {
            bool copyingAndVerifying = copyStillActive && verifyInfo is { IsPhaseActive: true };
            CopyPhase? displayedPhase = DisplayFormatting.GetDisplayedOperationPhase(
                copyStillActive,
                info,
                verifyInfo);
            StatusText.Text = copyingAndVerifying
                ? ResourceService.GetString("Status.CopyingAndVerifying")
                : displayedPhase switch
            {
                CopyPhase.Scanning => ResourceService.GetString("Status.Scanning"),
                CopyPhase.Copying => ResourceService.GetString("Status.Copying"),
                CopyPhase.Verifying => ResourceService.GetString("Status.Verifying"),
                CopyPhase.WaitingForDuplicateDecision => ResourceService.GetString("Status.WaitingDuplicateChoices"),
                _ => ResourceService.GetString("Status.Preparing")
            };
            PhaseText.Text = copyingAndVerifying
                ? ResourceService.GetString("Status.BackgroundVerification")
                : displayedPhase switch
            {
                CopyPhase.Scanning => ResourceService.GetString("Status.ReadingDirectories"),
                CopyPhase.Copying => ResourceService.GetString("Status.CopyingFiles"),
                CopyPhase.Verifying => ResourceService.GetString("Status.SHA256Verification"),
                CopyPhase.WaitingForDuplicateDecision => ResourceService.GetString("Info.ChooseActionBelow"),
                _ => ResourceService.GetString("Status.TaskStarting")
            };
            LogText.Text = info?.Phase == CopyPhase.Scanning
                ? ResourceService.Format(
                    "Format.ScannedSummary",
                    (scanInfo?.ScannedDirectories ?? 0).ToString("N0", CultureInfo.InvariantCulture),
                    (scanInfo?.TotalFiles ?? 0).ToString("N0", CultureInfo.InvariantCulture),
                    FormatBytes(scanInfo?.TotalBytes ?? 0))
                : job.IsPriority
                    ? ResourceService.GetString("Info.PriorityTaskRunning")
                    : ResourceService.GetString("Info.TaskRunning");
        }
        ShowRuntimeDuplicateChoices(runtime);
        ShowRuntimeFailedFiles(runtime);
    }

    private static double GetJobPercent(long totalBytes, int totalFiles, long bytes, int files)
    {
        if (totalBytes > 0)
        {
            return Math.Clamp(bytes * 100d / totalBytes, 0, 100);
        }
        return totalFiles > 0 ? Math.Clamp(files * 100d / totalFiles, 0, 100) : 0;
    }

    private void ShowRuntimeDuplicateChoices(CopyJobRuntime runtime)
    {
        SyncDisplayedChoices(_duplicateChoices, runtime.DuplicateChoices);
        DuplicatePanel.Visibility = _duplicateChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyDuplicateChoicesButton.Visibility = runtime.Options.ExistingFilePolicy == ExistingFilePolicy.Ask
            ? Visibility.Visible
            : Visibility.Collapsed;
        int decided = _duplicateChoices.Count(item => item.IsDecided);
        int selectable = _duplicateChoices.Count(item => item.CanChoose);
        int selected = _duplicateChoices.Count(item => item.CanChoose && item.IsSelected);
        DuplicateSummaryText.Text = ResourceService.Format("Format.FoundNDuplicates", _duplicateChoices.Count.ToString("N0"));
        DuplicateSelectionHint.Text = ResourceService.Format("Format.ChoicesMadeNOfMSelectedK", decided.ToString(), _duplicateChoices.Count.ToString(), selected.ToString());
        _updatingDuplicateSelection = true;
        DuplicateSelectAllCheckBox.IsEnabled = selectable > 0;
        DuplicateSelectAllCheckBox.IsChecked = selectable == 0 || selected == 0
            ? false
            : selected == selectable ? true : null;
        _updatingDuplicateSelection = false;
        bool canBatch = runtime.DuplicateDecisionSource is not null && selected > 0;
        BatchOverwriteButton.IsEnabled = canBatch;
        BatchSkipButton.IsEnabled = canBatch;
        BatchCreateCopyButton.IsEnabled = canBatch;
        ApplyDuplicateChoicesButton.IsEnabled = runtime.DuplicateDecisionSource is not null &&
            _duplicateChoices.Count > 0 && decided == _duplicateChoices.Count;
    }

    private void ShowRuntimeFailedFiles(CopyJobRuntime runtime)
    {
        SyncDisplayedChoices(_failedFileChoices, runtime.FailedFileChoices);

        FailedFilesPanel.Visibility = _failedFileChoices.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        OverwriteFailedFilesButton.Visibility = _failedFileChoices.Any(item => item.CanOverwrite)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryFailedFilesButton.Visibility = Visibility.Visible;
        SkipFailedFilesButton.Visibility = Visibility.Visible;
        FailedFileChoice[] selectedChoices = _failedFileChoices
            .Where(item => item.IsSelected)
            .ToArray();
        int selected = selectedChoices.Length;
        bool canAct = runtime.FailureActionSource is not null && selected > 0;
        OverwriteFailedFilesButton.IsEnabled =
            canAct && selectedChoices.All(item => item.CanOverwrite);
        RetryFailedFilesButton.IsEnabled = canAct;
        SkipFailedFilesButton.IsEnabled = canAct;
        FailedFilesSummaryText.Text = ResourceService.Format("Format.FailedFilesNSelectedK", _failedFileChoices.Count.ToString("N0"), selected.ToString("N0"));
    }

    private void ShowFailedFileHistory(JobHistoryItem job)
    {
        _failedFileChoices.Clear();
        foreach (FileOperationFailure failure in job.FailedFiles)
        {
            var choice = new FailedFileChoice(failure) { IsSelected = false };
            _failedFileChoices.Add(choice);
        }

        FailedFilesPanel.Visibility = _failedFileChoices.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        OverwriteFailedFilesButton.Visibility = Visibility.Collapsed;
        RetryFailedFilesButton.Visibility = Visibility.Collapsed;
        SkipFailedFilesButton.Visibility = Visibility.Collapsed;
        FailedFilesSummaryText.Text = ResourceService.Format("Format.SkippedNFailedFiles", _failedFileChoices.Count.ToString("N0"));
    }

    private void ConcurrentFailedFileSelection_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            ShowRuntimeFailedFiles(runtime);
        }
    }

    private void ConcurrentOverwriteFailedFiles_Click(object sender, RoutedEventArgs e) =>
        CompleteFailedFileAction(FailureResolutionMode.Overwrite);

    private void ConcurrentRetryFailedFiles_Click(object sender, RoutedEventArgs e) =>
        CompleteFailedFileAction(FailureResolutionMode.Retry);

    private void ConcurrentSkipFailedFiles_Click(object sender, RoutedEventArgs e) =>
        CompleteFailedFileAction(FailureResolutionMode.Skip);

    private void CompleteFailedFileAction(FailureResolutionMode mode)
    {
        if (!TryGetSelectedRuntime(out CopyJobRuntime runtime) ||
            runtime.FailureActionSource is not TaskCompletionSource<FailureResolutionAction> source)
        {
            return;
        }

        FailedFileChoice[] selectedChoices = runtime.FailedFileChoices
            .Where(item => item.IsSelected)
            .ToArray();
        if (selectedChoices.Length == 0 ||
            mode == FailureResolutionMode.Overwrite &&
            selectedChoices.Any(item => !item.CanOverwrite))
        {
            return;
        }
        FileOperationFailure[] selected = selectedChoices
            .Select(item => item.Failure)
            .ToArray();

        if (source.TrySetResult(new FailureResolutionAction(mode, selected)))
        {
            runtime.FailureActionSource = null;
            OverwriteFailedFilesButton.IsEnabled = false;
            RetryFailedFilesButton.IsEnabled = false;
            SkipFailedFilesButton.IsEnabled = false;
        }
    }

    private static void SyncDisplayedChoices<T>(
        ObservableCollection<T> displayed,
        IReadOnlyList<T> runtimeChoices)
        where T : class
    {
        bool alreadySynchronized = displayed.Count == runtimeChoices.Count;
        for (int index = 0; alreadySynchronized && index < displayed.Count; index++)
        {
            alreadySynchronized = ReferenceEquals(displayed[index], runtimeChoices[index]);
        }

        if (alreadySynchronized)
        {
            return;
        }

        displayed.Clear();
        foreach (T choice in runtimeChoices)
        {
            displayed.Add(choice);
        }
    }

    private void ConcurrentDuplicateOverwrite_Click(object sender, RoutedEventArgs e) =>
        SetConcurrentDuplicateDecision(sender, ExistingFilePolicy.Overwrite);

    private void ConcurrentDuplicateSkip_Click(object sender, RoutedEventArgs e) =>
        SetConcurrentDuplicateDecision(sender, ExistingFilePolicy.Skip);

    private void ConcurrentDuplicateCreateCopy_Click(object sender, RoutedEventArgs e) =>
        SetConcurrentDuplicateDecision(sender, ExistingFilePolicy.CreateCopy);

    private void SetConcurrentDuplicateDecision(object sender, ExistingFilePolicy decision)
    {
        if (sender is not Button { Tag: DuplicateConflictChoice choice } ||
            !TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            return;
        }
        choice.SetDecision(decision);
        runtime.Job.DuplicateDecisions[choice.RelativePath] = decision;
        ShowRuntimeDuplicateChoices(runtime);
    }

    private void ConcurrentDuplicateSelectionCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            ShowRuntimeDuplicateChoices(runtime);
        }
    }

    private void ConcurrentDuplicateSelectAllCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingDuplicateSelection || !TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            return;
        }
        bool selected = DuplicateSelectAllCheckBox.IsChecked == true;
        foreach (DuplicateConflictChoice choice in runtime.DuplicateChoices.Where(item => item.CanChoose))
        {
            choice.IsSelected = selected;
        }
        ShowRuntimeDuplicateChoices(runtime);
    }

    private void ConcurrentBatchDuplicateOverwrite_Click(object sender, RoutedEventArgs e) =>
        SetSelectedConcurrentDuplicateDecisions(ExistingFilePolicy.Overwrite);

    private void ConcurrentBatchDuplicateSkip_Click(object sender, RoutedEventArgs e) =>
        SetSelectedConcurrentDuplicateDecisions(ExistingFilePolicy.Skip);

    private void ConcurrentBatchDuplicateCreateCopy_Click(object sender, RoutedEventArgs e) =>
        SetSelectedConcurrentDuplicateDecisions(ExistingFilePolicy.CreateCopy);

    private void SetSelectedConcurrentDuplicateDecisions(ExistingFilePolicy decision)
    {
        if (!TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            return;
        }
        foreach (DuplicateConflictChoice choice in runtime.DuplicateChoices.Where(item => item.CanChoose && item.IsSelected))
        {
            choice.SetDecision(decision);
            runtime.Job.DuplicateDecisions[choice.RelativePath] = decision;
        }
        ShowRuntimeDuplicateChoices(runtime);
    }

    private void ConcurrentApplyDuplicateChoicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRuntime(out CopyJobRuntime runtime) ||
            runtime.DuplicateDecisionSource is null ||
            runtime.DuplicateChoices.Any(item => !item.IsDecided))
        {
            return;
        }
        Dictionary<string, ExistingFilePolicy> decisions = runtime.DuplicateChoices.ToDictionary(
            item => item.RelativePath,
            item => item.Decision ?? ExistingFilePolicy.Skip,
            StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateConflictChoice choice in runtime.DuplicateChoices)
        {
            choice.SetCanChoose(false);
        }
        runtime.DuplicateDecisionSource.TrySetResult(decisions);
        ShowRuntimeDuplicateChoices(runtime);
    }

    private void ConcurrentPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            return;
        }
        if (runtime.ScheduleRegistration is null)
        {
            return;
        }

        if (IsRuntimeAutomaticallyPaused(runtime))
        {
            _jobScheduler.ForceResumeOrdinary(runtime.ScheduleRegistration);
        }
        else
        {
            _jobScheduler.SetPaused(runtime.ScheduleRegistration, !runtime.IsPaused);
        }

        SyncRuntimePauseStates();
        RefreshSchedulingPresentation();
    }

    private void SyncRuntimePauseStates()
    {
        foreach (CopyJobRuntime candidate in _jobRuntimes.Values)
        {
            if (candidate.ScheduleRegistration is not null)
            {
                candidate.IsPaused = _jobScheduler.IsPaused(candidate.ScheduleRegistration);
            }
        }
    }

    private void RefreshSchedulingPresentation()
    {
        foreach (CopyJobRuntime candidate in _jobRuntimes.Values)
        {
            candidate.Job.IsWaitingForPriority = IsRuntimeAutomaticallyPaused(candidate);
            RefreshHistoryItem(candidate.Job);
        }

        RefreshSelectedRuntime();
    }

    private void ConcurrentCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            return;
        }
        CancelButton.IsEnabled = false;
        StatusText.Text = ResourceService.GetString("Status.Cancelling");
        runtime.Cancellation.Cancel();
    }

    private async void ConcurrentDeleteJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedJob is null || _jobRuntimes.ContainsKey(_selectedJob.Id))
        {
            return;
        }
        if (!IsBatchDeletable(_selectedJob))
        {
            await ShowMessageAsync(
                "Error.CannotDeleteTaskTitle",
                "Error.OnlyEndedTaskCanDelete");
            return;
        }
        var dialog = new ContentDialog
        {
            Title = ResourceService.GetString("Dialog.DeleteHistory"),
            Content = ResourceService.GetString("Error.DeleteRecordReminder"),
            PrimaryButtonText = ResourceService.GetString("Button.DeleteRecord"),
            CloseButtonText = ResourceService.GetString("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await ShowLocalizedDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }
        JobHistoryItem deleting = _selectedJob;
        _history.Remove(deleting);
        RemoveTaskFromSections(deleting);
        await SaveHistorySafeAsync();
        UpdateHistoryEmptyState();
        _selectedJob = null;
        SelectInitialTask();
    }

    private async void ConcurrentStartVerificationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedJob is null || !CanStartVerification(_selectedJob))
        {
            return;
        }

        await EnqueueVerificationJobAsync(_selectedJob);
    }

    private async Task EnqueueVerificationJobAsync(JobHistoryItem originalJob)
    {
        if (_jobRuntimes.ContainsKey(originalJob.Id))
        {
            return;
        }
        if (!Directory.Exists(originalJob.SourcePath))
        {
            await ShowMessageAsync(ResourceService.GetString("Error.CannotStartVerification"), ResourceService.Format("Format.SourceFolderNotExist", originalJob.SourcePath));
            return;
        }
        if (!Directory.Exists(originalJob.DestinationPath))
        {
            await ShowMessageAsync(ResourceService.GetString("Error.CannotStartVerification"), ResourceService.Format("Format.DestinationFolderNotExist", originalJob.DestinationPath));
            return;
        }
        if (!ValidatePaths(originalJob.SourcePath, originalJob.DestinationPath, out string validationMessage))
        {
            await ShowMessageAsync("Error.CannotStartVerification", validationMessage);
            return;
        }

        bool reverification = originalJob.VerificationEnabled;
        var options = new CopyOptions(
            ExistingFilePolicy: ExistingFilePolicy.Overwrite,
            VerifyFiles: true,
            UseFastCopyAlgorithm: originalJob.UseFastCopyAlgorithm,
            SkipCopy: true,
            VerificationAlgorithm: originalJob.VerificationAlgorithm,
            VerificationExecutionMode: VerificationExecutionMode.AfterCopy)
        {
            DestinationPaths = originalJob.DestinationFiles
        };
        string displayName = ResourceService.Format(
            reverification
                ? "Format.ReverificationJobName"
                : "Format.VerificationJobName",
            originalJob.DisplayName);
        if (!EnqueueJob(
                originalJob.SourcePath,
                originalJob.DestinationPath,
                options,
                originalJob.IsPriority,
                originalJob.PreventSleep,
                displayName,
                out string? enqueueError))
        {
            if (_shutdownInProgress || _shutdownCompleted)
            {
                return;
            }
            await ShowMessageAsync(
                "Error.CannotStartOverlappingTask",
                enqueueError!);
        }
    }

    private async void ConcurrentExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        JobHistoryItem? job = _selectedJob;
        if (job is null || _jobRuntimes.ContainsKey(job.Id) || !IsReportable(job))
        {
            await ShowMessageAsync(
                "Error.ReportUnavailable",
                "Error.ReportNotAvailableRunning");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"ClipPort_Report_{job.StartedAt:yyyyMMdd_HHmmss}_{SanitizeReportFileName(job.DisplayName)}"
        };
        picker.FileTypeChoices.Add(ResourceService.GetString("Common.TextReport"), new List<string> { ".txt" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        ExportReportButton.IsEnabled = false;
        try
        {
            string? report = await _historyService.ReadReportAsync(GetReportReference(job));
            report ??= BuildIncompleteReport(job);
            await FileIO.WriteTextAsync(file, report);
            LogText.Text = ResourceService.Format("Format.TaskReportExported", file.Path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Error.ReportExportFailed", ex.Message);
        }
        finally
        {
            ExportReportButton.IsEnabled = true;
        }
    }

    private async void ConcurrentRestartJobButton_Click(object sender, RoutedEventArgs e)
    {
        JobHistoryItem? originalJob = _selectedJob;
        if (originalJob is null ||
            _jobRuntimes.ContainsKey(originalJob.Id) ||
            !originalJob.CanRestart)
        {
            return;
        }

        if (!Directory.Exists(originalJob.SourcePath))
        {
            await ShowMessageAsync(ResourceService.GetString("Error.CannotRestart"), ResourceService.Format("Format.SourceFolderNotExist", originalJob.SourcePath));
            return;
        }
        if (!originalJob.CopyEnabled &&
            !Directory.Exists(originalJob.DestinationPath))
        {
            await ShowMessageAsync(ResourceService.GetString("Error.CannotRestart"), ResourceService.Format("Format.DestinationFolderNotExist", originalJob.DestinationPath));
            return;
        }
        if (!ValidatePaths(originalJob.SourcePath, originalJob.DestinationPath, out string validationMessage))
        {
            await ShowMessageAsync("Error.CannotRestart", validationMessage);
            return;
        }
        if (!originalJob.CopyEnabled && !originalJob.VerificationEnabled)
        {
            await ShowMessageAsync("Error.CannotRestart", "Error.NoCopyOrVerify");
            return;
        }

        var options = new CopyOptions(
            ExistingFilePolicy: originalJob.CopyEnabled
                ? ExistingFilePolicy.Ask
                : ExistingFilePolicy.Overwrite,
            VerifyFiles: originalJob.VerificationEnabled,
            UseFastCopyAlgorithm: originalJob.UseFastCopyAlgorithm,
            SkipCopy: !originalJob.CopyEnabled,
            VerificationAlgorithm: originalJob.VerificationAlgorithm,
            VerificationExecutionMode: originalJob.VerificationExecutionMode)
        {
            DestinationPaths = !originalJob.CopyEnabled
                ? originalJob.DestinationFiles
                : null
        };
        string displayName = ResourceService.Format(
            "Format.RestartedJobName",
            originalJob.DisplayName);
        if (!EnqueueJob(
                originalJob.SourcePath,
                originalJob.DestinationPath,
                options,
                originalJob.IsPriority,
                originalJob.PreventSleep,
                displayName,
                out string? enqueueError))
        {
            if (_shutdownInProgress || _shutdownCompleted)
            {
                return;
            }
            await ShowMessageAsync(
                "Error.CannotStartOverlappingTask",
                enqueueError!);
        }
    }

    private void PrepareConcurrentNewJobView()
    {
        PrepareNewJobView();
        PauseButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = Visibility.Collapsed;
        StartVerificationButton.Visibility = Visibility.Collapsed;
        ExportReportButton.Visibility = Visibility.Collapsed;
        RestartJobButton.Visibility = Visibility.Collapsed;
        NewJobButton.IsEnabled = !_isMultiSelectMode;
        SourcePickerButton.IsEnabled = true;
        DestinationPickerButton.IsEnabled = true;
        HistoryList.IsEnabled = true;
        NewJobsList.IsEnabled = true;
    }

    private bool TryGetSelectedRuntime(out CopyJobRuntime runtime)
    {
        if (_selectedJob is not null && _jobRuntimes.TryGetValue(_selectedJob.Id, out CopyJobRuntime? found))
        {
            runtime = found;
            return true;
        }
        runtime = null!;
        return false;
    }

    private void RefreshSelectedRuntime()
    {
        void Refresh()
        {
            if (TryGetSelectedRuntime(out CopyJobRuntime runtime))
            {
                ShowRuntimeJob(runtime);
            }
            else if (_selectedJob is not null)
            {
                ShowHistoryJob(_selectedJob);
            }
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            Refresh();
        }
        else
        {
            DispatcherQueue.TryEnqueue(Refresh);
        }
    }

    private async void ConcurrentAppWindow_Closing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        args.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        _uiSettings.ColorValuesChanged -= SystemColorValuesChanged;
        CopyJobRuntime[] runtimes = _jobRuntimes.Values.ToArray();
        foreach (CopyJobRuntime runtime in runtimes)
        {
            runtime.Cancellation.Cancel();
        }

        try
        {
            Task[] executionTasks = runtimes
                .Select(runtime => runtime.ExecutionTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (executionTasks.Length > 0)
            {
                await Task.WhenAll(executionTasks);
            }
            await SaveHistorySafeAsync();
            await App.SettingsService.SaveAsync(_appSettings);
        }
        catch (Exception)
        {
            // Copy tasks already preserve committed files and clean their
            // unique partial files. No task or persistence exception may
            // escape this async closing event and crash the process.
        }
        finally
        {
            ReleaseSleepPreventionForShutdown();
            _shutdownCompleted = true;
            _shutdownInProgress = false;
            sender.Destroy();
            // AppWindow.Destroy can leave an unpackaged WinUI dispatcher alive
            // without a window. All task and persistence cleanup is complete.
            Environment.Exit(0);
        }
    }

    private async Task<bool> TryScheduleApplicationRestartAsync()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !File.Exists(executablePath))
        {
            await _logService.WriteAsync(
                "Restart failed because the executable path is unavailable.");
            return false;
        }

        try
        {
            ApplicationRestartService.StartReplacement(
                executablePath,
                AppContext.BaseDirectory);
            return true;
        }
        catch (Exception ex)
        {
            await _logService.WriteAsync(
                $"Restart failed with an exception: {ex.Message}");
            return false;
        }
    }

    private Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await action());
                }
                catch (OperationCanceledException ex)
                {
                    completion.TrySetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException(
                ResourceService.GetString("Error.AppExitedBeforeFinish")));
        }
        return completion.Task;
    }

    private enum FailureResolutionMode
    {
        Retry,
        Overwrite,
        Skip
    }

    private sealed record FailureResolutionAction(
        FailureResolutionMode Mode,
        IReadOnlyList<FileOperationFailure> Failures);

    private sealed class CopyJobRuntime
    {
        public CopyJobRuntime(JobHistoryItem job, CopyOptions options)
        {
            Job = job;
            Options = options;
        }

        public JobHistoryItem Job { get; }
        public CopyOptions Options { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public ObservableCollection<DuplicateConflictChoice> DuplicateChoices { get; } = [];
        public ObservableCollection<FailedFileChoice> FailedFileChoices { get; } = [];
        public List<FileOperationFailure> SkippedFailures { get; } = [];
        public CopyJobScheduler.CopyJobScheduleRegistration? ScheduleRegistration { get; set; }
        public TaskCompletionSource<IReadOnlyDictionary<string, ExistingFilePolicy>>? DuplicateDecisionSource { get; set; }
        public Task? ExecutionTask { get; set; }
        public TaskCompletionSource<FailureResolutionAction>? FailureActionSource { get; set; }
        public CopyThroughputSampler CopyThroughputSampler { get; } = new();
        public CopyThroughputSampler VerifyThroughputSampler { get; } = new(
            sampledPhase: CopyPhase.Verifying);
        public CopyProgressInfo? LastProgress { get; set; }
        public CopyProgressInfo? LastScanProgress { get; set; }
        public CopyProgressInfo? LastCopyProgress { get; set; }
        public CopyProgressInfo? LastVerifyProgress { get; set; }
        public CopyResult? Result { get; set; }
        public bool IsPaused { get; set; }
        public long CopiedBytes { get; set; }
        public long ProcessedCopyBytes { get; set; }
        public int ProcessedCopyFiles { get; set; }
        public bool IsAwaitingFailureDecision { get; set; }
        public bool IsRetryingFailures { get; set; }
        public FailureResolutionMode? ActiveFailureAction { get; set; }
        public CopyProgressInfo? RetryProgress { get; set; }

        public int CopiedFiles { get; set; }
        public long VerifiedBytes { get; set; }
        public int VerifiedFiles { get; set; }
        public long ProcessedVerifyBytes { get; set; }
        public int ProcessedVerifyFiles { get; set; }
        public TimeSpan CopyElapsed { get; set; }
        public TimeSpan VerifyElapsed { get; set; }
        public string Report { get; set; } = string.Empty;
    }
}
