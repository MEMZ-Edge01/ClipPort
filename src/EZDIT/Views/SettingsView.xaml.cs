using System.Diagnostics;
using System.Reflection;
using EZDIT.Models;
using EZDIT.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EZDIT.Views;

public sealed partial class SettingsView : UserControl
{
    private AppSettings? _settings;
    private bool _initializing;

    public event EventHandler? BackRequested;
    public event EventHandler? SettingsChanged;
    public event EventHandler? BrowseDirectoryRequested;

    public SettingsView()
    {
        InitializeComponent();

        // Set ComboBoxItem content via ResourceService so they match
        // the current language (x:Uid does not apply to collection items).
        SetComboBoxItemContent(ThemeModeComboBox, 0, "Settings.FollowSystem");
        SetComboBoxItemContent(ThemeModeComboBox, 1, "Settings.LightMode");
        SetComboBoxItemContent(ThemeModeComboBox, 2, "Settings.DarkMode");

        SetComboBoxItemContent(LanguageComboBox, 0, "Settings.SimplifiedChinese");
        SetComboBoxItemContent(LanguageComboBox, 1, "Settings.English");

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
        LanguageComboBox.SelectedIndex = settings.Language == AppLanguage.English ? 1 : 0;
        OutputDirectoryTextBox.Text = settings.LogAndReportDirectory;
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

    private void ShowSection(UIElement panel, Button selectedButton)
    {
        AppearancePanel.Visibility = panel == AppearancePanel ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = panel == GeneralPanel ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = panel == AboutPanel ? Visibility.Visible : Visibility.Collapsed;
        AppearanceNavButton.Background = panel == AppearancePanel
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlSecondaryBrush"]
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        GeneralNavButton.Background = panel == GeneralPanel
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
        if (_initializing || _settings is null || LanguageComboBox.SelectedIndex < 0)
        {
            return;
        }
        _settings.Language = LanguageComboBox.SelectedIndex == 1
            ? AppLanguage.English
            : AppLanguage.SimplifiedChinese;
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

    private void RepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/MEMZ-Edge01/EZ-DIT")
        {
            UseShellExecute = true
        });
    }
}
