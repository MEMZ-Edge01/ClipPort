using System.Diagnostics;
using System.Reflection;
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

    public event EventHandler? BackRequested;
    public event EventHandler? SettingsChanged;
    public event EventHandler? BrowseDirectoryRequested;
    public event EventHandler<ExplorerContextMenuToggleRequestedEventArgs>?
        ExplorerContextMenuToggleRequested;
    public event EventHandler? InstallExplorerCertificateRequested;
    public event EventHandler? UninstallExplorerCertificateRequested;
    public event EventHandler? InstallExplorerPackageRequested;
    public event EventHandler? UninstallExplorerPackageRequested;
    public event EventHandler? RefreshExplorerIntegrationRequested;

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

    private void ExplorerContextMenuToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || !TryBeginExplorerIntegrationOperation())
        {
            return;
        }

        ExplorerContextMenuToggleRequested?.Invoke(
            this,
            new ExplorerContextMenuToggleRequestedEventArgs(
                ExplorerContextMenuToggle.IsOn));
    }

    private void InstallCertificateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation())
        {
            return;
        }
        InstallExplorerCertificateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void InstallShellPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation())
        {
            return;
        }
        InstallExplorerPackageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UninstallCertificateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation())
        {
            return;
        }
        UninstallExplorerCertificateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UninstallShellPackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation())
        {
            return;
        }
        UninstallExplorerPackageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshShellIntegrationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginExplorerIntegrationOperation())
        {
            return;
        }
        RefreshExplorerIntegrationRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool TryBeginExplorerIntegrationOperation()
    {
        if (!_explorerIntegrationOperationGate.TryBegin())
        {
            return false;
        }

        // These controls act on the same package, certificate, and registry
        // state, so none may start while another operation is awaiting Windows.
        ExplorerContextMenuToggle.IsEnabled = false;
        InstallCertificateButton.IsEnabled = false;
        InstallShellPackageButton.IsEnabled = false;
        UninstallCertificateButton.IsEnabled = false;
        UninstallShellPackageButton.IsEnabled = false;
        RefreshShellIntegrationButton.IsEnabled = false;
        return true;
    }

    public void SetExplorerContextMenuState(
        ExplorerContextMenuStatus status,
        string menuStatusText,
        string certificateStatusText,
        string packageStatusText,
        string? operationStatusText = null)
    {
        _initializing = true;
        // Release the gate only when the operation's refreshed state is ready
        // to determine which integration actions are valid again.
        _explorerIntegrationOperationGate.Complete();
        ExplorerContextMenuToggle.IsOn = status.IsEnabled;
        ExplorerContextMenuToggle.IsEnabled = status.IsSupported;
        ExplorerContextMenuStatusText.Text = menuStatusText;
        CertificateInstallStatusText.Text = certificateStatusText;
        PackageInstallStatusText.Text = packageStatusText;
        InstallCertificateButton.IsEnabled =
            status.IsSupported &&
            status.IsCertificateFileAvailable &&
            status.CertificateTrustScope is not (
                CertificateTrustScope.LocalMachine or
                CertificateTrustScope.TrustedChain) &&
            !status.IsPackageRegistered;
        InstallShellPackageButton.IsEnabled =
            status.IsSupported &&
            status.IsPackageFileAvailable &&
            !status.IsPackageRegistered;
        UninstallCertificateButton.IsEnabled =
            status.IsSupported &&
            !status.IsPackageRegistered &&
            status.CertificateTrustScope is (
                CertificateTrustScope.CurrentUser or
                CertificateTrustScope.LocalMachine);
        UninstallShellPackageButton.IsEnabled =
            status.IsSupported &&
            status.IsPackageRegistered;
        RefreshShellIntegrationButton.IsEnabled = status.IsSupported;
        if (operationStatusText is not null)
        {
            ShellIntegrationOperationStatusText.Text = operationStatusText;
        }
        _initializing = false;
    }

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
}

public sealed class ExplorerContextMenuToggleRequestedEventArgs(bool enabled) : EventArgs
{
    public bool Enabled { get; } = enabled;
}
