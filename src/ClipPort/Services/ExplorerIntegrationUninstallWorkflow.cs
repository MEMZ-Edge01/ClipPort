using ClipPort.Models;

namespace ClipPort.Services;

public sealed record ExplorerIntegrationUninstallResult<T>(
    T? OperationResult,
    Exception? SettingsSaveError);

public static class ExplorerIntegrationUninstallWorkflow
{
    public static async Task<ExplorerIntegrationUninstallResult<T>> RunAsync<T>(
        AppSettings settings,
        bool disableSavedSettingBeforeUninstall,
        Func<AppSettings, Task> saveSettingsAsync,
        Func<Task<T>> uninstallAsync)
    {
        if (disableSavedSettingBeforeUninstall)
        {
            bool previousEnabled = settings.ExplorerContextMenuEnabled;
            settings.ExplorerContextMenuEnabled = false;
            try
            {
                // Persist the disabled state before removing the final package
                // so startup cannot restore an uninstall from stale settings.
                await saveSettingsAsync(settings);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                settings.ExplorerContextMenuEnabled = previousEnabled;
                return new ExplorerIntegrationUninstallResult<T>(default, ex);
            }
        }

        T operationResult = await uninstallAsync();
        return new ExplorerIntegrationUninstallResult<T>(operationResult, null);
    }
}
