using System.Runtime.InteropServices;
using System.Globalization;

namespace ClipPort.Services;

internal static class ApplicationRestartService
{
    private const int TaskActionExecute = 0;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskTriggerTime = 1;
    private const string RestartTaskPrefix = "ClipPort-Restart-";
    private const string CleanupTaskOption = "--clipport-restart-task";

    public static void StartReplacement(
        string executablePath,
        string workingDirectory)
    {
        string taskName =
            $"{RestartTaskPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}";
        object? serviceObject = null;
        object? folderObject = null;
        object? definitionObject = null;
        object? settingsObject = null;
        object? principalObject = null;
        object? triggersObject = null;
        object? triggerObject = null;
        object? actionsObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;
        bool taskRegistered = false;
        bool registrationComplete = false;

        try
        {
            Type serviceType = Type.GetTypeFromProgID(
                    "Schedule.Service",
                    throwOnError: true)
                ?? throw new InvalidOperationException(
                    "Windows Task Scheduler automation is unavailable.");
            serviceObject = Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException(
                    "Windows Task Scheduler automation could not be created.");

            dynamic service = serviceObject;
            service.Connect();

            folderObject = service.GetFolder("\\");
            definitionObject = service.NewTask(0);
            dynamic definition = definitionObject;

            settingsObject = definition.Settings;
            dynamic settings = settingsObject;
            settings.Enabled = true;
            settings.AllowDemandStart = true;
            settings.StartWhenAvailable = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            // If the replacement cannot start, Windows will remove the stale
            // task shortly after its one-time trigger expires.
            settings.DeleteExpiredTaskAfter = "PT1M";

            principalObject = definition.Principal;
            dynamic principal = principalObject;
            principal.LogonType = TaskLogonInteractiveToken;

            // Leave enough time for the old unpackaged WinUI lifetime job to
            // be released before Task Scheduler creates the replacement.
            DateTime startTime = DateTime.Now.AddSeconds(5);
            triggersObject = definition.Triggers;
            dynamic triggers = triggersObject;
            triggerObject = triggers.Create(TaskTriggerTime);
            dynamic trigger = triggerObject;
            trigger.StartBoundary = FormatBoundary(startTime);
            trigger.EndBoundary = FormatBoundary(startTime.AddMinutes(5));

            actionsObject = definition.Actions;
            dynamic actions = actionsObject;
            actionObject = actions.Create(TaskActionExecute);
            dynamic action = actionObject;
            action.Path = executablePath;
            action.WorkingDirectory = workingDirectory;
            action.Arguments = $"{CleanupTaskOption} {taskName}";

            dynamic folder = folderObject;
            registeredTaskObject = folder.RegisterTaskDefinition(
                taskName,
                definition,
                TaskCreateOrUpdate,
                null,
                null,
                TaskLogonInteractiveToken,
                null);
            taskRegistered = true;
            registrationComplete = true;
        }
        finally
        {
            if (taskRegistered &&
                !registrationComplete &&
                folderObject is not null)
            {
                try
                {
                    dynamic folder = folderObject;
                    folder.DeleteTask(taskName, 0);
                }
                catch
                {
                    // The registration also has automatic expiry cleanup.
                }
            }

            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(actionsObject);
            ReleaseComObject(triggerObject);
            ReleaseComObject(triggersObject);
            ReleaseComObject(principalObject);
            ReleaseComObject(settingsObject);
            ReleaseComObject(definitionObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }
    }

    public static void CleanupRegistrationFromCommandLine()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int optionIndex = Array.IndexOf(arguments, CleanupTaskOption);
        if (optionIndex < 0 || optionIndex + 1 >= arguments.Length)
        {
            return;
        }

        string taskName = arguments[optionIndex + 1];
        if (!taskName.StartsWith(RestartTaskPrefix, StringComparison.Ordinal) ||
            taskName.IndexOfAny(['\\', '/']) >= 0)
        {
            return;
        }

        object? serviceObject = null;
        object? folderObject = null;
        try
        {
            Type? serviceType = Type.GetTypeFromProgID("Schedule.Service");
            serviceObject = serviceType is null
                ? null
                : Activator.CreateInstance(serviceType);
            if (serviceObject is null)
            {
                return;
            }

            dynamic service = serviceObject;
            service.Connect();
            folderObject = service.GetFolder("\\");
            dynamic folder = folderObject;
            // Removing a running task registration does not stop the app that
            // Task Scheduler has already launched.
            folder.DeleteTask(taskName, 0);
        }
        catch
        {
            // The task also expires automatically, so cleanup must never stop
            // an otherwise successful application launch.
        }
        finally
        {
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }
    }

    private static string FormatBoundary(DateTime value) =>
        value.ToString(
            "yyyy-MM-dd'T'HH:mm:ss",
            CultureInfo.InvariantCulture);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
