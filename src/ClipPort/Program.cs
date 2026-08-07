using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClipPort.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace ClipPort;

public static class Program
{
    private const string SingleInstanceKey = "ClipPort.MainInstance";

    [STAThread]
    public static int Main(string[] arguments)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppActivationArguments activationArguments =
            AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance mainInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);
        if (!mainInstance.IsCurrent)
        {
            RedirectActivation(mainInstance, activationArguments);
            return 0;
        }

        mainInstance.Activated += (_, redirectedArguments) =>
            ActivationRouter.Enqueue(ParseActivation(redirectedArguments));
        ActivationRouter.Enqueue(new AppActivationRequest(
            QuickStartRequestParser.Parse(arguments)));

        Application.Start(_applicationInitializationCallbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
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

    private static void RedirectActivation(
        AppInstance mainInstance,
        AppActivationArguments activationArguments)
    {
        Task.Run(async () =>
        {
            await mainInstance.RedirectActivationToAsync(activationArguments);
            try
            {
                using Process process = Process.GetProcessById(
                    checked((int)mainInstance.ProcessId));
                nint windowHandle = process.MainWindowHandle;
                if (windowHandle != 0)
                {
                    ShowWindowAsync(windowHandle, ShowWindowRestore);
                    SetForegroundWindow(windowHandle);
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or InvalidOperationException)
            {
                // The primary instance can close while activation is being redirected.
            }
        }).GetAwaiter().GetResult();
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

    private const int ShowWindowRestore = 9;

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nint CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);
}

internal sealed record AppActivationRequest(QuickStartRequest? QuickStartRequest);

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
