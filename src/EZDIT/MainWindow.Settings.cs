using EZDIT.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace EZDIT;

public sealed partial class MainWindow
{
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        TaskWorkspace.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        SettingsPage.Initialize(_appSettings);
        AppTitleText.Text = LocalizationService.Text("设置");
    }

    private void SettingsPage_BackRequested(object? sender, EventArgs e)
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        TaskWorkspace.Visibility = Visibility.Visible;
        AppTitleText.Text = "EZ DIT-beta";
        ApplyAppearanceAndLanguage();
    }

    private async void SettingsPage_SettingsChanged(object? sender, EventArgs e)
    {
        LocalizationService.SetLanguage(_appSettings.Language);
        _historyService.SetReportsDirectory(_appSettings.LogAndReportDirectory);
        _logService.SetDirectory(_appSettings.LogAndReportDirectory);
        ApplyAppearanceAndLanguage();
        try
        {
            await App.SettingsService.SaveAsync(_appSettings);
        }
        catch (Exception ex)
        {
            LogText.Text = LocalizationService.Format("设置保存失败：{0}", ex.Message);
        }
    }

    private async void SettingsPage_BrowseDirectoryRequested(object? sender, EventArgs e)
    {
        string? path = await PickFolderAsync(LocalizationService.Text("选择文件夹"));
        if (!string.IsNullOrWhiteSpace(path))
        {
            SettingsPage.SetOutputDirectory(path);
        }
    }

    private void ApplyAppearanceAndLanguage()
    {
        if (_isApplyingAppearance)
        {
            return;
        }
        _isApplyingAppearance = true;
        try
        {
            ThemeManager.Apply(RootGrid, _appSettings);
            LocalizationService.SetLanguage(_appSettings.Language);
            AdjustButtonLayoutForLanguage();
            LocalizationService.Apply(RootGrid);
            LocalizationService.Apply(NewTaskDialog);
            LocalizeNewTaskDialog();
            SettingsPage.Localize();
            if (_historyLoaded)
            {
                RefreshSelectedRuntime();
            }

            bool dark = RootGrid.RequestedTheme == ElementTheme.Dark ||
                        RootGrid.RequestedTheme == ElementTheme.Default && RootGrid.ActualTheme == ElementTheme.Dark;
            if (AppWindow?.TitleBar is not null)
            {
                AppWindow.TitleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
                AppWindow.TitleBar.ButtonInactiveForegroundColor = dark
                    ? ColorHelper.FromArgb(160, 255, 255, 255)
                    : ColorHelper.FromArgb(160, 0, 0, 0);
                AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }
        finally
        {
            _isApplyingAppearance = false;
        }
    }

    private void AdjustButtonLayoutForLanguage()
    {
        bool isEnglish = _appSettings.Language == Models.AppLanguage.English;
        MultiSelectButtonText.Text = LocalizationService.Text("多选");
        TaskWorkspace.ColumnDefinitions[0].Width = isEnglish ? new GridLength(340) : new GridLength(292);
    }

    private void LocalizeTaskUi() =>
        LocalizationService.Apply(TaskWorkspace);

    private void LocalizeNewTaskDialog()
    {
        // Explicitly translate all inner elements of the Create Task dialog,
        // since VisualTreeHelper may not traverse ContentDialog content
        // when it is not yet in the visual tree.
        DialogSourceLabel.Text = LocalizationService.Text("数据源");
        DialogCopyLabel.Text = LocalizationService.Text("拷贝");
        DialogCopyDestLabel.Text = LocalizationService.Text("拷贝目的地");
        DialogSourcePathText.Text = LocalizationService.Text("请选择源目录或存储卡");
        DialogDestinationPathText.Text = LocalizationService.Text("请选择文件拷贝目的地");
        EnableCopyToggle.Header = LocalizationService.Text("拷贝文件");
        EnableCopyToggle.OnContent = LocalizationService.Text("开启");
        EnableCopyToggle.OffContent = LocalizationService.Text("关闭");
        DestinationSubfolderNameLabel.Text = LocalizationService.Text("拷贝目的地文件夹名");
        DestinationSubfolderHintText.Text = LocalizationService.Text("留空即不创建子文件夹");
        DuplicateHandlingLabel.Text = LocalizationService.Text("重复项处理");
        AskExistingRadio.Content = LocalizationService.Text("询问");
        OverwriteExistingRadio.Content = LocalizationService.Text("覆盖");
        SkipExistingRadio.Content = LocalizationService.Text("跳过");
        CreateCopyRadio.Content = LocalizationService.Text("创建副本");
        DuplicateAskHintText.Text = LocalizationService.Text("询问模式会先继续处理其他文件，再逐个处理检测到的重复文件。");
        VerifyFilesToggle.Header = LocalizationService.Text("文件校验（SHA-256）");
        VerifyFilesToggle.OnContent = LocalizationService.Text("开启");
        VerifyFilesToggle.OffContent = LocalizationService.Text("关闭");
        PreventSleepToggle.Header = LocalizationService.Text("任务期间阻止电脑休眠");
        PreventSleepToggle.OnContent = LocalizationService.Text("开启");
        PreventSleepToggle.OffContent = LocalizationService.Text("关闭");
        PriorityExecutionToggle.Header = LocalizationService.Text("优先执行");
        PriorityExecutionToggle.OnContent = LocalizationService.Text("开启");
        PriorityExecutionToggle.OffContent = LocalizationService.Text("关闭");
        PriorityHintText.Text = LocalizationService.Text("优先任务会并行执行；其他普通任务会在安全检查点等待，直到全部优先任务结束。");
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_appSettings.Theme == Models.AppThemeMode.System)
        {
            ThemeManager.Apply(RootGrid, _appSettings);
        }
    }

    private void SystemColorValuesChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appSettings.Theme == Models.AppThemeMode.System ||
                _appSettings.Accent == Models.AppAccentMode.System)
            {
                ThemeManager.Apply(RootGrid, _appSettings);
            }
        });
    }
}
