using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;

namespace ClipPort.Services;

public sealed record ExplorerPackageRegistration(
    string Name,
    string Publisher,
    string EffectiveExternalPath);

public sealed record ExplorerPackageIdentity(string Name, string Publisher)
{
    public bool Matches(string name, string publisher) =>
        string.Equals(Name, name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Publisher, publisher, StringComparison.OrdinalIgnoreCase);

    public bool MatchesRegistration(
        ExplorerPackageRegistration registration,
        string expectedExternalPath) =>
        Matches(registration.Name, registration.Publisher) &&
        ExternalPathsEqual(registration.EffectiveExternalPath, expectedExternalPath);

    public bool MatchesAny(
        IEnumerable<ExplorerPackageRegistration> registrations) =>
        registrations.Any(registration => Matches(
            registration.Name,
            registration.Publisher));

    public bool MatchesAny(
        IEnumerable<ExplorerPackageRegistration> registrations,
        string expectedExternalPath) =>
        registrations.Any(registration =>
            MatchesRegistration(registration, expectedExternalPath));

    public static ExplorerPackageIdentity ReadManifest(Stream manifestStream)
    {
        XDocument manifest;
        try
        {
            manifest = XDocument.Load(manifestStream);
        }
        catch (XmlException ex)
        {
            // Normalize malformed XML to the same package-data failure that
            // callers already surface as an operation status.
            throw new InvalidDataException(
                "The shell integration package manifest is malformed.",
                ex);
        }

        XElement identity = manifest.Root?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Identity") ??
            throw new InvalidDataException(
                "The shell integration package manifest has no Identity element.");
        string name = identity.Attribute("Name")?.Value ??
            throw new InvalidDataException(
                "The shell integration package manifest has no identity name.");
        string publisher = identity.Attribute("Publisher")?.Value ??
            throw new InvalidDataException(
                "The shell integration package manifest has no publisher.");
        return new ExplorerPackageIdentity(name, publisher);
    }

    public static ExplorerPackageIdentity Resolve(
        string packagePath,
        string looseManifestPath,
        string certificatePath,
        string expectedPackageName)
    {
        // Development registration can coexist with the unsigned MSIX used to
        // produce it, so its loose manifest is the authoritative local identity.
        if (File.Exists(looseManifestPath))
        {
            using FileStream manifestStream = File.OpenRead(looseManifestPath);
            return ReadManifest(manifestStream);
        }

        if (File.Exists(packagePath))
        {
            using ZipArchive archive = ZipFile.OpenRead(packagePath);
            ZipArchiveEntry manifestEntry = archive.Entries.FirstOrDefault(entry =>
                string.Equals(
                    entry.FullName,
                    "AppxManifest.xml",
                    StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidDataException(
                    "The shell integration package has no AppxManifest.xml file.");
            using Stream manifestStream = manifestEntry.Open();
            return ReadManifest(manifestStream);
        }

        // A signed installation can outlive the MSIX that originally registered it.
        if (File.Exists(certificatePath))
        {
            using var certificate = new X509Certificate2(certificatePath);
            return FromCertificate(expectedPackageName, certificate);
        }

        throw new FileNotFoundException(
            "No shell integration package, development manifest, or certificate is available to identify the registered publisher.");
    }

    public static ExplorerPackageIdentity FromCertificate(
        string expectedPackageName,
        X509Certificate2 certificate) =>
        new(expectedPackageName, certificate.Subject);

    public static ExplorerPackageIdentity? FindRegisteredForExternalPath(
        IEnumerable<ExplorerPackageRegistration> registrations,
        string expectedPackageName,
        string expectedExternalPath)
    {
        List<ExplorerPackageIdentity> identities = registrations
            .Where(registration =>
                string.Equals(
                    registration.Name,
                    expectedPackageName,
                    StringComparison.OrdinalIgnoreCase) &&
                ExternalPathsEqual(
                    registration.EffectiveExternalPath,
                    expectedExternalPath))
            .Select(registration => new ExplorerPackageIdentity(
                registration.Name,
                registration.Publisher))
            .DistinctBy(
                identity => $"{identity.Name}\0{identity.Publisher}",
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        return identities.Count switch
        {
            0 => null,
            1 => identities[0],
            _ => throw new InvalidOperationException(
                "Multiple shell integration publishers are registered for this application directory.")
        };
    }

    public static bool ExternalPathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        string normalizedLeft = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(left));
        string normalizedRight = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(right));
        return string.Equals(
            normalizedLeft,
            normalizedRight,
            StringComparison.OrdinalIgnoreCase);
    }
}
