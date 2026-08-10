using System.Diagnostics;

namespace ClipPort.Services;

internal static class CertificateInstallerWorkflow
{
    public static Task WaitForExitAsync(Process process) =>
        WaitForExitAsync(process, static currentProcess => currentProcess.WaitForExitAsync());

    internal static async Task WaitForExitAsync(
        Process process,
        Func<Process, Task> waitForExitAsync)
    {
        using (process)
        {
            await waitForExitAsync(process);
        }
    }
}
