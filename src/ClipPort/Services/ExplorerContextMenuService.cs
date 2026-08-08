using ClipPort.Models;
using Microsoft.Win32;
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
            return CreateStatus(
                true,
                packageRegistered,
                enabled && packageRegistered);
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

    private static bool ReadEnabledState()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return Convert.ToInt32(key?.GetValue("Enabled", 0)) == 1;
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
