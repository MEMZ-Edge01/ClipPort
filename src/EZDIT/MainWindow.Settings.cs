using EZDIT.Models;
using EZDIT.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EZDIT;

public sealed partial class MainWindow
{
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        TaskWorkspace.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        SettingsPage.Initialize(_appSettings);
        AppTitleText.Text = ResourceService.GetString("Settings.PageTitle");
    }

    private void SettingsPage_BackRequested(object? sender, EventArgs e)
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        TaskWorkspace.Visibility = Visibility.Visible;
        AppTitleText.Text = "EZ DIT-beta";
        ApplyTheme();
        if (_historyLoaded)
        {
            RefreshSelectedRuntime();
        }
    }

    private async void SettingsPage_SettingsChanged(object? sender, EventArgs e)
    {
        AppLanguage requestedLanguage = _appSettings.Language;
        AppLanguage previousLanguage = _previousLanguage;
        bool languageChanged = requestedLanguage != previousLanguage;

        _historyService.SetReportsDirectory(_appSettings.LogAndReportDirectory);
        _logService.SetDirectory(_appSettings.LogAndReportDirectory);
        ApplyTheme();

        try
        {
            await App.SettingsService.SaveAsync(_appSettings);
        }
        catch (Exception ex)
        {
            if (languageChanged)
            {
                // Keep the UI and persisted state aligned when saving fails.
                _appSettings.Language = previousLanguage;
                SettingsPage.Initialize(_appSettings);
            }
            LogText.Text = ResourceService.Format("Format.SettingsSaveFailed", ex.Message);
            return;
        }

        if (languageChanged)
        {
            _previousLanguage = requestedLanguage;
            await ShowLanguageRestartDialogAsync();
        }
    }

    private async Task ShowLanguageRestartDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = ResourceService.GetString("Settings.Language"),
            Content = ResourceService.GetString("Info.LanguageRestartRequired"),
            CloseButtonText = ResourceService.GetString("Common.OK"),
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void SettingsPage_BrowseDirectoryRequested(object? sender, EventArgs e)
    {
        string? path = await PickFolderAsync(ResourceService.GetString("Button.SelectFolder"));
        if (!string.IsNullOrWhiteSpace(path))
        {
            SettingsPage.SetOutputDirectory(path);
        }
    }

    /// <summary>
    /// Applies theme, accent, and title-bar appearance only.
    /// Language is selected before XAML loads at app startup and requires a restart to change.
    /// </summary>
    private void ApplyTheme()
    {
        if (_isApplyingAppearance)
        {
            return;
        }
        _isApplyingAppearance = true;
        try
        {
            ThemeManager.Apply(RootGrid, _appSettings);
            AdjustButtonLayoutForLanguage();

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

    /// <summary>
    /// Adjusts the sidebar width for the current language.
    /// English labels are wider than Chinese, so we use a larger column.
    /// </summary>
    private void AdjustButtonLayoutForLanguage()
    {
        bool isEnglish = _appSettings.Language == Models.AppLanguage.English;
        TaskWorkspace.ColumnDefinitions[0].Width = isEnglish ? new GridLength(340) : new GridLength(292);
    }

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_appSettings.Theme == Models.AppThemeMode.System)
        {
            ThemeManager.Apply(RootGrid, _appSettings);
        }
    }

    private void SystemColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
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
