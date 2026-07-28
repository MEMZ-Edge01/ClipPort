using System.Collections.ObjectModel;
using System.Globalization;
using EZDIT.Models;
using EZDIT.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EZDIT;

public sealed partial class MainWindow
{
    private readonly CopyJobScheduler _jobScheduler = new();
    private readonly Dictionary<string, CopyJobRuntime> _jobRuntimes = new(StringComparer.Ordinal);

    private async void ConcurrentStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfigureNewTaskAsync())
        {
            EnqueueConfiguredJob();
        }
    }

    private async void ConcurrentNewJobButton_Click(object sender, RoutedEventArgs e)
    {
        PrepareConcurrentNewJobView();
        if (await ConfigureNewTaskAsync())
        {
            EnqueueConfiguredJob();
        }
    }

    private void EnqueueConfiguredJob()
    {
        if (_sourcePath is null || _destinationPath is null)
        {
            return;
        }

        EnqueueJob(
            _sourcePath,
            _destinationPath,
            _copyOptions,
            PriorityExecutionToggle.IsOn,
            PreventSleepToggle.IsOn);
    }

    private void EnqueueJob(
        string sourcePath,
        string destinationPath,
        CopyOptions options,
        bool isPriority,
        bool preventSleep)
    {
        var job = new JobHistoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = GetDisplayName(sourcePath),
            SourcePath = sourcePath,
            DestinationPath = destinationPath,
            StartedAt = DateTimeOffset.Now,
            Status = JobStatus.Queued,
            CopyEnabled = !options.SkipCopy,
            VerificationEnabled = options.VerifyFiles,
            UseFastCopyAlgorithm = options.UseFastCopyAlgorithm,
            IsPriority = isPriority,
            PreventSleep = preventSleep,
            IsAcknowledged = false,
        };
        var runtime = new CopyJobRuntime(job, options);
        runtime.ScheduleRegistration = _jobScheduler.Register(isPriority);
        _jobRuntimes.Add(job.Id, runtime);
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
    }

    private async Task RunCopyJobAsync(CopyJobRuntime runtime)
    {
        try
        {
            await WaitForJobPermissionAsync(runtime, runtime.Cancellation.Token);
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
                (conflicts, token) => WaitForJobDuplicateChoicesAsync(runtime, conflicts, token),
                token => WaitForJobPermissionAsync(runtime, token),
                runtime.Cancellation.Token);

            runtime.Result = result;
            await ResolveFailedFilesAsync(runtime, result.FailedFiles, runtime.Cancellation.Token);
            CopyResult finalResult = runtime.Result ?? result;
            bool hasSkippedFiles = runtime.SkippedFailures.Count > 0;
            await FinalizeJobAsync(
                runtime,
                hasSkippedFiles ? JobStatus.CompletedWithErrors : JobStatus.Completed,
                finalResult,
                hasSkippedFiles ? finalResult.Errors.FirstOrDefault() : null);
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
                await ShowMessageAsync("拷卡失败", ex.Message);
            }
        }
        finally
        {
            runtime.ScheduleRegistration?.Dispose();
            runtime.ScheduleRegistration = null;
            runtime.Cancellation.Dispose();
            runtime.IsWaitingForPriority = false;
            _jobRuntimes.Remove(runtime.Job.Id);
            UpdateSleepPreventionState();
            RefreshSelectedRuntime();
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
        while (runtime.FailedFileChoices.Count > 0)
        {
            runtime.IsAwaitingFailureDecision = true;
            runtime.FailureActionSource = new TaskCompletionSource<FailureResolutionAction>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            RefreshSelectedRuntime();

            FailureResolutionAction action;
            using (cancellationToken.Register(() =>
                runtime.FailureActionSource?.TrySetCanceled(cancellationToken)))
            {
                action = await runtime.FailureActionSource.Task;
            }
            runtime.FailureActionSource = null;
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
                    runtime.RetryProgress = info;
                    RefreshSelectedRuntime();
                });
                FileRetryResult retryResult = action.Mode == FailureResolutionMode.Overwrite
                    ? await _copyService.OverwriteVerificationMismatchesAsync(
                        action.Failures,
                        runtime.Options,
                        retryProgress,
                        token => WaitForJobPermissionAsync(runtime, token),
                        cancellationToken)
                    : await _copyService.RetryFailedFilesAsync(
                        action.Failures,
                        runtime.Options,
                        retryProgress,
                        token => WaitForJobPermissionAsync(runtime, token),
                        cancellationToken);
                retryCopyDuration += retryResult.CopyDuration;
                retryVerifyDuration += retryResult.VerifyDuration;
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
            runtime.Result = original with
            {
                Success = skipped.Length == 0,
                CopyDuration = original.CopyDuration + retryCopyDuration,
                VerifyDuration = original.VerifyDuration + retryVerifyDuration,
                FailedFiles = skipped,
                Errors = skipped.Select(item => item.Error).ToArray()
            };
        }
    }

    private async Task WaitForJobPermissionAsync(CopyJobRuntime runtime, CancellationToken cancellationToken)
    {
        while (true)
        {
            while (runtime.IsPaused)
            {
                await Task.Delay(120, cancellationToken);
            }

            bool mustWaitForPriority = !runtime.Job.IsPriority && _jobScheduler.HasActivePriorityJobs;
            runtime.IsWaitingForPriority = mustWaitForPriority;
            RefreshSelectedRuntime();
            if (!mustWaitForPriority)
            {
                return;
            }

            await _jobScheduler.WaitForTurnAsync(false, cancellationToken);
            runtime.IsWaitingForPriority = false;
            RefreshSelectedRuntime();
        }
    }

    private void UpdateJobProgress(CopyJobRuntime runtime, CopyProgressInfo info)
    {
        runtime.LastProgress = info;
        runtime.Job.TotalBytes = info.TotalBytes;
        runtime.Job.FileCount = info.TotalFiles;
        switch (info.Phase)
        {
            case CopyPhase.Copying:
                runtime.CopiedBytes = info.ProcessedBytes;
                runtime.CopiedFiles = info.ProcessedFiles;
                runtime.CopyElapsed = info.Elapsed;
                runtime.Job.CopiedBytes = info.ProcessedBytes;
                runtime.Job.CopiedFiles = info.ProcessedFiles;
                runtime.Job.CopySeconds = info.Elapsed.TotalSeconds;
                break;
            case CopyPhase.Verifying:
                runtime.VerifiedBytes = info.ProcessedBytes;
                runtime.VerifiedFiles = info.ProcessedFiles;
                runtime.VerifyElapsed = info.Elapsed;
                runtime.Job.VerifiedFiles = info.ProcessedFiles;
                runtime.Job.VerifySeconds = info.Elapsed.TotalSeconds;
                break;
            case CopyPhase.Completed:
                if (!runtime.Options.SkipCopy)
                {
                    runtime.CopiedBytes = info.TotalBytes;
                    runtime.CopiedFiles = info.TotalFiles;
                    runtime.Job.CopiedBytes = info.TotalBytes;
                    runtime.Job.CopiedFiles = info.TotalFiles;
                }
                if (runtime.Options.VerifyFiles)
                {
                    runtime.VerifiedBytes = info.TotalBytes;
                    runtime.VerifiedFiles = info.TotalFiles;
                    runtime.Job.VerifiedFiles = info.TotalFiles;
                }
                break;
        }

        RefreshHistoryItem(runtime.Job);
        if (ReferenceEquals(_selectedJob, runtime.Job))
        {
            ShowRuntimeJob(runtime);
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
            ? result.TotalBytes
            : runtime.CopiedBytes;
        job.CopiedFiles = result is not null && !runtime.Options.SkipCopy
            ? result.FileCount
            : runtime.CopiedFiles;
        job.VerifiedFiles = result?.VerifiedFiles.Count ?? runtime.VerifiedFiles;
        job.CopySeconds = result?.CopyDuration.TotalSeconds ?? runtime.CopyElapsed.TotalSeconds;
        job.VerifySeconds = result?.VerifyDuration.TotalSeconds ?? runtime.VerifyElapsed.TotalSeconds;
        job.VerificationEnabled = result?.VerificationPerformed ?? runtime.Options.VerifyFiles;
        job.ErrorMessage = error;

        if (result is not null)
        {
            job.DuplicateFiles = result.DuplicateFiles.ToList();
            job.FailedFiles = result.FailedFiles.ToList();
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
            job.ReportFileName = await _historyService.SaveReportAsync(job.Id, runtime.Report);
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
        HeroNameText.Text = job.DisplayName + (job.IsPriority ? ResourceService.GetString("Common.Priority") : string.Empty);
        SourcePathText.Text = job.SourcePath;
        DestinationPathText.Text = job.DestinationPath;
        TotalSizeText.Text = info is null ? "--" : FormatBytes(info.TotalBytes);
        TotalCountText.Text = info?.TotalFiles.ToString("N0", CultureInfo.InvariantCulture) ?? "--";
        StartTimeText.Text = job.StartedAt.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        EndTimeText.Text = "--";
        DurationText.Text = FormatDuration(info?.Elapsed ?? TimeSpan.Zero);
        CurrentFileText.Text = string.IsNullOrWhiteSpace(info?.CurrentFile)
            ? $"{job.SourcePath} → {job.DestinationPath}"
            : info.CurrentFile;

        int totalFiles = info?.TotalFiles ?? job.FileCount;
        long totalBytes = info?.TotalBytes ?? job.TotalBytes;
        double copyPercent = GetJobPercent(totalBytes, totalFiles, runtime.CopiedBytes, runtime.CopiedFiles);
        double verifyPercent = runtime.Options.VerifyFiles
            ? GetJobPercent(totalBytes, totalFiles, runtime.VerifiedBytes, runtime.VerifiedFiles)
            : 100;
        double overall = runtime.Options.SkipCopy
            ? verifyPercent
            : runtime.Options.VerifyFiles
            ? copyPercent * 0.8 + verifyPercent * 0.2
            : copyPercent;

        CopyProgress.Value = copyPercent;
        VerifyProgress.Value = verifyPercent;
        OverallProgress.Value = overall;
        CopyProgressRow.Visibility = runtime.Options.SkipCopy
            ? Visibility.Collapsed
            : Visibility.Visible;
        VerifyProgressRow.Visibility = runtime.Options.VerifyFiles
            ? Visibility.Visible
            : Visibility.Collapsed;
        PercentText.Text = $"{overall:F2}%";
        CopySpeedText.Text = info?.Phase == CopyPhase.Copying ? $"{FormatBytes(info.BytesPerSecond)}/s" : "--";
        VerifySpeedText.Text = info?.Phase == CopyPhase.Verifying ? $"{FormatBytes(info.BytesPerSecond)}/s" : "--";
        CopyTimeText.Text = FormatDuration(runtime.CopyElapsed);
        VerifyTimeText.Text = FormatDuration(runtime.VerifyElapsed);
        CopyCountText.Text = runtime.Options.SkipCopy ? "--" : $"{runtime.CopiedFiles}/{totalFiles}";
        VerifyCountText.Text = $"{runtime.VerifiedFiles}/{totalFiles}";
        bool copyDone = runtime.Options.SkipCopy || totalFiles > 0 && runtime.CopiedFiles >= totalFiles;
        bool verifyDone = runtime.Options.VerifyFiles && totalFiles > 0 && runtime.VerifiedFiles >= totalFiles;
        CopyProgress.Visibility = copyDone ? Visibility.Collapsed : Visibility.Visible;
        CopyCompletedBadge.Visibility = copyDone ? Visibility.Visible : Visibility.Collapsed;
        CopyCompletedText.Text = runtime.Options.SkipCopy ? ResourceService.GetString("Common.Disabled") : ResourceService.GetString("Common.Completed");
        VerifyProgress.Visibility = verifyDone || !runtime.Options.VerifyFiles ? Visibility.Collapsed : Visibility.Visible;
        VerifyCompletedBadge.Visibility = verifyDone || !runtime.Options.VerifyFiles ? Visibility.Visible : Visibility.Collapsed;
        VerifyCompletedText.Text = runtime.Options.VerifyFiles ? ResourceService.GetString("Common.Completed") : ResourceService.GetString("Common.Disabled");

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
        PauseText.Text = runtime.IsPaused ? ResourceService.GetString("Button.Resume") : ResourceService.GetString("Button.Pause");
        PauseIcon.Glyph = runtime.IsPaused ? "\uE768" : "\uE769";
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
        else if (runtime.IsPaused)
        {
            StatusText.Text = ResourceService.GetString("Status.Paused");
            PhaseText.Text = ResourceService.GetString("Info.WaitingToResume");
            LogText.Text = ResourceService.GetString("Info.TaskPaused");
        }
        else if (runtime.IsWaitingForPriority || job.Status == JobStatus.Queued)
        {
            StatusText.Text = ResourceService.GetString("Status.WaitingPriorityTasks");
            PhaseText.Text = ResourceService.GetString("Info.AutoResumeAfterPriority");
            LogText.Text = ResourceService.GetString("Info.TaskSafelyPaused");
        }
        else
        {
            StatusText.Text = info?.Phase switch
            {
                CopyPhase.Scanning => ResourceService.GetString("Status.Scanning"),
                CopyPhase.Copying => ResourceService.GetString("Status.Copying"),
                CopyPhase.Verifying => ResourceService.GetString("Status.Verifying"),
                CopyPhase.WaitingForDuplicateDecision => ResourceService.GetString("Status.WaitingDuplicateChoices"),
                _ => ResourceService.GetString("Status.Preparing")
            };
            PhaseText.Text = info?.Phase switch
            {
                CopyPhase.Scanning => ResourceService.GetString("Status.ReadingDirectories"),
                CopyPhase.Copying => ResourceService.GetString("Status.CopyingFiles"),
                CopyPhase.Verifying => ResourceService.GetString("Status.SHA256Verification"),
                CopyPhase.WaitingForDuplicateDecision => ResourceService.GetString("Info.ChooseActionBelow"),
                _ => job.IsPriority ? ResourceService.GetString("Status.PriorityTaskStarting") : ResourceService.GetString("Status.TaskStarting")
            };
            LogText.Text = job.IsPriority
                ? ResourceService.GetString("Info.PriorityTasksRunning")
                : ResourceService.GetString("Info.TasksRunningConcurrently");
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
        _duplicateChoices.Clear();
        foreach (DuplicateConflictChoice choice in runtime.DuplicateChoices)
        {
            _duplicateChoices.Add(choice);
        }
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
        _failedFileChoices.Clear();
        foreach (FailedFileChoice choice in runtime.FailedFileChoices)
        {
            _failedFileChoices.Add(choice);
        }

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

        runtime.FailureActionSource = null;
        OverwriteFailedFilesButton.IsEnabled = false;
        RetryFailedFilesButton.IsEnabled = false;
        SkipFailedFilesButton.IsEnabled = false;
        source.TrySetResult(new FailureResolutionAction(mode, selected));
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
        runtime.IsPaused = !runtime.IsPaused;
        ShowRuntimeJob(runtime);
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
            await ShowMessageAsync("无法删除任务", "只能删除已经结束处理的任务记录。");
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
            await ShowMessageAsync("无法开始校验", validationMessage);
            return;
        }

        var options = new CopyOptions(
            ExistingFilePolicy: ExistingFilePolicy.Overwrite,
            VerifyFiles: true,
            UseFastCopyAlgorithm: originalJob.UseFastCopyAlgorithm,
            SkipCopy: true);
        EnqueueJob(
            originalJob.SourcePath,
            originalJob.DestinationPath,
            options,
            originalJob.IsPriority,
            originalJob.PreventSleep);
    }

    private async void ConcurrentExportReportButton_Click(object sender, RoutedEventArgs e)
    {
        JobHistoryItem? job = _selectedJob;
        if (job is null || _jobRuntimes.ContainsKey(job.Id) || !IsReportable(job))
        {
            await ShowMessageAsync("报告尚不可用", "运行中或排队中的任务暂时不能导出报告。");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"EZDIT_Report_{job.StartedAt:yyyyMMdd_HHmmss}_{SanitizeReportFileName(job.DisplayName)}"
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
            string? report = await _historyService.ReadReportAsync(job.ReportFileName);
            report ??= BuildIncompleteReport(job);
            await FileIO.WriteTextAsync(file, report);
            LogText.Text = ResourceService.Format("Format.TaskReportExported", file.Path);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("导出报告失败", ex.Message);
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
        if (!Directory.Exists(originalJob.DestinationPath))
        {
            await ShowMessageAsync(ResourceService.GetString("Error.CannotRestart"), ResourceService.Format("Format.DestinationFolderNotExist", originalJob.DestinationPath));
            return;
        }
        if (!ValidatePaths(originalJob.SourcePath, originalJob.DestinationPath, out string validationMessage))
        {
            await ShowMessageAsync("无法重新开始", validationMessage);
            return;
        }
        if (!originalJob.CopyEnabled && !originalJob.VerificationEnabled)
        {
            await ShowMessageAsync("无法重新开始", "原任务没有启用拷贝或校验。");
            return;
        }

        var options = new CopyOptions(
            ExistingFilePolicy: originalJob.CopyEnabled
                ? ExistingFilePolicy.Ask
                : ExistingFilePolicy.Overwrite,
            VerifyFiles: originalJob.VerificationEnabled,
            UseFastCopyAlgorithm: originalJob.UseFastCopyAlgorithm,
            SkipCopy: !originalJob.CopyEnabled);
        EnqueueJob(
            originalJob.SourcePath,
            originalJob.DestinationPath,
            options,
            originalJob.IsPriority,
            originalJob.PreventSleep);
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

    private void ConcurrentMainWindow_Closed(object sender, WindowEventArgs args)
    {
        _uiSettings.ColorValuesChanged -= SystemColorValuesChanged;
        foreach (CopyJobRuntime runtime in _jobRuntimes.Values.ToList())
        {
            runtime.Cancellation.Cancel();
        }
        ReleaseSleepPreventionForShutdown();
        try
        {
            App.SettingsService.Save(_appSettings);
        }
        catch
        {
            // Best-effort save on exit; ignore failures
        }
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
        public CopyProgressInfo? LastProgress { get; set; }
        public CopyResult? Result { get; set; }
        public bool IsPaused { get; set; }
        public bool IsWaitingForPriority { get; set; }
        public long CopiedBytes { get; set; }
        public bool IsAwaitingFailureDecision { get; set; }
        public bool IsRetryingFailures { get; set; }
        public FailureResolutionMode? ActiveFailureAction { get; set; }
        public CopyProgressInfo? RetryProgress { get; set; }

        public int CopiedFiles { get; set; }
        public long VerifiedBytes { get; set; }
        public int VerifiedFiles { get; set; }
        public TimeSpan CopyElapsed { get; set; }
        public TimeSpan VerifyElapsed { get; set; }
        public string Report { get; set; } = string.Empty;
    }
}
