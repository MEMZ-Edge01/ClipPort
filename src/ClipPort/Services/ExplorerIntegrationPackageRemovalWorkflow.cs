namespace ClipPort.Services;

public static class ExplorerIntegrationPackageRemovalWorkflow
{
    public static async Task RunAsync(
        Action disableLiveState,
        Func<Task> removePackageAsync,
        Action clearConfiguration)
    {
        // The shell extension reads this state for every menu query, so it
        // must be disabled before Windows starts an operation that may fail.
        disableLiveState();
        await removePackageAsync();
        clearConfiguration();
    }
}
