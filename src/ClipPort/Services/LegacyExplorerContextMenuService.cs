using ClipPort.Models;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClipPort.Services;

public sealed record LegacyExplorerContextMenuStatus(
    bool IsSupported,
    bool IsEnabled,
    string? ErrorMessage = null);

public sealed record LegacyExplorerContextMenuRegistration(
    string MenuKeyPath,
    string MenuText,
    string IconValue,
    string SourceText,
    string DestinationText,
    string SourceCommand,
    string DestinationCommand);

/// <summary>
/// Builds the two per-user static menu registrations used for a selected
/// directory and for a directory background. Keeping this description pure
/// makes command-line quoting and localization testable without touching the
/// user's registry.
/// </summary>
public static class LegacyExplorerContextMenuRegistrationFactory
{
    public const string DirectoryMenuKeyPath =
        @"Software\Classes\Directory\shell\ClipPort";
    public const string BackgroundMenuKeyPath =
        @"Software\Classes\Directory\Background\shell\ClipPort";
    public const string ExtendedSubCommandsKeyName = "ExtendedSubCommandsKey";
    public const string SourceVerbKeyName = "01Source";
    public const string DestinationVerbKeyName = "02Destination";

    public static IReadOnlyList<LegacyExplorerContextMenuRegistration> Create(
        string executablePath,
        AppLanguage language)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException(
                "The ClipPort executable path cannot be empty.",
                nameof(executablePath));
        }

        string normalizedExecutablePath = Path.GetFullPath(executablePath);
        if (normalizedExecutablePath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The ClipPort executable path cannot contain a quotation mark.",
                nameof(executablePath));
        }

        (string menuText, string sourceText, string destinationText) = language switch
        {
            AppLanguage.English =>
                ("New ClipPort task", "Use as source directory", "Use as destination directory"),
            AppLanguage.ClassicalChinese =>
                ("立 ClipPort 之役", "以为源目录", "以为所往目录"),
            _ =>
                ("新建 ClipPort 任务", "作为源目录", "作为目标目录")
        };
        string iconValue = $"\"{normalizedExecutablePath}\",0";

        return
        [
            CreateRegistration(
                DirectoryMenuKeyPath,
                "%1",
                normalizedExecutablePath,
                iconValue,
                menuText,
                sourceText,
                destinationText),
            CreateRegistration(
                BackgroundMenuKeyPath,
                "%V",
                normalizedExecutablePath,
                iconValue,
                menuText,
                sourceText,
                destinationText)
        ];
    }

    private static LegacyExplorerContextMenuRegistration CreateRegistration(
        string menuKeyPath,
        string directoryArgument,
        string executablePath,
        string iconValue,
        string menuText,
        string sourceText,
        string destinationText) =>
        new(
            menuKeyPath,
            menuText,
            iconValue,
            sourceText,
            destinationText,
            $"\"{executablePath}\" {QuickStartRequestParser.SourceOption} \"{directoryArgument}\"",
            $"\"{executablePath}\" {QuickStartRequestParser.DestinationOption} \"{directoryArgument}\"");
}

