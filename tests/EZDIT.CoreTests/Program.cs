using System.Security.Cryptography;
using EZDIT.Models;
using EZDIT.Services;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("copy and SHA-256 verification", TestCopyAndVerifyAsync),
            ("verification-only mode never copies", TestVerificationOnlyAsync),
            ("verification mismatch can be overwritten", TestOverwriteVerificationMismatchAsync),
            ("FastCopy pipeline copy and verification", TestFastCopyAlgorithmAsync),
            ("pause and resume", TestPauseAndResumeAsync),
            ("cancellation preserves existing destination", TestCancellationSafetyAsync),
            ("corruption is detected", TestCorruptionDetectionAsync),
            ("file failure continues and can retry", TestFileFailureRecoveryAsync),
            ("empty source completes", TestEmptySourceAsync),
            ("existing file can be skipped", TestSkipExistingAsync),
            ("existing file can create a copy", TestCreateCopyAsync),
            ("verification can be disabled", TestVerificationDisabledAsync),
            ("ask mode supports per-file decisions", TestAskPerFileDecisionsAsync),
            ("local history persistence", TestHistoryPersistenceAsync),
            ("priority jobs gate ordinary jobs", TestPrioritySchedulerAsync)
        };

        foreach (var test in tests)
        {
            await test.Run();
            Console.WriteLine($"PASS: {test.Name}");
        }

        Console.WriteLine($"All {tests.Length} core tests passed.");
        return 0;
    }

    private static async Task TestCopyAndVerifyAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            Directory.CreateDirectory(Path.Combine(source, "DCIM", "100MEDIA"));
            Directory.CreateDirectory(Path.Combine(source, "EMPTY_FOLDER"));
            await File.WriteAllTextAsync(Path.Combine(source, "notes.txt"), "EZ DIT test - 你好");
            await File.WriteAllBytesAsync(Path.Combine(source, "empty.bin"), []);
            byte[] media = RandomNumberGenerator.GetBytes(6 * 1024 * 1024 + 137);
            string sourceMedia = Path.Combine(source, "DCIM", "100MEDIA", "clip.bin");
            await File.WriteAllBytesAsync(sourceMedia, media);
            DateTime expectedWriteTime = DateTime.UtcNow.AddDays(-2);
            File.SetLastWriteTimeUtc(sourceMedia, expectedWriteTime);

            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask, CancellationToken.None);

            Assert(result.Success, "A normal copy should pass verification.");
            Assert(result.FileCount == 3, "All source files should be counted.");
            Assert(events.Any(item => item.Phase == CopyPhase.Copying && item.ProcessedFiles == 3),
                "The final copied-file count must be reported.");
            Assert(events.Any(item => item.Phase == CopyPhase.Verifying), "Verification progress is missing.");
            Assert(events.Last().Phase == CopyPhase.Completed, "The final phase should be Completed.");

            foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, sourceFile);
                string destinationFile = Path.Combine(destination, relative);
                Assert(File.Exists(destinationFile), $"Missing destination file: {relative}");
                Assert(await HashAsync(sourceFile) == await HashAsync(destinationFile),
                    $"Hash mismatch for {relative}");
            }

            Assert(Directory.Exists(Path.Combine(destination, "EMPTY_FOLDER")),
                "Empty source directories should be preserved.");
            TimeSpan writeTimeDifference = File.GetLastWriteTimeUtc(Path.Combine(destination, "DCIM", "100MEDIA", "clip.bin")) - expectedWriteTime;
            Assert(Math.Abs(writeTimeDifference.TotalSeconds) < 2, "Last-write time should be preserved.");
            Assert(!Directory.EnumerateFiles(destination, "*.ezdit-partial", SearchOption.AllDirectories).Any(),
                "No partial files should remain after success.");
        });
    }

    private static async Task TestFastCopyAlgorithmAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            Directory.CreateDirectory(Path.Combine(source, "DCIM"));
            await File.WriteAllBytesAsync(
                Path.Combine(source, "DCIM", "large-clip.bin"),
                RandomNumberGenerator.GetBytes(18 * 1024 * 1024 + 137));
            await File.WriteAllTextAsync(Path.Combine(source, "metadata.txt"), "FastCopy pipeline");

            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination,
                new CopyOptions(ExistingFilePolicy.Overwrite, true, true),
                new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask, CancellationToken.None);

            Assert(result.Success && result.VerificationPerformed,
                "The FastCopy pipeline should copy and verify successfully.");
            Assert(result.FileCount == 2 && result.VerifiedFiles.Count == 2,
                "Every file should be copied and verified by the FastCopy pipeline.");
            Assert(events.Last().Phase == CopyPhase.Completed,
                "The FastCopy pipeline should report completion.");
            foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, sourceFile);
                string destinationFile = Path.Combine(destination, relative);
                Assert(await HashAsync(sourceFile) == await HashAsync(destinationFile),
                    $"FastCopy pipeline hash mismatch for {relative}");
            }
            Assert(!Directory.EnumerateFiles(destination, "*.ezdit-partial", SearchOption.AllDirectories).Any(),
                "The FastCopy pipeline must not leave partial files after success.");
        });
    }

    private static async Task TestVerificationOnlyAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "same.txt"), "same");
            await File.WriteAllTextAsync(Path.Combine(destination, "same.txt"), "same");
            await File.WriteAllTextAsync(Path.Combine(source, "different.txt"), "new source content");
            await File.WriteAllTextAsync(Path.Combine(destination, "different.txt"), "keep destination content");
            await File.WriteAllTextAsync(Path.Combine(source, "missing.txt"), "must not be copied");
            Directory.CreateDirectory(Path.Combine(source, "empty-source-folder"));

            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source,
                destination,
                new CopyOptions(
                    ExistingFilePolicy: ExistingFilePolicy.Overwrite,
                    VerifyFiles: true,
                    SkipCopy: true),
                new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(!result.Success, "Mismatched and missing destination files must fail verification.");
            Assert(result.VerificationPerformed, "Verification-only mode must always perform verification.");
            Assert(!events.Any(item => item.Phase == CopyPhase.Copying),
                "Verification-only mode must never enter the copying phase.");
            Assert(events.Any(item => item.Phase == CopyPhase.Verifying),
                "Verification-only mode must report verification progress.");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "different.txt")) == "keep destination content",
                "Verification-only mode must not overwrite destination files.");
            Assert(!File.Exists(Path.Combine(destination, "missing.txt")),
                "Verification-only mode must not copy missing destination files.");
            Assert(!Directory.Exists(Path.Combine(destination, "empty-source-folder")),
                "Verification-only mode must not create destination directories.");
            Assert(result.FailedFiles.All(item => item.Stage == FileOperationStage.Verifying),
                "Verification-only failures must be reported as verification failures.");
        });
    }

    private static async Task TestOverwriteVerificationMismatchAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "mismatch.txt");
            string destinationFile = Path.Combine(destination, "mismatch.txt");
            await File.WriteAllTextAsync(sourceFile, "authoritative source");
            await File.WriteAllTextAsync(destinationFile, "stale destination");

            var service = new FileCopyService();
            CopyOptions options = new(
                ExistingFilePolicy.Overwrite,
                VerifyFiles: true,
                SkipCopy: true);
            CopyResult result = await service.CopyAndVerifyAsync(
                source,
                destination,
                options,
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(result.FailedFiles.Count == 1 &&
                   result.FailedFiles[0].IsVerificationMismatch,
                "A hash mismatch should be eligible for overwrite.");

            var overwriteEvents = new List<CopyProgressInfo>();
            FileRetryResult overwrite = await service.OverwriteVerificationMismatchesAsync(
                result.FailedFiles,
                options,
                new InlineProgress<CopyProgressInfo>(overwriteEvents.Add),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(overwrite.FailedFiles.Count == 0,
                "Overwrite should clear a verification mismatch after copying the source file.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "authoritative source",
                "Overwrite should replace the destination with the source file.");
            Assert(overwriteEvents.Any(item => item.Phase == CopyPhase.Copying),
                "Overwrite should report copy progress even for a verification-only task.");
        });
    }

    private static async Task TestPauseAndResumeAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllBytesAsync(Path.Combine(source, "paused.bin"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
            var enteredPause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;

            async Task PauseOnSecondCheckpoint(CancellationToken token)
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    enteredPause.TrySetResult(true);
                    await resume.Task.WaitAsync(token);
                }
            }

            Task<CopyResult> task = new FileCopyService().CopyAndVerifyAsync(
                source, destination, new InlineProgress<CopyProgressInfo>(_ => { }),
                PauseOnSecondCheckpoint, CancellationToken.None);

            await enteredPause.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert(!task.IsCompleted, "The copy must remain blocked while paused.");
            resume.TrySetResult(true);
            CopyResult result = await task;
            Assert(result.Success, "The copy should finish after resume.");
        });
    }

    private static async Task TestCancellationSafetyAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "video.bin");
            string destinationFile = Path.Combine(destination, "video.bin");
            await File.WriteAllBytesAsync(sourceFile, RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
            await File.WriteAllTextAsync(destinationFile, "keep-existing-file");

            var paused = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            async Task PauseUntilCancelled(CancellationToken token)
            {
                if (Interlocked.Increment(ref calls) == 2)
                {
                    paused.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }

            using var cancellation = new CancellationTokenSource();
            Task<CopyResult> task = new FileCopyService().CopyAndVerifyAsync(
                source, destination,
                new CopyOptions(ExistingFilePolicy.Overwrite, true, true),
                new InlineProgress<CopyProgressInfo>(_ => { }),
                PauseUntilCancelled, cancellation.Token);
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            bool cancelled = false;
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert(cancelled, "Cancellation should propagate to the caller.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "keep-existing-file",
                "Cancellation must not overwrite an existing completed file.");
            Assert(!File.Exists(destinationFile + ".ezdit-partial"),
                "The partial file must be cleaned up after cancellation.");
        });
    }

    private static async Task TestCorruptionDetectionAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "manifest.txt"), "original data");
            bool corrupted = false;
            var progress = new InlineProgress<CopyProgressInfo>(info =>
            {
                if (!corrupted && info.Phase == CopyPhase.Copying && info.ProcessedFiles == info.TotalFiles)
                {
                    File.AppendAllText(Path.Combine(destination, "manifest.txt"), "tampered");
                    corrupted = true;
                }
            });

            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, progress, _ => Task.CompletedTask, CancellationToken.None);

            Assert(corrupted, "The test must alter the copied file before verification.");
            Assert(!result.Success, "A corrupted destination must fail verification.");
            Assert(result.Errors.Count == 1, "The mismatched file must be listed once.");
        });
    }

    private static async Task TestEmptySourceAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask, CancellationToken.None);
            Assert(result.Success && result.FileCount == 0 && result.TotalBytes == 0,
                "An empty card should complete successfully.");
            Assert(events.Last().Phase == CopyPhase.Completed, "An empty copy should report completion.");
        });
    }

    private static async Task TestSkipExistingAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "clip.txt");
            string destinationFile = Path.Combine(destination, "clip.txt");
            await File.WriteAllTextAsync(sourceFile, "new card data");
            await File.WriteAllTextAsync(destinationFile, "existing archive data");
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new CopyOptions(ExistingFilePolicy.Skip, false),
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask, CancellationToken.None);
            Assert(result.Success && !result.VerificationPerformed,
                "Skipping an existing file should complete without verification.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "existing archive data",
                "Skip must preserve the existing destination file.");
        });
    }

    private static async Task TestCreateCopyAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            string sourceFile = Path.Combine(source, "clip.mov");
            string destinationFile = Path.Combine(destination, "clip.mov");
            string duplicateFile = Path.Combine(destination, "clip (1).mov");
            await File.WriteAllTextAsync(sourceFile, "new card data");
            await File.WriteAllTextAsync(destinationFile, "existing archive data");
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new CopyOptions(ExistingFilePolicy.CreateCopy, true),
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask, CancellationToken.None);
            Assert(result.Success && result.VerificationPerformed,
                "The created copy should pass SHA-256 verification.");
            Assert(await File.ReadAllTextAsync(destinationFile) == "existing archive data",
                "Create-copy must preserve the original destination file.");
            Assert(File.Exists(duplicateFile), "Create-copy should use the '(1)' suffix.");
            Assert(await HashAsync(sourceFile) == await HashAsync(duplicateFile),
                "The created copy must match the source.");
        });
    }

    private static async Task TestVerificationDisabledAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "fast-copy.txt"), "copy only");
            var events = new List<CopyProgressInfo>();
            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new CopyOptions(ExistingFilePolicy.Overwrite, false),
                new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask, CancellationToken.None);
            Assert(result.Success && !result.VerificationPerformed,
                "A copy-only task should succeed without verification.");
            Assert(result.VerifiedFiles.Count == 0, "No verification results should be created.");
            Assert(!events.Any(item => item.Phase == CopyPhase.Verifying),
                "The verifying phase must not run when disabled.");
            Assert(events.Last().Phase == CopyPhase.Completed,
                "A copy-only task should still report completion.");
        });
    }
    private static async Task TestAskPerFileDecisionsAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "overwrite.txt"), "new overwrite data");
            await File.WriteAllTextAsync(Path.Combine(source, "skip.txt"), "same skip data");
            await File.WriteAllTextAsync(Path.Combine(source, "copy.txt"), "new copy data");
            await File.WriteAllTextAsync(Path.Combine(source, "fresh.txt"), "fresh data");
            await File.WriteAllTextAsync(Path.Combine(destination, "overwrite.txt"), "old overwrite data");
            await File.WriteAllTextAsync(Path.Combine(destination, "skip.txt"), "same skip data");
            await File.WriteAllTextAsync(Path.Combine(destination, "copy.txt"), "old copy data");
            DateTime skipWriteTime = DateTime.UtcNow.AddDays(-4);
            File.SetLastWriteTimeUtc(Path.Combine(destination, "skip.txt"), skipWriteTime);

            var events = new List<CopyProgressInfo>();
            var conflicts = new List<DuplicateFileConflict>();
            bool resolverObservedVerifiedFreshFile = false;
            async Task<IReadOnlyDictionary<string, ExistingFilePolicy>> ResolveAsync(
                IReadOnlyList<DuplicateFileConflict> pending,
                CancellationToken token)
            {
                resolverObservedVerifiedFreshFile = events.Any(item => item.Phase == CopyPhase.Verifying) &&
                    File.Exists(Path.Combine(destination, "fresh.txt"));
                await Task.Yield();
                return new Dictionary<string, ExistingFilePolicy>(StringComparer.OrdinalIgnoreCase)
                {
                    ["overwrite.txt"] = ExistingFilePolicy.Overwrite,
                    ["skip.txt"] = ExistingFilePolicy.Skip,
                    ["copy.txt"] = ExistingFilePolicy.CreateCopy
                };
            }

            CopyResult result = await new FileCopyService().CopyAndVerifyAsync(
                source, destination, new CopyOptions(ExistingFilePolicy.Ask, true),
                new InlineProgress<CopyProgressInfo>(events.Add),
                new InlineProgress<DuplicateFileConflict>(conflicts.Add),
                ResolveAsync, _ => Task.CompletedTask, CancellationToken.None);

            Assert(result.Success, "Per-file duplicate decisions should complete and verify.");
            Assert(conflicts.Count == 3, "Every duplicate file should be reported once.");
            Assert(resolverObservedVerifiedFreshFile,
                "Non-conflicting files should copy and verify before duplicate choices are applied.");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "overwrite.txt")) == "new overwrite data",
                "The overwrite decision must replace only its selected file.");
            Assert(Math.Abs((File.GetLastWriteTimeUtc(Path.Combine(destination, "skip.txt")) - skipWriteTime).TotalSeconds) < 2,
                "The skip decision must leave its selected file untouched.");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "copy.txt")) == "old copy data",
                "Create-copy must preserve the conflicting original.");
            Assert(await File.ReadAllTextAsync(Path.Combine(destination, "copy (1).txt")) == "new copy data",
                "Create-copy must write the selected duplicate to a suffixed file.");
            Assert(events.Any(item => item.Phase == CopyPhase.WaitingForDuplicateDecision),
                "Ask mode should report that it is waiting for per-file choices.");
        });
    }

    private static async Task TestFileFailureRecoveryAsync()
    {
        await WithTempFoldersAsync(async (source, destination) =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "good.txt"), "good data");
            await File.WriteAllTextAsync(Path.Combine(source, "blocked.txt"), "retry data");
            string blockedDestination = Path.Combine(destination, "blocked.txt");
            Directory.CreateDirectory(blockedDestination);

            var events = new List<CopyProgressInfo>();
            var service = new FileCopyService();
            CopyOptions options = new(ExistingFilePolicy.Overwrite, false, false);
            CopyResult result = await service.CopyAndVerifyAsync(
                source,
                destination,
                options,
                new InlineProgress<CopyProgressInfo>(events.Add),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(!result.Success, "A blocked destination should be reported as a file failure.");
            Assert(result.FailedFiles.Count == 1 && result.FailedFiles[0].RelativePath == "blocked.txt",
                "The blocked file should be returned as a structured failure.");
            Assert(File.Exists(Path.Combine(destination, "good.txt")),
                "Files after an individual failure must continue copying.");
            Assert(events.Last().Phase == CopyPhase.Completed,
                "The overall pass should reach completion while failures await a decision.");

            Directory.Delete(blockedDestination, true);
            FileRetryResult retry = await service.RetryFailedFilesAsync(
                result.FailedFiles,
                options,
                new InlineProgress<CopyProgressInfo>(_ => { }),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert(retry.FailedFiles.Count == 0, "The failed file should succeed after its blocker is removed.");
            Assert(await File.ReadAllTextAsync(blockedDestination) == "retry data",
                "Retry should copy only the previously failed file to its intended destination.");
        });
    }

    private static async Task TestHistoryPersistenceAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "EZDIT-HistoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new JobHistoryService(root);
            var item = new JobHistoryItem
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = "Card A",
                SourcePath = @"F:\",
                DestinationPath = @"D:\Media\CardA",
                StartedAt = new DateTimeOffset(2026, 7, 21, 10, 30, 0, TimeSpan.FromHours(8)),
                FinishedAt = new DateTimeOffset(2026, 7, 21, 10, 45, 0, TimeSpan.FromHours(8)),
                TotalBytes = 123456789,
                FileCount = 42,
                CopiedBytes = 0,
                CopiedFiles = 0,
                VerifiedFiles = 42,
                CopySeconds = 0,
                VerifySeconds = 300,
                Status = JobStatus.Completed,
                CopyEnabled = false,
                VerificationEnabled = true,
                UseFastCopyAlgorithm = true,
                IsPriority = true,
                PreventSleep = false,
                IsAcknowledged = false,
                DuplicateFiles = [new DuplicateFileConflict("clip.mov", @"F:\clip.mov", @"D:\Media\CardA\clip.mov", 1024)],
                DuplicateDecisions = new Dictionary<string, ExistingFilePolicy>(StringComparer.OrdinalIgnoreCase)
                {
                    ["clip.mov"] = ExistingFilePolicy.CreateCopy
                }
            };

            await service.SaveAsync([item]);
            List<JobHistoryItem> loaded = await service.LoadAsync();
            Assert(loaded.Count == 1, "Exactly one history item should be restored.");
            Assert(loaded[0].Id == item.Id && loaded[0].TotalBytes == item.TotalBytes,
                "Persisted history details should round-trip.");
            Assert(loaded[0].Status == JobStatus.Completed,
                "The job status should round-trip as an enum value.");
            Assert(loaded[0].VerificationEnabled,
                "The verification setting should round-trip.");
            Assert(!loaded[0].CopyEnabled,
                "The copy setting should round-trip.");
            Assert(loaded[0].StatusText == "校验完成" &&
                   !loaded[0].CanStartVerification &&
                   loaded[0].CanExportReport,
                "A verification-only job should not offer starting verification again.");
            var copyOnly = new JobHistoryItem
                {
                    Status = JobStatus.Completed,
                    CopyEnabled = true,
                    VerificationEnabled = false
                };
            Assert(copyOnly.StatusText == "拷贝完成" &&
                   copyOnly.CanStartVerification &&
                   copyOnly.CanExportReport,
                "A copy-only job should offer starting verification.");
            Assert(new JobHistoryItem
                {
                    Status = JobStatus.Completed,
                    CopyEnabled = true,
                    VerificationEnabled = true
                }.StatusText == "任务完成",
                "A copy-and-verification job should be labeled as task completed.");
            Assert(!new JobHistoryItem
                {
                    Status = JobStatus.Running,
                    CopyEnabled = true,
                    VerificationEnabled = false
                }.CanExportReport,
                "A running job should not offer report export.");
            JobStatus[] restartableStatuses =
            [
                JobStatus.CompletedWithErrors,
                JobStatus.VerificationFailed,
                JobStatus.Failed,
                JobStatus.Cancelled,
                JobStatus.Interrupted
            ];
            Assert(restartableStatuses.All(status => new JobHistoryItem { Status = status }.CanRestart),
                "Every unsuccessful terminal state should offer restarting.");
            Assert(!new JobHistoryItem { Status = JobStatus.Completed }.CanRestart &&
                   !new JobHistoryItem { Status = JobStatus.Running }.CanRestart,
                "Completed and running jobs should not offer restarting.");
            Assert(loaded[0].UseFastCopyAlgorithm,
                "The FastCopy algorithm setting should round-trip.");
            Assert(loaded[0].IsPriority,
                "The priority setting should round-trip.");
            Assert(!loaded[0].PreventSleep,
                "The sleep-prevention setting should round-trip.");
            Assert(loaded[0].DuplicateFiles.Count == 1 &&
                loaded[0].DuplicateDecisions["clip.mov"] == ExistingFilePolicy.CreateCopy,
                "Per-file duplicate decisions should round-trip.");
            Assert(!loaded[0].IsAcknowledged,
                "The acknowledgement state should round-trip.");
            Assert(loaded[0].NeedsAttention,
                "An unacknowledged completed job should request attention.");
            Assert(loaded[0].MetaText.Contains("117.74 MB"),
                "The history card should expose a formatted size.");

            string reportFile = await service.SaveReportAsync(item.Id, "report-body");
            Assert(await service.ReadReportAsync(reportFile) == "report-body",
                "A history report should be readable after restart.");
            await service.DeleteReportAsync(reportFile);
            Assert(await service.ReadReportAsync(reportFile) is null,
                "Deleting a history report should remove only that report.");
            Assert(!File.Exists(Path.Combine(root, "history.json.tmp")),
                "Atomic history writes should not leave a temporary file.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task TestPrioritySchedulerAsync()
    {
        var scheduler = new CopyJobScheduler();
        using CopyJobScheduler.CopyJobScheduleRegistration firstOrdinary = scheduler.Register(false);
        using CopyJobScheduler.CopyJobScheduleRegistration secondOrdinary = scheduler.Register(false);

        await Task.WhenAll(
            scheduler.WaitForTurnAsync(false),
            scheduler.WaitForTurnAsync(false)).WaitAsync(TimeSpan.FromSeconds(1));

        CopyJobScheduler.CopyJobScheduleRegistration firstPriority = scheduler.Register(true);
        CopyJobScheduler.CopyJobScheduleRegistration secondPriority = scheduler.Register(true);
        await Task.WhenAll(
            scheduler.WaitForTurnAsync(true),
            scheduler.WaitForTurnAsync(true)).WaitAsync(TimeSpan.FromSeconds(1));

        Task waitingOrdinary = scheduler.WaitForTurnAsync(false);
        await Task.Delay(50);
        Assert(!waitingOrdinary.IsCompleted,
            "An ordinary job must wait while priority jobs are active.");

        firstPriority.Dispose();
        await Task.Delay(50);
        Assert(!waitingOrdinary.IsCompleted,
            "An ordinary job must wait until every priority job finishes.");

        secondPriority.Dispose();
        await waitingOrdinary.WaitAsync(TimeSpan.FromSeconds(1));
        Assert(!scheduler.HasActivePriorityJobs,
            "The priority gate should reopen after the last priority job finishes.");

        using CopyJobScheduler.CopyJobScheduleRegistration laterOrdinary = scheduler.Register(false);
        await scheduler.WaitForTurnAsync(false).WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task WithTempFoldersAsync(Func<string, string, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "EZDIT-CoreTests", Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        try
        {
            await test(source, destination);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<string> HashAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
