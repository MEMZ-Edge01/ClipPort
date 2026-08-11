using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using ClipPort.Models;
using ClipPort.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClipPort.Views;

public sealed partial class SettingsView : UserControl
{
    private AppSettings? _settings;
    private bool _initializing;
    private readonly ExplorerIntegrationOperationGate _explorerIntegrationOperationGate =
        new();
    private readonly UpdateService _updateService = new();
    private GitHubRelease? _pendingUpdate;
    private GitHubReleaseAsset? _pendingZipAsset;
    private bool _isUpdateOperationInProgress;

    public event EventHandler? BackRequested;
    public event EventHandler? SettingsChanged;
    public event EventHandler? BrowseDirectoryRequested;
    public event EventHandler<ExplorerContextMenuToggleRequestedEventArgs>?
        ExplorerContextMenuToggleRequested;
    public event EventHandler<LegacyExplorerContextMenuToggleRequestedEventArgs>?
        LegacyExplorerContextMenuToggleRequested;
    public event EventHandler<ExplorerIntegrationOperationRequestedEventArgs>?
        InstallExplorerCertificateRequested;
    public event EventHandler<ExplorerIntegrationOperationRequestedEventArgs>?
        UninstallExplorerCertificateRequested;
    public event EventHandler<ExplorerIntegrationOperationRequestedEventArgs>?
        InstallExplorerPackageRequested;
    public event EventHandler<ExplorerIntegrationOperationRequestedEventArgs>?
        UninstallExplorerPackageRequested;
    public event EventHandler<ExplorerIntegrationOperationRequestedEventArgs>?
        RefreshExplorerIntegrationRequested;

    public SettingsView()
    {
        InitializeComponent();

        // Set ComboBoxItem content via ResourceService so they match
        // the current language (x:Uid does not apply to collection items).
        SetComboBoxItemContent(ThemeModeComboBox, 0, "Settings.FollowSystem");
        SetComboBoxItemContent(ThemeModeComboBox, 1, "Settings.LightMode");
        SetComboBoxItemContent(ThemeModeComboBox, 2, "Settings.DarkMode");

        foreach (AppLanguageDefinition language in AppLanguages.Supported)
        {
            LanguageComboBox.Items.Add(new ComboBoxItem
            {
                Tag = language.Language,
                Content = ResourceService.GetString(language.DisplayNameResourceKey)
            });
        }

        string? version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionTextBlock.Text = version ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0-beta";
    }

    private static void SetComboBoxItemContent(ComboBox combo, int index, string resourceKey)
    {
        if (combo.Items.Count > index && combo.Items[index] is ComboBoxItem item)
        {
            item.Content = ResourceService.GetString(resourceKey);
        }
    }

