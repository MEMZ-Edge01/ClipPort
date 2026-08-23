namespace ClipPort.Models;

/// <summary>
/// Converts cumulative progress for one operation phase into bounded,
/// instantaneous throughput samples.
/// </summary>
public sealed class CopyThroughputSampler
{
    // Long-running jobs retain a bounded multi-resolution history. Compaction
    // keeps the first/newest readings and byte/item extrema instead of dropping
    // the beginning of the timeline.
    public const int DefaultCapacity = 4096;

    private readonly int _capacity;
    private readonly double _minimumIntervalSeconds;
    private readonly CopyPhase _sampledPhase;
    private double _lastElapsedSeconds;
    private double _lastTransferredBytes;
    private int _lastProcessedFiles;

    public CopyThroughputSampler(
        int capacity = DefaultCapacity,
        double minimumIntervalSeconds = 0.2,
        CopyPhase sampledPhase = CopyPhase.Copying)
    {
        if (capacity < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumIntervalSeconds);
        if (sampledPhase is not CopyPhase.Copying and not CopyPhase.Verifying)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampledPhase),
                "Throughput can only be sampled during copying or verification.");
        }
        _capacity = capacity;
        _minimumIntervalSeconds = minimumIntervalSeconds;
        _sampledPhase = sampledPhase;
    }

    /// <summary>
    /// Adds a sample when enough active phase time has elapsed, or when the phase completes.
    /// </summary>
    public bool TrySample(
        CopyProgressInfo progress,
        IList<double> byteRates,
        IList<double> itemRates,
        IList<double> progressPositions)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(byteRates);
        ArgumentNullException.ThrowIfNull(itemRates);
        ArgumentNullException.ThrowIfNull(progressPositions);

        if (progress.Phase != _sampledPhase)
        {
            return false;
        }

        double elapsedSeconds = Math.Max(0, progress.Elapsed.TotalSeconds);
        double transferredBytes = Math.Max(0, progress.BytesPerSecond * elapsedSeconds);

        // Retry progress uses a new stopwatch and smaller counters, so begin a
        // fresh interval without discarding the samples from the original pass.
        if (elapsedSeconds < _lastElapsedSeconds ||
            progress.ProcessedFiles < _lastProcessedFiles ||
            transferredBytes < _lastTransferredBytes)
        {
            _lastElapsedSeconds = 0;
            _lastTransferredBytes = 0;
            _lastProcessedFiles = 0;
        }

        double intervalSeconds = elapsedSeconds - _lastElapsedSeconds;
        bool phaseCompleted = progress.TotalFiles == progress.ProcessedFiles;
        if (intervalSeconds <= 0 ||
            intervalSeconds < _minimumIntervalSeconds && !phaseCompleted)
        {
            return false;
        }

        double byteRate = Math.Max(
            0,
            (transferredBytes - _lastTransferredBytes) / intervalSeconds);
        double itemRate = Math.Max(
            0,
            (progress.ProcessedFiles - _lastProcessedFiles) / intervalSeconds);
        if (!double.IsFinite(byteRate) || !double.IsFinite(itemRate))
        {
            return false;
        }

        double progressPosition = GetProgressPosition(progress);
        if (progressPositions.Count > 0)
        {
            // Retries can restart their counters; never let a later point move
            // behind an already rendered point on the phase timeline.
            progressPosition = Math.Max(progressPosition, progressPositions[^1]);
        }

        AppendSample(
            byteRates,
            itemRates,
            progressPositions,
            byteRate,
            itemRate,
            progressPosition);
        _lastElapsedSeconds = elapsedSeconds;
        _lastTransferredBytes = transferredBytes;
        _lastProcessedFiles = progress.ProcessedFiles;
        return true;
    }

    /// <summary>
    /// Ends a visible operation interval at zero without duplicating idle samples.
    /// </summary>
    public bool TryAppendIdleSample(
        IList<double> byteRates,
        IList<double> itemRates,
        IList<double> progressPositions)
    {
        ArgumentNullException.ThrowIfNull(byteRates);
        ArgumentNullException.ThrowIfNull(itemRates);
        ArgumentNullException.ThrowIfNull(progressPositions);
        if (byteRates.Count == 0 || itemRates.Count == 0 ||
            byteRates[^1] == 0 && itemRates[^1] == 0)
        {
            return false;
        }

        AppendSample(
            byteRates,
            itemRates,
            progressPositions,
            byteRate: 0,
            itemRate: 0,
            progressPositions.Count == 0 ? 0 : progressPositions[^1]);
        return true;
    }

    /// <summary>
    /// Maps elapsed time onto the current estimated total duration. Because
    /// elapsed / estimated duration equals completed work / total work, storing
    /// this ratio keeps every existing point at a stable horizontal position.
    /// </summary>
    private static double GetProgressPosition(CopyProgressInfo progress)
    {
        if (progress.TotalBytes > 0)
        {
            return Math.Clamp(progress.ProcessedBytes / (double)progress.TotalBytes, 0, 1);
        }

        if (progress.TotalFiles > 0)
        {
            return Math.Clamp(progress.ProcessedFiles / (double)progress.TotalFiles, 0, 1);
        }

        return progress.ProcessedFiles >= progress.TotalFiles ? 1 : 0;
    }

    private void AppendSample(
        IList<double> byteRates,
        IList<double> itemRates,
        IList<double> progressPositions,
        double byteRate,
        double itemRate,
        double progressPosition)
    {
        EnsureAligned(byteRates, itemRates, progressPositions);
        if (byteRates.Count >= _capacity)
        {
            CompactToCapacity(
                byteRates,
                itemRates,
                progressPositions,
                _capacity - 1);
        }

        byteRates.Add(byteRate);
        itemRates.Add(itemRate);
        progressPositions.Add(progressPosition);
    }

    internal static void CompactToCapacity(
        IList<double> byteRates,
        IList<double> itemRates,
        IList<double> progressPositions,
        int capacity = DefaultCapacity)
    {
        EnsureAligned(byteRates, itemRates, progressPositions);
        if (byteRates.Count <= capacity)
        {
            return;
        }

        if (capacity < 8)
        {
            while (byteRates.Count > capacity)
            {
                // Preserve the beginning and the newest reading for tiny test
                // capacities while discarding the least recent interior point.
                byteRates.RemoveAt(1);
                itemRates.RemoveAt(1);
                progressPositions.RemoveAt(1);
            }
            return;
        }

        int perSeriesPointLimit = Math.Max(4, capacity / 4);
        var retainedIndices = new SortedSet<int> { 0, byteRates.Count - 1 };
        foreach (WaveformDisplaySample sample in
                 WaveformTimeline.CreateDisplaySamples(
                     byteRates as IReadOnlyList<double> ?? byteRates.ToArray(),
                     perSeriesPointLimit))
        {
            retainedIndices.Add(sample.SourceIndex);
        }
        foreach (WaveformDisplaySample sample in
                 WaveformTimeline.CreateDisplaySamples(
                     itemRates as IReadOnlyList<double> ?? itemRates.ToArray(),
                     perSeriesPointLimit))
        {
            retainedIndices.Add(sample.SourceIndex);
        }

        int[] indices = retainedIndices.Take(capacity).ToArray();
        ReplaceWithSelected(byteRates, indices);
        ReplaceWithSelected(itemRates, indices);
        ReplaceWithSelected(progressPositions, indices);
    }

    private static void EnsureAligned(
        IList<double> byteRates,
        IList<double> itemRates,
        IList<double> progressPositions)
    {
        if (byteRates.Count != itemRates.Count ||
            byteRates.Count != progressPositions.Count)
        {
            throw new ArgumentException("Throughput sample collections must stay aligned.");
        }
    }

    private static void ReplaceWithSelected(IList<double> samples, int[] indices)
    {
        double[] retained = indices.Select(index => samples[index]).ToArray();
        samples.Clear();
        foreach (double sample in retained)
        {
            samples.Add(sample);
        }
    }
}
