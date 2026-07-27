using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using EZDIT.Models;
using EZDIT.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EZDIT;

public sealed partial class MainWindow : Window
{
    private readonly FileCopyService _copyService = new();
    private readonly JobHistoryService _historyService = new();
    private readonly ObservableCollection<JobHistoryItem> _history = [];
    private readonly ObservableCollection<DuplicateConflictChoice> _duplicateChoices = [];
    private readonly ObservableCollection<FailedFileChoice> _failedFileChoices = [];
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<IReadOnlyDictionary<string, ExistingFilePolicy>>? _duplicateDecisionSource;
    private string? _sourcePath;
    private string? _destinationPath;
    private string? _destinationParentPath;
    private string? _dialogSourcePath;
    private string? _dialogDestinationParentPath;
    private CopyOptions _copyOptions = new();
    private bool _isRunning;
    private bool _historyLoaded;
    private bool _isMultiSelectMode;
    private bool _isChangingMultiSelectMode;
    private bool _updatingDuplicateSelection;
    private volatile bool _isPaused;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private CopyResult? _lastResult;
    private CopyProgressInfo? _lastProgress;
    private JobHistoryItem? _activeJob;
    private JobHistoryItem? _selectedJob;
    private string _lastReport = string.Empty;
    private long _copiedBytes;
    private int _copiedFiles;
    private int _verifiedFiles;
    private long _verifiedBytes;
    private TimeSpan _copyElapsed;
    private TimeSpan _verifyElapsed;

    public MainWindow()
    {
        InitializeComponent();
        NewJobsList.ItemsSource = _newJobs;
        HistoryList.ItemsSource = _visibleHistory;
        DuplicateList.ItemsSource = _duplicateChoices;
        FailedFilesList.ItemsSource = _failedFileChoices;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureWindow();
        Closed += ConcurrentMainWindow_Closed;
    }

