using ClipPort.Models;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using System.Text;
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
    private const string CertificateSubjectMarker = "ClipPort";

    // 与 GetTrustScope 保持一致的存储枚举顺序：LocalMachine 优先，
    // 这样当发布目录中的证书文件丢失时，仍能按同样的顺序识别已安装的证书。
    // 除 Root/TrustedPeople 外，还覆盖 CA（中间证书颁发机构）、My（个人）和
    // TrustedPublisher（受信任的发布者），避免证书装到这些存储后无法识别或卸载。
    private static readonly (StoreLocation Location, StoreName StoreName)[] TrustStoreTargets =
    [
        (StoreLocation.LocalMachine, StoreName.Root),
        (StoreLocation.LocalMachine, StoreName.TrustedPeople),
        (StoreLocation.LocalMachine, StoreName.CertificateAuthority),
        (StoreLocation.LocalMachine, StoreName.My),
        (StoreLocation.LocalMachine, StoreName.TrustedPublisher),
        (StoreLocation.CurrentUser, StoreName.Root),
        (StoreLocation.CurrentUser, StoreName.TrustedPeople),
        (StoreLocation.CurrentUser, StoreName.CertificateAuthority),
        (StoreLocation.CurrentUser, StoreName.My),
        (StoreLocation.CurrentUser, StoreName.TrustedPublisher)
    ];

    public bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// 解析当前部署使用的文件目录：优先应用运行目录；当运行目录缺少配套的
    /// 证书/组件文件（例如从开发或临时目录启动）时，回退到注册表中记录的
    /// 发布目录，避免"文件在发布目录却找不到"导致安装类按钮被错误禁用。
    /// </summary>
    private static string GetDeploymentDirectory()
    {
        string baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (File.Exists(Path.Combine(baseDirectory, PackageFileName)) ||
            File.Exists(Path.Combine(baseDirectory, CertificateFileName)))
        {
            return baseDirectory;
        }

        try
        {
            string? installDirectory = ReadExplorerContextMenuConfiguration()?.InstallDirectory;
            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                string normalizedInstallDirectory =
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
                if (File.Exists(Path.Combine(normalizedInstallDirectory, PackageFileName)) ||
                    File.Exists(Path.Combine(normalizedInstallDirectory, CertificateFileName)))
                {
                    return normalizedInstallDirectory;
                }
            }
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                ArgumentException or NotSupportedException or PathTooLongException or
                System.Security.SecurityException)
        {
            // 注册表记录不可用时保持原行为，仅使用应用运行目录。
        }

        return baseDirectory;
    }

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
            GetDeploymentDirectory());
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
                CryptographicException or InvalidDataException or
                System.Runtime.InteropServices.COMException)
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
                System.Runtime.InteropServices.COMException or CryptographicException or
                InvalidDataException)
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
                            GetDeploymentDirectory()))
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
                GetDeploymentDirectory()))
            .ToList();

    public async Task<ExplorerContextMenuStatus> UninstallCertificateAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return CreateStatus(false, false, false);
        }

        try
        {
            // 优先使用发布目录中的证书文件；文件缺失时从系统证书存储中
            // 识别 ClipPort 证书，避免"证书已安装但文件丢失"时无法卸载的死锁。
            if (!TryResolveClipPortCertificate(
                    out string thumbprint,
                    out string certificateSubject,
                    out _) ||
                string.IsNullOrWhiteSpace(thumbprint))
            {
                throw new InvalidOperationException(
                    "No ClipPort shell integration certificate is available in the certificate stores.");
            }

            ExplorerPackageIdentity certificateIdentity =
                new(PackageIdentityName, certificateSubject);
            if (IsPackageRegisteredForAnyExternalPath(certificateIdentity))
            {
                throw new InvalidOperationException(
                    "Uninstall the shell integration package before removing its certificate.");
            }

            List<CertificateStoreTarget> targets = FindCertificateStoreTargets(
                thumbprint);
            if (targets.Count == 0)
            {
                throw new InvalidOperationException(
                    "No ClipPort shell integration certificate is present in the certificate stores.");
            }

            // 当前用户存储通常不需要管理员权限，先直接尝试删除；
            // 某些环境（受保护存储/策略限制）也会拒绝访问，此时保留目标走提升删除。
            try
            {
                foreach (CertificateStoreTarget target in targets.Where(
                             target => target.Location == StoreLocation.CurrentUser))
                {
                    RemoveCertificateFromCurrentUserStore(thumbprint, target.StoreName);
                }
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or CryptographicException)
            {
                // 忽略，剩余目标统一走提升删除。
            }

            List<CertificateStoreTarget> remainingTargets =
                FindCertificateStoreTargets(thumbprint);
            if (remainingTargets.Count > 0)
            {
                // 本地计算机存储必然需要管理员权限；当前用户存储若直接删除
                // 被拒绝，也需要在同一个提升进程里删除。
                await RemoveCertificateStoresWithElevationAsync(
                    thumbprint,
                    remainingTargets,
                    certificateSubject);
            }

            if (FindCertificateStoreTargets(thumbprint).Count > 0)
            {
                throw new InvalidOperationException(
                    "The certificate is still present in a Windows certificate store.");
            }

            return CreateStatus(true, false, false);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException or InvalidOperationException or
                CryptographicException or Win32Exception or
                System.Runtime.InteropServices.COMException)
        {
            // 证书删除需要管理员授权；区分"用户取消授权"与"授权窗口未能弹出"
            // 两种情况，给出可执行的提示，避免界面看起来像"卸载了却没有任何变化"。
            string errorMessage = ex switch
            {
                Win32Exception { NativeErrorCode: 1223 } =>
                    "用户取消了管理员权限确认，证书未删除。",
                Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException =>
                    "删除证书需要管理员权限，但系统未弹出授权窗口。请完全退出 ClipPort，右键点击 ClipPort.exe 选择“以管理员身份运行”，再重试卸载证书。",
                _ => ex.Message
            };
            return CreateStatus(
                true,
                IsPackageRegisteredSafe(),
                false,
                errorMessage);
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
                System.Runtime.InteropServices.COMException or CryptographicException or
                InvalidDataException)
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
                GetDeploymentDirectory()));

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
                GetDeploymentDirectory());
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
            GetDeploymentDirectory());
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
                GetDeploymentDirectory());
        if (registeredIdentity is not null)
        {
            return registeredIdentity;
        }

        return ExplorerPackageIdentity.Resolve(
            GetPackagePath(),
            Path.Combine(
                GetDeploymentDirectory(),
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
        else if (TryFindClipPortCertificateInStores(
                     out string storedThumbprint,
                     out _,
                     out CertificateTrustScope storedTrustScope))
        {
            // 发布目录的证书文件缺失时，仍可从系统证书存储识别已安装的
            // ClipPort 证书，使"卸载证书"不会因为文件丢失而死锁。
            thumbprint = storedThumbprint;
            trustScope = storedTrustScope;
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

    /// <summary>
    /// 解析 ClipPort 的签名证书：优先使用发布目录中的证书文件，
    /// 文件缺失时从系统证书存储中按主题识别（例如开发证书
    /// "CN=ClipPort Development"）。
    /// </summary>
    private static bool TryResolveClipPortCertificate(
        out string thumbprint,
        out string subject,
        out CertificateTrustScope trustScope)
    {
        string certificatePath = GetCertificatePath();
        if (File.Exists(certificatePath))
        {
            using var certificate = new X509Certificate2(certificatePath);
            thumbprint = certificate.Thumbprint;
            subject = certificate.Subject;
            trustScope = GetTrustScope(certificate);
            return true;
        }

        return TryFindClipPortCertificateInStores(
            out thumbprint,
            out subject,
            out trustScope);
    }

    private static bool TryFindClipPortCertificateInStores(
        out string thumbprint,
        out string subject,
        out CertificateTrustScope trustScope)
    {
        foreach ((StoreLocation location, StoreName storeName) in TrustStoreTargets)
        {
            using var store = new X509Store(storeName, location);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (X509Certificate2 candidate in store.Certificates)
            {
                if (candidate.Subject.Contains(
                        CertificateSubjectMarker,
                        StringComparison.OrdinalIgnoreCase))
                {
                    thumbprint = candidate.Thumbprint;
                    subject = candidate.Subject;
                    trustScope = location == StoreLocation.LocalMachine
                        ? CertificateTrustScope.LocalMachine
                        : CertificateTrustScope.CurrentUser;
                    return true;
                }
            }
        }

        thumbprint = string.Empty;
        subject = string.Empty;
        trustScope = CertificateTrustScope.None;
        return false;
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
        foreach (StoreName storeName in new[]
        {
            StoreName.Root,
            StoreName.TrustedPeople,
            StoreName.CertificateAuthority,
            StoreName.My,
            StoreName.TrustedPublisher
        })
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
                StoreName.TrustedPeople,
                StoreName.CertificateAuthority,
                StoreName.My,
                StoreName.TrustedPublisher
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
        string thumbprint,
        StoreName storeName)
    {
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        X509Certificate2Collection matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint,
            validOnly: false);
        store.RemoveRange(matches);
    }

    private static async Task RemoveCertificateStoresWithElevationAsync(
        string thumbprint,
        IReadOnlyList<CertificateStoreTarget> targets,
        string certificateSubject)
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
        string quotedPackageName = ToPowerShellSingleQuotedLiteral(
            PackageIdentityName);
        string quotedPublisher = ToPowerShellSingleQuotedLiteral(
            certificateSubject);
        // The all-user package check requires elevation, so it runs inside the
        // same elevated helper that owns the machine store removal. A failed
        // enumeration does not prove another user's registration exists, so it
        // is treated as empty instead of blocking certificate removal forever.
        // The script is passed with -EncodedCommand so publisher DN characters
        // (including quotes) survive Windows command-line parsing unchanged.
        string guardCommands =
            "try { " +
            $"$clipPortPackages = @(Get-AppxPackage -Name {quotedPackageName} " +
            "-AllUsers -ErrorAction Stop); " +
            "} catch { $clipPortPackages = @() }; " +
            $"if ($clipPortPackages | Where-Object {{ $_.Publisher -eq {quotedPublisher} }}) " +
            "{{ exit 2 }}; ";
        // certutil 不带 -user 时操作本地计算机存储（需要管理员），
        // 带 -user 时操作当前用户存储，因此两类目标都要在提升进程内覆盖。
        string removalCommands = string.Join(
            "; ",
            targets.Select(target =>
                $"& {quotedCertutilPath} " +
                (target.Location == StoreLocation.CurrentUser ? "-user " : "") +
                $"-delstore {GetCertutilStoreName(target.StoreName)} {quotedThumbprint}; " +
                "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }"));
        string powerShellScript = guardCommands + removalCommands;
        string encodedScript = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(powerShellScript));
        string powerShellPath = Path.Combine(
            systemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powerShellPath)
        {
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encodedScript}",
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
            string message = process.ExitCode switch
            {
                2 => "Uninstall the shell integration package for every user before removing its certificate.",
                3 => "Could not verify other users' registrations before removing the certificate.",
                _ => $"Certificate removal exited with code {process.ExitCode}."
            };
            throw new InvalidOperationException(
                message);
        }
    }

    private static string ToPowerShellSingleQuotedLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    // certutil 使用短存储名，与 X509Store 的 StoreName 枚举字符串并不完全一致
    // （例如中间证书颁发机构是 "CA" 而非 "CertificateAuthority"）。
    private static string GetCertutilStoreName(StoreName storeName) =>
        storeName == StoreName.CertificateAuthority ? "CA" : storeName.ToString();

    private static string GetPackagePath() =>
        Path.Combine(GetDeploymentDirectory(), PackageFileName);

    private static string GetCertificatePath() =>
        Path.Combine(GetDeploymentDirectory(), CertificateFileName);

    [SupportedOSPlatform("windows10.0.19041.0")]
    private static async Task RegisterPackageAsync()
    {
        string packagePath = GetPackagePath();
        if (!File.Exists(packagePath))
        {
            throw new InvalidOperationException(
                $"Shell integration package is missing: {packagePath}");
        }

        string externalDirectory = Path.TrimEndingDirectorySeparator(GetDeploymentDirectory()) +
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
