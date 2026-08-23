using System.Globalization;

namespace ClipPort.Models;

public static class DisplayFormatting
{
    private static readonly double[] WaveformDivisionMultipliers = [1, 1.5, 2, 3, 5];

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:F0} {units[unit]}"
            : $"{value:F2} {units[unit]}";
    }

    public static string FormatDuration(TimeSpan value)
    {
        TimeSpan safeValue = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return safeValue.TotalDays >= 1
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)safeValue.TotalDays}:{safeValue.Hours:00}:{safeValue.Minutes:00}:{safeValue.Seconds:00}")
            : safeValue.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    public static double GetWaveformDivisionStep(double displayPeak)
    {
        if (!double.IsFinite(displayPeak) || displayPeak <= 0)
        {
            return 0;
        }

        // Four grid labels create three labelled intervals. Choose a pleasant
        // step that keeps the peak at or below the highest labelled gridline;
        // the drawing area retains one additional interval as visual headroom.
        double requiredStep = displayPeak / 3;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(requiredStep)));
        foreach (double multiplier in WaveformDivisionMultipliers)
        {
            double candidate = multiplier * magnitude;
            if (candidate >= requiredStep)
            {
                return candidate;
            }
        }

        return 10 * magnitude;
    }

    /// <summary>
    /// An idle background-verification event updates its speed but must not
    /// replace the foreground copy phase shown to the user.
    /// </summary>
    public static CopyPhase? GetDisplayedOperationPhase(
        bool copyStillActive,
        CopyProgressInfo? latestProgress,
        CopyProgressInfo? verificationProgress) =>
        copyStillActive && verificationProgress is { IsPhaseActive: false }
            ? CopyPhase.Copying
            : latestProgress?.Phase;
}
