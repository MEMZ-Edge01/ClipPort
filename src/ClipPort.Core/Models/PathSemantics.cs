namespace ClipPort.Models;

/// <summary>
/// Centralizes path comparison rules so Windows keeps its case-insensitive
/// behavior while Linux respects case-sensitive file systems.
/// </summary>
public static class PathSemantics
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
