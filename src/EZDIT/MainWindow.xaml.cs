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
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace EZDIT;

public sealed partial class MainWindow : Window
{
    private readonly FileCopyService _copyService = new();
    private readonly AppSettings _appSettings = App.Settings;
    private readonly JobHistoryService _historyService;
    private readonly AppLogService _logService;
    private readonly UISettings _uiSettings = new();
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
    private AppLanguage _previousLanguage;
    private bool _isMultiSelectMode;
    private bool _isChangingMultiSelectMode;
    private bool _updatingDuplicateSelection;
    private bool _isApplyingAppearance;
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
        _historyService = new JobHistoryService(reportsDirectory: _appSettings.LogAndReportDirectory);
        _logService = new AppLogService(_appSettings.LogAndReportDirectory);
        _previousLanguage = _appSettings.Language;
        InitializeComponent();
        NewJobsList.ItemsSource = _newJobs;
        HistoryList.ItemsSource = _visibleHistory;
        DuplicateList.ItemsSource = _duplicateChoices;
        FailedFilesList.ItemsSource = _failedFileChoices;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ConfigureWindow();
        SettingsPage.Initialize(_appSettings);
        SettingsPage.BackRequested += SettingsPage_BackRequested;
        SettingsPage.SettingsChanged += SettingsPage_SettingsChanged;
        SettingsPage.BrowseDirectoryRequested += SettingsPage_BrowseDirectoryRequested;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        _uiSettings.ColorValuesChanged += SystemColorValuesChanged;
        LogText.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => _ = _logService.WriteAsync(LogText.Text));
        ApplyTheme();
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
        ApplyTheme();
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
                item.ErrorMessage = ResourceService.GetString("Error.AppExitedBeforeFinish");
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
        string? path = await PickFolderAsync(ResourceService.GetString("Picker.SourceFolderTitle"));
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
        CurrentFileText.Text = ResourceService.GetString("Info.SourceReady");
        LogText.Text = ResourceService.Format("Format.SelectedSourcePath", path);
        UpdateStartButton();
    }

    private async void ChooseDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = await PickFolderAsync(ResourceService.GetString("Picker.DestinationFolderTitle"));
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
        CurrentFileText.Text = ResourceService.GetString("Info.DirectoriesConfigured");
        LogText.Text = ResourceService.Format("Format.SelectedDestinationPath", path);
        UpdateStartButton();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            CommitButtonText = ResourceService.GetString("Button.SelectThisFolder")
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
        string? path = await PickFolderAsync(ResourceService.GetString("Picker.SourceFolderTitle"));
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
        string? path = await PickFolderAsync(ResourceService.GetString("Picker.DestinationFolderTitle"));
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
        DialogSourcePathText.Text = _dialogSourcePath ?? ResourceService.GetString("DialogSourcePathText.Text");
        DialogDestinationPathText.Text = _dialogDestinationParentPath ?? ResourceService.GetString("DialogDestinationPathText.Text");

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

        while (await ShowLocalizedDialogAsync(NewTaskDialog) == ContentDialogResult.Primary)
        {
            bool copyEnabled = EnableCopyToggle.IsOn;
            AskExistingRadio.IsEnabled = copyEnabled;
            OverwriteExistingRadio.IsEnabled = copyEnabled;
            SkipExistingRadio.IsEnabled = copyEnabled;
            CreateCopyRadio.IsEnabled = copyEnabled;

            if (_dialogSourcePath is null || _dialogDestinationParentPath is null)
            {
                await ShowMessageAsync("Error.FoldersNotConfigured", "Error.SelectSourceAndDest");
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
                await ShowMessageAsync("Error.UnableToStart", validationMessage);
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
            CurrentFileText.Text = ResourceService.GetString("Info.TaskConfigured");
            LogText.Text = ResourceService.Format("Format.SourcePathLabel", _sourcePath) + "\n" + ResourceService.Format("Format.DestinationPathLabel", _destinationPath);
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
            await ShowMessageAsync("Error.FoldersNotConfigured", "Error.SelectSourceAndDestFirst");
            return;
        }

        if (!ValidatePaths(_sourcePath, _destinationPath, out string validationMessage))
        {
            await ShowMessageAsync("Error.UnableToStart", validationMessage);
            return;
        }

        ResetProgress();
        CopyProgressRow.Visibility = _copyOptions.SkipCopy
            ? Visibility.Collapsed
            : Visibility.Visible;
        VerifyProgressRow.Visibility = _copyOptions.VerifyFiles
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        StatusText.Text = ResourceService.GetString("Status.Scanning");
        StatusText.Foreground = (SolidColorBrush)Application.Current.Resources["MutedTextBrush"];
        PhaseText.Text = ResourceService.GetString("Status.CountingFiles");
        LogText.Text = ResourceService.GetString("Info.ScanningDescription");
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
            LogText.Text = ResourceService.Format("Format.TaskReportSaveFailed", ex.Message);
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
        StatusText.Text = ResourceService.GetString("Status.WaitingDuplicateChoices");
        PhaseText.Text = ResourceService.GetString("Info.ChooseActionPerFile");
        LogText.Text = ResourceService.GetString("Info.OtherFilesContinue");
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
            await ShowMessageAsync(ResourceService.GetString("Error.FileUnavailable"), ResourceService.Format("Format.FileNotFound", path));
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
            await ShowMessageAsync("Error.CannotOpenExplorer", ex.Message);
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
        DuplicateSelectionHint.Text = ResourceService.GetString("Status.ApplyingIndividualChoices");
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
        DuplicateSummaryText.Text = ResourceService.Format("Format.FoundNDuplicates", _duplicateChoices.Count.ToString("N0"));
        DuplicateSelectionHint.Text = _copyOptions.ExistingFilePolicy == ExistingFilePolicy.Ask
            ? ResourceService.Format("Format.ChoicesMadeNOfMSelectedK", decided.ToString(), _duplicateChoices.Count.ToString(), selected.ToString())
            : ResourceService.Format("Format.HandlingAs", GetDuplicatePolicyText(_copyOptions.ExistingFilePolicy));

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
        ExistingFilePolicy.Overwrite => ResourceService.GetString("Button.OverwriteSelected"),
        ExistingFilePolicy.Skip => ResourceService.GetString("Button.SkipSelected"),
        ExistingFilePolicy.CreateCopy => ResourceService.GetString("Button.CopySelected"),
        _ => ResourceService.GetString("Button.AskEachFile")
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
            DuplicateSummaryText.Text = ResourceService.Format("Format.DuplicateFileRecords", _duplicateChoices.Count.ToString("N0"));
            DuplicateSelectionHint.Text = ResourceService.GetString("Status.AppliedEachAction");
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
                StatusText.Text = ResourceService.GetString("Status.Scanning");
                PhaseText.Text = ResourceService.GetString("Status.ReadingDirectories");
                break;

            case CopyPhase.Copying:
                _copiedBytes = info.ProcessedBytes;
                _copiedFiles = info.ProcessedFiles;
                _copyElapsed = info.Elapsed;
                StatusText.Text = _isPaused ? ResourceService.GetString("Status.Paused") : ResourceService.GetString("Status.Copying");
                PhaseText.Text = ResourceService.GetString("Status.CopyingFiles");
                double copyPercent = GetPercent(_copiedBytes, _copiedFiles);
                CopyProgress.Value = copyPercent;
                bool copyFinished = _copiedFiles >= info.TotalFiles;
                CopyProgress.Visibility = copyFinished ? Visibility.Collapsed : Visibility.Visible;
                CopyCompletedBadge.Visibility = copyFinished ? Visibility.Visible : Visibility.Collapsed;
                CopyCompletedText.Text = ResourceService.GetString("Common.Completed");
                CopySpeedText.Text = $"{FormatBytes(info.BytesPerSecond)}/s";
                CopyTimeText.Text = FormatDuration(info.Elapsed);
                CopyCountText.Text = $"{info.ProcessedFiles}/{info.TotalFiles}";
                UpdateOverallProgress();
                break;

            case CopyPhase.Verifying:
                _verifiedFiles = info.ProcessedFiles;
                _verifiedBytes = info.ProcessedBytes;
                _verifyElapsed = info.Elapsed;
                StatusText.Text = _isPaused ? ResourceService.GetString("Status.Paused") : ResourceService.GetString("Status.Verifying");
                PhaseText.Text = ResourceService.GetString("Status.SHA256Verification");
                double verifyPercent = GetPercent(info.ProcessedBytes, info.ProcessedFiles);
                VerifyProgress.Value = verifyPercent;
                bool verificationFinished = _verifiedFiles >= info.TotalFiles;
                VerifyProgress.Visibility = verificationFinished ? Visibility.Collapsed : Visibility.Visible;
                VerifyCompletedBadge.Visibility = verificationFinished ? Visibility.Visible : Visibility.Collapsed;
                VerifyCompletedText.Text = ResourceService.GetString("Common.Completed");
                VerifySpeedText.Text = $"{FormatBytes(info.BytesPerSecond)}/s";
                VerifyTimeText.Text = FormatDuration(info.Elapsed);
                VerifyCountText.Text = $"{info.ProcessedFiles}/{info.TotalFiles}";
                UpdateOverallProgress();
                break;

            case CopyPhase.WaitingForDuplicateDecision:
                StatusText.Text = ResourceService.GetString("Status.WaitingDuplicateChoices");
                PhaseText.Text = ResourceService.GetString("Info.ChooseActionBelow");
                CurrentFileText.Text = ResourceService.Format("Format.RecordedNDuplicates", _duplicateChoices.Count.ToString("N0"));
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
                CopyCompletedText.Text = _copyOptions.SkipCopy ? ResourceService.GetString("Common.Disabled") : ResourceService.GetString("Common.Completed");
                VerifyProgress.Visibility = Visibility.Collapsed;
                VerifyCompletedBadge.Visibility = Visibility.Visible;
                VerifyCompletedText.Text = _copyOptions.VerifyFiles ? ResourceService.GetString("Common.Completed") : ResourceService.GetString("Common.Disabled");
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
        PauseText.Text = _isPaused ? ResourceService.GetString("Button.Resume") : ResourceService.GetString("Button.Pause");
        PauseIcon.Glyph = _isPaused ? "\uE768" : "\uE769";
        StatusText.Text = _isPaused ? ResourceService.GetString("Status.Paused") : ResourceService.GetString("Status.ProcessingResumed");
        LogText.Text = _isPaused ? ResourceService.GetString("Info.TaskPaused") : ResourceService.GetString("Info.TaskResumed");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = ResourceService.GetString("Status.Cancelling");
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
            await ShowMessageAsync("Error.TaskRunning", "Error.FinishOrCancelFirst");
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
        SourcePathText.Text = ResourceService.GetString("Info.NotSelected");
        DestinationPathText.Text = ResourceService.GetString("Info.NotSelected");
        PriorityExecutionToggle.IsOn = false;
        UseFastCopyAlgorithmToggle.IsOn = false;
        PreventSleepToggle.IsOn = true;
        HeroNameText.Text = ResourceService.GetString("Info.PrepareNewTask");
        CurrentFileText.Text = ResourceService.GetString("Info.SelectSourceAndDest");
        LogText.Text = ResourceService.GetString("Info.Ready");
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
        CopyProgressRow.Visibility = job.CopyEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        VerifyProgressRow.Visibility = job.VerificationEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        CopyProgress.Visibility = copyFinished ? Visibility.Collapsed : Visibility.Visible;
        CopyCompletedBadge.Visibility = copyFinished ? Visibility.Visible : Visibility.Collapsed;
        CopyCompletedText.Text = job.CopyEnabled ? ResourceService.GetString("Common.Completed") : ResourceService.GetString("Common.Disabled");
        VerifyProgress.Visibility = verificationFinished || !job.VerificationEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;
        VerifyCompletedBadge.Visibility = verificationFinished || !job.VerificationEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        VerifyCompletedText.Text = !job.VerificationEnabled
            ? ResourceService.GetString("Common.Disabled")
            : job.Status == JobStatus.VerificationFailed ? ResourceService.GetString("Error.VerificationFailed") : ResourceService.GetString("Common.Completed");
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
        StatusText.Text = ResourceService.GetString(job.StatusText);
        PauseButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = IsBatchDeletable(job)
            ? Visibility.Visible
            : Visibility.Collapsed;
        StartVerificationButton.Visibility = CanStartVerification(job)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExportReportButton.Visibility = IsReportable(job)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RestartJobButton.Visibility = job.CanRestart
            ? Visibility.Visible
            : Visibility.Collapsed;
        RestartJobButtonText.Text = ResourceService.GetString("Button.Restart");
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
                ? ResourceService.GetString("Result.CopiedFailedFilesSkipped")
                : ResourceService.GetString("Result.VerifiedFailedFilesSkipped"),
            JobStatus.Completed => job.CopyEnabled && job.VerificationEnabled
                ? ResourceService.GetString("Result.CopyAndVerifyCompleted")
                : job.CopyEnabled
                    ? ResourceService.GetString("Result.CopyCompleted")
                    : ResourceService.GetString("Result.SHA256VerificationCompleted"),
            JobStatus.VerificationFailed => job.CopyEnabled
                ? ResourceService.GetString("Result.CopyCompletedButVerifyFailed")
                : ResourceService.GetString("Result.SHA256VerificationFailed"),
            JobStatus.Cancelled => ResourceService.GetString("Error.TaskCancelledKeptShort"),
            JobStatus.Interrupted => ResourceService.GetString("Error.AppExitedBeforeFinishShort"),
            JobStatus.Failed => ResourceService.GetString("Error.TaskExecutionFailed"),
            _ => ResourceService.GetString("Dialog.TaskRecord")
        };
        LogText.Text = job.Status switch
        {
            JobStatus.CompletedWithErrors => ResourceService.Format("Format.TaskPartiallyCompletedSkipped", job.FailedFiles.Count.ToString("N0")),
            JobStatus.Completed => job.CopyEnabled && job.VerificationEnabled
                ? ResourceService.Format("Format.TaskCompletedCopiedVerified", job.FileCount.ToString("N0"))
                : job.CopyEnabled
                    ? ResourceService.Format("Format.CopyCompletedCopied", job.FileCount.ToString("N0"))
                    : ResourceService.Format("Format.VerificationCompletedAllPassed", job.FileCount.ToString("N0")),
            JobStatus.VerificationFailed => job.ErrorMessage ?? ResourceService.GetString("Error.VerificationMismatch"),
            JobStatus.Cancelled => ResourceService.GetString("Error.TaskCancelledKept"),
            JobStatus.Interrupted => ResourceService.GetString("Error.InterruptedRecord"),
            _ => job.ErrorMessage ?? ResourceService.GetString("Error.TaskNotFinished")
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
        StartVerificationButton.Visibility = Visibility.Collapsed;
        ExportReportButton.Visibility = Visibility.Collapsed;
        RestartJobButton.Visibility = Visibility.Collapsed;
        BatchActionPanel.Visibility = Visibility.Visible;
        MultiSelectButtonText.Text = ResourceService.GetString("Common.Done");
        BatchDeleteButtonText.Text = ResourceService.GetString("Button.BatchDelete");
        BatchReportButtonText.Text = ResourceService.GetString("Button.BatchCreateReports");
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
        MultiSelectButtonText.Text = ResourceService.GetString("Button.MultiSelect");
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
        job.CanExportReport;

    private static bool IsReportable(JobHistoryItem job) =>
        job.CanExportReport;

    private static bool CanStartVerification(JobHistoryItem job) =>
        job.CanStartVerification;

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
            ? ResourceService.GetString("Info.SelectTask")
            : ResourceService.Format("Format.SelectedNTasksOnlyFinished", selected.Count.ToString("N0"));
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
            await ShowMessageAsync("Error.CannotBatchDeleteTitle", "Error.CannotBatchDelete");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = ResourceService.Format("Format.DeleteNTaskRecords", selected.Count.ToString("N0")),
            Content = ResourceService.GetString("Error.DeleteBatchReminder"),
            PrimaryButtonText = ResourceService.GetString("Button.BatchDelete"),
            CloseButtonText = ResourceService.GetString("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot
        };
        if (await ShowLocalizedDialogAsync(dialog) != ContentDialogResult.Primary)
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
        LogText.Text = ResourceService.Format("Format.DeletedNTaskRecords", selected.Count.ToString("N0"));
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
            await ShowMessageAsync("Error.ReportUnavailable", "Error.ReportNotAvailableQueued");
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
            LogText.Text = ResourceService.Format("Format.CreatedNReportsInPath", folder.Path, created.ToString("N0"));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Error.BatchReportFailed",
                ResourceService.Format("Format.BatchReportFailedDetail", created.ToString("N0"), ex.Message));
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
        StartVerificationButton.Visibility = Visibility.Collapsed;
        ExportReportButton.Visibility = Visibility.Collapsed;
        RestartJobButton.Visibility = Visibility.Collapsed;
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
        OverwriteFailedFilesButton.Visibility = Visibility.Collapsed;
        OverwriteFailedFilesButton.IsEnabled = false;
        RetryFailedFilesButton.IsEnabled = false;
        SkipFailedFilesButton.IsEnabled = false;
        DuplicateList.IsEnabled = true;
        ApplyDuplicateChoicesButton.Visibility = Visibility.Visible;
        ApplyDuplicateChoicesButton.IsEnabled = false;
        DuplicateSelectionHint.Text = ResourceService.GetString("Info.ChooseActionEach");
        DuplicateSelectAllCheckBox.IsChecked = false;
        DuplicateSelectAllCheckBox.IsEnabled = false;
        BatchOverwriteButton.IsEnabled = false;
        BatchSkipButton.IsEnabled = false;
        BatchCreateCopyButton.IsEnabled = false;
        OverallProgress.Value = 0;
        CopyProgress.Value = 0;
        VerifyProgress.Value = 0;
        CopyProgressRow.Visibility = Visibility.Visible;
        VerifyProgressRow.Visibility = Visibility.Visible;
        CopyProgress.Visibility = Visibility.Visible;
        VerifyProgress.Visibility = Visibility.Visible;
        CopyCompletedBadge.Visibility = Visibility.Collapsed;
        VerifyCompletedBadge.Visibility = Visibility.Collapsed;
        CopyCompletedText.Text = ResourceService.GetString("Common.Completed");
        VerifyCompletedText.Text = ResourceService.GetString("Common.Completed");
        VerifyCompletedBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 0x15, 0xA8, 0x77));
        PercentText.Text = "0.00%";
        PercentText.Visibility = Visibility.Visible;
        CompletionIcon.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = Visibility.Collapsed;
        StartVerificationButton.Visibility = Visibility.Collapsed;
        ExportReportButton.Visibility = Visibility.Collapsed;
        RestartJobButton.Visibility = Visibility.Collapsed;
        PauseButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = ResourceService.GetString("Status.WaitingSetup");
        StatusText.FontSize = 15;
        StatusText.Foreground = (SolidColorBrush)Application.Current.Resources["MutedTextBrush"];
        PhaseText.Text = ResourceService.GetString("Status.WaitingStart");
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
        PauseText.Text = ResourceService.GetString("Button.Pause");
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
            LogText.Text = ResourceService.Format("Format.HistorySaveFailed", ex.Message);
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
        report.AppendLine($"校验算法：{(result.VerificationPerformed ? "SHA-256" : ResourceService.GetString("Common.Disabled"))}");
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
            message = ResourceService.GetString("Error.SourceDestSame");
            return false;
        }
        if (normalizedDestination.StartsWith(normalizedSource + Path.DirectorySeparatorChar, comparison))
        {
            message = ResourceService.GetString("Error.DestInsideSource");
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
            Title = ResourceService.GetString(title),
            Content = ResourceService.GetString(message),
            CloseButtonText = ResourceService.GetString("Common.OK"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task<ContentDialogResult> ShowLocalizedDialogAsync(ContentDialog dialog)
    {
        return await dialog.ShowAsync();
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
