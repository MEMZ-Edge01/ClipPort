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

    public void OpenCertificateInstaller()
    {
        string certificatePath = GetCertificatePath();
        if (!File.Exists(certificatePath))
        {
            throw new InvalidOperationException(
                $"Shell integration certificate is missing: {certificatePath}");
        }

        Process.Start(new ProcessStartInfo(certificatePath)
        {
            // Windows owns the certificate wizard and the trust decision. ClipPort
            // must never add a certificate to a trusted store silently.
            UseShellExecute = true
        });
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
            await ExplorerIntegrationPackageRemovalWorkflow.RunAsync(
                () => WriteExplorerContextMenuEnabledState(false),
                () => RemoveBundledPackagesAsync(packageManager),
                () => Registry.CurrentUser.DeleteSubKeyTree(
                    RegistryPath,
                    throwOnMissingSubKey: false));

            return CreateStatus(true, false, false);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                System.Runtime.InteropServices.COMException or InvalidDataException)
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
        List<Windows.ApplicationModel.Package> packages = packageManager
            .FindPackagesForUser(string.Empty)
            .Where(package => packageIdentity.Matches(
                package.Id.Name,
                package.Id.Publisher))
            .ToList();

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

    public async Task<ExplorerContextMenuStatus> UninstallCertificateAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return CreateStatus(false, false, false);
        }

        try
        {
            var packageManager = new PackageManager();
            ExplorerPackageIdentity packageIdentity =
                ReadAvailablePackageIdentity(packageManager);
            if (IsPackageRegistered(packageIdentity))
            {
                throw new InvalidOperationException(
                    "Uninstall the shell integration package before removing its certificate.");
            }

            string certificatePath = GetCertificatePath();
            if (!File.Exists(certificatePath))
            {
                throw new InvalidOperationException(
                    $"Shell integration certificate is missing: {certificatePath}");
            }

            using var certificate = new X509Certificate2(certificatePath);
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
                System.Runtime.InteropServices.COMException)
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
        AppLanguage language)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
        WriteExplorerContextMenuEnabledState(key, enabled);
        key.SetValue(
            "Language",
            AppLanguages.Get(language).LanguageTag,
            RegistryValueKind.String);
        key.SetValue(
            "InstallDirectory",
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            RegistryValueKind.String);
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

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static bool IsPackageRegistered(
        PackageManager packageManager,
        ExplorerPackageIdentity identity)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return false;
        }

        return identity.MatchesAny(packageManager.FindPackagesForUser(string.Empty)
            .Select(package => new ExplorerPackageRegistration(
                package.Id.Name,
                package.Id.Publisher,
                package.EffectiveExternalPath)));
    }

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
            catch (CryptographicException ex)
            {
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
            try
            {
                using var store = new X509Store(storeName, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                if (store.Certificates.Find(
                    X509FindType.FindByThumbprint,
                    thumbprint,
                    validOnly: false).Count > 0)
                {
                    return true;
                }
            }
            catch (CryptographicException)
            {
                // A missing or inaccessible store is treated as not trusted.
            }
        }
        return false;
    }

    private static List<CertificateStoreTarget> FindCertificateStoreTargets(
        string thumbprint)
    {
        var targets = new List<CertificateStoreTarget>();
        foreach (StoreLocation location in new[]
                 {
                     StoreLocation.CurrentUser,
                     StoreLocation.LocalMachine
                 })
        {
            foreach (StoreName storeName in new[]
                     {
                         StoreName.Root,
                         StoreName.TrustedPeople
                     })
            {
                if (ContainsCertificate(location, storeName, thumbprint))
                {
                    targets.Add(new CertificateStoreTarget(location, storeName));
                }
            }
        }
        return targets;
    }

    private static bool ContainsCertificate(
        StoreLocation location,
        StoreName storeName,
        string thumbprint)
    {
        try
        {
            using var store = new X509Store(storeName, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return store.Certificates.Find(
                X509FindType.FindByThumbprint,
                thumbprint,
                validOnly: false).Count > 0;
        }
        catch (CryptographicException)
        {
            return false;
        }
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
