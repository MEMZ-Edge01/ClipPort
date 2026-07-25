using System.Collections.ObjectModel;
using System.Globalization;
using EZDIT.Models;
using EZDIT.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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

        bool isPriority = PriorityExecutionToggle.IsOn;
        var job = new JobHistoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = GetDisplayName(_sourcePath),
            SourcePath = _sourcePath,
            DestinationPath = _destinationPath,
            StartedAt = DateTimeOffset.Now,
            Status = JobStatus.Queued,
            VerificationEnabled = _copyOptions.VerifyFiles,
            UseFastCopyAlgorithm = _copyOptions.UseFastCopyAlgorithm,
            IsPriority = isPriority,
            PreventSleep = PreventSleepToggle.IsOn,
            IsAcknowledged = false,
        };
        var runtime = new CopyJobRuntime(job, _copyOptions);
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
                "任务已由用户取消，已完成的文件予以保留。");
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

            if (action.Retry)
            {
                runtime.IsRetryingFailures = true;
                var retryProgress = new Progress<CopyProgressInfo>(info =>
                {
                    runtime.RetryProgress = info;
                    RefreshSelectedRuntime();
                });
                FileRetryResult retryResult = await _copyService.RetryFailedFilesAsync(
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
                runtime.CopiedBytes = info.TotalBytes;
                runtime.CopiedFiles = info.TotalFiles;
                runtime.Job.CopiedBytes = info.TotalBytes;
                runtime.Job.CopiedFiles = info.TotalFiles;
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
        job.CopiedBytes = result is not null ? result.TotalBytes : runtime.CopiedBytes;
        job.CopiedFiles = result is not null ? result.FileCount : runtime.CopiedFiles;
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
        _lastReport = runtime.Report;
        try
        {
            job.ReportFileName = await _historyService.SaveReportAsync(job.Id, runtime.Report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ReferenceEquals(_selectedJob, job))
            {
                LogText.Text = $"任务已结束，但报告保存失败：{ex.Message}";
            }
        }

        RefreshHistoryItem(job);
        await SaveHistorySafeAsync();
    }

    private void ConcurrentHistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
        HeroNameText.Text = job.DisplayName + (job.IsPriority ? " · 优先" : string.Empty);
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
        double overall = runtime.Options.VerifyFiles
            ? copyPercent * 0.8 + verifyPercent * 0.2
            : copyPercent;

        CopyProgress.Value = copyPercent;
        VerifyProgress.Value = verifyPercent;
        OverallProgress.Value = overall;
        PercentText.Text = $"{overall:F2}%";
        CopySpeedText.Text = info?.Phase == CopyPhase.Copying ? $"{FormatBytes(info.BytesPerSecond)}/s" : "--";
        VerifySpeedText.Text = info?.Phase == CopyPhase.Verifying ? $"{FormatBytes(info.BytesPerSecond)}/s" : "--";
        CopyTimeText.Text = FormatDuration(runtime.CopyElapsed);
        VerifyTimeText.Text = FormatDuration(runtime.VerifyElapsed);
        CopyCountText.Text = $"{runtime.CopiedFiles}/{totalFiles}";
        VerifyCountText.Text = $"{runtime.VerifiedFiles}/{totalFiles}";
        bool copyDone = totalFiles > 0 && runtime.CopiedFiles >= totalFiles;
        bool verifyDone = runtime.Options.VerifyFiles && totalFiles > 0 && runtime.VerifiedFiles >= totalFiles;
        CopyProgress.Visibility = copyDone ? Visibility.Collapsed : Visibility.Visible;
        CopyCompletedBadge.Visibility = copyDone ? Visibility.Visible : Visibility.Collapsed;
        VerifyProgress.Visibility = verifyDone || !runtime.Options.VerifyFiles ? Visibility.Collapsed : Visibility.Visible;
        VerifyCompletedBadge.Visibility = verifyDone || !runtime.Options.VerifyFiles ? Visibility.Visible : Visibility.Collapsed;
        VerifyCompletedText.Text = runtime.Options.VerifyFiles ? "已完成" : "未启用";

        CompletionIcon.Visibility = Visibility.Collapsed;
        PercentText.Visibility = Visibility.Visible;
        StatusText.FontSize = 15;
        StatusText.Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 174, 180, 197));
        StartButton.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        PauseButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        PauseText.Text = runtime.IsPaused ? "继续" : "暂停";
        PauseIcon.Glyph = runtime.IsPaused ? "\uE768" : "\uE769";
        NewJobButton.IsEnabled = true;
        SourcePickerButton.IsEnabled = false;
        DestinationPickerButton.IsEnabled = false;
        HistoryList.IsEnabled = true;
        NewJobsList.IsEnabled = true;
        ReportButton.IsEnabled = false;

        if (runtime.IsRetryingFailures)
        {
            StatusText.Text = "\u6B63\u5728\u91CD\u8BD5\u5931\u8D25\u6587\u4EF6";
            PhaseText.Text = "\u4EC5\u5904\u7406\u5DF2\u9009\u5931\u8D25\u9879";
            CurrentFileText.Text = runtime.RetryProgress?.CurrentFile ?? job.SourcePath;
            LogText.Text = "\u91CD\u8BD5\u5B8C\u6210\u540E\uFF0C\u4ECD\u5931\u8D25\u7684\u6587\u4EF6\u4F1A\u7EE7\u7EED\u4FDD\u7559\u5728\u4E0B\u65B9\u3002";
        }
        else if (runtime.IsAwaitingFailureDecision)
        {
            StatusText.Text = "\u7B49\u5F85\u5904\u7406\u5931\u8D25\u6587\u4EF6";
            PhaseText.Text = "\u8BF7\u5728\u4E0B\u65B9\u9009\u62E9\u91CD\u8BD5\u6216\u8DF3\u8FC7";
            CurrentFileText.Text = $"{runtime.FailedFileChoices.Count:N0} \u4E2A\u6587\u4EF6\u5F85\u5904\u7406";
            LogText.Text = "\u5176\u4ED6\u6587\u4EF6\u5DF2\u7EE7\u7EED\u5904\u7406\uFF0C\u4EFB\u52A1\u4E0D\u4F1A\u56E0\u5355\u4E2A\u6587\u4EF6\u9519\u8BEF\u76F4\u63A5\u5931\u8D25\u3002";
        }
        else if (runtime.IsPaused)
        {
            StatusText.Text = "已暂停";
            PhaseText.Text = "等待用户继续";
            LogText.Text = "任务已暂停。";
        }
        else if (runtime.IsWaitingForPriority || job.Status == JobStatus.Queued)
        {
            StatusText.Text = "等待优先任务";
            PhaseText.Text = "优先任务结束后自动继续";
            LogText.Text = "当前任务已安全暂停；全部优先任务结束后会自动继续。";
        }
        else
        {
            StatusText.Text = info?.Phase switch
            {
                CopyPhase.Scanning => "正在扫描",
                CopyPhase.Copying => "正在拷贝",
                CopyPhase.Verifying => "正在校验",
                CopyPhase.WaitingForDuplicateDecision => "等待处理重复文件",
                _ => "准备执行"
            };
            PhaseText.Text = info?.Phase switch
            {
                CopyPhase.Scanning => "正在读取目录",
                CopyPhase.Copying => "拷贝文件",
                CopyPhase.Verifying => "SHA-256 完整性校验",
                CopyPhase.WaitingForDuplicateDecision => "请在下方逐个选择处理方式",
                _ => job.IsPriority ? "优先任务即将开始" : "任务即将开始"
            };
            LogText.Text = job.IsPriority
                ? "优先任务正在执行；普通任务将在安全检查点等待。"
                : "任务正在并行执行。";
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
        DuplicateSummaryText.Text = $"发现 {_duplicateChoices.Count:N0} 个重复文件";
        DuplicateSelectionHint.Text = $"已选择处理方式 {decided}/{_duplicateChoices.Count}，已勾选 {selected} 项";
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
        RetryFailedFilesButton.Visibility = Visibility.Visible;
        SkipFailedFilesButton.Visibility = Visibility.Visible;
        int selected = _failedFileChoices.Count(item => item.IsSelected);
        bool canAct = runtime.FailureActionSource is not null && selected > 0;
        RetryFailedFilesButton.IsEnabled = canAct;
        SkipFailedFilesButton.IsEnabled = canAct;
        FailedFilesSummaryText.Text = $"\u5931\u8D25\u6587\u4EF6\uFF1A{_failedFileChoices.Count:N0} \u4E2A\uFF0C\u5DF2\u9009 {selected:N0} \u4E2A";
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
        RetryFailedFilesButton.Visibility = Visibility.Collapsed;
        SkipFailedFilesButton.Visibility = Visibility.Collapsed;
        FailedFilesSummaryText.Text = $"\u5DF2\u8DF3\u8FC7\u7684\u5931\u8D25\u6587\u4EF6\uFF1A{_failedFileChoices.Count:N0} \u4E2A";
    }

    private void ConcurrentFailedFileSelection_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            ShowRuntimeFailedFiles(runtime);
        }
    }

    private void ConcurrentRetryFailedFiles_Click(object sender, RoutedEventArgs e) =>
        CompleteFailedFileAction(true);

    private void ConcurrentSkipFailedFiles_Click(object sender, RoutedEventArgs e) =>
        CompleteFailedFileAction(false);

    private void CompleteFailedFileAction(bool retry)
    {
        if (!TryGetSelectedRuntime(out CopyJobRuntime runtime) ||
            runtime.FailureActionSource is not TaskCompletionSource<FailureResolutionAction> source)
        {
            return;
        }

        FileOperationFailure[] selected = runtime.FailedFileChoices
            .Where(item => item.IsSelected)
            .Select(item => item.Failure)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        runtime.FailureActionSource = null;
        RetryFailedFilesButton.IsEnabled = false;
        SkipFailedFilesButton.IsEnabled = false;
        source.TrySetResult(new FailureResolutionAction(retry, selected));
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
        StatusText.Text = "正在取消";
        runtime.Cancellation.Cancel();
    }

    private async void ConcurrentDeleteJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedJob is null || _jobRuntimes.ContainsKey(_selectedJob.Id))
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "删除历史记录？",
            Content = "仅删除这条历史记录及其本地报告，不会删除源文件或已经拷贝的素材。",
            PrimaryButtonText = "删除记录",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        JobHistoryItem deleting = _selectedJob;
        _history.Remove(deleting);
        RemoveTaskFromSections(deleting);
        await _historyService.DeleteReportAsync(deleting.ReportFileName);
        await SaveHistorySafeAsync();
        UpdateHistoryEmptyState();
        _selectedJob = null;
        SelectInitialTask();
    }

    private void PrepareConcurrentNewJobView()
    {
        PrepareNewJobView();
        PauseButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = Visibility.Collapsed;
        NewJobButton.IsEnabled = true;
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
        foreach (CopyJobRuntime runtime in _jobRuntimes.Values.ToList())
        {
            runtime.Cancellation.Cancel();
        }
        ReleaseSleepPreventionForShutdown();
    }

    private sealed record FailureResolutionAction(
        bool Retry,
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
        public CopyProgressInfo? RetryProgress { get; set; }

        public int CopiedFiles { get; set; }
        public long VerifiedBytes { get; set; }
        public int VerifiedFiles { get; set; }
        public TimeSpan CopyElapsed { get; set; }
        public TimeSpan VerifyElapsed { get; set; }
        public string Report { get; set; } = string.Empty;
    }
}
