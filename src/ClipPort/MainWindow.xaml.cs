using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using ClipPort.Models;
using ClipPort.Services;
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

namespace ClipPort;

public sealed partial class MainWindow : Window
{
    private const double TaskContentMaximumWidth = 1180;
    private const double TaskContentHorizontalMargin = 92;

    private readonly FileCopyService _copyService = new();
    private readonly AppSettings _appSettings = App.Settings;
    private readonly JobHistoryService _historyService;
    private readonly AppLogService _logService;
    private readonly ExplorerContextMenuService _explorerContextMenuService = new();
    private readonly UISettings _uiSettings = new();
    private readonly ObservableCollection<JobHistoryItem> _history = [];
    private readonly ObservableCollection<DuplicateConflictChoice> _duplicateChoices = [];
    private readonly ObservableCollection<FailedFileChoice> _failedFileChoices = [];
    private string? _sourcePath;
    private string? _destinationPath;
    private string? _destinationParentPath;
    private string? _dialogSourcePath;
    private string? _dialogDestinationParentPath;
    private string? _automaticDialogSubfolderName;
    private bool _quickStartDialogOpen;
    private CopyOptions _copyOptions = new();
    private bool _historyLoaded;
    private AppLanguage _previousLanguage;
    private AppSettings _lastSavedSettings = null!;
    private bool _isMultiSelectMode;
    private bool _isChangingMultiSelectMode;
    private bool _updatingDuplicateSelection;
    private bool _isApplyingAppearance;
    private JobHistoryItem? _selectedJob;

    public MainWindow()
    {
        _historyService = new JobHistoryService(reportsDirectory: _appSettings.LogAndReportDirectory);
        _logService = new AppLogService(_appSettings.LogAndReportDirectory);
        _previousLanguage = _appSettings.Language;
        _lastSavedSettings = CloneSettings(_appSettings);
        InitializeComponent();
        UpdateVerificationAlgorithmDescription();
        UpdateVerificationAlgorithmControls();
        ApplyThroughputChartLayouts();
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
        SettingsPage.ExplorerContextMenuToggleRequested +=
            SettingsPage_ExplorerContextMenuToggleRequested;
        SettingsPage.InstallExplorerCertificateRequested +=
            SettingsPage_InstallExplorerCertificateRequested;
        SettingsPage.UninstallExplorerCertificateRequested +=
            SettingsPage_UninstallExplorerCertificateRequested;
        SettingsPage.InstallExplorerPackageRequested +=
            SettingsPage_InstallExplorerPackageRequested;
        SettingsPage.UninstallExplorerPackageRequested +=
            SettingsPage_UninstallExplorerPackageRequested;
        SettingsPage.RefreshExplorerIntegrationRequested +=
            SettingsPage_RefreshExplorerIntegrationRequested;
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
        _uiSettings.ColorValuesChanged += SystemColorValuesChanged;
        LogText.RegisterPropertyChangedCallback(
            TextBlock.TextProperty,
            (_, _) => _ = _logService.WriteAsync(LogText.Text));
        ApplyTheme();
        AppWindow.Closing += ConcurrentAppWindow_Closing;
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
        appWindow.Title = "ClipPort-beta";

        // Keep the title bar, taskbar, and Alt+Tab identity aligned with the
        // executable icon instead of relying on the generic unpackaged-app icon.
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Icons",
            "clipport-app-icon.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyTheme();
        await SynchronizeExplorerContextMenuAsync();
        if (_historyLoaded)
        {
            return;
        }
        _historyLoaded = true;
        await LoadHistoryAsync();
    }

