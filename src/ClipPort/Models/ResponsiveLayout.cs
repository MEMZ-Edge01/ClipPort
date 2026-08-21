namespace ClipPort.Models;

internal static class ResponsiveLayout
{
    internal const double TaskContentHorizontalMargin = 92;

    public static double GetTaskContentWidth(double viewportWidth) =>
        Math.Max(0, viewportWidth - TaskContentHorizontalMargin);
}
