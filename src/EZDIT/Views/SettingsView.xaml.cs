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

        // Store original XAML resource keys on each ComboBoxItem's Tag so that
        // Localize() can always re-translate from the original key, regardless
        // of the item's current (possibly already translated) Content.
        foreach (ComboBoxItem item in ThemeModeComboBox.Items)
        {
            item.Tag = item.Content?.ToString() ?? string.Empty;
        }
        foreach (ComboBoxItem item in LanguageComboBox.Items)
        {
            item.Tag = item.Content?.ToString() ?? string.Empty;
        }

        string? version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionTextBlock.Text = version ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0-beta";

        // Ensure localization runs after the visual tree is fully loaded.
        // When SettingsView is Collapsed, VisualTreeHelper may not traverse its
        // children, so Apply() inside Localize() can't reach TextBlocks.
        Loaded += (_, _) => Localize();
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
        Localize();
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

    public void Localize()
    {
        // Directly set all translatable text by x:Name.
        // VisualTreeHelper traversal is unreliable for collapsed UserControls,
        // so we bypass it entirely and set text explicitly.

        // Sidebar
        SidebarSettingsLabel.Text = LocalizationService.Text("设置");
        SettingsSectionLabel.Text = LocalizationService.Text("设置");
        AppearanceNavText.Text = LocalizationService.Text("外观");
        GeneralNavText.Text = LocalizationService.Text("常规");
        AboutNavText.Text = LocalizationService.Text("关于");
        BackToTaskText.Text = LocalizationService.Text("返回任务");

        // Appearance panel
        AppearanceTitle.Text = LocalizationService.Text("外观");
        AppearanceDesc.Text = LocalizationService.Text("选择应用的明暗外观与强调色。");
        ColorModeSectionTitle.Text = LocalizationService.Text("颜色模式与主题色");
        ColorModeLabel.Text = LocalizationService.Text("颜色模式");
        AccentColorLabel.Text = LocalizationService.Text("主题色");

        // General panel
        GeneralTitle.Text = LocalizationService.Text("常规");
        GeneralDesc.Text = LocalizationService.Text("设置界面语言以及日志与报告的默认位置。");
        LangFileSectionTitle.Text = LocalizationService.Text("语言与文件");
        LanguageLabel.Text = LocalizationService.Text("语言");
        LogPathLabel.Text = LocalizationService.Text("日志与报告默认保存位置");
        BrowseFolderButton.Content = LocalizationService.Text("选择文件夹");

        // About panel
        AboutTitle.Text = LocalizationService.Text("关于");
        AboutDesc.Text = LocalizationService.Text("查看版本信息并访问 EZ DIT 项目仓库。");
        AppInfoTitle.Text = LocalizationService.Text("应用信息");
        VersionLabel.Text = LocalizationService.Text("版本");
        RepoLabel.Text = LocalizationService.Text("项目仓库");
        OpenGitHubText.Text = LocalizationService.Text("在 GitHub 中打开");

        // ComboBox items via stored original keys
        foreach (ComboBoxItem item in ThemeModeComboBox.Items)
        {
            if (item.Tag is string key)
            {
                item.Content = LocalizationService.Text(key);
            }
        }
        foreach (ComboBoxItem item in LanguageComboBox.Items)
        {
            if (item.Tag is string key)
            {
                item.Content = LocalizationService.Text(key);
            }
        }

        UpdateAccentSelectionText();
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
        string text = _settings.Accent switch
        {
            AppAccentMode.Seafoam => "海沫绿 · #00B7C3",
            AppAccentMode.BrightRose => "亮玫红 · #EA005E",
            AppAccentMode.Gold => "黄金色 · #FFB900",
            AppAccentMode.Mint => "浅薄荷色 · #00B294",
            AppAccentMode.PurpleShadow => "紫影色 · #8E8CD8",
            _ => "Windows 主题色"
        };
        AccentSelectionText.Text = LocalizationService.Text(text.Split(" · ")[0]) +
                                   (text.Contains(" · ", StringComparison.Ordinal)
                                       ? " · " + text.Split(" · ")[1]
                                       : string.Empty);
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
