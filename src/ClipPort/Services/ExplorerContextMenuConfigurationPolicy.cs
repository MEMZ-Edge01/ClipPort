namespace ClipPort.Services;

public sealed record ExplorerContextMenuConfiguration(
    bool Enabled,
    string Language,
    string InstallDirectory);

public static class ExplorerContextMenuConfigurationPolicy
{
    public static bool HasSiblingRegistration(
        IEnumerable<ExplorerPackageRegistration> registrations,
        string expectedPackageName,
        string currentExternalPath) =>
        registrations.Any(registration =>
            string.Equals(
                registration.Name,
                expectedPackageName,
                StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(registration.EffectiveExternalPath) &&
            !ExplorerPackageIdentity.ExternalPathsEqual(
                registration.EffectiveExternalPath,
                currentExternalPath));

    public static bool ShouldDeferSynchronizationToConfigurationOwner(
        ExplorerContextMenuConfiguration? configuration,
        IEnumerable<ExplorerPackageRegistration> registrations,
        string expectedPackageName,
        string currentExternalPath)
    {
        List<ExplorerPackageRegistration> packageRegistrations = registrations
            .Where(registration =>
                string.Equals(
                    registration.Name,
                    expectedPackageName,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(registration.EffectiveExternalPath))
            .ToList();
        bool currentPackageRegistered = packageRegistrations.Any(registration =>
            ExplorerPackageIdentity.ExternalPathsEqual(
                registration.EffectiveExternalPath,
                currentExternalPath));

        if (!currentPackageRegistered && packageRegistrations.Count > 0)
        {
            return true;
        }

        return configuration is not null &&
            !ExplorerPackageIdentity.ExternalPathsEqual(
                configuration.InstallDirectory,
                currentExternalPath) &&
            packageRegistrations.Any(registration =>
                ExplorerPackageIdentity.ExternalPathsEqual(
                    registration.EffectiveExternalPath,
                    configuration.InstallDirectory));
    }

    public static bool ShouldDisableBeforeRemoval(
        ExplorerContextMenuConfiguration? configuration,
        IEnumerable<ExplorerPackageRegistration> registrations,
        string expectedPackageName,
        string removedExternalPath) =>
        configuration is not null &&
        !registrations.Any(registration =>
            string.Equals(
                registration.Name,
                expectedPackageName,
                StringComparison.OrdinalIgnoreCase) &&
            !ExplorerPackageIdentity.ExternalPathsEqual(
                registration.EffectiveExternalPath,
                removedExternalPath) &&
            ExplorerPackageIdentity.ExternalPathsEqual(
                registration.EffectiveExternalPath,
                configuration.InstallDirectory));

    public static ExplorerContextMenuConfiguration? ReconcileAfterRemoval(
        ExplorerContextMenuConfiguration? configuration,
        IEnumerable<ExplorerPackageRegistration> remainingRegistrations,
        string expectedPackageName)
    {
        if (configuration is null)
        {
            return null;
        }

        List<ExplorerPackageRegistration> candidates = remainingRegistrations
            .Where(registration =>
                string.Equals(
                    registration.Name,
                    expectedPackageName,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(registration.EffectiveExternalPath))
            .OrderBy(
                registration => registration.EffectiveExternalPath,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                registration => registration.Publisher,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        ExplorerPackageRegistration? replacement = candidates.FirstOrDefault(
            registration => ExplorerPackageIdentity.ExternalPathsEqual(
                registration.EffectiveExternalPath,
                configuration.InstallDirectory)) ??
            candidates.FirstOrDefault();

        return replacement is null
            ? null
            : configuration with
            {
                InstallDirectory = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(replacement.EffectiveExternalPath))
            };
    }
}
