using EZDIT.Models;
using EZDIT.Services;
using Microsoft.Windows.Globalization;
using Microsoft.UI.Xaml;

namespace EZDIT;

public partial class App : Application
{
    private Window? _window;
    public static AppSettingsService SettingsService { get; } = new();
    public static AppSettings Settings { get; } = SettingsService.Load();

    public App()
    {
        AppLanguageDefinition language = AppLanguages.Get(Settings.Language);

        // This is an unpackaged WinUI app, so the preferred language override
        // must be restored before any XAML or PRI-backed resource is loaded.
        ApplicationLanguages.PrimaryLanguageOverride = language.LanguageTag;
        ResourceService.SetLanguage(language.Language);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        ApplicationRestartService.CleanupRegistrationFromCommandLine();
    }
}
