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
    /// Builds normalized coordinates for the retained display samples.
    /// Keeping this mapping outside the WinUI drawing code makes the live
    /// timeline behavior deterministic and directly testable.
    /// </summary>
    public static IReadOnlyList<WaveformCoordinate> CreateDisplayCoordinates(
        IReadOnlyList<double> samples,
        int maximumPointCount)
    {
        ArgumentNullException.ThrowIfNull(samples);

        IReadOnlyList<WaveformDisplaySample> displaySamples =
            CreateDisplaySamples(samples, maximumPointCount);
        return displaySamples
            .Select(sample => new WaveformCoordinate(
                GetNormalizedX(samples.Count, sample.SourceIndex),
                sample.Value))
            .ToArray();
    }

    public static double EaseOutCubic(double progress)
    {
        double normalized = double.IsFinite(progress)
            ? Math.Clamp(progress, 0, 1)
            : 0;
        return 1 - Math.Pow(1 - normalized, 3);
    }

    /// <summary>
    /// Preserves the currently rendered geometry as the first animation frame.
    /// Existing points keep their exact screen coordinates, while newly appended
    /// points begin at the previous tail. Both axes can then move continuously
    /// without a refresh-time coordinate jump.
    /// </summary>
    public static IReadOnlyList<WaveformCoordinate> AlignContinuousTransition(
        IReadOnlyList<WaveformCoordinate> currentPoints,
        IReadOnlyList<WaveformCoordinate> targetPoints)
    {
        ArgumentNullException.ThrowIfNull(currentPoints);
        ArgumentNullException.ThrowIfNull(targetPoints);
        if (targetPoints.Count == 0)
        {
            return [];
        }
        if (currentPoints.Count == 0)
        {
            return targetPoints.ToArray();
        }
        if (currentPoints.Count == targetPoints.Count)
        {
            return currentPoints.ToArray();
        }

        var aligned = new WaveformCoordinate[targetPoints.Count];
        if (targetPoints.Count > currentPoints.Count)
        {
            for (int index = 0; index < targetPoints.Count; index++)
            {
                aligned[index] = currentPoints[Math.Min(index, currentPoints.Count - 1)];
            }
            return aligned;
        }

        for (int index = 0; index < targetPoints.Count; index++)
        {
            int sourceIndex = targetPoints.Count == 1
                ? currentPoints.Count - 1
                : (int)Math.Round(
                    index * (currentPoints.Count - 1d) /
                    (targetPoints.Count - 1d));
            aligned[index] = currentPoints[sourceIndex];
        }
        return aligned;
    }

}
