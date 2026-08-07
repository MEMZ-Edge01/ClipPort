using ClipPort.Models;
using Microsoft.Win32;
using System.Runtime.Versioning;
using Windows.Management.Deployment;

namespace ClipPort.Services;

public sealed record ExplorerContextMenuStatus(
    bool IsSupported,
    bool IsPackageRegistered,
    bool IsEnabled,
    string? ErrorMessage = null);

public sealed class ExplorerContextMenuService
{
    public const string PackageIdentityName = "MEMZEdge01.ClipPort.ShellIntegration";
    public const string RegistryPath = @"Software\ClipPort\ExplorerContextMenu";
    public const string PackageFileName = "ClipPort.ShellIntegration.msix";

    public bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public ExplorerContextMenuStatus GetStatus()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return new ExplorerContextMenuStatus(false, false, false);
        }

        try
        {
            bool packageRegistered = IsPackageRegistered();
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            bool enabled = packageRegistered &&
                Convert.ToInt32(key?.GetValue("Enabled", 0)) == 1;
            return new ExplorerContextMenuStatus(true, packageRegistered, enabled);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return new ExplorerContextMenuStatus(true, false, false, ex.Message);
        }
    }

    public async Task<ExplorerContextMenuStatus> SetEnabledAsync(
        bool enabled,
        AppLanguage language)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return new ExplorerContextMenuStatus(false, false, false);
        }

        try
        {
            bool packageRegistered = IsPackageRegistered();
            if (enabled && !packageRegistered)
            {
                await RegisterPackageAsync();
                packageRegistered = IsPackageRegistered();
                if (!packageRegistered)
                {
                    throw new InvalidOperationException(
                        "Windows did not report the shell integration package after registration.");
                }
            }

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
            key.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(
                "Language",
                AppLanguages.Get(language).LanguageTag,
                RegistryValueKind.String);
            key.SetValue(
                "InstallDirectory",
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                RegistryValueKind.String);
            return new ExplorerContextMenuStatus(
                true,
                packageRegistered,
                enabled && packageRegistered);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                System.Runtime.InteropServices.COMException)
        {
            return new ExplorerContextMenuStatus(
                true,
                IsPackageRegisteredSafe(),
                false,
                ex.Message);
        }
    }

    public async Task<ExplorerContextMenuStatus> SynchronizeAsync(AppSettings settings)
    {
        return await SetEnabledAsync(
            settings.ExplorerContextMenuEnabled,
            settings.Language);
    }

    private static bool IsPackageRegisteredSafe()
    {
        try
        {
            return IsPackageRegistered();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPackageRegistered()
    {
        var packageManager = new PackageManager();
        return packageManager.FindPackagesForUser(string.Empty)
            .Any(package => string.Equals(
                package.Id.Name,
                PackageIdentityName,
                StringComparison.OrdinalIgnoreCase));
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static async Task RegisterPackageAsync()
    {
        string packagePath = Path.Combine(AppContext.BaseDirectory, PackageFileName);
        if (!File.Exists(packagePath))
        {
            throw new InvalidOperationException(
                $"Shell integration package is missing: {packagePath}");
        }

        string externalDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) +
            Path.DirectorySeparatorChar;
        var options = new AddPackageOptions
        {
            ExternalLocationUri = new Uri(externalDirectory)
        };
        var packageManager = new PackageManager();
        DeploymentResult result = await packageManager.AddPackageByUriAsync(
            new Uri(packagePath),
            options);
        if (result.ExtendedErrorCode is Exception extendedError &&
            extendedError.HResult != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.ErrorText)
                    ? extendedError.Message
                    : result.ErrorText,
                extendedError);
        }
    }
}
