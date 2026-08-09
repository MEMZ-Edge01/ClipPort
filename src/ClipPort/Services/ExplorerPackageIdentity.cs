using System.Xml.Linq;

namespace ClipPort.Services;

public sealed record ExplorerPackageIdentity(string Name, string Publisher)
{
    public bool Matches(string name, string publisher) =>
        string.Equals(Name, name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Publisher, publisher, StringComparison.OrdinalIgnoreCase);

    public static ExplorerPackageIdentity ReadManifest(Stream manifestStream)
    {
        XDocument manifest = XDocument.Load(manifestStream);
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
}
