namespace ClipPort.Models;

public readonly record struct WaveformDisplaySample(int SourceIndex, double Value);

public readonly record struct WaveformCoordinate(double X, double Y);

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

    /// <summary>
    /// Reduces drawing work without shortening the represented timeline. The
    /// first and newest samples are always retained, while power-of-two buckets
    /// preserve both extrema so old peaks do not vanish from long tasks.
    /// </summary>
    public static IReadOnlyList<WaveformDisplaySample> CreateDisplaySamples(
        IReadOnlyList<double> samples,
        int maximumPointCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (maximumPointCount < 4)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPointCount));
        }

        if (samples.Count <= maximumPointCount)
        {
            return samples
                .Select((value, index) => new WaveformDisplaySample(index, value))
                .ToArray();
        }

        int interiorCount = samples.Count - 2;
        int groupSize = 1;
        while ((interiorCount + groupSize - 1L) / groupSize * 2 > maximumPointCount - 2L)
        {
            groupSize = checked(groupSize * 2);
        }

        var result = new List<WaveformDisplaySample>(maximumPointCount)
        {
            new(0, samples[0])
        };
        for (int start = 1; start < samples.Count - 1; start += groupSize)
        {
            int end = Math.Min(samples.Count - 1, start + groupSize);
            int minimumIndex = start;
            int maximumIndex = start;
            for (int index = start + 1; index < end; index++)
            {
                if (samples[index] < samples[minimumIndex])
                {
                    minimumIndex = index;
                }
                if (samples[index] > samples[maximumIndex])
                {
                    maximumIndex = index;
                }
            }

            if (minimumIndex == maximumIndex)
            {
                result.Add(new WaveformDisplaySample(minimumIndex, samples[minimumIndex]));
            }
            else if (minimumIndex < maximumIndex)
            {
                result.Add(new WaveformDisplaySample(minimumIndex, samples[minimumIndex]));
                result.Add(new WaveformDisplaySample(maximumIndex, samples[maximumIndex]));
            }
            else
            {
                result.Add(new WaveformDisplaySample(maximumIndex, samples[maximumIndex]));
                result.Add(new WaveformDisplaySample(minimumIndex, samples[minimumIndex]));
            }
        }
        result.Add(new WaveformDisplaySample(samples.Count - 1, samples[^1]));
        return result;
    }

    /// <summary>
    /// Keeps vertical values fixed at their new targets while existing points
    /// slide left. This prevents a sampling update from animating the entire
    /// waveform up and down.
    /// </summary>
    public static IReadOnlyList<WaveformCoordinate> AlignHorizontalTransition(
        IReadOnlyList<WaveformCoordinate> currentPoints,
        IReadOnlyList<WaveformCoordinate> targetPoints)
    {
        ArgumentNullException.ThrowIfNull(currentPoints);
        ArgumentNullException.ThrowIfNull(targetPoints);
        if (targetPoints.Count == 0)
        {
            return [];
        }

        var aligned = new List<WaveformCoordinate>(targetPoints.Count);
        for (int index = 0; index < targetPoints.Count; index++)
        {
            double startX;
            if (currentPoints.Count == 0)
            {
                startX = 0;
            }
            else if (currentPoints.Count <= targetPoints.Count)
            {
                startX = currentPoints[Math.Min(index, currentPoints.Count - 1)].X;
            }
            else
            {
                int sourceIndex = targetPoints.Count == 1
                    ? currentPoints.Count - 1
                    : (int)Math.Round(
                        index * (currentPoints.Count - 1d) / (targetPoints.Count - 1d));
                startX = currentPoints[sourceIndex].X;
            }
            aligned.Add(new WaveformCoordinate(startX, targetPoints[index].Y));
        }
        return aligned;
    }
}