    public void Initialize(AppSettings settings)
    {
        _settings = settings;
        _initializing = true;
        ThemeModeComboBox.SelectedIndex = (int)settings.Theme;
        LanguageComboBox.SelectedItem = LanguageComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is AppLanguage language && language == settings.Language);
        OutputDirectoryTextBox.Text = settings.LogAndReportDirectory;
        ExplorerContextMenuToggle.IsOn = settings.ExplorerContextMenuEnabled;
        LegacyExplorerContextMenuToggle.IsOn =
            settings.LegacyExplorerContextMenuEnabled;
        UpdateAccentSelectionText();
        _initializing = false;
    }

    public void SetOutputDirectory(string path)
    {
        if (_settings is null)
        {
            return;
        }
        _settings.LogAndReportDirectory = path;
        OutputDirectoryTextBox.Text = path;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AppearanceNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(AppearancePanel, AppearanceNavButton);

    private void GeneralNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(GeneralPanel, GeneralNavButton);

    private void AboutNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(AboutPanel, AboutNavButton);

    private void QuickStartNavButton_Click(object sender, RoutedEventArgs e) =>
        ShowSection(QuickStartPanel, QuickStartNavButton);

    private void ShowSection(UIElement panel, Button selectedButton)
    {
        AppearancePanel.Visibility = panel == AppearancePanel ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = panel == GeneralPanel ? Visibility.Visible : Visibility.Collapsed;
        QuickStartPanel.Visibility = panel == QuickStartPanel ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = panel == AboutPanel ? Visibility.Visible : Visibility.Collapsed;
        AppearanceNavButton.Background = panel == AppearancePanel
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlSecondaryBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        GeneralNavButton.Background = panel == GeneralPanel
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlSecondaryBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        QuickStartNavButton.Background = panel == QuickStartPanel
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlSecondaryBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AboutNavButton.Background = panel == AboutPanel
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlSecondaryBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackRequested?.Invoke(this, EventArgs.Empty);

    private void ThemeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings is null || ThemeModeComboBox.SelectedIndex < 0)
        {
            return;
        }
        _settings.Theme = (AppThemeMode)ThemeModeComboBox.SelectedIndex;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings is null ||
            LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: AppLanguage language })
        {
            return;
        }
        _settings.Language = language;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AccentButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || sender is not Button { Tag: string tag } ||
            !Enum.TryParse(tag, out AppAccentMode accent))
        {
            return;
        }
        _settings.Accent = accent;
        UpdateAccentSelectionText();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateAccentSelectionText()
    {
        if (_settings is null)
        {
            return;
        }
        string colorCode = _settings.Accent switch
        {
            AppAccentMode.Seafoam => " · #00B7C3",
            AppAccentMode.BrightRose => " · #EA005E",
            AppAccentMode.Gold => " · #FFB900",
            AppAccentMode.Mint => " · #00B294",
            AppAccentMode.PurpleShadow => " · #8E8CD8",
            _ => ""
        };
        string key = _settings.Accent switch
        {
            AppAccentMode.Seafoam => "Settings.Seafoam",
            AppAccentMode.BrightRose => "Settings.BrightRose",
            AppAccentMode.Gold => "Settings.Gold",
            AppAccentMode.Mint => "Settings.LightMint",
            AppAccentMode.PurpleShadow => "Settings.PurpleShadow",
            _ => "Settings.WindowsAccent"
        };
        AccentSelectionText.Text = ResourceService.GetString(key) + colorCode;
    }

    private void BrowseDirectoryButton_Click(object sender, RoutedEventArgs e) =>
        BrowseDirectoryRequested?.Invoke(this, EventArgs.Empty);

    private void LegacyExplorerContextMenuToggle_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        LegacyExplorerContextMenuToggle.IsEnabled = false;
        if (LegacyExplorerContextMenuToggleRequested is null)
        {
            LegacyExplorerContextMenuToggle.IsEnabled = true;
            return;
        }

        LegacyExplorerContextMenuToggleRequested.Invoke(
            this,
            new LegacyExplorerContextMenuToggleRequestedEventArgs(
                LegacyExplorerContextMenuToggle.IsOn));
    }

    private void ExplorerContextMenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing ||
            !TryBeginExplorerIntegrationOperation(out long operationId))
        {
            return;
        }

        ExplorerContextMenuToggleRequested?.Invoke(
            this,
            new ExplorerContextMenuToggleRequestedEventArgs(
                ExplorerContextMenuToggle.IsOn,
                operationId));
    }

    private void InstallCertificateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation(out long operationId))
        {
            return;
        }
        InstallExplorerCertificateRequested?.Invoke(
            this,
            new ExplorerIntegrationOperationRequestedEventArgs(operationId));
    }

    private void InstallShellPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation(out long operationId))
        {
            return;
        }
        InstallExplorerPackageRequested?.Invoke(
            this,
            new ExplorerIntegrationOperationRequestedEventArgs(operationId));
    }

    private void UninstallCertificateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation(out long operationId))
        {
            return;
        }
        UninstallExplorerCertificateRequested?.Invoke(
            this,
            new ExplorerIntegrationOperationRequestedEventArgs(operationId));
    }

    private void UninstallShellPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation(out long operationId))
        {
            return;
        }
        UninstallExplorerPackageRequested?.Invoke(
            this,
            new ExplorerIntegrationOperationRequestedEventArgs(operationId));
    }

    private void RefreshShellIntegrationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation(out long operationId))
        {
            return;
        }
        RefreshExplorerIntegrationRequested?.Invoke(
            this,
            new ExplorerIntegrationOperationRequestedEventArgs(operationId));
    }

    internal bool TryBeginExplorerIntegrationOperation(out long operationId)
    {
        if (!_explorerIntegrationOperationGate.TryBegin(out operationId))
        {
            return false;
        }

        DisableExplorerIntegrationControls();
        return true;
    }

    private void DisableExplorerIntegrationControls()
    {
        // These controls act on the same package, certificate, and registry
        // state, so none may start while another operation is awaiting Windows.
        ExplorerContextMenuToggle.IsEnabled = false;
        LanguageComboBox.IsEnabled = false;
        InstallCertificateButton.IsEnabled = false;
        InstallShellPackageButton.IsEnabled = false;
        UninstallCertificateButton.IsEnabled = false;
        UninstallShellPackageButton.IsEnabled = false;
        RefreshShellIntegrationButton.IsEnabled = false;
    }

    public void SetExplorerContextMenuState(
        ExplorerContextMenuStatus status,
        string menuStatusText,
        string certificateStatusText,
        string packageStatusText,
        string? operationStatusText = null,
        long? completingOperationId = null)
    {
        _initializing = true;
        if (completingOperationId is long operationId)
        {
            // Only the operation that acquired the gate may release it after
            // its final status is ready to be applied.
            _explorerIntegrationOperationGate.Complete(operationId);
        }
        ExplorerContextMenuToggle.IsOn = status.IsEnabled;
        ExplorerContextMenuToggle.IsEnabled = status.IsSupported;
        LanguageComboBox.IsEnabled =
            _explorerIntegrationOperationGate.CanUpdateSharedConfiguration;
        ExplorerContextMenuStatusText.Text = menuStatusText;
        CertificateInstallStatusText.Text = certificateStatusText;
        PackageInstallStatusText.Text = packageStatusText;
        UpdateExplorerIntegrationControlAvailability(status);
        if (operationStatusText is not null)
        {
            ShellIntegrationOperationStatusText.Text = operationStatusText;
        }
        _initializing = false;
    }

    public void SetLegacyExplorerContextMenuState(
        LegacyExplorerContextMenuStatus status,
        string statusText)
    {
        _initializing = true;
        LegacyExplorerContextMenuToggle.IsOn = status.IsEnabled;
        LegacyExplorerContextMenuToggle.IsEnabled = status.IsSupported;
        LegacyExplorerContextMenuStatusText.Text = statusText;
        _initializing = false;
    }

    private void UpdateExplorerIntegrationControlAvailability(
        ExplorerContextMenuStatus status)
    {
        InstallCertificateButton.IsEnabled = CanInstallCertificate(status);
        InstallShellPackageButton.IsEnabled = CanInstallPackage(status);
        UninstallCertificateButton.IsEnabled = CanUninstallCertificate(status);
        UninstallShellPackageButton.IsEnabled =
            status.IsSupported && status.IsPackageRegistered;
        RefreshShellIntegrationButton.IsEnabled = status.IsSupported;
        if (_explorerIntegrationOperationGate.IsBusy)
        {
            // An unrelated refresh may update text while deployment is active,
            // but it must never make a conflicting action available.
            DisableExplorerIntegrationControls();
        }
    }

    private static bool CanInstallCertificate(ExplorerContextMenuStatus status) =>
        status.IsSupported &&
        status.IsCertificateFileAvailable &&
        status.CertificateTrustScope is not (
            CertificateTrustScope.LocalMachine or
            CertificateTrustScope.TrustedChain) &&
        !status.IsPackageRegistered;

    private static bool CanInstallPackage(ExplorerContextMenuStatus status) =>
        status.IsSupported &&
        status.IsPackageFileAvailable &&
        !status.IsPackageRegistered;

    private static bool CanUninstallCertificate(ExplorerContextMenuStatus status) =>
        status.IsSupported &&
        !status.IsPackageRegistered &&
        status.CertificateTrustScope is (
            CertificateTrustScope.CurrentUser or
            CertificateTrustScope.LocalMachine);

    public void SetExplorerIntegrationOperationStatus(string statusText) =>
        ShellIntegrationOperationStatusText.Text = statusText;

    private async void RepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/MEMZ-Edge01/ClipPort")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("Error.CannotOpenRepository"),
                Content = ex.Message,
                CloseButtonText = ResourceService.GetString("Common.OK"),
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdateOperationInProgress)
        {
            return;
        }

        SetUpdateOperationBusy(true);
        ApplyUpdateButton.Visibility = Visibility.Collapsed;
        UpdateReleaseNotesText.Visibility = Visibility.Collapsed;
        UpdateProgressBar.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = ResourceService.GetString("Update.Checking");
        try
        {
            GitHubRelease? release = await _updateService.GetLatestReleaseAsync();
            if (release is null)
            {
                UpdateStatusText.Text = ResourceService.GetString("Update.NoReleaseFound");
                return;
            }

            _pendingZipAsset = release.Assets.FirstOrDefault(UpdateService.IsZipAsset);
            string currentVersion = UpdateService.GetCurrentVersion();
            string latestVersion = release.TagName ?? release.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(latestVersion) ||
                !UpdateService.IsNewerVersion(currentVersion, latestVersion))
            {
                _pendingUpdate = null;
                _pendingZipAsset = null;
                UpdateStatusText.Text = ResourceService.Format(
                    "Update.UpToDate",
                    currentVersion);
                return;
            }

            _pendingUpdate = release;
            string prereleaseMarker = release.IsPrerelease
                ? " " + ResourceService.GetString("Update.Prerelease")
                : string.Empty;
            UpdateStatusText.Text = ResourceService.Format(
                "Update.Available",
                latestVersion,
                prereleaseMarker);

            string releaseNotes = release.Body?.Trim() ?? string.Empty;
            if (releaseNotes.Length > 2000)
            {
                releaseNotes = releaseNotes[..2000] + "…";
            }
            if (releaseNotes.Length > 0)
            {
                UpdateReleaseNotesText.Text = ResourceService.Format(
                    "Update.ReleaseNotes",
                    releaseNotes);
                UpdateReleaseNotesText.Visibility = Visibility.Visible;
            }
            ApplyUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or JsonException or
                InvalidOperationException)
        {
            UpdateStatusText.Text = ResourceService.Format(
                "Update.CheckFailed",
                ex.Message);
        }
        finally
        {
            SetUpdateOperationBusy(false);
        }
    }

    private async void ApplyUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdateOperationInProgress ||
            _pendingUpdate is null ||
            _pendingZipAsset is null)
        {
            return;
        }

        var confirmDialog = new ContentDialog
        {
            Title = ResourceService.GetString("Update.ConfirmTitle"),
            Content = ResourceService.GetString("Update.ConfirmMessage"),
            PrimaryButtonText = ResourceService.GetString("Update.ConfirmAction"),
            CloseButtonText = ResourceService.GetString("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetUpdateOperationBusy(true);
        ApplyUpdateButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        UpdateStatusText.Text = ResourceService.Format("Update.Downloading", 0);
        try
        {
            // 进度回调可能来自后台线程，通过 DispatcherQueue 回到 UI 线程更新。
            var progress = new Progress<double>(value =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateProgressBar.Value = value;
                    UpdateStatusText.Text = ResourceService.Format(
                        "Update.Downloading",
                        Math.Round(value));
                }));

            string zipPath = await _updateService.DownloadUpdateAsync(
                _pendingUpdate,
                _pendingZipAsset,
                progress);
            UpdateStatusText.Text = ResourceService.GetString("Update.PreparingRestart");

            // 更新器等待本进程退出后替换文件并重新启动应用。
            _updateService.LaunchUpdater(zipPath, AppContext.BaseDirectory);
            Application.Current.Exit();
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or IOException or
                UnauthorizedAccessException or InvalidOperationException)
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = ResourceService.Format(
                "Update.DownloadFailed",
                ex.Message);
            ApplyUpdateButton.IsEnabled = true;
            SetUpdateOperationBusy(false);
        }
    }

    private void SetUpdateOperationBusy(bool busy)
    {
        _isUpdateOperationInProgress = busy;
        CheckForUpdatesButton.IsEnabled = !busy;
        if (busy)
        {
            ApplyUpdateButton.IsEnabled = false;
        }
    }
}

public class ExplorerIntegrationOperationRequestedEventArgs(
    long operationId) : EventArgs
{
    public long OperationId { get; } = operationId;
}

public sealed class ExplorerContextMenuToggleRequestedEventArgs(
    bool enabled,
    long operationId) : ExplorerIntegrationOperationRequestedEventArgs(operationId)
{
    public bool Enabled { get; } = enabled;
}

public sealed class LegacyExplorerContextMenuToggleRequestedEventArgs(
    bool enabled) : EventArgs
{
    public bool Enabled { get; } = enabled;
}
