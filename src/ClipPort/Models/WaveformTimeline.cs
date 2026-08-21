namespace ClipPort.Models;

/// <summary>
/// Maps retained waveform samples onto a rolling, full-width timeline.
/// </summary>
public static class WaveformTimeline
{
    public static double GetNormalizedX(int sampleCount, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= sampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return sampleCount == 1 ? 1 : index / (double)(sampleCount - 1);
    }
}