    private void TaskContentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // An explicit width avoids a WinUI ScrollViewer layout jump when a stretched
        // child crosses MaxWidth, while retaining the same 1180-DIP reading width.
        TaskContentGrid.Width = Math.Min(
            TaskContentMaximumWidth,
            Math.Max(0, e.NewSize.Width - TaskContentHorizontalMargin));
    }

    private async Task LoadHistoryAsync()
    {
        List<JobHistoryItem> items = await _historyService.LoadAsync();
        bool repairedInterruptedJobs = false;
        JobHistoryItem[] ordered = items
            .OrderByDescending(item => item.StartedAt)
            .ToArray();
        foreach (JobHistoryItem item in ordered.Take(200))
        {
            if (item.Status is JobStatus.Running or JobStatus.Queued)
            {
                item.Status = JobStatus.Interrupted;
                item.IsAcknowledged = false;
                item.FinishedAt ??= DateTimeOffset.Now;
                item.ErrorMessage = ResourceService.GetString("Error.AppExitedBeforeFinish");
                repairedInterruptedJobs = true;
            }
            item.ReportPath ??= _historyService.ResolveReportPath(item.ReportFileName);
            _history.Add(item);
        }
        foreach (JobHistoryItem discarded in ordered.Skip(200))
        {
            await _historyService.DeleteReportAsync(GetReportReference(discarded));
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
            // Always generate a relative, filesystem-safe folder name,
            // including when the selected source is a drive root.
            SetAutomaticDialogSubfolderName(path);
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

        // Default to a relative, filesystem-safe folder name.
        if (_dialogSourcePath is not null)
        {
            SetAutomaticDialogSubfolderName(_dialogSourcePath);
        }
        else
        {
            DialogDestinationSubfolderName.Text = "";
            _automaticDialogSubfolderName = null;
        }

        EnableCopyToggle.Toggled += OnEnableCopyToggled;
        OnEnableCopyToggled(EnableCopyToggle, null!);

        try
        {
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

                string subfolderName = (DialogDestinationSubfolderName.Text ?? "").Trim();
                if (!PathSafety.TryResolveSubfolder(
                        _dialogDestinationParentPath,
                        subfolderName,
                        out string destination))
                {
                    await ShowMessageAsync(
                        "Error.UnableToStart",
                        "Error.InvalidSubfolderName");
                    continue;
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
                    SkipCopy: verifyOnly,
                    VerificationAlgorithm: GetSelectedVerificationAlgorithm());
                SourcePathText.Text = _sourcePath;
                DestinationPathText.Text = _destinationPath;
                HeroNameText.Text = GetDisplayName(_sourcePath);
                CurrentFileText.Text = ResourceService.GetString("Info.TaskConfigured");
                LogText.Text = ResourceService.Format("Format.SourcePathLabel", _sourcePath) + "\n" + ResourceService.Format("Format.DestinationPathLabel", _destinationPath);
                UpdateStartButton();
                return true;
            }

            return false;
        }
        finally
        {
            EnableCopyToggle.Toggled -= OnEnableCopyToggled;
        }
    }
    private void DuplicateOverwrite_Click(object sender, RoutedEventArgs e) =>
        ConcurrentDuplicateOverwrite_Click(sender, e);

    private void DuplicateSkip_Click(object sender, RoutedEventArgs e) =>
        ConcurrentDuplicateSkip_Click(sender, e);

    private void DuplicateCreateCopy_Click(object sender, RoutedEventArgs e) =>
        ConcurrentDuplicateCreateCopy_Click(sender, e);

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
        => ConcurrentApplyDuplicateChoicesButton_Click(sender, e);

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
        DuplicateSelectAllCheckBox.IsChecked = false;
        DuplicateSelectAllCheckBox.IsEnabled = false;
        BatchOverwriteButton.IsEnabled = false;
        BatchSkipButton.IsEnabled = false;
        BatchCreateCopyButton.IsEnabled = false;
        if (_duplicateChoices.Count > 0)
        {
            DuplicateSummaryText.Text = ResourceService.Format("Format.DuplicateFileRecords", _duplicateChoices.Count.ToString("N0"));
            DuplicateSelectionHint.Text = ResourceService.GetString("Status.AppliedEachAction");
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
        _sourcePath = null;
        _destinationPath = null;
        _destinationParentPath = null;
        AskExistingRadio.IsChecked = true;
        SourcePathText.Text = ResourceService.GetString("Info.NotSelected");
        DestinationPathText.Text = ResourceService.GetString("Info.NotSelected");
        PriorityExecutionToggle.IsOn = false;
        UseFastCopyAlgorithmToggle.IsOn = false;
        VerificationAlgorithmComboBox.SelectedIndex = 0;
        UpdateVerificationAlgorithmDescription();
        UpdateVerificationAlgorithmControls();
        PreventSleepToggle.IsOn = true;
        HeroNameText.Text = ResourceService.GetString("Info.PrepareNewTask");
        CurrentFileText.Text = ResourceService.GetString("Info.SelectSourceAndDest");
        LogText.Text = ResourceService.GetString("Info.Ready");
        ResetProgress();
        StartButton.Visibility = Visibility.Visible;
        UpdateStartButton();
    }

    private void ShowHistoryJob(JobHistoryItem job)
    {
        ShowHistorySummary(job);
        CurrentFileText.Text = job.ErrorMessage ?? $"{job.SourcePath} → {job.DestinationPath}";
        ShowDuplicateHistory(job);
        ShowFailedFileHistory(job);

        HistoryProgressState progress = CalculateHistoryProgress(job);
        ShowHistoryProgress(job, progress);
        ShowHistoryPerformance(job);
        ShowHistoryActions(job);
        ShowHistoryOutcome(job);
    }

    private void ShowHistorySummary(JobHistoryItem job)
    {
        HeroNameText.Text = job.DisplayName;
        SourcePathText.Text = job.SourcePath;
        DestinationPathText.Text = job.DestinationPath;
        TotalSizeText.Text = FormatBytes(job.TotalBytes);
        TotalCountText.Text = job.FileCount.ToString(
            "N0",
            CultureInfo.InvariantCulture);
        StartTimeText.Text = job.StartedAt.ToString(
            "MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture);
        EndTimeText.Text = job.FinishedAt?.ToString(
            "MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture) ?? "--";
        DurationText.Text = job.DurationText;
    }

    private static HistoryProgressState CalculateHistoryProgress(JobHistoryItem job)
    {
        bool taskFinished = IsTaskFinished(job);
        bool copyFinished = IsCopyFinished(job, taskFinished);
        bool verificationFinished = job.VerificationEnabled && taskFinished;
        double copyPercent = CalculateCopyPercent(job, copyFinished);
        double verifyPercent = CalculateVerifyPercent(job, verificationFinished);

        if (taskFinished)
        {
            copyPercent = job.CopyEnabled ? 100 : 0;
            verifyPercent = job.VerificationEnabled ? 100 : 0;
        }

        return new HistoryProgressState(
            taskFinished,
            copyFinished,
            verificationFinished,
            copyPercent,
            verifyPercent);
    }

    private static bool IsTaskFinished(JobHistoryItem job) =>
        job.Status is JobStatus.Completed or
            JobStatus.CompletedWithErrors or
            JobStatus.VerificationFailed;

    private static bool IsCopyFinished(JobHistoryItem job, bool taskFinished) =>
        !job.CopyEnabled ||
        taskFinished ||
        (job.FileCount > 0 &&
            job.CopiedFiles >= job.FileCount &&
            job.CopiedBytes >= job.TotalBytes);

    private static double CalculateCopyPercent(
        JobHistoryItem job,
        bool copyFinished)
    {
        if (!job.CopyEnabled)
        {
            return 0;
        }

        return job.TotalBytes <= 0
            ? (copyFinished ? 100 : 0)
            : Math.Clamp(job.CopiedBytes * 100d / job.TotalBytes, 0, 100);
    }

    private static double CalculateVerifyPercent(
        JobHistoryItem job,
        bool verificationFinished) =>
        job.FileCount <= 0
            ? (verificationFinished ? 100 : 0)
            : Math.Clamp(job.VerifiedFiles * 100d / job.FileCount, 0, 100);

    private void ShowHistoryProgress(
        JobHistoryItem job,
        HistoryProgressState progress)
    {
        CopyProgress.Value = progress.CopyPercent;
        VerifyProgress.Value = progress.VerifyPercent;
        CopyProgressRow.Visibility = VisibleWhen(job.CopyEnabled);
        VerifyProgressRow.Visibility = VisibleWhen(job.VerificationEnabled);
        CopyProgress.Visibility = VisibleWhen(!progress.CopyFinished);
        CopyCompletedBadge.Visibility = VisibleWhen(progress.CopyFinished);
        CopyCompletedText.Text = GetCopyCompletedText(job);
        bool verificationActive =
            !progress.VerificationFinished && job.VerificationEnabled;
        VerifyProgress.Visibility = VisibleWhen(verificationActive);
        VerifyCompletedBadge.Visibility = VisibleWhen(!verificationActive);
        VerifyCompletedText.Text = GetVerifyCompletedText(job);
        VerifyCompletedBadge.Background = GetVerificationBadgeBrush(job);
        OverallProgress.Value = CalculateOverallProgress(job, progress);
    }

    private static Visibility VisibleWhen(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private static string GetCopyCompletedText(JobHistoryItem job)
    {
        if (!job.CopyEnabled)
        {
            return ResourceService.GetString("Common.Disabled");
        }

        return job.Status == JobStatus.CompletedWithErrors
            ? ResourceService.GetString("Result.CompletedWithErrors")
            : ResourceService.GetString("Common.Completed");
    }

    private static string GetVerifyCompletedText(JobHistoryItem job)
    {
        if (!job.VerificationEnabled)
        {
            return ResourceService.GetString("Common.Disabled");
        }

        return job.Status switch
        {
            JobStatus.VerificationFailed =>
                ResourceService.GetString("Error.VerificationFailed"),
            JobStatus.CompletedWithErrors =>
                ResourceService.GetString("Result.CompletedWithErrors"),
            _ => ResourceService.GetString("Common.Completed")
        };
    }

    private static SolidColorBrush GetVerificationBadgeBrush(JobHistoryItem job)
    {
        Windows.UI.Color color = job.Status == JobStatus.VerificationFailed
            ? ColorHelper.FromArgb(255, 0xE8, 0x46, 0x3A) // Error surface
            : job.VerificationEnabled
                ? ColorHelper.FromArgb(255, 0x15, 0xA8, 0x77) // Success surface
                : ColorHelper.FromArgb(255, 0xE5, 0xE5, 0xE5);
        return new SolidColorBrush(color);
    }

    private static double CalculateOverallProgress(
        JobHistoryItem job,
        HistoryProgressState progress)
    {
        if (progress.TaskFinished)
        {
            return 100;
        }
        if (!job.CopyEnabled)
        {
            return progress.VerifyPercent;
        }
        if (!job.VerificationEnabled)
        {
            return progress.CopyPercent;
        }
        return (progress.CopyPercent * 0.8) +
            (progress.VerifyPercent * 0.2);
    }

    private void ShowHistoryPerformance(JobHistoryItem job)
    {
        CopySpeedText.Text = job.CopyEnabled && job.CopySeconds > 0
            ? $"{FormatBytes(job.CopiedBytes / job.CopySeconds)}/s"
            : "--";
        VerifySpeedText.Text = job.VerifySeconds > 0
            ? $"{FormatBytes(job.TotalBytes / job.VerifySeconds)}/s"
            : "--";
        UpdateThroughputCharts(
            job.CopyByteSpeedSamples,
            job.CopyItemSpeedSamples,
            job.CopyThroughputProgressSamples,
            job.VerifyByteSpeedSamples,
            job.VerifyItemSpeedSamples,
            job.VerifyThroughputProgressSamples);
        CopyTimeText.Text = job.CopyEnabled
            ? FormatDuration(TimeSpan.FromSeconds(job.CopySeconds))
            : "--";
        VerifyTimeText.Text = FormatDuration(TimeSpan.FromSeconds(job.VerifySeconds));
        CopyCountText.Text = job.CopyEnabled
            ? $"{job.CopiedFiles}/{job.FileCount}"
            : "--";
        VerifyCountText.Text = $"{job.VerifiedFiles}/{job.FileCount}";
    }

    private void ShowHistoryActions(JobHistoryItem job)
    {
        CompletionIcon.Visibility = Visibility.Visible;
        CompletionIcon.Glyph = job.StatusGlyph;
        PercentText.Visibility = Visibility.Collapsed;
        StatusText.FontSize = 30;
        StatusText.Text = ResourceService.GetString(job.StatusText);
        PauseButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        DeleteJobButton.Visibility = VisibleWhen(IsBatchDeletable(job));
        StartVerificationButton.Visibility = VisibleWhen(CanStartVerification(job));
        StartVerificationButtonText.Text = ResourceService.GetString(
            job.VerificationEnabled
                ? "Button.Reverify"
                : "Button.StartVerification");
        ExportReportButton.Visibility = VisibleWhen(IsReportable(job));
        RestartJobButton.Visibility = VisibleWhen(job.CanRestart);
        RestartJobButtonText.Text = ResourceService.GetString("Button.Restart");
        StartButton.IsEnabled = false;
        StartButton.Visibility = Visibility.Collapsed;
    }

    private void ShowHistoryOutcome(JobHistoryItem job)
    {
        SolidColorBrush stateBrush = GetHistoryStateBrush(job);
        CompletionIcon.Foreground = stateBrush;
        StatusText.Foreground = stateBrush;
        PhaseText.Text = GetHistoryPhaseText(job);
        LogText.Text = GetHistoryLogText(job);
    }

    private static SolidColorBrush GetHistoryStateBrush(JobHistoryItem job) =>
        new(job.Status == JobStatus.Completed
            ? ColorHelper.FromArgb(255, 0x15, 0xA8, 0x77) // Success
            : ColorHelper.FromArgb(255, 0xE8, 0x46, 0x3A)); // Error

    private static string GetHistoryPhaseText(JobHistoryItem job) =>
        job.Status switch
        {
            JobStatus.CompletedWithErrors => job.CopyEnabled
                ? ResourceService.GetString("Result.CopiedFailedFilesSkipped")
                : ResourceService.GetString("Result.VerifiedFailedFilesSkipped"),
            JobStatus.Completed => GetCompletedHistoryPhaseText(job),
            JobStatus.VerificationFailed => job.CopyEnabled
                ? ResourceService.GetString("Result.CopyCompletedButVerifyFailed")
                : ResourceService.GetString("Result.SHA256VerificationFailed"),
            JobStatus.Cancelled =>
                ResourceService.GetString("Error.TaskCancelledKeptShort"),
            JobStatus.Interrupted =>
                ResourceService.GetString("Error.AppExitedBeforeFinishShort"),
            JobStatus.Failed =>
                ResourceService.GetString("Error.TaskExecutionFailed"),
            _ => ResourceService.GetString("Dialog.TaskRecord")
        };

    private static string GetCompletedHistoryPhaseText(JobHistoryItem job)
    {
        if (!job.CopyEnabled)
        {
            return ResourceService.GetString("Result.SHA256VerificationCompleted");
        }

        return job.VerificationEnabled
            ? ResourceService.GetString("Result.CopyAndVerifyCompleted")
            : ResourceService.GetString("Result.CopyCompleted");
    }

    private static string GetHistoryLogText(JobHistoryItem job) =>
        job.Status switch
        {
            JobStatus.CompletedWithErrors => ResourceService.Format(
                "Format.TaskPartiallyCompletedSkipped",
                job.FailedFiles.Count.ToString("N0")),
            JobStatus.Completed => GetCompletedHistoryLogText(job),
            JobStatus.VerificationFailed => job.ErrorMessage ??
                ResourceService.GetString("Error.VerificationMismatch"),
            JobStatus.Cancelled =>
                ResourceService.GetString("Error.TaskCancelledKept"),
            JobStatus.Interrupted =>
                ResourceService.GetString("Error.InterruptedRecord"),
            _ => job.ErrorMessage ??
                ResourceService.GetString("Error.TaskNotFinished")
        };

    private static string GetCompletedHistoryLogText(JobHistoryItem job)
    {
        string fileCount = job.FileCount.ToString("N0");
        if (!job.CopyEnabled)
        {
            return ResourceService.Format(
                "Format.VerificationCompletedAllPassed",
                fileCount);
        }

        return job.VerificationEnabled
            ? ResourceService.Format("Format.TaskCompletedCopiedVerified", fileCount)
            : ResourceService.Format("Format.CopyCompletedCopied", fileCount);
    }

    private readonly record struct HistoryProgressState(
        bool TaskFinished,
        bool CopyFinished,
        bool VerificationFinished,
        double CopyPercent,
        double VerifyPercent);

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
                string? report = await _historyService.ReadReportAsync(GetReportReference(job));
                report ??= BuildIncompleteReport(job);
                string displayName = SanitizeReportFileName(job.DisplayName);
                string fileName = $"ClipPort_Report_{job.StartedAt:yyyyMMdd_HHmmss}_{displayName}.txt";
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

    private void UpdateStartButton()
    {
        StartButton.IsEnabled = _selectedJob is null;
    }

    private void SetAutomaticDialogSubfolderName(string sourcePath)
    {
        _automaticDialogSubfolderName =
            PathSafety.GetSuggestedSubfolderName(sourcePath, DateTime.Now);
        DialogDestinationSubfolderName.Text = _automaticDialogSubfolderName;
    }

    private void ResetProgress()
    {
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
        UpdateThroughputCharts(
            EmptyWaveformSamples,
            EmptyWaveformSamples,
            EmptyWaveformSamples,
            EmptyWaveformSamples,
            EmptyWaveformSamples,
            EmptyWaveformSamples);
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
        if (index < 0)
        {
            for (int candidateIndex = 0; candidateIndex < _history.Count; candidateIndex++)
            {
                if (string.Equals(
                        _history[candidateIndex].Id,
                        item.Id,
                        StringComparison.Ordinal))
                {
                    index = candidateIndex;
                    break;
                }
            }
        }
        if (index >= 0 && !ReferenceEquals(_history[index], item))
        {
            _history[index] = item;
        }
        SyncTaskSection(item);
    }

    private bool TrimHistory()
    {
        bool changed = false;
        while (_history.Count > 200)
        {
            int removableIndex = HistoryRetentionPolicy.FindOldestRemovableIndex(
                _history,
                _jobRuntimes.ContainsKey);
            if (removableIndex < 0)
            {
                // Active tasks are retained even if temporary concurrency
                // pushes the collection past its persisted-history limit.
                break;
            }

            JobHistoryItem removed = _history[removableIndex];
            _history.RemoveAt(removableIndex);
            _ = _historyService.DeleteReportAsync(GetReportReference(removed));
            RemoveTaskFromSections(removed);
            changed = true;
        }
        return changed;
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

    private static string? GetReportReference(JobHistoryItem job) =>
        !string.IsNullOrWhiteSpace(job.ReportPath)
            ? job.ReportPath
            : job.ReportFileName;

    private static string BuildReport(CopyResult result, JobHistoryItem job) =>
        TaskReportBuilder.Build(result, job);

    private static string BuildIncompleteReport(JobHistoryItem job) =>
        TaskReportBuilder.BuildIncomplete(job);
    private static bool ValidatePaths(string source, string destination, out string message)
    {
        if (!PathSafety.TryValidateSourceAndDestination(
                source,
                destination,
                out PathValidationError error))
        {
            message = ResourceService.GetString(error switch
            {
                PathValidationError.SourceAndDestinationAreSame => "Error.SourceDestSame",
                PathValidationError.DestinationIsInsideSource => "Error.DestInsideSource",
                PathValidationError.InvalidSubfolderName => "Error.InvalidSubfolderName",
                PathValidationError.DestinationContainsReparsePoint => "Error.DestinationContainsReparsePoint",
                PathValidationError.InvalidPath => "Error.InvalidPath",
                _ => "Error.UnableToStart"
            });
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

    private static string FormatBytes(double bytes) =>
        DisplayFormatting.FormatBytes(bytes);

    private static string FormatDuration(TimeSpan value) =>
        DisplayFormatting.FormatDuration(value);

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

    private void OnEnableCopyToggled(object sender, RoutedEventArgs e)
    {
        bool enabled = EnableCopyToggle.IsOn;
        DialogDestinationSubfolderName.IsEnabled = enabled;
        VerifyFilesToggle.IsEnabled = enabled;
        if (!enabled)
        {
            DialogDestinationSubfolderName.Text = "";
            _automaticDialogSubfolderName = null;
            VerifyFilesToggle.IsOn = true;
        }
        AskExistingRadio.IsEnabled = enabled;
        OverwriteExistingRadio.IsEnabled = enabled;
        SkipExistingRadio.IsEnabled = enabled;
        CreateCopyRadio.IsEnabled = enabled;
        UpdateVerificationAlgorithmControls();
    }

    private void VerifyFilesToggle_Toggled(object sender, RoutedEventArgs e) =>
        UpdateVerificationAlgorithmControls();

    private void VerificationAlgorithmComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        UpdateVerificationAlgorithmDescription();

    private VerificationAlgorithmKind GetSelectedVerificationAlgorithm()
    {
        if (VerificationAlgorithmComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse(tag, ignoreCase: true, out VerificationAlgorithmKind algorithm))
        {
            return VerificationAlgorithms.Normalize(algorithm);
        }

        return VerificationAlgorithmKind.Sha256;
    }

    private void UpdateVerificationAlgorithmDescription()
    {
        if (VerificationAlgorithmHintText is null)
        {
            return;
        }

        VerificationAlgorithmHintText.Text = ResourceService.GetString(
            VerificationAlgorithms.GetDescriptionResourceKey(GetSelectedVerificationAlgorithm()));
    }

    private void UpdateVerificationAlgorithmControls()
    {
        if (VerificationAlgorithmComboBox is null || VerificationAlgorithmHintText is null)
        {
            return;
        }

        // Verification-only tasks always execute a hash comparison, even though
        // the ordinary verification toggle is locked on in that mode.
        bool verificationWillRun = !EnableCopyToggle.IsOn || VerifyFilesToggle.IsOn;
        VerificationAlgorithmComboBox.IsEnabled = verificationWillRun;
        VerificationAlgorithmHintText.Opacity = verificationWillRun ? 1 : 0.55;
    }
}
