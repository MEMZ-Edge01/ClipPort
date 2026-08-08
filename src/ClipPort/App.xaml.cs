using ClipPort.Models;
using ClipPort.Services;
using Microsoft.Windows.Globalization;
using Microsoft.UI.Xaml;

namespace ClipPort;

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
        if (_window.Content is FrameworkElement content)
        {
            // A cold-start Explorer request can arrive before WinUI assigns a
            // XamlRoot. Keep it queued until ContentDialog can be shown safely.
            void RegisterAfterLoaded(object sender, RoutedEventArgs eventArgs)
            {
                content.Loaded -= RegisterAfterLoaded;
                RegisterActivationHandler();
            }

            content.Loaded += RegisterAfterLoaded;
        }
        _window.Activate();
        if (_window.Content is not FrameworkElement)
        {
            RegisterActivationHandler();
        }
        ApplicationRestartService.CleanupRegistrationFromCommandLine();
    }

    private void RegisterActivationHandler()
    {
        if (_window is null)
        {
            return;
        }

        ActivationRouter.Register(
            _window.DispatcherQueue,
            HandleActivationAsync);
    }

    private Task HandleActivationAsync(AppActivationRequest request)
    {
        if (_window is not MainWindow mainWindow)
        {
            return Task.CompletedTask;
        }

        mainWindow.ActivateAndRestore();
        if (request.QuickStartRequest is not null)
        {
            // Do not block activation routing on the modal task dialog. A second
            // Explorer request must be able to update the dialog while it is open.
            _ = mainWindow.HandleQuickStartRequestAsync(request.QuickStartRequest);
        }
        return Task.CompletedTask;
    }
}