/// <summary>
/// Registers ClipPort in the traditional Explorer context menu for the current
/// user. This path intentionally does not install an MSIX package or a signing
/// certificate; Windows 11 displays these verbs under "Show more options".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LegacyExplorerContextMenuService
{
    private const uint ShellChangeAssociationChanged = 0x08000000;
    private const uint ShellChangeNotifyIdList = 0x0000;
    private readonly string _executablePath;
    private readonly string _directoryMenuKeyPath;
    private readonly string _backgroundMenuKeyPath;

    public LegacyExplorerContextMenuService()
        : this(
            Path.Combine(AppContext.BaseDirectory, "ClipPort.exe"),
            LegacyExplorerContextMenuRegistrationFactory.DirectoryMenuKeyPath,
            LegacyExplorerContextMenuRegistrationFactory.BackgroundMenuKeyPath)
    {
    }

    internal LegacyExplorerContextMenuService(
        string executablePath,
        string directoryMenuKeyPath,
        string backgroundMenuKeyPath)
    {
        _executablePath = executablePath;
        _directoryMenuKeyPath = directoryMenuKeyPath;
        _backgroundMenuKeyPath = backgroundMenuKeyPath;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public LegacyExplorerContextMenuStatus GetStatus(AppLanguage language)
    {
        if (!IsSupported)
        {
            return new LegacyExplorerContextMenuStatus(false, false);
        }

        try
        {
            IReadOnlyList<LegacyExplorerContextMenuRegistration> registrations =
                CreateCurrentRegistrations(language);
            return new LegacyExplorerContextMenuStatus(
                true,
                registrations.All(IsRegistered));
        }
        catch (Exception ex) when (IsRegistryOperationException(ex))
        {
            return new LegacyExplorerContextMenuStatus(true, false, ex.Message);
        }
    }

    public LegacyExplorerContextMenuStatus SetEnabled(
        bool enabled,
        AppLanguage language)
    {
        if (!IsSupported)
        {
            return new LegacyExplorerContextMenuStatus(false, false);
        }

        try
        {
            IReadOnlyList<LegacyExplorerContextMenuRegistration> registrations =
                CreateCurrentRegistrations(language);
            if (enabled)
            {
                foreach (LegacyExplorerContextMenuRegistration registration in registrations)
                {
                    WriteRegistration(registration);
                }
            }
            else
            {
                foreach (LegacyExplorerContextMenuRegistration registration in registrations)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(
                        registration.MenuKeyPath,
                        throwOnMissingSubKey: false);
                }
            }

            // Explorer caches file associations and static verbs. Notify it
            // after the complete two-key update so a new Explorer window can
            // observe the new state without requiring a sign-out.
            SHChangeNotify(
                ShellChangeAssociationChanged,
                ShellChangeNotifyIdList,
                nint.Zero,
                nint.Zero);

            LegacyExplorerContextMenuStatus status = GetStatus(language);
            if (status.ErrorMessage is null && status.IsEnabled != enabled)
            {
                return status with
                {
                    ErrorMessage = "Windows did not retain the requested traditional context-menu state."
                };
            }
            return status;
        }
        catch (Exception ex) when (IsRegistryOperationException(ex))
        {
            return new LegacyExplorerContextMenuStatus(
                true,
                GetStatus(language).IsEnabled,
                ex.Message);
        }
    }

    private IReadOnlyList<LegacyExplorerContextMenuRegistration>
        CreateCurrentRegistrations(AppLanguage language)
    {
        IReadOnlyList<LegacyExplorerContextMenuRegistration> registrations =
            LegacyExplorerContextMenuRegistrationFactory.Create(
                _executablePath,
                language);
        return
        [
            registrations[0] with { MenuKeyPath = _directoryMenuKeyPath },
            registrations[1] with { MenuKeyPath = _backgroundMenuKeyPath }
        ];
    }

    private static void WriteRegistration(
        LegacyExplorerContextMenuRegistration registration)
    {
        using RegistryKey menuKey = Registry.CurrentUser.CreateSubKey(
            registration.MenuKeyPath,
            writable: true);
        // The default value must remain unset for an ExtendedSubCommandsKey
        // cascade. MUIVerb is the localized label Explorer displays.
        menuKey.DeleteValue(string.Empty, throwOnMissingValue: false);
        menuKey.SetValue("MUIVerb", registration.MenuText, RegistryValueKind.String);
        menuKey.SetValue("Icon", registration.IconValue, RegistryValueKind.String);
        menuKey.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);

        using RegistryKey subCommandsKey = menuKey.CreateSubKey(
            LegacyExplorerContextMenuRegistrationFactory.ExtendedSubCommandsKeyName,
            writable: true);
        using RegistryKey shellKey = subCommandsKey.CreateSubKey("shell", writable: true);
        WriteVerb(
            shellKey,
            LegacyExplorerContextMenuRegistrationFactory.SourceVerbKeyName,
            registration.SourceText,
            registration.IconValue,
            registration.SourceCommand);
        WriteVerb(
            shellKey,
            LegacyExplorerContextMenuRegistrationFactory.DestinationVerbKeyName,
            registration.DestinationText,
            registration.IconValue,
            registration.DestinationCommand);
    }

    private static void WriteVerb(
        RegistryKey shellKey,
        string verbKeyName,
        string text,
        string iconValue,
        string command)
    {
        using RegistryKey verbKey = shellKey.CreateSubKey(verbKeyName, writable: true);
        verbKey.SetValue("MUIVerb", text, RegistryValueKind.String);
        verbKey.SetValue("Icon", iconValue, RegistryValueKind.String);
        verbKey.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);
        using RegistryKey commandKey = verbKey.CreateSubKey("command", writable: true);
        commandKey.SetValue(null, command, RegistryValueKind.String);
    }

    private static bool IsRegistered(
        LegacyExplorerContextMenuRegistration registration)
    {
        using RegistryKey? menuKey = Registry.CurrentUser.OpenSubKey(
            registration.MenuKeyPath,
            writable: false);
        if (menuKey is null ||
            !RegistryStringEquals(menuKey, "MUIVerb", registration.MenuText) ||
            !RegistryStringEquals(menuKey, "Icon", registration.IconValue))
        {
            return false;
        }

        using RegistryKey? shellKey = menuKey.OpenSubKey(
            $"{LegacyExplorerContextMenuRegistrationFactory.ExtendedSubCommandsKeyName}\\shell",
            writable: false);
        return shellKey is not null &&
            VerbMatches(
                shellKey,
                LegacyExplorerContextMenuRegistrationFactory.SourceVerbKeyName,
                registration.SourceText,
                registration.SourceCommand) &&
            VerbMatches(
                shellKey,
                LegacyExplorerContextMenuRegistrationFactory.DestinationVerbKeyName,
                registration.DestinationText,
                registration.DestinationCommand);
    }

    private static bool VerbMatches(
        RegistryKey shellKey,
        string verbKeyName,
        string text,
        string command)
    {
        using RegistryKey? verbKey = shellKey.OpenSubKey(verbKeyName, writable: false);
        if (verbKey is null || !RegistryStringEquals(verbKey, "MUIVerb", text))
        {
            return false;
        }

        using RegistryKey? commandKey = verbKey.OpenSubKey("command", writable: false);
        return commandKey?.GetValue(null) is string registeredCommand &&
            string.Equals(registeredCommand, command, StringComparison.Ordinal);
    }

    private static bool RegistryStringEquals(
        RegistryKey key,
        string valueName,
        string expected) =>
        key.GetValue(valueName) is string value &&
        string.Equals(value, expected, StringComparison.Ordinal);

    private static bool IsRegistryOperationException(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or InvalidOperationException or
            ArgumentException or NotSupportedException or System.Security.SecurityException;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        nint item1,
        nint item2);
}
