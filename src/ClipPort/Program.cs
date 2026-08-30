using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ClipPort.Models;
using ClipPort.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace ClipPort;

public static class Program
{
    [STAThread]
    public static int Main(string[] arguments)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppActivationArguments activationArguments =
            AppInstance.GetCurrent().GetActivatedEventArgs();
        AppActivationRequest request = CreateActivationRequest(
            arguments,
            activationArguments);
        using SingleInstanceCoordinator instanceCoordinator =
            SingleInstanceCoordinator.Acquire();
        if (!instanceCoordinator.IsPrimary)
        {
            return instanceCoordinator.TryRedirect(request) ? 0 : 2;
        }

        // Start the listener before XAML initialization. Early Explorer launches
        // stay queued in ActivationRouter until the main window has loaded.
        instanceCoordinator.StartListening(ActivationRouter.Enqueue);
        ActivationRouter.Enqueue(request);

        Application.Start(_applicationInitializationCallbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    private static AppActivationRequest CreateActivationRequest(
        IReadOnlyList<string> commandLineArguments,
        AppActivationArguments activationArguments)
    {
        QuickStartRequest? commandLineRequest =
            QuickStartRequestParser.Parse(commandLineArguments);
        return commandLineRequest is not null
            ? new AppActivationRequest(commandLineRequest)
            : ParseActivation(activationArguments);
    }

    private static AppActivationRequest ParseActivation(
        AppActivationArguments activationArguments)
    {
        if (activationArguments.Data is ILaunchActivatedEventArgs launchArguments)
        {
            string[] arguments = SplitCommandLine(launchArguments.Arguments);
            return new AppActivationRequest(QuickStartRequestParser.Parse(arguments));
        }

        return new AppActivationRequest(null);
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        nint argumentsPointer = CommandLineToArgvW(commandLine, out int argumentCount);
        if (argumentsPointer == 0)
        {
            return [];
        }

        try
        {
            var arguments = new string[argumentCount];
            for (int index = 0; index < argumentCount; index++)
            {
                nint argumentPointer = Marshal.ReadIntPtr(
                    argumentsPointer,
                    index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(argumentPointer) ?? string.Empty;
            }
            return arguments;
        }
        finally
        {
            LocalFree(argumentsPointer);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nint CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

}

internal static class ActivationRouter
{
    private static readonly ConcurrentQueue<AppActivationRequest> Pending = new();
    private static DispatcherQueue? _dispatcherQueue;
    private static Func<AppActivationRequest, Task>? _handler;
    private static int _drainScheduled;

    public static void Enqueue(AppActivationRequest request)
    {
        Pending.Enqueue(request);
        ScheduleDrain();
    }

    public static void Register(
        DispatcherQueue dispatcherQueue,
        Func<AppActivationRequest, Task> handler)
    {
        _dispatcherQueue = dispatcherQueue;
        _handler = handler;
        ScheduleDrain();
    }

    private static void ScheduleDrain()
    {
        DispatcherQueue? dispatcherQueue = _dispatcherQueue;
        if (dispatcherQueue is null || _handler is null ||
            Interlocked.Exchange(ref _drainScheduled, 1) != 0)
        {
            return;
        }

        if (!dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    while (_handler is not null && Pending.TryDequeue(out AppActivationRequest? request))
                    {
                        await _handler(request);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _drainScheduled, 0);
                    if (!Pending.IsEmpty)
                    {
                        ScheduleDrain();
                    }
                }
            }))
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
        }
    }
}
