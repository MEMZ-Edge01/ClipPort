using ClipPort.FnOS.Contracts;
using ClipPort.FnOS.FnOs;
using ClipPort.FnOS.Persistence;
using ClipPort.FnOS.Realtime;
using ClipPort.Models;
using ClipPort.Services;
using ClipPort.FnOS.Settings;

namespace ClipPort.FnOS.Tasks;

public sealed class FnOsTaskManager : IHostedService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TaskRuntime> _runtimes = new(StringComparer.Ordinal);
    private readonly List<FnOsTaskRecord> _records = [];
    private readonly CopyJobScheduler _scheduler = new();
    private readonly FileCopyService _copyService;
    private readonly AuthorizedFolderModule _authorizedFolders;
    private readonly FnOsTaskStore _store;
    private readonly JobHistoryService _reports;
    private readonly TaskEventHub _events;
    private readonly JsonPartialFileJournal _partialFiles;
    private readonly FnOsSettingsStore _settings;
    private readonly NotificationService _notifications;

    public FnOsTaskManager(
        FileCopyService copyService,
        AuthorizedFolderModule authorizedFolders,
        FnOsTaskStore store,
        JobHistoryService reports,
        TaskEventHub events,
        JsonPartialFileJournal partialFiles,
        FnOsSettingsStore settings,
        NotificationService notifications)
    {
        _copyService = copyService;
        _authorizedFolders = authorizedFolders;
        _store = store;
        _reports = reports;
        _events = events;
        _partialFiles = partialFiles;
        _settings = settings;
        _notifications = notifications;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _partialFiles.CleanupTrackedFiles();
        List<FnOsTaskRecord> loaded = await _store.LoadAsync(cancellationToken);
        lock (_sync)
        {
            _records.AddRange(loaded);
        }
        await PersistAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] tasks;
        lock (_sync)
        {
            foreach (TaskRuntime runtime in _runtimes.Values)
            {
                runtime.Cancellation.Cancel();
                runtime.DuplicateDecisionSource?.TrySetCanceled();
                runtime.FailureActionSource?.TrySetCanceled();
            }
            tasks = _runtimes.Values
                .Select(runtime => runtime.ExecutionTask)
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch (Exception ex) when (
            ex is OperationCanceledException or TimeoutException)
        {
        }
        await PersistAsync(CancellationToken.None);
    }

    public IReadOnlyList<FnOsTaskRecord> Snapshot()
    {
        lock (_sync)
        {
            return _records.ToArray();
        }
    }

    public FnOsTaskRecord Get(string id)
    {
        lock (_sync)
        {
            return FindRecordLocked(id);
        }
    }

    public void EnsureNoActiveTaskUses(string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            throw new TaskManagerException("invalid_request", "The authorization path is invalid.");
        }
        lock (_sync)
        {
            if (_records.Any(record => IsActive(record.Status) &&
                (PathSafety.PathsOverlap(normalized, record.Request.SourcePath) ||
                 PathSafety.PathsOverlap(normalized, record.Request.DestinationPath))))
            {
                throw new TaskManagerException(
                    "task_conflict",
                    "This authorization is used by an active task.");
            }
        }
    }

    public async Task<FnOsTaskRecord> CreateAsync(
        int userId,
        CreateTaskRequest request,
        CancellationToken cancellationToken)
    {
        ValidatedTaskRequest validated = await _authorizedFolders.ValidateTaskAsync(
            userId,
            request,
            cancellationToken);
        var normalizedRequest = validated.Request with
        {
            SourcePath = validated.SourcePath,
            DestinationPath = validated.DestinationPath,
            DestinationSubfolder = null,
            VerificationExecutionMode = validated.Request.Mode == FnOsTaskMode.CopyAndVerify
                ? validated.Request.VerificationExecutionMode
                : VerificationExecutionMode.AfterCopy
        };
        var record = new FnOsTaskRecord
        {
            DisplayName = new DirectoryInfo(validated.SourcePath).Name,
            Request = normalizedRequest,
            Status = FnOsTaskStatus.Queued
        };
        TaskRuntime runtime;
        lock (_sync)
        {
            EnsureNoPathConflictLocked(normalizedRequest);
            runtime = new TaskRuntime(
                record,
                userId,
                _scheduler.Register(normalizedRequest.IsPriority));
            _records.Insert(0, record);
            _runtimes.Add(record.Id, runtime);
            TrimHistoryLocked();
            runtime.ExecutionTask = RunAsync(runtime);
        }

        await PersistAsync(cancellationToken);
        await _events.PublishAsync("taskCreated", record, cancellationToken);
        return record;
    }

    public async Task<FnOsTaskRecord> RestartAsync(
        int userId,
        string id,
        CancellationToken cancellationToken)
    {
        CreateTaskRequest request;
        lock (_sync)
        {
            FnOsTaskRecord record = FindRecordLocked(id);
            if (IsActive(record.Status))
            {
                throw new TaskManagerException("task_conflict", "An active task cannot be restarted.");
            }
            request = record.Request;
        }
        return await CreateAsync(userId, request, cancellationToken);
    }

    public Task<FnOsTaskRecord> VerifyAgainAsync(
        int userId,
        string id,
        CancellationToken cancellationToken)
    {
        CreateTaskRequest request;
        lock (_sync)
        {
            FnOsTaskRecord record = FindRecordLocked(id);
            if (IsActive(record.Status))
            {
                throw new TaskManagerException("task_conflict", "An active task cannot be verified again.");
            }
            request = record.Request with
            {
                Mode = FnOsTaskMode.VerifyOnly,
                ExistingFilePolicy = ExistingFilePolicy.Overwrite,
                VerificationExecutionMode = VerificationExecutionMode.AfterCopy
            };
        }
        return CreateAsync(userId, request, cancellationToken);
    }

    public async Task PauseAsync(string id, CancellationToken cancellationToken)
    {
        FnOsTaskRecord record;
        lock (_sync)
        {
            TaskRuntime runtime = FindRuntimeLocked(id);
            if (runtime.Record.Status is not (FnOsTaskStatus.Running or FnOsTaskStatus.Queued))
            {
                throw new TaskManagerException("task_state_invalid", "Only a running or queued task can be paused.");
            }
            _scheduler.SetPausedAndYield(runtime.ScheduleRegistration, true);
            runtime.Record.Status = FnOsTaskStatus.Paused;
            record = runtime.Record;
        }
        await PersistAsync(cancellationToken);
        await _events.PublishAsync("taskPaused", record, cancellationToken);
    }

    public async Task ResumeAsync(string id, CancellationToken cancellationToken)
    {
        FnOsTaskRecord record;
        lock (_sync)
        {
            TaskRuntime runtime = FindRuntimeLocked(id);
            if (runtime.Record.Status is not FnOsTaskStatus.Paused)
            {
                throw new TaskManagerException("task_state_invalid", "Only a paused task can be resumed.");
            }
            _scheduler.SetPausedAndYield(runtime.ScheduleRegistration, false);
            runtime.Record.Status = FnOsTaskStatus.Queued;
            record = runtime.Record;
        }
        await PersistAsync(cancellationToken);
        await _events.PublishAsync("taskQueued", record, cancellationToken);
    }

    public Task CancelAsync(string id)
    {
        lock (_sync)
        {
            TaskRuntime runtime = FindRuntimeLocked(id);
            runtime.Cancellation.Cancel();
            runtime.DuplicateDecisionSource?.TrySetCanceled();
            runtime.FailureActionSource?.TrySetCanceled();
            return Task.CompletedTask;
        }
    }

    public Task SubmitDuplicateDecisionsAsync(
        string id,
        DuplicateDecisionRequest request)
    {
        lock (_sync)
        {
            TaskRuntime runtime = FindRuntimeLocked(id);
            TaskCompletionSource<IReadOnlyDictionary<string, ExistingFilePolicy>> source =
                runtime.DuplicateDecisionSource ?? throw new TaskManagerException(
                    "task_state_invalid",
                    "The task is not waiting for duplicate decisions.");
            var expected = runtime.Record.DuplicateFiles
                .Select(item => item.RelativePath)
                .ToHashSet(PathSemantics.Comparer);
            var decisions = request.Decisions
                .Where(item => expected.Contains(item.RelativePath) &&
                               item.Decision is ExistingFilePolicy.Overwrite or
                                   ExistingFilePolicy.Skip or ExistingFilePolicy.CreateCopy)
                .ToDictionary(
                    item => item.RelativePath,
                    item => item.Decision,
                    PathSemantics.Comparer);
            if (decisions.Count != expected.Count)
            {
                throw new TaskManagerException(
                    "invalid_request",
                    "Every duplicate file requires a decision.");
            }
            runtime.Record.DuplicateDecisions = new Dictionary<string, ExistingFilePolicy>(
                decisions,
                PathSemantics.Comparer);
            source.TrySetResult(decisions);
            return Task.CompletedTask;
        }
    }

    public Task SubmitFailureActionAsync(string id, FailureActionRequest request)
    {
        lock (_sync)
        {
            TaskRuntime runtime = FindRuntimeLocked(id);
            TaskCompletionSource<FailureActionRequest> source =
                runtime.FailureActionSource ?? throw new TaskManagerException(
                    "task_state_invalid",
                    "The task is not waiting for a failure action.");
            var expected = runtime.Record.FailedFiles
                .Select(item => item.RelativePath)
                .ToHashSet(PathSemantics.Comparer);
            string[] selected = request.RelativePaths.Count == 0
                ? expected.ToArray()
                : request.RelativePaths
                    .Where(expected.Contains)
                    .Distinct(PathSemantics.Comparer)
                    .ToArray();
            if (selected.Length == 0)
            {
                throw new TaskManagerException("invalid_request", "Select at least one failed file.");
            }
            if (request.Action == FailureActionKind.Overwrite &&
                runtime.Record.FailedFiles
                    .Where(item => selected.Contains(item.RelativePath, PathSemantics.Comparer))
                    .Any(item => !item.IsVerificationMismatch))
            {
                throw new TaskManagerException(
                    "invalid_request",
                    "Only verification mismatches can be overwritten.");
            }
            source.TrySetResult(request with { RelativePaths = selected });
            return Task.CompletedTask;
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        string? reportFileName;
        lock (_sync)
        {
            FnOsTaskRecord record = FindRecordLocked(id);
            if (IsActive(record.Status))
            {
                throw new TaskManagerException("task_conflict", "An active task cannot be deleted.");
            }
            reportFileName = record.ReportFileName;
            _records.Remove(record);
        }
        await _reports.DeleteReportAsync(reportFileName);
        await PersistAsync(cancellationToken);
        await _events.PublishAsync("taskDeleted", new { id }, cancellationToken);
    }

    public async Task DeleteManyAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken cancellationToken)
    {
        string[] distinctIds = ids.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (distinctIds.Length == 0)
        {
            throw new TaskManagerException("invalid_request", "Select at least one task.");
        }
        List<(string Id, string? Report)> removed;
        lock (_sync)
        {
            FnOsTaskRecord[] records = distinctIds.Select(FindRecordLocked).ToArray();
            if (records.Any(record => IsActive(record.Status)))
            {
                throw new TaskManagerException("task_conflict", "Active tasks cannot be deleted.");
            }
            removed = records.Select(record => (record.Id, record.ReportFileName)).ToList();
            foreach (FnOsTaskRecord record in records)
            {
                _records.Remove(record);
            }
        }
        foreach ((string _, string? report) in removed)
        {
            await _reports.DeleteReportAsync(report);
        }
        await PersistAsync(cancellationToken);
        foreach ((string id, string? _) in removed)
        {
            await _events.PublishAsync("taskDeleted", new { id }, cancellationToken);
        }
    }

    public async Task<BatchReportExportResponse> ExportReportsAsync(
        IReadOnlyCollection<string> ids,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        string[] distinctIds = ids.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (distinctIds.Length == 0)
        {
            throw new TaskManagerException("invalid_request", "Select at least one task report.");
        }
        (string Id, string Source)[] reports;
        lock (_sync)
        {
            reports = distinctIds.Select(id =>
            {
                FnOsTaskRecord record = FindRecordLocked(id);
                if (string.IsNullOrWhiteSpace(record.ReportFileName))
                {
                    throw new TaskManagerException("report_not_found", "One or more task reports are unavailable.");
                }
                return (record.Id, Path.Combine(_reports.ReportsDirectory, Path.GetFileName(record.ReportFileName)));
            }).ToArray();
        }
        var exported = new List<string>();
        foreach ((string id, string source) in reports)
        {
            if (!File.Exists(source))
            {
                throw new TaskManagerException("report_not_found", "One or more task reports are unavailable.");
            }
            string fileName = $"clipport-{id}.txt";
            string destination = Path.Combine(destinationDirectory, fileName);
            await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            exported.Add(fileName);
        }
        return new BatchReportExportResponse(exported.Count, exported);
    }

    public string GetReportPath(string id)
    {
        lock (_sync)
        {
            FnOsTaskRecord record = FindRecordLocked(id);
            if (string.IsNullOrWhiteSpace(record.ReportFileName))
            {
                throw new TaskManagerException("report_not_found", "The task report is not available.");
            }
            return Path.Combine(
                _reports.ReportsDirectory,
                Path.GetFileName(record.ReportFileName));
        }
    }

    private async Task RunAsync(TaskRuntime runtime)
    {
        CopyResult? result = null;
        try
        {
            await _scheduler.WaitForTurnAsync(
                runtime.ScheduleRegistration,
                runtime.Cancellation.Token);
            SetStatus(runtime, FnOsTaskStatus.Running);
            runtime.Record.StartedAt = DateTimeOffset.UtcNow;
            await PersistAndPublishAsync("taskStarted", runtime.Record, runtime.Cancellation.Token);

            var progress = new InlineProgress<CopyProgressInfo>(info =>
            {
                CaptureProgress(runtime, info);
            });
            var duplicateProgress = new InlineProgress<DuplicateFileConflict>(conflict =>
            {
                lock (_sync)
                {
                    if (!runtime.Record.DuplicateFiles.Any(item =>
                            PathSemantics.Comparer.Equals(item.RelativePath, conflict.RelativePath)))
                    {
                        runtime.Record.DuplicateFiles.Add(conflict);
                    }
                }
            });

            result = await _copyService.CopyAndVerifyAsync(
                runtime.Record.Request.SourcePath,
                runtime.Record.Request.DestinationPath,
                CreateOptions(runtime.Record.Request),
                progress,
                duplicateProgress,
                (conflicts, cancellationToken) => ResolveDuplicatesAsync(
                    runtime,
                    conflicts,
                    cancellationToken),
                cancellationToken => WaitForExecutionOrResumeAsync(runtime, cancellationToken),
                runtime.Cancellation.Token,
                cancellationToken => _scheduler.AcquireExecutionLeaseAsync(
                    runtime.ScheduleRegistration,
                    cancellationToken));

            ApplyResult(runtime.Record, result);
            await ResolveFailuresAsync(runtime, result);
            CompleteRecord(runtime.Record);
            await SaveReportAsync(runtime, result, runtime.Cancellation.Token);
            await NotifyTerminalAsync(runtime.Record);
            await PersistAndPublishAsync("terminal", runtime.Record, runtime.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            runtime.Record.Status = FnOsTaskStatus.Cancelled;
            runtime.Record.FinishedAt = DateTimeOffset.UtcNow;
            runtime.Record.Errors.Add("The task was cancelled; committed destination files were kept.");
            await SaveIncompleteReportAsync(runtime.Record);
            await NotifyTerminalAsync(runtime.Record);
            await PersistAndPublishAsync("terminal", runtime.Record, CancellationToken.None);
        }
        catch (Exception ex)
        {
            runtime.Record.Status = FnOsTaskStatus.Failed;
            runtime.Record.FinishedAt = DateTimeOffset.UtcNow;
            runtime.Record.Errors.Add(ex.Message);
            await SaveIncompleteReportAsync(runtime.Record);
            await NotifyTerminalAsync(runtime.Record);
            await PersistAndPublishAsync("terminal", runtime.Record, CancellationToken.None);
        }
        finally
        {
            runtime.ScheduleRegistration.Dispose();
            runtime.Cancellation.Dispose();
            lock (_sync)
            {
                _runtimes.Remove(runtime.Record.Id);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, ExistingFilePolicy>> ResolveDuplicatesAsync(
        TaskRuntime runtime,
        IReadOnlyList<DuplicateFileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        runtime.Record.DuplicateFiles = conflicts.ToList();
        runtime.Record.Status = FnOsTaskStatus.AwaitingDuplicateDecision;
        _scheduler.SetPausedAndYield(runtime.ScheduleRegistration, true);
        runtime.DuplicateDecisionSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await PersistAndPublishAsync("duplicateDecisionRequired", runtime.Record, cancellationToken);
        try
        {
            IReadOnlyDictionary<string, ExistingFilePolicy> decisions =
                await runtime.DuplicateDecisionSource.Task.WaitAsync(cancellationToken);
            runtime.Record.Status = FnOsTaskStatus.Queued;
            _scheduler.SetPausedAndYield(runtime.ScheduleRegistration, false);
            await WaitForExecutionOrResumeAsync(runtime, cancellationToken);
            return decisions;
        }
        finally
        {
            runtime.DuplicateDecisionSource = null;
        }
    }

    private async Task ResolveFailuresAsync(TaskRuntime runtime, CopyResult initialResult)
    {
        List<FileOperationFailure> remaining = initialResult.FailedFiles.ToList();
        while (remaining.Count > 0)
        {
            runtime.Record.FailedFiles = remaining;
            runtime.Record.Status = FnOsTaskStatus.AwaitingFailureDecision;
            _scheduler.SetPausedAndYield(runtime.ScheduleRegistration, true);
            runtime.FailureActionSource = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await PersistAndPublishAsync(
                "failureDecisionRequired",
                runtime.Record,
                runtime.Cancellation.Token);
            FailureActionRequest action;
            try
            {
                action = await runtime.FailureActionSource.Task.WaitAsync(runtime.Cancellation.Token);
            }
            finally
            {
                runtime.FailureActionSource = null;
            }

            if (action.Action == FailureActionKind.Skip)
            {
                break;
            }

            var selectedSet = action.RelativePaths.ToHashSet(PathSemantics.Comparer);
            List<FileOperationFailure> selected = remaining
                .Where(item => selectedSet.Contains(item.RelativePath))
                .ToList();
            List<FileOperationFailure> untouched = remaining
                .Where(item => !selectedSet.Contains(item.RelativePath))
                .ToList();
            runtime.Record.Status = FnOsTaskStatus.Queued;
            _scheduler.SetPausedAndYield(runtime.ScheduleRegistration, false);
            await WaitForExecutionOrResumeAsync(runtime, runtime.Cancellation.Token);
            var retryProgress = new InlineProgress<CopyProgressInfo>(info =>
            {
                CaptureProgress(runtime, info);
            });
            FileRetryResult retry = action.Action == FailureActionKind.Overwrite
                ? await _copyService.OverwriteVerificationMismatchesAsync(
                    selected,
                    CreateOptions(runtime.Record.Request),
                    retryProgress,
                    cancellationToken => WaitForExecutionOrResumeAsync(runtime, cancellationToken),
                    runtime.Cancellation.Token,
                    cancellationToken => _scheduler.AcquireExecutionLeaseAsync(
                        runtime.ScheduleRegistration,
                        cancellationToken))
                : await _copyService.RetryFailedFilesAsync(
                    selected,
                    CreateOptions(runtime.Record.Request),
                    retryProgress,
                    cancellationToken => WaitForExecutionOrResumeAsync(runtime, cancellationToken),
                    runtime.Cancellation.Token,
                    cancellationToken => _scheduler.AcquireExecutionLeaseAsync(
                        runtime.ScheduleRegistration,
                        cancellationToken));
            runtime.Record.CopiedBytes += retry.CopiedBytes;
            runtime.Record.CopiedFiles += retry.CopiedFiles;
            runtime.Record.CopySeconds += retry.CopyDuration.TotalSeconds;
            runtime.Record.VerifySeconds += retry.VerifyDuration.TotalSeconds;
            runtime.Record.Warnings.AddRange(retry.Warnings);
            runtime.RetryVerificationResults.AddRange(retry.VerificationResults);
            FileVerificationResult[] verified = retry.VerificationResults
                .Where(item => item.IsMatch)
                .ToArray();
            runtime.Record.VerifiedFiles += verified.Length;
            runtime.Record.VerifiedBytes += verified.Sum(item => item.Length);
            remaining = untouched.Concat(retry.FailedFiles).ToList();
        }
        runtime.Record.FailedFiles = remaining;
    }

    private async Task SaveReportAsync(
        TaskRuntime runtime,
        CopyResult result,
        CancellationToken cancellationToken)
    {
        FnOsTaskRecord record = runtime.Record;
        var reportResult = result with
        {
            FailedFiles = record.FailedFiles,
            Warnings = record.Warnings,
            CopiedBytes = record.CopiedBytes,
            CopiedFiles = record.CopiedFiles,
            VerifiedBytes = record.VerifiedBytes,
            VerifiedFileCount = record.VerifiedFiles,
            VerifiedFiles = result.VerifiedFiles
                .Concat(runtime.RetryVerificationResults)
                .ToArray()
        };
        string report = TaskReportBuilder.Build(reportResult, ToHistoryItem(record));
        string path = await _reports.SaveReportAsync(record.Id, report, cancellationToken);
        record.ReportFileName = Path.GetFileName(path);
    }

    private async Task SaveIncompleteReportAsync(FnOsTaskRecord record)
    {
        try
        {
            string report = TaskReportBuilder.BuildIncomplete(ToHistoryItem(record));
            string path = await _reports.SaveReportAsync(record.Id, report);
            record.ReportFileName = Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task NotifyTerminalAsync(FnOsTaskRecord record)
    {
        try
        {
            FnOsSettingsDocument settings = await _settings.LoadAsync(CancellationToken.None);
            ResourceService.SetLanguage(settings.Language);
            NotificationBatchResult result = await _notifications.NotifyJobAsync(
                settings.Notifications,
                ToHistoryItem(record),
                CancellationToken.None);
            if (result.FailureCount > 0)
            {
                record.Warnings.Add("One or more notification channels failed.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Warnings.Add("Task notification delivery could not be started.");
        }
    }

    private static JobHistoryItem ToHistoryItem(FnOsTaskRecord record) => new()
    {
        Id = record.Id,
        DisplayName = record.DisplayName,
        SourcePath = record.Request.SourcePath,
        DestinationPath = record.Request.DestinationPath,
        StartedAt = record.StartedAt ?? record.CreatedAt,
        FinishedAt = record.FinishedAt,
        TotalBytes = record.TotalBytes,
        FileCount = record.FileCount,
        CopiedBytes = record.CopiedBytes,
        CopiedFiles = record.CopiedFiles,
        VerifiedFiles = record.VerifiedFiles,
        CopySeconds = record.CopySeconds,
        VerifySeconds = record.VerifySeconds,
        CopyEnabled = record.Request.Mode is not FnOsTaskMode.VerifyOnly,
        VerificationEnabled = record.Request.Mode is not FnOsTaskMode.CopyOnly,
        VerificationAlgorithm = record.Request.VerificationAlgorithm,
        VerificationExecutionMode = record.Request.VerificationExecutionMode,
        UseFastCopyAlgorithm = false,
        PreventSleep = false,
        IsPriority = record.Request.IsPriority,
        CopyByteSpeedSamples = record.CopyByteSpeedSamples,
        CopyItemSpeedSamples = record.CopyItemSpeedSamples,
        CopyThroughputProgressSamples = record.CopyThroughputProgressSamples,
        VerifyByteSpeedSamples = record.VerifyByteSpeedSamples,
        VerifyItemSpeedSamples = record.VerifyItemSpeedSamples,
        VerifyThroughputProgressSamples = record.VerifyThroughputProgressSamples,
        FailedFiles = record.FailedFiles,
        DuplicateFiles = record.DuplicateFiles,
        DuplicateDecisions = record.DuplicateDecisions,
        ErrorMessage = record.Errors.FirstOrDefault(),
        Status = record.Status switch
        {
            FnOsTaskStatus.Completed => JobStatus.Completed,
            FnOsTaskStatus.CompletedWithErrors => JobStatus.CompletedWithErrors,
            FnOsTaskStatus.VerificationFailed => JobStatus.VerificationFailed,
            FnOsTaskStatus.Cancelled => JobStatus.Cancelled,
            FnOsTaskStatus.Interrupted => JobStatus.Interrupted,
            FnOsTaskStatus.Failed => JobStatus.Failed,
            _ => JobStatus.Running
        }
    };

    private static CopyOptions CreateOptions(CreateTaskRequest request) => new(
        ExistingFilePolicy: request.ExistingFilePolicy,
        VerifyFiles: request.Mode is not FnOsTaskMode.CopyOnly,
        UseFastCopyAlgorithm: false,
        SkipCopy: request.Mode == FnOsTaskMode.VerifyOnly,
        VerificationAlgorithm: request.VerificationAlgorithm,
        VerificationExecutionMode: request.Mode == FnOsTaskMode.CopyAndVerify
            ? request.VerificationExecutionMode
            : VerificationExecutionMode.AfterCopy);

    private static void ApplyResult(FnOsTaskRecord record, CopyResult result)
    {
        record.TotalBytes = result.TotalBytes;
        record.FileCount = result.FileCount;
        record.CopiedBytes = result.CopiedBytes;
        record.CopiedFiles = result.CopiedFiles;
        record.VerifiedBytes = result.VerifiedBytes;
        record.VerifiedFiles = result.VerifiedFileCount;
        record.CopySeconds = result.CopyDuration.TotalSeconds;
        record.VerifySeconds = result.VerifyDuration.TotalSeconds;
        record.DuplicateFiles = result.DuplicateFiles.ToList();
        record.FailedFiles = result.FailedFiles.ToList();
        record.Errors = result.Errors.ToList();
        record.Warnings = result.Warnings.ToList();
    }

    private static void CompleteRecord(FnOsTaskRecord record)
    {
        record.FinishedAt = DateTimeOffset.UtcNow;
        record.Status = record.FailedFiles.Any(item => item.IsVerificationMismatch)
            ? FnOsTaskStatus.VerificationFailed
            : record.FailedFiles.Count > 0 || record.Errors.Count > 0
                ? FnOsTaskStatus.CompletedWithErrors
                : FnOsTaskStatus.Completed;
    }

    private void EnsureNoPathConflictLocked(CreateTaskRequest request)
    {
        foreach (FnOsTaskRecord record in _records.Where(item => IsActive(item.Status)))
        {
            if (PathSafety.PathsOverlap(request.SourcePath, record.Request.SourcePath) ||
                PathSafety.PathsOverlap(request.SourcePath, record.Request.DestinationPath) ||
                PathSafety.PathsOverlap(request.DestinationPath, record.Request.SourcePath) ||
                PathSafety.PathsOverlap(request.DestinationPath, record.Request.DestinationPath))
            {
                throw new TaskManagerException(
                    "task_conflict",
                    "The requested paths overlap an active task.",
                    new { conflictingTaskId = record.Id });
            }
        }
    }

    private TaskRuntime FindRuntimeLocked(string id) =>
        _runtimes.TryGetValue(id, out TaskRuntime? runtime)
            ? runtime
            : throw new TaskManagerException("task_not_found", "The active task was not found.");

    private FnOsTaskRecord FindRecordLocked(string id) =>
        _records.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal)) ??
        throw new TaskManagerException("task_not_found", "The task was not found.");

    private void TrimHistoryLocked()
    {
        while (_records.Count > 200)
        {
            int index = _records.FindLastIndex(item => !IsActive(item.Status));
            if (index < 0)
            {
                return;
            }
            _records.RemoveAt(index);
        }
    }

    private static bool IsActive(FnOsTaskStatus status) => status is
        FnOsTaskStatus.Queued or
        FnOsTaskStatus.Running or
        FnOsTaskStatus.Paused or
        FnOsTaskStatus.AwaitingDuplicateDecision or
        FnOsTaskStatus.AwaitingFailureDecision;

    private void CaptureProgress(TaskRuntime runtime, CopyProgressInfo info)
    {
        runtime.Record.Progress = new TaskProgressDto(
            info.Phase,
            info.TotalBytes,
            info.ProcessedBytes,
            info.TotalFiles,
            info.ProcessedFiles,
            info.CurrentFile,
            double.IsFinite(info.BytesPerSecond) ? Math.Max(0, info.BytesPerSecond) : 0,
            Math.Max(0, info.Elapsed.TotalSeconds),
            info.IsTotalKnown,
            info.IsPhaseActive);
        runtime.Record.TotalBytes = info.TotalBytes;
        runtime.Record.FileCount = info.TotalFiles;
        runtime.CopyThroughputSampler.TrySample(
            info,
            runtime.Record.CopyByteSpeedSamples,
            runtime.Record.CopyItemSpeedSamples,
            runtime.Record.CopyThroughputProgressSamples);
        runtime.VerifyThroughputSampler.TrySample(
            info,
            runtime.Record.VerifyByteSpeedSamples,
            runtime.Record.VerifyItemSpeedSamples,
            runtime.Record.VerifyThroughputProgressSamples);
        if (info.Phase != CopyPhase.Copying)
        {
            runtime.CopyThroughputSampler.TryAppendIdleSample(
                runtime.Record.CopyByteSpeedSamples,
                runtime.Record.CopyItemSpeedSamples,
                runtime.Record.CopyThroughputProgressSamples);
        }
        if (info.Phase != CopyPhase.Verifying)
        {
            runtime.VerifyThroughputSampler.TryAppendIdleSample(
                runtime.Record.VerifyByteSpeedSamples,
                runtime.Record.VerifyItemSpeedSamples,
                runtime.Record.VerifyThroughputProgressSamples);
        }
        _ = _events.PublishAsync("progress", new
        {
            runtime.Record.Id,
            runtime.Record.Progress,
            runtime.Record.CopyByteSpeedSamples,
            runtime.Record.CopyItemSpeedSamples,
            runtime.Record.CopyThroughputProgressSamples,
            runtime.Record.VerifyByteSpeedSamples,
            runtime.Record.VerifyItemSpeedSamples,
            runtime.Record.VerifyThroughputProgressSamples,
        });
    }

    private static void SetStatus(TaskRuntime runtime, FnOsTaskStatus status)
    {
        runtime.Record.Status = status;
    }

    private async Task PersistAndPublishAsync(
        string eventType,
        FnOsTaskRecord record,
        CancellationToken cancellationToken)
    {
        await PersistAsync(cancellationToken);
        await _events.PublishAsync(eventType, record, cancellationToken);
    }

    private async Task WaitForExecutionOrResumeAsync(
        TaskRuntime runtime,
        CancellationToken cancellationToken)
    {
        await _scheduler.WaitForExecutionAsync(
            runtime.ScheduleRegistration,
            cancellationToken);
        bool transitioned;
        lock (_sync)
        {
            transitioned = runtime.Record.Status == FnOsTaskStatus.Queued;
            if (transitioned)
            {
                runtime.Record.Status = FnOsTaskStatus.Running;
            }
        }
        if (transitioned)
        {
            await PersistAndPublishAsync("taskResumed", runtime.Record, cancellationToken);
        }
    }

    private Task PersistAsync(CancellationToken cancellationToken) =>
        _store.SaveAsync(Snapshot(), cancellationToken);

    private sealed class TaskRuntime(
        FnOsTaskRecord record,
        int userId,
        CopyJobScheduler.CopyJobScheduleRegistration registration)
    {
        public FnOsTaskRecord Record { get; } = record;
        public int UserId { get; } = userId;
        public CopyJobScheduler.CopyJobScheduleRegistration ScheduleRegistration { get; } = registration;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? ExecutionTask { get; set; }
        public TaskCompletionSource<IReadOnlyDictionary<string, ExistingFilePolicy>>?
            DuplicateDecisionSource { get; set; }
        public TaskCompletionSource<FailureActionRequest>? FailureActionSource { get; set; }
        public List<FileVerificationResult> RetryVerificationResults { get; } = [];
        public CopyThroughputSampler CopyThroughputSampler { get; } = new();
        public CopyThroughputSampler VerifyThroughputSampler { get; } = new(
            sampledPhase: CopyPhase.Verifying);
    }
}
