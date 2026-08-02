using System.Runtime.InteropServices;
using ClipPort.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace ClipPort;

public sealed partial class MainWindow
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsContinuous = 0x80000000;
    private bool _sleepPreventionApplied;
    private bool _isClosing;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint executionState);

    private void UpdateSleepPreventionState()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(UpdateSleepPreventionState);
            return;
        }

        bool shouldPreventSleep = !_isClosing &&
            _jobRuntimes.Values.Any(runtime => runtime.Job.PreventSleep);
        if (shouldPreventSleep != _sleepPreventionApplied)
        {
            uint result = SetThreadExecutionState(
                shouldPreventSleep ? EsContinuous | EsSystemRequired : EsContinuous);
            if (result != 0)
            {
                _sleepPreventionApplied = shouldPreventSleep;
            }
        }

        UpdateWindowSleepTitle(_sleepPreventionApplied);
    }

    private void ReleaseSleepPreventionForShutdown()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ReleaseSleepPreventionForShutdown);
            return;
        }

        _isClosing = true;
        if (_sleepPreventionApplied)
        {
            SetThreadExecutionState(EsContinuous);
            _sleepPreventionApplied = false;
        }
        UpdateWindowSleepTitle(false);
    }

    private void UpdateWindowSleepTitle(bool sleepPrevented)
    {
        string title = sleepPrevented
            ? ResourceService.GetString("Window.TitleSleepPrevented")
            : "ClipPort-beta";
        AppTitleText.Text = title;
        nint hwnd = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow.GetFromWindowId(windowId).Title = title;
    }
}
