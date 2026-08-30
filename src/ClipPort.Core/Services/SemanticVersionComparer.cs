namespace ClipPort.Services;

/// <summary>
/// Compares the semantic version subset used by ClipPort release tags.
/// Both Windows and fnOS update checks use this implementation so an older
/// release can never be presented as an upgrade on only one platform.
/// </summary>
public static class SemanticVersionComparer
{
    public static bool IsNewer(string? currentVersion, string? latestVersion) =>
        TryCompare(currentVersion, latestVersion, out int comparison) && comparison < 0;

    private static bool TryCompare(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParse(left, out VersionParts? leftParts) ||
            !TryParse(right, out VersionParts? rightParts))
        {
            return false;
        }

        comparison = leftParts!.CompareCore(rightParts!);
        if (comparison == 0)
        {
            comparison = leftParts.ComparePrerelease(rightParts!);
        }
        return true;
    }

    private static bool TryParse(string? value, out VersionParts? parts)
    {
        parts = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }
        int buildIndex = text.IndexOf('+');
        if (buildIndex >= 0)
        {
            text = text[..buildIndex];
        }

        int dashIndex = text.IndexOf('-');
        string core = dashIndex >= 0 ? text[..dashIndex] : text;
        string? prerelease = dashIndex >= 0 ? text[(dashIndex + 1)..] : null;
        string[] segments = core.Split('.');
        if (segments.Length is < 1 or > 3 ||
            !int.TryParse(segments[0], out int major) ||
            !int.TryParse(segments.Length > 1 ? segments[1] : "0", out int minor) ||
            !int.TryParse(segments.Length > 2 ? segments[2] : "0", out int patch) ||
            major < 0 || minor < 0 || patch < 0)
        {
            return false;
        }

        parts = new VersionParts(major, minor, patch, prerelease);
        return true;
    }

    private sealed record VersionParts(int Major, int Minor, int Patch, string? Prerelease)
    {
        public int CompareCore(VersionParts other)
        {
            int result = Major.CompareTo(other.Major);
            if (result != 0) return result;
            result = Minor.CompareTo(other.Minor);
            return result != 0 ? result : Patch.CompareTo(other.Patch);
        }

        public int ComparePrerelease(VersionParts other)
        {
            bool leftHasPre = !string.IsNullOrEmpty(Prerelease);
            bool rightHasPre = !string.IsNullOrEmpty(other.Prerelease);
            if (leftHasPre != rightHasPre) return leftHasPre ? -1 : 1;
            if (!leftHasPre) return 0;

            string[] leftParts = Prerelease!.Split('.');
            string[] rightParts = other.Prerelease!.Split('.');
            int count = Math.Min(leftParts.Length, rightParts.Length);
            for (int index = 0; index < count; index++)
            {
                int result = ComparePrereleaseSegment(leftParts[index], rightParts[index]);
                if (result != 0) return result;
            }
            return leftParts.Length.CompareTo(rightParts.Length);
        }

        private static int ComparePrereleaseSegment(string left, string right)
        {
            bool leftIsNumber = int.TryParse(left, out int leftNumber);
            bool rightIsNumber = int.TryParse(right, out int rightNumber);
            if (leftIsNumber && rightIsNumber) return leftNumber.CompareTo(rightNumber);
            if (leftIsNumber != rightIsNumber) return leftIsNumber ? -1 : 1;
            return string.CompareOrdinal(left, right);
        }
    }
}
