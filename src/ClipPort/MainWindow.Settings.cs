using ClipPort.Models;
using ClipPort.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClipPort;

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
        AppTitleText.Text = "ClipPort-beta";
        ApplyTheme();
        if (_historyLoaded)
        {
            RefreshSelectedRuntime();
        }
    }

    private async void SettingsPage_SettingsChanged(object? sender, EventArgs e)
    {
        AppSettings requestedSettings = CloneSettings(_appSettings);
        AppLanguage requestedLanguage = requestedSettings.Language;
        AppLanguage previousLanguage = _previousLanguage;
        bool languageChanged = requestedLanguage != previousLanguage;

        _historyService.SetReportsDirectory(_appSettings.LogAndReportDirectory);
        _logService.SetDirectory(_appSettings.LogAndReportDirectory);
        ApplyTheme();

        try
        {
            await App.SettingsService.SaveAsync(requestedSettings);
        }
        catch (Exception ex)
        {
            // A failed save must not leave the visible settings ahead of
            // what will actually be restored on the next launch.
            ApplySettingsSnapshot(_lastSavedSettings);
            _historyService.SetReportsDirectory(_appSettings.LogAndReportDirectory);
            _logService.SetDirectory(_appSettings.LogAndReportDirectory);
            SettingsPage.Initialize(_appSettings);
            ApplyTheme();
            LogText.Text = ResourceService.Format("Format.SettingsSaveFailed", ex.Message);
            return;
        }

        _lastSavedSettings = requestedSettings;
        if (languageChanged)
        {
            _previousLanguage = requestedLanguage;
            await ShowLanguageRestartDialogAsync();
        }
    }

    private static AppSettings CloneSettings(AppSettings settings) => new()
    {
        Theme = settings.Theme,
        Accent = settings.Accent,
        Language = settings.Language,
        LogAndReportDirectory = settings.LogAndReportDirectory
    };

    private void ApplySettingsSnapshot(AppSettings settings)
    {
        _appSettings.Theme = settings.Theme;
        _appSettings.Accent = settings.Accent;
        _appSettings.Language = settings.Language;
        _appSettings.LogAndReportDirectory = settings.LogAndReportDirectory;
    }

    private async Task ShowLanguageRestartDialogAsync()
    {
        var dialog = new ContentDialog
        {
            Title = ResourceService.GetString("Settings.Language"),
            Content = ResourceService.GetString("Info.LanguageRestartRequired"),
            PrimaryButtonText = ResourceService.GetString("Button.RestartNow"),
            CloseButtonText = ResourceService.GetString("Button.RestartLater"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            // Register the replacement before closing. The AppWindow closing
            // event can otherwise race with WinUI's dispatcher teardown.
            if (await TryScheduleApplicationRestartAsync())
            {
                Close();
            }
        }
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
            else
            {
                ThemeManager.RefreshSystemAccentPreview();
            }
        });
    }
}