    private void ConfigureWindow()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        }
        catch
        {
            // Older Windows builds gracefully fall back to the solid background.
        }

        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(1266, 792));
        appWindow.Title = "EZ DIT-beta";
        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_historyLoaded)
        {
            return;
        }
        _historyLoaded = true;
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        List<JobHistoryItem> items = await _historyService.LoadAsync();
        bool repairedInterruptedJobs = false;
        foreach (JobHistoryItem item in items.OrderByDescending(item => item.StartedAt).Take(200))
        {
            if (item.Status is JobStatus.Running or JobStatus.Queued)
            {
                item.Status = JobStatus.Interrupted;
                item.IsAcknowledged = false;
                item.FinishedAt ??= DateTimeOffset.Now;
                item.ErrorMessage = "应用在任务完成前退出。";
                repairedInterruptedJobs = true;
            }
            _history.Add(item);
        }

        RebuildTaskSections();
        UpdateHistoryEmptyState();
        if (repairedInterruptedJobs)
        {
            await SaveHistorySafeAsync();
        }

        SelectInitialTask();
    }

    private async void ChooseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync("选择源目录或存储卡");
        if (path is null)
        {
            return;
        }

        if (_selectedJob is not null)
        {
            PrepareNewJobView();
        }
        _sourcePath = path;
        SourcePathText.Text = path;
        HeroNameText.Text = GetDisplayName(path);
        CurrentFileText.Text = "源目录已就绪，请选择拷卡目的地";
        LogText.Text = $"已选择源目录：{path}";
        UpdateStartButton();
    }

    private async void ChooseDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync("选择拷卡目的地");
        if (path is null)
        {
            return;
        }

        if (_selectedJob is not null)
        {
            PrepareNewJobView();
        }
        _destinationParentPath = path;
        _destinationPath = path;
        DestinationPathText.Text = path;
        CurrentFileText.Text = "目录设置完成";
        LogText.Text = $"已选择目标目录：{path}";
        UpdateStartButton();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = "选择此目录"
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfigureNewTaskAsync())
        {
            await StartCopyAsync();
        }
    }

    private async void DialogSourceButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync("选择源目录或存储卡");
        if (path is null)
        {
            return;
        }

        _dialogSourcePath = path;
        DialogSourcePathText.Text = path;

        if (EnableCopyToggle.IsOn)
        {
            // 自动填入子文件夹名：源目录名 + 时间戳
            string dirName = new DirectoryInfo(path).Name;
            DialogDestinationSubfolderName.Text = dirName + DateTime.Now.ToString("yyyyMMddHHmmss");
        }
    }


    private async void DialogDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync("选择拷卡目的地");
        if (path is null)
        {
            return;
        }

        _dialogDestinationParentPath = path;
        DialogDestinationPathText.Text = path;
    }

    private async Task<bool> ConfigureNewTaskAsync()
    {
        _dialogSourcePath = _sourcePath;
        _dialogDestinationParentPath = _destinationParentPath;
        DialogSourcePathText.Text = _dialogSourcePath ?? "请选择源目录或存储卡";
        DialogDestinationPathText.Text = _dialogDestinationParentPath ?? "请选择文件拷贝目的地";

        // 默认子文件夹名称：源目录名 + 时间戳
        if (_dialogSourcePath is not null)
        {
            string sourceDirName = new DirectoryInfo(_dialogSourcePath).Name;
            DialogDestinationSubfolderName.Text = sourceDirName + DateTime.Now.ToString("yyyyMMddHHmmss");
        }
        else
        {
            DialogDestinationSubfolderName.Text = "";
        }

        EnableCopyToggle.Toggled += OnEnableCopyToggled;
        OnEnableCopyToggled(EnableCopyToggle, null!);

        while (await NewTaskDialog.ShowAsync() == ContentDialogResult.Primary)
        {
            bool copyEnabled = EnableCopyToggle.IsOn;
            AskExistingRadio.IsEnabled = copyEnabled;
            OverwriteExistingRadio.IsEnabled = copyEnabled;
            SkipExistingRadio.IsEnabled = copyEnabled;
            CreateCopyRadio.IsEnabled = copyEnabled;

            if (_dialogSourcePath is null || _dialogDestinationParentPath is null)
            {
                await ShowMessageAsync("目录尚未设置", "请选择数据源和拷贝目的地。");
                continue;
            }

            string destination = _dialogDestinationParentPath;
            string subfolderName = (DialogDestinationSubfolderName.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(subfolderName))
            {
                destination = Path.Combine(destination, subfolderName);
            }
            if (!ValidatePaths(_dialogSourcePath, destination, out string validationMessage))
            {
                await ShowMessageAsync("无法开始", validationMessage);
                continue;
            }

            _sourcePath = _dialogSourcePath;
            _destinationParentPath = _dialogDestinationParentPath;
            _destinationPath = destination;
            ExistingFilePolicy duplicatePolicy = AskExistingRadio.IsChecked == true
                ? ExistingFilePolicy.Ask
                : OverwriteExistingRadio.IsChecked == true
                    ? ExistingFilePolicy.Overwrite
                    : SkipExistingRadio.IsChecked == true
                        ? ExistingFilePolicy.Skip
                        : ExistingFilePolicy.CreateCopy;
            bool verifyOnly = !EnableCopyToggle.IsOn;
            _copyOptions = new CopyOptions(
                ExistingFilePolicy: duplicatePolicy,
                VerifyFiles: verifyOnly || VerifyFilesToggle.IsOn,
                UseFastCopyAlgorithm: false,
                SkipCopy: verifyOnly);
            SourcePathText.Text = _sourcePath;
            DestinationPathText.Text = _destinationPath;
            HeroNameText.Text = GetDisplayName(_sourcePath);
            CurrentFileText.Text = "任务设置完成，准备扫描文件";
            LogText.Text = $"源目录：{_sourcePath}\n目标目录：{_destinationPath}";
            UpdateStartButton();
            EnableCopyToggle.Toggled -= OnEnableCopyToggled;
            return true;
        }

        EnableCopyToggle.Toggled -= OnEnableCopyToggled;
        return false;
    }
    private async Task StartCopyAsync()
    {
        if (_isRunning)
        {
            return;
        }

        if (_sourcePath is null || _destinationPath is null)
        {
            await ShowMessageAsync("目录尚未设置", "请先选择源目录和拷卡目的地。");
            return;
        }

        if (!ValidatePaths(_sourcePath, _destinationPath, out string validationMessage))
        {
            await ShowMessageAsync("无法开始", validationMessage);
            return;
        }

        ResetProgress();
        _isRunning = true;
        _isPaused = false;
        _cancellation = new CancellationTokenSource();
        _startedAt = DateTimeOffset.Now;
        _finishedAt = null;
        _lastResult = null;
        _lastProgress = null;
        _lastReport = string.Empty;
        _copiedBytes = 0;
        _copiedFiles = 0;
        _verifiedFiles = 0;
        _verifiedBytes = 0;
        _copyElapsed = TimeSpan.Zero;
        _verifyElapsed = TimeSpan.Zero;

        _activeJob = new JobHistoryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = GetDisplayName(_sourcePath),
            SourcePath = _sourcePath,
            DestinationPath = _destinationPath,
            StartedAt = _startedAt.Value,
            Status = JobStatus.Running,
            CopyEnabled = !_copyOptions.SkipCopy,
            VerificationEnabled = _copyOptions.VerifyFiles,
            UseFastCopyAlgorithm = _copyOptions.UseFastCopyAlgorithm
        };
        _selectedJob = _activeJob;
        _history.Insert(0, _activeJob);
        TrimHistory();
        UpdateHistoryEmptyState();
        HistoryList.SelectedItem = _activeJob;
        await SaveHistorySafeAsync();

        StartTimeText.Text = _startedAt.Value.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        StatusText.Text = "正在扫描";
        StatusText.Foreground = (SolidColorBrush)Application.Current.Resources["MutedTextBrush"];
        PhaseText.Text = "正在统计文件…";
        LogText.Text = "正在扫描源目录并计算任务大小。";
        SetRunningUi(true);

        try
        {
            var progress = new Progress<CopyProgressInfo>(UpdateProgress);
            var duplicateProgress = new Progress<DuplicateFileConflict>(RecordDuplicateConflict);
            CopyResult result = await _copyService.CopyAndVerifyAsync(
                _sourcePath, _destinationPath, _copyOptions, progress, duplicateProgress,
                WaitForDuplicateChoicesAsync, WaitWhilePausedAsync, _cancellation.Token);

            _lastResult = result;
            _finishedAt = DateTimeOffset.Now;
            JobStatus outcome = result.Success ? JobStatus.Completed : JobStatus.VerificationFailed;
            await FinalizeActiveJobAsync(outcome, result, result.Errors.FirstOrDefault());
        }
        catch (OperationCanceledException)
        {
            _finishedAt = DateTimeOffset.Now;
            await FinalizeActiveJobAsync(JobStatus.Cancelled, null, "任务已由用户取消，已完成的文件予以保留。");
        }
        catch (Exception ex)
        {
            _finishedAt = DateTimeOffset.Now;
            await FinalizeActiveJobAsync(JobStatus.Failed, null, ex.Message);
            await ShowMessageAsync("拷卡失败", ex.Message);
        }
        finally
        {
            _isRunning = false;
            _isPaused = false;
            _cancellation?.Dispose();
            _cancellation = null;
            SetRunningUi(false);
            if (_activeJob is not null)
            {
                _selectedJob = _activeJob;
                HistoryList.SelectedItem = _activeJob;
                ShowHistoryJob(_activeJob);
            }
        }
    }

    private async Task FinalizeActiveJobAsync(JobStatus status, CopyResult? result, string? error)
    {
        if (_activeJob is null)
        {
            return;
        }

        JobHistoryItem job = _activeJob;
        job.Status = status;
        job.FinishedAt = _finishedAt ?? DateTimeOffset.Now;
        job.TotalBytes = result?.TotalBytes ?? _lastProgress?.TotalBytes ?? job.TotalBytes;
        job.FileCount = result?.FileCount ?? _lastProgress?.TotalFiles ?? job.FileCount;
        job.CopiedBytes = result is not null && !_copyOptions.SkipCopy ? result.TotalBytes : _copiedBytes;
        job.CopiedFiles = result is not null && !_copyOptions.SkipCopy ? result.FileCount : _copiedFiles;
        job.VerifiedFiles = result?.VerifiedFiles.Count ?? _verifiedFiles;
        job.CopySeconds = result?.CopyDuration.TotalSeconds ?? _copyElapsed.TotalSeconds;
        job.VerifySeconds = result?.VerifyDuration.TotalSeconds ?? _verifyElapsed.TotalSeconds;
        job.VerificationEnabled = result?.VerificationPerformed ?? _copyOptions.VerifyFiles;
        if (result is not null)
        {
            job.DuplicateFiles = result.DuplicateFiles.ToList();
            foreach (DuplicateFileConflict conflict in result.DuplicateFiles)
            {
                if (!job.DuplicateDecisions.ContainsKey(conflict.RelativePath))
                {
                    job.DuplicateDecisions[conflict.RelativePath] =
                        _copyOptions.ExistingFilePolicy == ExistingFilePolicy.Ask
                            ? ExistingFilePolicy.Skip
                            : _copyOptions.ExistingFilePolicy;
                }
            }
        }
        job.ErrorMessage = error;

        _lastReport = result is not null ? BuildReport(result, job) : BuildIncompleteReport(job);
        try
        {
            job.ReportFileName = await _historyService.SaveReportAsync(job.Id, _lastReport);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogText.Text = $"任务已结束，但报告保存失败：{ex.Message}";
        }

        RefreshHistoryItem(job);
        await SaveHistorySafeAsync();
    }

    private void RecordDuplicateConflict(DuplicateFileConflict conflict)
    {
        if (_duplicateChoices.Any(item =>
            string.Equals(item.RelativePath, conflict.RelativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ExistingFilePolicy? initialDecision = _copyOptions.ExistingFilePolicy == ExistingFilePolicy.Ask
            ? null
            : _copyOptions.ExistingFilePolicy;
        var choice = new DuplicateConflictChoice(
            conflict, initialDecision,
            _isRunning && initialDecision is null);
        _duplicateChoices.Add(choice);
        if (_activeJob is not null)
        {
            _activeJob.DuplicateFiles.Add(conflict);
            if (initialDecision is ExistingFilePolicy decision)
            {
                _activeJob.DuplicateDecisions[conflict.RelativePath] = decision;
            }
        }

        DuplicatePanel.Visibility = Visibility.Visible;
        DuplicateList.IsEnabled = true;
        ApplyDuplicateChoicesButton.Visibility = _copyOptions.ExistingFilePolicy == ExistingFilePolicy.Ask
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateDuplicateChoiceUi();
    }

    private async Task<IReadOnlyDictionary<string, ExistingFilePolicy>> WaitForDuplicateChoicesAsync(
        IReadOnlyList<DuplicateFileConflict> conflicts,
        CancellationToken cancellationToken)
    {
        foreach (DuplicateFileConflict conflict in conflicts)
        {
            RecordDuplicateConflict(conflict);
        }

        _duplicateDecisionSource = new TaskCompletionSource<IReadOnlyDictionary<string, ExistingFilePolicy>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        DuplicatePanel.Visibility = Visibility.Visible;
        DuplicateList.IsEnabled = true;
        foreach (DuplicateConflictChoice choice in _duplicateChoices)
        {
            choice.SetCanChoose(true);
        }
        StatusText.Text = "等待处理重复文件";
        PhaseText.Text = "请为每个重复文件选择处理方式";
        LogText.Text = "其他文件已继续复制和校验。完成下方每一项选择后即可继续处理重复文件。";
        UpdateDuplicateChoiceUi();

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => _duplicateDecisionSource?.TrySetCanceled(cancellationToken));
        try
        {
            return await _duplicateDecisionSource.Task;
        }
        finally
        {
            _duplicateDecisionSource = null;
        }
    }

    private void DuplicateOverwrite_Click(object sender, RoutedEventArgs e) =>
        SetDuplicateDecision(sender, ExistingFilePolicy.Overwrite);

    private void DuplicateSkip_Click(object sender, RoutedEventArgs e) =>
        SetDuplicateDecision(sender, ExistingFilePolicy.Skip);

    private void DuplicateCreateCopy_Click(object sender, RoutedEventArgs e) =>
        SetDuplicateDecision(sender, ExistingFilePolicy.CreateCopy);

    private void SetDuplicateDecision(object sender, ExistingFilePolicy decision)
    {
        if (TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            SetConcurrentDuplicateDecision(sender, decision);
            return;
        }
        if (sender is not Button { Tag: DuplicateConflictChoice choice } || !_isRunning)
        {
            return;
        }

        choice.SetDecision(decision);
        if (_activeJob is not null)
        {
            _activeJob.DuplicateDecisions[choice.RelativePath] = decision;
        }
        UpdateDuplicateChoiceUi();
    }

    private void DuplicateSelectionCheckBox_Click(object sender, RoutedEventArgs e) =>
        UpdateDuplicateChoiceUi();

    private void DuplicateSelectAllCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingDuplicateSelection)
        {
            return;
        }

        bool selected = DuplicateSelectAllCheckBox.IsChecked == true;
        foreach (DuplicateConflictChoice choice in _duplicateChoices.Where(item => item.CanChoose))
        {
            choice.IsSelected = selected;
        }
        UpdateDuplicateChoiceUi();
    }

    private void BatchDuplicateOverwrite_Click(object sender, RoutedEventArgs e) =>
        SetSelectedDuplicateDecisions(ExistingFilePolicy.Overwrite);

    private void BatchDuplicateSkip_Click(object sender, RoutedEventArgs e) =>
        SetSelectedDuplicateDecisions(ExistingFilePolicy.Skip);

    private void BatchDuplicateCreateCopy_Click(object sender, RoutedEventArgs e) =>
        SetSelectedDuplicateDecisions(ExistingFilePolicy.CreateCopy);

    private void SetSelectedDuplicateDecisions(ExistingFilePolicy decision)
    {
        foreach (DuplicateConflictChoice choice in _duplicateChoices.Where(item => item.CanChoose && item.IsSelected))
        {
            choice.SetDecision(decision);
            if (_activeJob is not null)
            {
                _activeJob.DuplicateDecisions[choice.RelativePath] = decision;
            }
        }
        UpdateDuplicateChoiceUi();
    }

    private async void OpenDuplicateSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DuplicateConflictChoice choice })
        {
            await SelectInFileExplorerAsync(choice.SourcePath);
        }
    }

    private async void OpenDuplicateDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DuplicateConflictChoice choice })
        {
            await SelectInFileExplorerAsync(choice.DestinationPath);
        }
    }

    private async Task SelectInFileExplorerAsync(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            await ShowMessageAsync("文件不可用", $"当前无法找到该文件：\n{path}");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法打开文件资源管理器", ex.Message);
        }
    }
    private void ApplyDuplicateChoicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            ConcurrentApplyDuplicateChoicesButton_Click(sender, e);
            return;
        }
        if (_duplicateDecisionSource is null || _duplicateChoices.Count == 0 ||
            _duplicateChoices.Any(item => !item.IsDecided))
        {
            return;
        }

        Dictionary<string, ExistingFilePolicy> decisions = _duplicateChoices.ToDictionary(
            item => item.RelativePath,
            item => item.Decision ?? ExistingFilePolicy.Skip,
            StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateConflictChoice choice in _duplicateChoices)
        {
            choice.SetCanChoose(false);
        }
        ApplyDuplicateChoicesButton.IsEnabled = false;
        UpdateDuplicateChoiceUi();
        DuplicateSelectionHint.Text = "正在按逐项选择处理";
        _duplicateDecisionSource.TrySetResult(decisions);
    }

    private void UpdateDuplicateChoiceUi()
    {
        if (TryGetSelectedRuntime(out CopyJobRuntime runtime))
        {
            ShowRuntimeDuplicateChoices(runtime);
            return;
        }
        int decided = _duplicateChoices.Count(item => item.IsDecided);
        int selectable = _duplicateChoices.Count(item => item.CanChoose);
        int selected = _duplicateChoices.Count(item => item.CanChoose && item.IsSelected);
        DuplicateSummaryText.Text = $"发现 {_duplicateChoices.Count:N0} 个重复文件";
        DuplicateSelectionHint.Text = _copyOptions.ExistingFilePolicy == ExistingFilePolicy.Ask
            ? $"已选择处理方式 {decided}/{_duplicateChoices.Count}，已勾选 {selected} 项"
            : $"按“{GetDuplicatePolicyText(_copyOptions.ExistingFilePolicy)}”处理";

        _updatingDuplicateSelection = true;
        DuplicateSelectAllCheckBox.IsEnabled = selectable > 0;
        DuplicateSelectAllCheckBox.IsChecked = selectable == 0 || selected == 0
            ? false
            : selected == selectable ? true : null;
        _updatingDuplicateSelection = false;

        bool canBatch = _isRunning && _copyOptions.ExistingFilePolicy == ExistingFilePolicy.Ask && selected > 0;
        BatchOverwriteButton.IsEnabled = canBatch;
        BatchSkipButton.IsEnabled = canBatch;
        BatchCreateCopyButton.IsEnabled = canBatch;
        ApplyDuplicateChoicesButton.IsEnabled = _duplicateDecisionSource is not null &&
            _duplicateChoices.Count > 0 && decided == _duplicateChoices.Count;
    }

    private static string GetDuplicatePolicyText(ExistingFilePolicy policy) => policy switch
    {
        ExistingFilePolicy.Overwrite => "覆盖",
        ExistingFilePolicy.Skip => "跳过",
        ExistingFilePolicy.CreateCopy => "创建副本",
        _ => "逐个询问"
    };

    private void ShowDuplicateHistory(JobHistoryItem job)
    {
        _duplicateChoices.Clear();
        foreach (DuplicateFileConflict conflict in job.DuplicateFiles)
        {
            job.DuplicateDecisions.TryGetValue(conflict.RelativePath, out ExistingFilePolicy decision);
            ExistingFilePolicy? selected = decision == ExistingFilePolicy.Ask ? null : decision;
            _duplicateChoices.Add(new DuplicateConflictChoice(conflict, selected, false));
        }

        DuplicatePanel.Visibility = _duplicateChoices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DuplicateList.IsEnabled = true;
        ApplyDuplicateChoicesButton.Visibility = Visibility.Collapsed;
        UpdateDuplicateChoiceUi();
        if (_duplicateChoices.Count > 0)
        {
            DuplicateSummaryText.Text = $"重复文件记录：{_duplicateChoices.Count:N0} 个";
            DuplicateSelectionHint.Text = "已按各自选择处理";
        }
    }
    private void UpdateProgress(CopyProgressInfo info)
    {
        _lastProgress = info;
        TotalSizeText.Text = FormatBytes(info.TotalBytes);
        TotalCountText.Text = info.TotalFiles.ToString("N0", CultureInfo.InvariantCulture);
        DurationText.Text = FormatDuration(info.Elapsed);
        CurrentFileText.Text = string.IsNullOrEmpty(info.CurrentFile) ? PhaseText.Text : info.CurrentFile;

        if (_activeJob is not null && _activeJob.TotalBytes == 0 && (info.TotalBytes > 0 || info.TotalFiles > 0))
        {
            _activeJob.TotalBytes = info.TotalBytes;
            _activeJob.FileCount = info.TotalFiles;
            RefreshHistoryItem(_activeJob);
        }

        double GetPercent(long bytes, int processedFiles)
        {
            if (info.TotalBytes > 0)
            {
                return Math.Clamp(bytes * 100d / info.TotalBytes, 0, 100);
            }
            if (info.TotalFiles > 0)
            {
                return Math.Clamp(processedFiles * 100d / info.TotalFiles, 0, 100);
            }
            return info.Phase is CopyPhase.Completed ? 100 : 0;
        }

        void UpdateOverallProgress()
        {
            double copyPercent = GetPercent(_copiedBytes, _copiedFiles);
            double verifyPercent = _copyOptions.VerifyFiles ? GetPercent(_verifiedBytes, _verifiedFiles) : 100;
            double overall = _copyOptions.SkipCopy
                ? verifyPercent
                : _copyOptions.VerifyFiles
                ? copyPercent * 0.8 + verifyPercent * 0.2
                : copyPercent;
            OverallProgress.Value = overall;
            PercentText.Text = $"{overall:F2}%";
        }

        switch (info.Phase)
        {
            case CopyPhase.Scanning:
                StatusText.Text = "正在扫描";
                PhaseText.Text = "正在读取目录";
                break;

            case CopyPhase.Copying:
                _copiedBytes = info.ProcessedBytes;
                _copiedFiles = info.ProcessedFiles;
                _copyElapsed = info.Elapsed;
                StatusText.Text = _isPaused ? "已暂停" : "正在拷贝";
                PhaseText.Text = "拷贝文件";
                double copyPercent = GetPercent(_copiedBytes, _copiedFiles);
                CopyProgress.Value = copyPercent;
                bool copyFinished = _copiedFiles >= info.TotalFiles;
                CopyProgress.Visibility = copyFinished ? Visibility.Collapsed : Visibility.Visible;
                CopyCompletedBadge.Visibility = copyFinished ? Visibility.Visible : Visibility.Collapsed;
                CopyCompletedText.Text = "已完成";
                CopySpeedText.Text = $"{FormatBytes(info.BytesPerSecond)}/s";
                CopyTimeText.Text = FormatDuration(info.Elapsed);
                CopyCountText.Text = $"{info.ProcessedFiles}/{info.TotalFiles}";
                UpdateOverallProgress();
                break;

            case CopyPhase.Verifying:
                _verifiedFiles = info.ProcessedFiles;
                _verifiedBytes = info.ProcessedBytes;
                _verifyElapsed = info.Elapsed;
                StatusText.Text = _isPaused ? "已暂停" : "正在校验";
                PhaseText.Text = "SHA-256 完整性校验";
                double verifyPercent = GetPercent(info.ProcessedBytes, info.ProcessedFiles);
                VerifyProgress.Value = verifyPercent;
                bool verificationFinished = _verifiedFiles >= info.TotalFiles;
                VerifyProgress.Visibility = verificationFinished ? Visibility.Collapsed : Visibility.Visible;
                VerifyCompletedBadge.Visibility = verificationFinished ? Visibility.Visible : Visibility.Collapsed;
                VerifyCompletedText.Text = "已完成";
                VerifySpeedText.Text = $"{FormatBytes(info.BytesPerSecond)}/s";
                VerifyTimeText.Text = FormatDuration(info.Elapsed);
                VerifyCountText.Text = $"{info.ProcessedFiles}/{info.TotalFiles}";
                UpdateOverallProgress();
                break;

            case CopyPhase.WaitingForDuplicateDecision:
                StatusText.Text = "等待处理重复文件";
                PhaseText.Text = "请在下方逐个选择处理方式";
                CurrentFileText.Text = $"已记录 {_duplicateChoices.Count:N0} 个重复文件";
                UpdateOverallProgress();
                break;

            case CopyPhase.Completed:
                if (!_copyOptions.SkipCopy)
                {
                    _copiedBytes = info.TotalBytes;
                    _copiedFiles = info.TotalFiles;
                }
                if (_copyOptions.VerifyFiles)
                {
                    _verifiedFiles = info.TotalFiles;
                    _verifiedBytes = info.TotalBytes;
                }
                CopyProgress.Visibility = Visibility.Collapsed;
                CopyCompletedBadge.Visibility = Visibility.Visible;
                CopyCompletedText.Text = _copyOptions.SkipCopy ? "未启用" : "已完成";
                VerifyProgress.Visibility = Visibility.Collapsed;
                VerifyCompletedBadge.Visibility = Visibility.Visible;
                VerifyCompletedText.Text = _copyOptions.VerifyFiles ? "已完成" : "未启用";
                OverallProgress.Value = 100;
                PercentText.Text = "100.00%";
                break;
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        _isPaused = !_isPaused;
        PauseText.Text = _isPaused ? "继续" : "暂停";
        PauseIcon.Glyph = _isPaused ? "\uE768" : "\uE769";
        StatusText.Text = _isPaused ? "已暂停" : "继续处理中";
        LogText.Text = _isPaused ? "任务已暂停。" : "任务已继续。";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消";
        _cancellation?.Cancel();
    }

    private async Task WaitWhilePausedAsync(CancellationToken cancellationToken)
    {
        while (_isPaused)
        {
            await Task.Delay(120, cancellationToken);
        }
    }

    private async void NewJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            await ShowMessageAsync("任务正在运行", "请先完成或取消当前任务。");
            return;
        }
        PrepareNewJobView();
        if (await ConfigureNewTaskAsync())
        {
            await StartCopyAsync();
        }
    }

    private void PrepareNewJobView()
    {
        if (_isMultiSelectMode)
        {
            ExitMultiSelectMode(false);
        }
        HistoryList.SelectedItem = null;
        _selectedJob = null;
        NewJobsList.SelectedItem = null;
        _activeJob = null;
        _sourcePath = null;
        _destinationPath = null;
        _destinationParentPath = null;
        AskExistingRadio.IsChecked = true;
        SourcePathText.Text = "尚未选择";
        DestinationPathText.Text = "尚未选择";
        PriorityExecutionToggle.IsOn = false;
        UseFastCopyAlgorithmToggle.IsOn = false;
        PreventSleepToggle.IsOn = true;
        HeroNameText.Text = "准备新任务";
        CurrentFileText.Text = "请选择源目录和目标目录";
        LogText.Text = "就绪。选择目录后即可开始。";
        ResetProgress();
        StartButton.Visibility = Visibility.Visible;
        UpdateStartButton();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isMultiSelectMode)
        {
            if (!_isChangingMultiSelectMode)
            {
                UpdateBatchSelectionUi();
            }
            return;
        }
        if (_isRunning || HistoryList.SelectedItem is not JobHistoryItem item)
        {
            return;
        }
        _selectedJob = item;
        ShowHistoryJob(item);
    }

    private void ShowHistoryJob(JobHistoryItem job)
    {
        HeroNameText.Text = job.DisplayName;
        SourcePathText.Text = job.SourcePath;
        DestinationPathText.Text = job.DestinationPath;
        TotalSizeText.Text = FormatBytes(job.TotalBytes);
        TotalCountText.Text = job.FileCount.ToString("N0", CultureInfo.InvariantCulture);
        StartTimeText.Text = job.StartedAt.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        EndTimeText.Text = job.FinishedAt?.ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "--";
        DurationText.Text = job.DurationText;
        CurrentFileText.Text = job.ErrorMessage ?? $"{job.SourcePath} → {job.DestinationPath}";
        ShowDuplicateHistory(job);
        ShowFailedFileHistory(job);

        bool taskFinished = job.Status is JobStatus.Completed or JobStatus.CompletedWithErrors or JobStatus.VerificationFailed;
        bool copyFinished = !job.CopyEnabled || taskFinished ||
            (job.FileCount > 0 && job.CopiedFiles >= job.FileCount && job.CopiedBytes >= job.TotalBytes);
        bool verificationFinished = job.VerificationEnabled && taskFinished;
        double copyPercent = !job.CopyEnabled
            ? 0
            : job.TotalBytes <= 0
            ? (copyFinished ? 100 : 0)
            : Math.Clamp(job.CopiedBytes * 100d / job.TotalBytes, 0, 100);
        double verifyPercent = job.FileCount <= 0
            ? (verificationFinished ? 100 : 0)
            : Math.Clamp(job.VerifiedFiles * 100d / job.FileCount, 0, 100);
        if (taskFinished)
        {
            copyPercent = job.CopyEnabled ? 100 : 0;
            verifyPercent = job.VerificationEnabled ? 100 : 0;
        }

        CopyProgress.Value = copyPercent;
        VerifyProgress.Value = verifyPercent;
        CopyProgress.Visibility = copyFinished ? Visibility.Collapsed : Visibility.Visible;
        CopyCompletedBadge.Visibility = copyFinished ? Visibility.Visible : Visibility.Collapsed;
        CopyCompletedText.Text = job.CopyEnabled ? "已完成" : "未启用";
        VerifyProgress.Visibility = verificationFinished || !job.VerificationEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;
        VerifyCompletedBadge.Visibility = verificationFinished || !job.VerificationEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        VerifyCompletedText.Text = !job.VerificationEnabled
            ? "未启用"
            : job.Status == JobStatus.VerificationFailed ? "校验失败" : "已完成";
        VerifyCompletedBadge.Background = new SolidColorBrush(
            job.Status == JobStatus.VerificationFailed
                ? ColorHelper.FromArgb(255, 0xE8, 0x46, 0x3A) // Error surface
                : job.VerificationEnabled
                    ? ColorHelper.FromArgb(255, 0x15, 0xA8, 0x77) // Success surface
                    : ColorHelper.FromArgb(255, 0xE5, 0xE5, 0xE5));
        OverallProgress.Value = taskFinished
            ? 100
            : !job.CopyEnabled
                ? verifyPercent
                : !job.VerificationEnabled
                    ? copyPercent
                    : copyPercent * 0.8 + verifyPercent * 0.2;
        CopySpeedText.Text = job.CopyEnabled && job.CopySeconds > 0
            ? $"{FormatBytes(job.TotalBytes / job.CopySeconds)}/s"
            : "--";
        VerifySpeedText.Text = job.VerifySeconds > 0 ? $"{FormatBytes(job.TotalBytes / job.VerifySeconds)}/s" : "--";
        CopyTimeText.Text = job.CopyEnabled ? FormatDuration(TimeSpan.FromSeconds(job.CopySeconds)) : "--";
        VerifyTimeText.Text = FormatDuration(TimeSpan.FromSeconds(job.VerifySeconds));
        CopyCountText.Text = job.CopyEnabled ? $"{job.CopiedFiles}/{job.FileCount}" : "--";
        VerifyCountText.Text = $"{job.VerifiedFiles}/{job.FileCount}";

        CompletionIcon.Visibility = Visibility.Visible;
        CompletionIcon.Glyph = job.StatusGlyph;
        PercentText.Visibility = Visibility.Collapsed;
        StatusText.FontSize = 30;
        StatusText.Text = job.StatusText;
        PauseButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = IsBatchDeletable(job)
            ? Visibility.Visible
            : Visibility.Collapsed;
        StartButton.IsEnabled = false;
        StartButton.Visibility = Visibility.Collapsed;

        bool succeeded = job.Status == JobStatus.Completed;
        SolidColorBrush stateBrush = new(succeeded
            ? ColorHelper.FromArgb(255, 0x15, 0xA8, 0x77) // Success
            : ColorHelper.FromArgb(255, 0xE8, 0x46, 0x3A)); // Error
        CompletionIcon.Foreground = stateBrush;
        StatusText.Foreground = stateBrush;

        PhaseText.Text = job.Status switch
        {
            JobStatus.CompletedWithErrors => job.CopyEnabled
                ? "拷贝已完成，失败文件已跳过"
                : "校验已完成，失败文件已跳过",
            JobStatus.Completed => job.CopyEnabled && job.VerificationEnabled
                ? "拷贝和 SHA-256 校验均已完成"
                : job.CopyEnabled
                    ? "拷贝已完成"
                    : "SHA-256 校验已完成",
            JobStatus.VerificationFailed => job.CopyEnabled
                ? "拷贝完成，但完整性校验未通过"
                : "SHA-256 校验未通过",
            JobStatus.Cancelled => "任务已取消；已完成文件保留",
            JobStatus.Interrupted => "应用在任务完成前退出",
            JobStatus.Failed => "任务执行失败",
            _ => "任务记录"
        };
        LogText.Text = job.Status switch
        {
            JobStatus.CompletedWithErrors => $"\u4EFB\u52A1\u90E8\u5206\u5B8C\u6210\uFF1A\u5DF2\u8DF3\u8FC7 {job.FailedFiles.Count:N0} \u4E2A\u5931\u8D25\u6587\u4EF6\u3002",
            JobStatus.Completed => job.CopyEnabled && job.VerificationEnabled
                ? $"任务完成：已拷贝并校验 {job.FileCount:N0} 个文件。"
                : job.CopyEnabled
                    ? $"拷贝完成：已拷贝 {job.FileCount:N0} 个文件。"
                    : $"校验完成：{job.FileCount:N0} 个文件均通过 SHA-256 校验。",
            JobStatus.VerificationFailed => job.ErrorMessage ?? "校验发现不一致文件。",
            JobStatus.Cancelled => "任务已取消，已完成文件予以保留。",
            JobStatus.Interrupted => "应用上次在任务结束前退出，此记录已标记为中断。",
            _ => job.ErrorMessage ?? "任务未完成。"
        };
    }

    private async void DeleteJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _selectedJob is null)
        {
            return;
        }
        if (!IsBatchDeletable(_selectedJob))
        {
            await ShowMessageAsync("无法删除任务", "只能删除已经完成处理的任务记录。");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "删除历史记录？",
            Content = "只会从 EZ DIT 中移除这条已完成任务记录，不会删除任何文件。",
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
        await SaveHistorySafeAsync();
        UpdateHistoryEmptyState();
        _selectedJob = null;
        if (_activeJob?.Id == deleting.Id)
        {
            _activeJob = null;
        }

        if (_history.Count > 0)
        {
            HistoryList.SelectedIndex = 0;
        }
        else
        {
            PrepareNewJobView();
        }
    }

    private void MultiSelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMultiSelectMode)
        {
            ExitMultiSelectMode(true);
        }
        else
        {
            EnterMultiSelectMode();
        }
    }

    private void EnterMultiSelectMode()
    {
        _isMultiSelectMode = true;
        _isChangingMultiSelectMode = true;
        try
        {
            NewJobsList.SelectedItem = null;
            HistoryList.SelectedItem = null;
            NewJobsList.SelectionMode = ListViewSelectionMode.Multiple;
            HistoryList.SelectionMode = ListViewSelectionMode.Multiple;
        }
        finally
        {
            _isChangingMultiSelectMode = false;
        }
        NewJobButton.IsEnabled = false;
        DeleteJobButton.Visibility = Visibility.Collapsed;
        BatchActionPanel.Visibility = Visibility.Visible;
        MultiSelectButtonText.Text = "完成";
        UpdateBatchSelectionUi();
    }

    private void ExitMultiSelectMode(bool selectInitialTask)
    {
        _isChangingMultiSelectMode = true;
        try
        {
            NewJobsList.SelectedItems.Clear();
            HistoryList.SelectedItems.Clear();
            NewJobsList.SelectionMode = ListViewSelectionMode.Single;
            HistoryList.SelectionMode = ListViewSelectionMode.Single;
        }
        finally
        {
            _isChangingMultiSelectMode = false;
        }
        _isMultiSelectMode = false;
        BatchActionPanel.Visibility = Visibility.Collapsed;
        MultiSelectButtonText.Text = "多选";
        NewJobButton.IsEnabled = true;
        BatchDeleteButton.IsEnabled = false;
        BatchReportButton.IsEnabled = false;
        if (selectInitialTask)
        {
            SelectInitialTask();
        }
    }

    private List<JobHistoryItem> GetBatchSelectedJobs()
    {
        if (!_isMultiSelectMode ||
            NewJobsList.SelectionMode != ListViewSelectionMode.Multiple ||
            HistoryList.SelectionMode != ListViewSelectionMode.Multiple)
        {
            return [];
        }

        return NewJobsList.SelectedItems
            .OfType<JobHistoryItem>()
            .Concat(HistoryList.SelectedItems.OfType<JobHistoryItem>())
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static bool IsBatchDeletable(JobHistoryItem job) =>
        job.Status == JobStatus.Completed;

    private static bool IsReportable(JobHistoryItem job) =>
        job.Status is not JobStatus.Queued and not JobStatus.Running;

    private void UpdateBatchSelectionUi()
    {
        if (!_isMultiSelectMode ||
            _isChangingMultiSelectMode ||
            NewJobsList.SelectionMode != ListViewSelectionMode.Multiple ||
            HistoryList.SelectionMode != ListViewSelectionMode.Multiple)
        {
            return;
        }

        List<JobHistoryItem> selected = GetBatchSelectedJobs();
        BatchSelectionText.Text = selected.Count == 0
            ? "请选择任务"
            : $"已选择 {selected.Count:N0} 个任务，仅已完成任务可删除";
        BatchDeleteButton.IsEnabled = selected.Count > 0 && selected.All(IsBatchDeletable);
        BatchReportButton.IsEnabled = selected.Count > 0 && selected.All(IsReportable);
    }

    private async void BatchDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        List<JobHistoryItem> selected = GetBatchSelectedJobs();
        if (selected.Count == 0)
        {
            return;
        }
        if (selected.Any(job => !IsBatchDeletable(job)))
        {
            await ShowMessageAsync("无法批量删除", "只能删除已经完成处理的任务记录，请取消选择其他状态的任务。");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"删除 {selected.Count:N0} 条任务记录？",
            Content = "只会从 EZ DIT 中移除已完成的任务记录，不会删除源文件、目的地文件或已经导出的报告。",
            PrimaryButtonText = "批量删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        foreach (JobHistoryItem job in selected)
        {
            _history.Remove(job);
            RemoveTaskFromSections(job);
            if (_selectedJob?.Id == job.Id)
            {
                _selectedJob = null;
            }
            if (_activeJob?.Id == job.Id)
            {
                _activeJob = null;
            }
        }

        await SaveHistorySafeAsync();
        UpdateHistoryEmptyState();
        ExitMultiSelectMode(true);
        LogText.Text = $"已删除 {selected.Count:N0} 条任务记录，所有文件均已保留。";
    }

    private async void BatchReportButton_Click(object sender, RoutedEventArgs e)
    {
        List<JobHistoryItem> selected = GetBatchSelectedJobs();
        if (selected.Count == 0)
        {
            return;
        }
        if (selected.Any(job => !IsReportable(job)))
        {
            await ShowMessageAsync("报告尚不可用", "运行中或排队中的任务暂时不能创建报告。");
            return;
        }

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        BatchReportButton.IsEnabled = false;
        int created = 0;
        try
        {
            foreach (JobHistoryItem job in selected)
            {
                string? report = await _historyService.ReadReportAsync(job.ReportFileName);
                report ??= BuildIncompleteReport(job);
                string displayName = SanitizeReportFileName(job.DisplayName);
                string fileName = $"EZDIT_Report_{job.StartedAt:yyyyMMdd_HHmmss}_{displayName}.txt";
                StorageFile file = await folder.CreateFileAsync(
                    fileName, CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteTextAsync(file, report);
                created++;
            }
            LogText.Text = $"已在 {folder.Path} 创建 {created:N0} 份任务报告。";
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("批量创建报告失败", $"已创建 {created:N0} 份报告，随后发生错误：{ex.Message}");
        }
        finally
        {
            UpdateBatchSelectionUi();
        }
    }

    private static string SanitizeReportFileName(string value)
    {
        char[] invalidCharacters = Path.GetInvalidFileNameChars();
        string safeName = new(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
        safeName = safeName.Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "Task";
        }
        return safeName.Length <= 60 ? safeName : safeName[..60];
    }

    private void SetRunningUi(bool running)
    {
        PauseButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        DeleteJobButton.Visibility = Visibility.Collapsed;
        CompletionIcon.Visibility = Visibility.Collapsed;
        PercentText.Visibility = Visibility.Visible;
        StatusText.FontSize = 15;
        PauseButton.IsEnabled = running;
        CancelButton.IsEnabled = running;
        if (running)
        {
            StartButton.Visibility = Visibility.Collapsed;
        }
        NewJobButton.IsEnabled = !running;
        MultiSelectButton.IsEnabled = !running;
        SourcePickerButton.IsEnabled = !running;
        DestinationPickerButton.IsEnabled = !running;
        HistoryList.IsEnabled = !running;
    }

    private void UpdateStartButton()
    {
        StartButton.IsEnabled = !_isRunning && _selectedJob is null;
    }

    private void ResetProgress()
    {
        _duplicateDecisionSource = null;
        _duplicateChoices.Clear();
        DuplicatePanel.Visibility = Visibility.Collapsed;
        _failedFileChoices.Clear();
        FailedFilesPanel.Visibility = Visibility.Collapsed;
        RetryFailedFilesButton.IsEnabled = false;
        SkipFailedFilesButton.IsEnabled = false;
        DuplicateList.IsEnabled = true;
        ApplyDuplicateChoicesButton.Visibility = Visibility.Visible;
        ApplyDuplicateChoicesButton.IsEnabled = false;
        DuplicateSelectionHint.Text = "请逐个选择处理方式";
        DuplicateSelectAllCheckBox.IsChecked = false;
        DuplicateSelectAllCheckBox.IsEnabled = false;
        BatchOverwriteButton.IsEnabled = false;
        BatchSkipButton.IsEnabled = false;
        BatchCreateCopyButton.IsEnabled = false;
        OverallProgress.Value = 0;
        CopyProgress.Value = 0;
        VerifyProgress.Value = 0;
        CopyProgress.Visibility = Visibility.Visible;
        VerifyProgress.Visibility = Visibility.Visible;
        CopyCompletedBadge.Visibility = Visibility.Collapsed;
        VerifyCompletedBadge.Visibility = Visibility.Collapsed;
        CopyCompletedText.Text = "已完成";
        VerifyCompletedText.Text = "已完成";
        VerifyCompletedBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 0x15, 0xA8, 0x77));
        PercentText.Text = "0.00%";
        PercentText.Visibility = Visibility.Visible;
        CompletionIcon.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = "等待设置";
        StatusText.FontSize = 15;
        StatusText.Foreground = (SolidColorBrush)Application.Current.Resources["MutedTextBrush"];
        PhaseText.Text = "等待开始";
        TotalSizeText.Text = "--";
        TotalCountText.Text = "--";
        StartTimeText.Text = "--";
        EndTimeText.Text = "--";
        DurationText.Text = "--";
        CopySpeedText.Text = "0 B/s";
        VerifySpeedText.Text = "0 B/s";
        CopyTimeText.Text = "00:00:00";
        VerifyTimeText.Text = "00:00:00";
        CopyCountText.Text = "0/0";
        VerifyCountText.Text = "0/0";
        PauseText.Text = "暂停";
        PauseIcon.Glyph = "\uE769";
    }

    private void RefreshHistoryItem(JobHistoryItem item)
    {
        int index = _history.IndexOf(item);
        if (index >= 0 && !ReferenceEquals(_history[index], item))
        {
            _history[index] = item;
        }
        SyncTaskSection(item);
    }

    private void TrimHistory()
    {
        while (_history.Count > 200)
        {
            JobHistoryItem removed = _history[^1];
            _history.RemoveAt(_history.Count - 1);
            _ = _historyService.DeleteReportAsync(removed.ReportFileName);
            RemoveTaskFromSections(removed);
        }
    }

    private void UpdateHistoryEmptyState()
    {
        UpdateTaskSectionEmptyStates();
    }

    private async Task SaveHistorySafeAsync()
    {
        try
        {
            await _historyService.SaveAsync(_history);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogText.Text = $"历史记录保存失败：{ex.Message}";
        }
    }

    private string BuildReport(CopyResult result, JobHistoryItem job)
    {
        var report = new StringBuilder();
        report.AppendLine("EZ DIT 任务报告");
        report.AppendLine(new string('=', 42));
        report.AppendLine($"任务名称：{job.DisplayName}");
        report.AppendLine($"源目录：{job.SourcePath}");
        report.AppendLine($"目标目录：{job.DestinationPath}");
        report.AppendLine($"开始时间：{job.StartedAt:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"结束时间：{job.FinishedAt:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"文件数量：{result.FileCount:N0}");
        report.AppendLine($"数据大小：{FormatBytes(result.TotalBytes)}");
        report.AppendLine(job.CopyEnabled
            ? $"拷贝用时：{FormatDuration(result.CopyDuration)}"
            : "拷贝：未启用");
        report.AppendLine($"校验用时：{FormatDuration(result.VerifyDuration)}");
        if (job.CopyEnabled)
        {
            report.AppendLine($"拷贝算法：{(job.UseFastCopyAlgorithm ? "FastCopy 流水线" : "标准顺序复制")}");
        }
        report.AppendLine($"校验算法：{(result.VerificationPerformed ? "SHA-256" : "未启用")}");
        report.AppendLine($"最终结果：{(result.Success ? "通过" : "失败")}");
        report.AppendLine();
        report.AppendLine("文件校验明细：");
        foreach (FileVerificationResult file in result.VerifiedFiles)
        {
            if (file.IsMatch)
            {
                report.AppendLine($"[通过] {file.RelativePath} | {FormatBytes(file.Length)} | SHA-256: {file.SourceSha256}");
            }
            else
            {
                report.AppendLine($"[失败] {file.RelativePath} | 源: {file.SourceSha256} | 目标: {file.DestinationSha256} | {file.Error}");
            }
        }
        AppendDuplicateReport(report, job);
        return report.ToString();
    }

    private string BuildIncompleteReport(JobHistoryItem job)
    {
        var report = new StringBuilder();
        report.AppendLine("EZ DIT 拷卡任务报告");
        report.AppendLine(new string('=', 42));
        report.AppendLine($"任务名称：{job.DisplayName}");
        report.AppendLine($"源目录：{job.SourcePath}");
        report.AppendLine($"目标目录：{job.DestinationPath}");
        report.AppendLine($"开始时间：{job.StartedAt:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"结束时间：{job.FinishedAt:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"任务状态：{job.StatusText}");
        if (job.CopyEnabled)
        {
            report.AppendLine($"拷贝算法：{(job.UseFastCopyAlgorithm ? "FastCopy 流水线" : "标准顺序复制")}");
            report.AppendLine($"已拷贝：{job.CopiedFiles}/{job.FileCount} 个文件，{FormatBytes(job.CopiedBytes)}");
        }
        else
        {
            report.AppendLine("拷贝：未启用");
        }
        report.AppendLine(job.VerificationEnabled
            ? $"已校验：{job.VerifiedFiles}/{job.FileCount} 个文件"
            : "文件校验：未启用");
        if (!string.IsNullOrWhiteSpace(job.ErrorMessage))
        {
            report.AppendLine($"说明：{job.ErrorMessage}");
        }
        AppendDuplicateReport(report, job);
        return report.ToString();
    }

    private static void AppendDuplicateReport(StringBuilder report, JobHistoryItem job)
    {
        if (job.DuplicateFiles.Count == 0)
        {
            return;
        }

        report.AppendLine();
        report.AppendLine($"重复文件处理：{job.DuplicateFiles.Count:N0} 个");
        foreach (DuplicateFileConflict conflict in job.DuplicateFiles)
        {
            ExistingFilePolicy decision = job.DuplicateDecisions.TryGetValue(
                conflict.RelativePath, out ExistingFilePolicy selected)
                ? selected
                : ExistingFilePolicy.Ask;
            report.AppendLine($"[{GetDuplicatePolicyText(decision)}] {conflict.RelativePath}");
            report.AppendLine($"  来源：{conflict.SourcePath}");
            report.AppendLine($"  冲突：{conflict.DestinationPath}");
        }
    }
    private static bool ValidatePaths(string source, string destination, out string message)
    {
        string normalizedSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedDestination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = StringComparison.OrdinalIgnoreCase;

        if (string.Equals(normalizedSource, normalizedDestination, comparison))
        {
            message = "源目录和目标目录不能相同。";
            return false;
        }
        if (normalizedDestination.StartsWith(normalizedSource + Path.DirectorySeparatorChar, comparison))
        {
            message = "目标目录不能位于源目录内部，否则会产生递归复制。";
            return false;
        }
        message = string.Empty;
        return true;
    }

    private static string GetDisplayName(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!string.IsNullOrWhiteSpace(directory.Name))
        {
            return directory.Name;
        }
        try
        {
            var drive = new DriveInfo(directory.Root.FullName);
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : drive.VolumeLabel;
        }
        catch
        {
            return path;
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

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 24
            ? $"{(int)value.TotalDays}.{value:hh\\:mm\\:ss}"
            : value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "确定",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args) => _cancellation?.Cancel();

    private void OnEnableCopyToggled(object sender, RoutedEventArgs e)
    {
        bool enabled = EnableCopyToggle.IsOn;
        DialogDestinationSubfolderName.IsEnabled = enabled;
        VerifyFilesToggle.IsEnabled = enabled;
        if (!enabled)
        {
            DialogDestinationSubfolderName.Text = "";
            VerifyFilesToggle.IsOn = true;
        }
        AskExistingRadio.IsEnabled = enabled;
        OverwriteExistingRadio.IsEnabled = enabled;
        SkipExistingRadio.IsEnabled = enabled;
        CreateCopyRadio.IsEnabled = enabled;
    }
}
