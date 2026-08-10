using ClipPort.Models;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using Windows.Management.Deployment;

namespace ClipPort.Services;

public enum CertificateTrustScope
{
    None,
    CurrentUser,
    LocalMachine,
    TrustedChain
}

public sealed record ExplorerContextMenuStatus(
    bool IsSupported,
    bool IsPackageRegistered,
    bool IsEnabled,
    bool IsPackageFileAvailable,
    bool IsCertificateFileAvailable,
    CertificateTrustScope CertificateTrustScope,
    string? CertificateThumbprint,
    string? CertificateErrorMessage = null,
    string? ErrorMessage = null);

public sealed class ExplorerContextMenuService
{
    public const string PackageIdentityName = "MEMZEdge01.ClipPort.ShellIntegration";
    public const string RegistryPath = @"Software\ClipPort\ExplorerContextMenu";
    public const string PackageFileName = "ClipPort.ShellIntegration.msix";
    public const string CertificateFileName = "ClipPort.ShellIntegration.cer";
    public const string DevelopmentRegistrationDirectoryName =
        "ShellIntegration.Development";

    public bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public bool ShouldDisableSavedSettingBeforePackageRemoval()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return true;
        }

        var packageManager = new PackageManager();
        return !ExplorerContextMenuConfigurationPolicy.HasSiblingRegistration(
            GetPackageRegistrations(packageManager),
            PackageIdentityName,
            AppContext.BaseDirectory);
    }

    public ExplorerContextMenuStatus GetStatus()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return CreateStatus(false, false, false);
        }

        try
        {
            bool packageRegistered = IsPackageRegistered();
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            bool enabled = packageRegistered &&
                Convert.ToInt32(key?.GetValue("Enabled", 0)) == 1;
            return CreateStatus(true, packageRegistered, enabled);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                CryptographicException)
        {
            return CreateStatus(true, false, false, ex.Message);
        }
    }

    public Process OpenCertificateInstaller()
    {
        string certificatePath = GetCertificatePath();
        if (!File.Exists(certificatePath))
        {
            throw new InvalidOperationException(
                $"Shell integration certificate is missing: {certificatePath}");
        }

        return Process.Start(new ProcessStartInfo(certificatePath)
        {
            // Windows owns the certificate wizard and the trust decision. ClipPort
            // must never add a certificate to a trusted store silently.
            UseShellExecute = true
        }) ?? throw new InvalidOperationException(
            "Windows did not start the certificate installer.");
    }

    public async Task<ExplorerContextMenuStatus> InstallPackageAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return CreateStatus(false, false, false);
        }

        try
        {
            if (!IsPackageRegistered())
            {
                await RegisterPackageAsync();
            }

            bool packageRegistered = IsPackageRegistered();
            if (!packageRegistered)
            {
                throw new InvalidOperationException(
                    "Windows did not report the shell integration package after registration.");
            }

            return CreateStatus(true, true, ReadEnabledState());
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                System.Runtime.InteropServices.COMException or CryptographicException)
        {
            return CreateStatus(
                true,
                IsPackageRegisteredSafe(),
                false,
                ex.Message);
        }
    }

    public async Task<ExplorerContextMenuStatus> UninstallPackageAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return CreateStatus(false, false, false);
        }

        try
        {
            var packageManager = new PackageManager();
            ExplorerContextMenuConfiguration? configuration =
                ReadExplorerContextMenuConfiguration();
            List<ExplorerPackageRegistration> registrations =
                GetPackageRegistrations(packageManager);
            await ExplorerIntegrationPackageRemovalWorkflow.RunAsync(
                () =>
                {
                    if (ExplorerContextMenuConfigurationPolicy.ShouldDisableBeforeRemoval(
                            configuration,
                            registrations,
                            PackageIdentityName,
                            AppContext.BaseDirectory))
                    {
                        WriteExplorerContextMenuEnabledState(false);
                    }
                },
                () => RemoveBundledPackagesAsync(packageManager),
                () => ReconcileExplorerContextMenuConfiguration(
                    packageManager,
                    configuration));

            return CreateStatus(true, false, false);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                System.Runtime.InteropServices.COMException or InvalidDataException or
                CryptographicException)
        {
            bool packageRegistered = IsPackageRegisteredSafe();
            return CreateStatus(
                true,
                packageRegistered,
                packageRegistered && ReadEnabledStateSafe(),
                ex.Message);
        }
    }

    private static async Task RemoveBundledPackagesAsync(
        PackageManager packageManager)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException(
                "Shell integration package removal requires Windows 10 version 2004 or later.");
        }

        ExplorerPackageIdentity packageIdentity =
            ReadAvailablePackageIdentity(packageManager);
        List<Windows.ApplicationModel.Package> packages =
            FindRegisteredPackagesForCurrentDirectory(packageManager, packageIdentity);

        foreach (Windows.ApplicationModel.Package package in packages)
        {
            DeploymentResult result =
                await packageManager.RemovePackageAsync(package.Id.FullName);
            ThrowIfDeploymentFailed(result);
        }

        if (IsPackageRegistered(packageIdentity))
        {
            throw new InvalidOperationException(
                "Windows still reports the shell integration package after removal.");
        }
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static List<Windows.ApplicationModel.Package>
        FindRegisteredPackagesForCurrentDirectory(
            PackageManager packageManager,
            ExplorerPackageIdentity packageIdentity) =>
        packageManager
            .FindPackagesForUser(string.Empty)
            .Where(package => packageIdentity.MatchesRegistration(
                new ExplorerPackageRegistration(
                    package.Id.Name,
                    package.Id.Publisher,
                    package.EffectiveExternalPath),
                AppContext.BaseDirectory))
            .ToList();

    public async Task<ExplorerContextMenuStatus> UninstallCertificateAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return CreateStatus(false, false, false);
        }

        try
        {
            string certificatePath = GetCertificatePath();
            if (!File.Exists(certificatePath))
            {
                throw new InvalidOperationException(
                    $"Shell integration certificate is missing: {certificatePath}");
            }

            using var certificate = new X509Certificate2(certificatePath);
            ExplorerPackageIdentity certificateIdentity =
                ExplorerPackageIdentity.FromCertificate(
                    PackageIdentityName,
                    certificate);
            if (IsPackageRegisteredForAnyExternalPath(certificateIdentity))
            {
                throw new InvalidOperationException(
                    "Uninstall the shell integration package before removing its certificate.");
            }

            List<CertificateStoreTarget> targets = FindCertificateStoreTargets(
                certificate.Thumbprint);

            foreach (CertificateStoreTarget target in targets.Where(
                         target => target.Location == StoreLocation.CurrentUser))
            {
                RemoveCertificateFromCurrentUserStore(certificate, target.StoreName);
            }

            List<CertificateStoreTarget> machineTargets = targets
                .Where(target => target.Location == StoreLocation.LocalMachine)
                .ToList();
            if (machineTargets.Count > 0)
            {
                await RemoveCertificateFromLocalMachineStoresAsync(
                    certificate.Thumbprint,
                    machineTargets);
            }

            if (FindCertificateStoreTargets(certificate.Thumbprint).Count > 0)
            {
                throw new InvalidOperationException(
                    "The certificate is still present in a Windows certificate store.");
            }

            return CreateStatus(true, false, false);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                CryptographicException or Win32Exception)
        {
            return CreateStatus(
                true,
                IsPackageRegisteredSafe(),
                false,
                ex.Message);
        }
    }

    public async Task<ExplorerContextMenuStatus> SetEnabledAsync(
        bool enabled,
        AppLanguage language)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return CreateStatus(false, false, false);
        }

        try
        {
            return await SetEnabledOnSupportedWindowsAsync(enabled, language);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                System.Runtime.InteropServices.COMException or CryptographicException)
        {
            return CreateStatus(
                true,
                IsPackageRegisteredSafe(),
                false,
                ex.Message);
        }
    }

    private async Task<ExplorerContextMenuStatus> SetEnabledOnSupportedWindowsAsync(
        bool enabled,
        AppLanguage language)
    {
        bool packageRegistered = IsPackageRegistered();
        if (enabled && !packageRegistered)
        {
            ExplorerContextMenuStatus installationStatus = await InstallPackageAsync();
            if (installationStatus.ErrorMessage is not null ||
                !installationStatus.IsPackageRegistered)
            {
                return installationStatus;
            }
            packageRegistered = true;
        }

        WriteExplorerContextMenuSettings(enabled, language);
        return CreateStatus(
            true,
            packageRegistered,
            enabled && packageRegistered);
    }

    private static void WriteExplorerContextMenuSettings(
        bool enabled,
        AppLanguage language) =>
        WriteExplorerContextMenuConfiguration(
            new ExplorerContextMenuConfiguration(
                enabled,
                AppLanguages.Get(language).LanguageTag,
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)));

    private static ExplorerContextMenuConfiguration?
        ReadExplorerContextMenuConfiguration()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key is null)
        {
            return null;
        }

        return new ExplorerContextMenuConfiguration(
            key.GetValue("Enabled", 0) is int enabled && enabled == 1,
            key.GetValue("Language") as string ?? "zh-CN",
            key.GetValue("InstallDirectory") as string ?? string.Empty);
    }

    private static void WriteExplorerContextMenuConfiguration(
        ExplorerContextMenuConfiguration configuration)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
        WriteExplorerContextMenuEnabledState(key, configuration.Enabled);
        key.SetValue(
            "Language",
            configuration.Language,
            RegistryValueKind.String);
        key.SetValue(
            "InstallDirectory",
            configuration.InstallDirectory,
            RegistryValueKind.String);
    }

    private static void ReconcileExplorerContextMenuConfiguration(
        PackageManager packageManager,
        ExplorerContextMenuConfiguration? configuration)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new PlatformNotSupportedException(
                "Shell integration package maintenance requires Windows 10 version 2004 or later.");
        }

        ExplorerContextMenuConfiguration? reconciled =
            ExplorerContextMenuConfigurationPolicy.ReconcileAfterRemoval(
                configuration,
                GetPackageRegistrations(packageManager),
                PackageIdentityName);
        if (reconciled is null)
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                RegistryPath,
                throwOnMissingSubKey: false);
            return;
        }

        WriteExplorerContextMenuConfiguration(reconciled);
    }

    private static void WriteExplorerContextMenuEnabledState(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
        WriteExplorerContextMenuEnabledState(key, enabled);
    }

    private static void WriteExplorerContextMenuEnabledState(
        RegistryKey key,
        bool enabled) =>
        key.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);

    public async Task<ExplorerContextMenuStatus> SynchronizeAsync(AppSettings settings)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return CreateStatus(false, false, false);
        }

        try
        {
            if (ShouldDeferExplorerSynchronization())
            {
                // Another registered ClipPort copy owns the shared shell menu.
                // Startup must not reinstall this copy or overwrite that owner.
                return GetStatus();
            }
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                System.Runtime.InteropServices.COMException or ArgumentException or
                NotSupportedException)
        {
            return CreateStatus(
                true,
                IsPackageRegisteredSafe(),
                false,
                ex.Message);
        }

        return await SetEnabledAsync(
            settings.ExplorerContextMenuEnabled,
            settings.Language);
    }

    public bool ShouldDeferExplorerSynchronization()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        var packageManager = new PackageManager();
        return ExplorerContextMenuConfigurationPolicy
            .ShouldDeferSynchronizationToConfigurationOwner(
                ReadExplorerContextMenuConfiguration(),
                GetPackageRegistrations(packageManager),
                PackageIdentityName,
                AppContext.BaseDirectory);
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        var packageManager = new PackageManager();
        try
        {
            // Status and maintenance actions must select the same package.
            // A same-name package from another publisher or application path
            // must not make this installation appear removable.
            ExplorerPackageIdentity identity =
                ReadAvailablePackageIdentity(packageManager);
            return IsPackageRegistered(packageManager, identity);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static bool IsPackageRegistered(ExplorerPackageIdentity identity)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        var packageManager = new PackageManager();
        return IsPackageRegistered(packageManager, identity);
    }

    private static bool IsPackageRegisteredForAnyExternalPath(
        ExplorerPackageIdentity identity)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        var packageManager = new PackageManager();
        return identity.MatchesAny(GetPackageRegistrations(packageManager));
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static bool IsPackageRegistered(
        PackageManager packageManager,
        ExplorerPackageIdentity identity)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        return identity.MatchesAny(
            packageManager.FindPackagesForUser(string.Empty)
                .Select(package => new ExplorerPackageRegistration(
                    package.Id.Name,
                    package.Id.Publisher,
                    package.EffectiveExternalPath)),
            AppContext.BaseDirectory);
    }

    private static List<ExplorerPackageRegistration> GetPackageRegistrations(
        PackageManager packageManager)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return [];
        }

        return GetPackageRegistrationsOnSupportedWindows(packageManager);
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static List<ExplorerPackageRegistration>
        GetPackageRegistrationsOnSupportedWindows(
            PackageManager packageManager) =>
        packageManager.FindPackagesForUser(string.Empty)
            .Select(package => new ExplorerPackageRegistration(
                package.Id.Name,
                package.Id.Publisher,
                package.EffectiveExternalPath))
            .ToList();

    private static bool ReadEnabledState()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return Convert.ToInt32(key?.GetValue("Enabled", 0)) == 1;
    }

    private static bool ReadEnabledStateSafe()
    {
        try
        {
            return ReadEnabledState();
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static ExplorerPackageIdentity ReadAvailablePackageIdentity(
        PackageManager packageManager)
    {
        // The package registered for this exact external directory is stronger
        // evidence than files left beside the application by another workflow.
        ExplorerPackageIdentity? registeredIdentity =
            ExplorerPackageIdentity.FindRegisteredForExternalPath(
                packageManager.FindPackagesForUser(string.Empty)
                    .Where(package => string.Equals(
                        package.Id.Name,
                        PackageIdentityName,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(package => new ExplorerPackageRegistration(
                        package.Id.Name,
                        package.Id.Publisher,
                        package.EffectiveExternalPath)),
                PackageIdentityName,
                AppContext.BaseDirectory);
        if (registeredIdentity is not null)
        {
            return registeredIdentity;
        }

        return ExplorerPackageIdentity.Resolve(
            GetPackagePath(),
            Path.Combine(
                AppContext.BaseDirectory,
                DevelopmentRegistrationDirectoryName,
                "AppxManifest.xml"),
            GetCertificatePath(),
            PackageIdentityName);
    }

    private static ExplorerContextMenuStatus CreateStatus(
        bool supported,
        bool packageRegistered,
        bool enabled,
        string? errorMessage = null)
    {
        string packagePath = GetPackagePath();
        string certificatePath = GetCertificatePath();
        bool certificateAvailable = File.Exists(certificatePath);
        string? thumbprint = null;
        string? certificateErrorMessage = null;
        CertificateTrustScope trustScope = CertificateTrustScope.None;

        if (certificateAvailable)
        {
            try
            {
                using var certificate = new X509Certificate2(certificatePath);
                thumbprint = certificate.Thumbprint;
                trustScope = GetTrustScope(certificate);
            }
            catch (Exception ex) when (
                ex is CryptographicException or UnauthorizedAccessException or
                    IOException or InvalidOperationException)
            {
                // A failing store must be reported through the status instead
                // of re-enumerated by the caller's error handling.
                certificateErrorMessage = ex.Message;
            }
        }

        return new ExplorerContextMenuStatus(
            supported,
            packageRegistered,
            enabled && packageRegistered,
            File.Exists(packagePath),
            certificateAvailable,
            trustScope,
            thumbprint,
            certificateErrorMessage,
            errorMessage);
    }

    private static CertificateTrustScope GetTrustScope(X509Certificate2 certificate)
    {
        if (ContainsCertificate(StoreLocation.LocalMachine, certificate.Thumbprint))
        {
            return CertificateTrustScope.LocalMachine;
        }
        if (ContainsCertificate(StoreLocation.CurrentUser, certificate.Thumbprint))
        {
            return CertificateTrustScope.CurrentUser;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate)
            ? CertificateTrustScope.TrustedChain
            : CertificateTrustScope.None;
    }

    private static bool ContainsCertificate(
        StoreLocation location,
        string thumbprint)
    {
        foreach (StoreName storeName in new[] { StoreName.Root, StoreName.TrustedPeople })
        {
            if (ContainsCertificate(location, storeName, thumbprint))
            {
                return true;
            }
        }

        return false;
    }

    private static List<CertificateStoreTarget> FindCertificateStoreTargets(
        string thumbprint)
    {
        IEnumerable<CertificateStoreTarget> targets =
            from location in new[]
            {
                StoreLocation.CurrentUser,
                StoreLocation.LocalMachine
            }
            from storeName in new[]
            {
                StoreName.Root,
                StoreName.TrustedPeople
            }
            select new CertificateStoreTarget(location, storeName);

        return CertificateStoreSearch.FindMatches(
            targets,
            target => ContainsCertificate(
                target.Location,
                target.StoreName,
                thumbprint));
    }

    private static bool ContainsCertificate(
        StoreLocation location,
        StoreName storeName,
        string thumbprint)
    {
        using var store = new X509Store(storeName, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint,
            validOnly: false).Count > 0;
    }

    private static void RemoveCertificateFromCurrentUserStore(
        X509Certificate2 certificate,
        StoreName storeName)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        X509Certificate2Collection matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            certificate.Thumbprint,
            validOnly: false);
        store.RemoveRange(matches);
    }

    private static async Task RemoveCertificateFromLocalMachineStoresAsync(
        string thumbprint,
        IReadOnlyList<CertificateStoreTarget> targets)
    {
        string systemDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.System);
        string certutilPath = Path.Combine(systemDirectory, "certutil.exe");
        if (!File.Exists(certutilPath))
        {
            throw new InvalidOperationException(
                $"Windows certificate utility is missing: {certutilPath}");
        }

        string quotedCertutilPath = ToPowerShellSingleQuotedLiteral(certutilPath);
        string quotedThumbprint = ToPowerShellSingleQuotedLiteral(thumbprint);
        string commands = string.Join(
            "; ",
            targets.Select(target =>
                $"& {quotedCertutilPath} -delstore {target.StoreName} {quotedThumbprint}; " +
                "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }"));
        string powerShellPath = Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powerShellPath)
        {
            Arguments = $"-NoProfile -NonInteractive -Command \"& {{ {commands} }}\"",
            UseShellExecute = true,
            Verb = "runas"
        };
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException(
                "Windows did not start the elevated certificate removal process.");
        }

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Certificate removal exited with code {process.ExitCode}.");
        }
    }

    private static string ToPowerShellSingleQuotedLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string GetPackagePath() =>
        Path.Combine(AppContext.BaseDirectory, PackageFileName);

    private static string GetCertificatePath() =>
        Path.Combine(AppContext.BaseDirectory, CertificateFileName);

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static async Task RegisterPackageAsync()
    {
        string packagePath = GetPackagePath();
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
        ThrowIfDeploymentFailed(result);
    }

    private static void ThrowIfDeploymentFailed(DeploymentResult result)
    {
        if (result.ExtendedErrorCode is not Exception extendedError ||
            extendedError.HResult == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.ErrorText)
                ? extendedError.Message
                : result.ErrorText,
            extendedError);
    }

    private sealed record CertificateStoreTarget(
        StoreLocation Location,
        StoreName StoreName);
}
