namespace ClipPort.Models;

public enum QuickStartDirectoryRole
{
    Source,
    Destination
}

public sealed record QuickStartRequest(
    QuickStartDirectoryRole Role,
    string DirectoryPath)
{
    public static bool TryCreate(
        QuickStartDirectoryRole role,
        string? directoryPath,
        out QuickStartRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(directoryPath);
            if (!Path.IsPathFullyQualified(fullPath) || !Directory.Exists(fullPath))
            {
                return false;
            }

            request = new QuickStartRequest(role, fullPath);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }
}

public sealed record QuickStartDraft(
    string? SourceDirectory,
    string? DestinationDirectory)
{
    public QuickStartDraft Apply(QuickStartRequest request) => request.Role switch
    {
        QuickStartDirectoryRole.Source => this with
        {
            SourceDirectory = request.DirectoryPath
        },
        QuickStartDirectoryRole.Destination => this with
        {
            DestinationDirectory = request.DirectoryPath
        },
        _ => this
    };
}

public static class QuickStartRequestParser
{
    public const string SourceOption = "--quick-start-source";
    public const string DestinationOption = "--quick-start-destination";

    public static QuickStartRequest? Parse(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            QuickStartDirectoryRole? role = arguments[index] switch
            {
                SourceOption => QuickStartDirectoryRole.Source,
                DestinationOption => QuickStartDirectoryRole.Destination,
                _ => null
            };
            if (role is null || index + 1 >= arguments.Count)
            {
                continue;
            }

            if (QuickStartRequest.TryCreate(
                    role.Value,
                    arguments[index + 1],
                    out QuickStartRequest? request))
            {
                return request;
            }
        }

        return null;
    }
}
