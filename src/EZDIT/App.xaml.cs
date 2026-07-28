using EZDIT.Models;
using EZDIT.Services;
using Microsoft.UI.Xaml;

namespace EZDIT;

public partial class App : Application
{
    private Window? _window;
    public static AppSettingsService SettingsService { get; } = new();
    public static AppSettings Settings { get; } = SettingsService.Load();

    public App()
    {
        InitializeComponent();
        LocalizationService.SetLanguage(Settings.Language);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
