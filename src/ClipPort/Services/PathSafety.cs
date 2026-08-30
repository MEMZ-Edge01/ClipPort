namespace ClipPort.Services;

using ClipPort.Models;

public enum PathValidationError
{
    None,
    SourceAndDestinationAreSame,
    DestinationIsInsideSource,
    SourceIsInsideDestination,
    InvalidSubfolderName,
    DestinationContainsReparsePoint,
    InvalidPath
}

public static class PathSafety
{
    public static bool TryValidateSourceAndDestination(
        string source,
        string destination,
        out PathValidationError error)
    {
        try
        {
            string normalizedSource = NormalizeDirectoryPath(source);
            string normalizedDestination = NormalizeDirectoryPath(destination);

            if (string.Equals(
                    normalizedSource,
                    normalizedDestination,
                    PathSemantics.Comparison))
            {
                error = PathValidationError.SourceAndDestinationAreSame;
                return false;
            }

            if (IsSameOrDescendant(normalizedDestination, normalizedSource))
            {
                error = PathValidationError.DestinationIsInsideSource;
                return false;
            }

            if (IsSameOrDescendant(normalizedSource, normalizedDestination))
            {
                error = PathValidationError.SourceIsInsideDestination;
                return false;
            }

            if (ContainsReparsePoint(normalizedDestination))
            {
                error = PathValidationError.DestinationContainsReparsePoint;
                return false;
            }

            error = PathValidationError.None;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or IOException)
        {
            error = PathValidationError.InvalidPath;
            return false;
        }
    }

    public static bool TryResolveSubfolder(
        string parentDirectory,
        string? subfolderName,
        out string destination)
    {
        try
        {
            string normalizedParent = NormalizeDirectoryPath(parentDirectory);
            string name = (subfolderName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                destination = normalizedParent;
                return true;
            }

            if (Path.IsPathRooted(name) ||
                name is "." or ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.Contains(Path.DirectorySeparatorChar) ||
                name.Contains(Path.AltDirectorySeparatorChar) ||
                name.EndsWith(' ') ||
                name.EndsWith('.'))
            {
                destination = normalizedParent;
                return false;
            }

            string resolved = NormalizeDirectoryPath(Path.Combine(normalizedParent, name));
            if (!IsSameOrDescendant(resolved, normalizedParent) ||
                string.Equals(resolved, normalizedParent, PathSemantics.Comparison))
            {
                destination = normalizedParent;
                return false;
            }

            destination = resolved;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or IOException)
        {
            destination = parentDirectory;
            return false;
        }
    }

    public static string GetSuggestedSubfolderName(
        string sourcePath,
        DateTime timestamp)
    {
        string normalized = NormalizeDirectoryPath(sourcePath);
        string name = new DirectoryInfo(normalized).Name;
        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            string root = Path.GetPathRoot(normalized) ?? normalized;
            try
            {
                var drive = new DriveInfo(root);
                name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .TrimEnd(Path.VolumeSeparatorChar)
                    : drive.VolumeLabel;
            }
            catch (Exception ex) when (
                ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                name = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .TrimEnd(Path.VolumeSeparatorChar);
            }
        }

        string safeName = string.Concat(name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ||
            character == Path.DirectorySeparatorChar ||
            character == Path.AltDirectorySeparatorChar
                ? '_'
                : character)).Trim(' ', '.', '_');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "Media";
        }

        return $"{safeName}_{timestamp:yyyyMMddHHmmss}";
    }

    public static bool PathsOverlap(string first, string second)
    {
        string normalizedFirst = NormalizeDirectoryPath(first);
        string normalizedSecond = NormalizeDirectoryPath(second);
        return IsSameOrDescendant(normalizedFirst, normalizedSecond) ||
               IsSameOrDescendant(normalizedSecond, normalizedFirst);
    }

    public static bool IsSameOrDescendantPath(string candidate, string parent) =>
        IsSameOrDescendant(
            NormalizeDirectoryPath(candidate),
            NormalizeDirectoryPath(parent));

    public static void EnsureDestinationDoesNotTraverseReparsePoint(string path)
    {
        if (ContainsReparsePoint(path))
        {
            throw new IOException(
                ResourceService.Format("Error.DestinationReparsePoint", path));
        }
    }

    private static bool ContainsReparsePoint(string path)
    {
        string normalized = NormalizeDirectoryPath(path);
        string? root = Path.GetPathRoot(normalized);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        string current = root;
        string relative = Path.GetRelativePath(root, normalized);
        if (relative == ".")
        {
            return GetExistingPathState(current) == ExistingPathState.Unsafe;
        }

        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            ExistingPathState state = GetExistingPathState(current);
            if (state == ExistingPathState.Missing)
            {
                break;
            }

            if (state == ExistingPathState.Unsafe)
            {
                return true;
            }
        }

        return false;
    }

    private static ExistingPathState GetExistingPathState(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                ? ExistingPathState.Unsafe
                : ExistingPathState.Safe;
        }
        catch (Exception ex) when (
            ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return ExistingPathState.Missing;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Fail closed when an existing path cannot be inspected.
            return ExistingPathState.Unsafe;
        }
    }

    private enum ExistingPathState
    {
        Missing,
        Safe,
        Unsafe
    }

    private static bool IsSameOrDescendant(string candidate, string parent)
    {
        if (string.Equals(candidate, parent, PathSemantics.Comparison))
        {
            return true;
        }

        string parentWithSeparator = parent.EndsWith(Path.DirectorySeparatorChar) ||
                                     parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parentWithSeparator, PathSemantics.Comparison);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(fullPath, root, PathSemantics.Comparison))
        {
            return root;
        }

        string trimmed = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) ? root ?? fullPath : trimmed;
    }
}
