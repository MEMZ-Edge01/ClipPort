using System.Globalization;

namespace EZDIT.Models;

public static class DisplayFormatting
{
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
}
