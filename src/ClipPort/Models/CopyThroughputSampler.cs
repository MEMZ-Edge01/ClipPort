namespace ClipPort.Models;

/// <summary>
/// Converts cumulative progress for one operation phase into bounded,
/// instantaneous throughput samples.
/// </summary>
public sealed class CopyThroughputSampler
{
    public const int DefaultCapacity = 90;

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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
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

        AppendBounded(byteRates, byteRate);
        AppendBounded(itemRates, itemRate);
        AppendBounded(progressPositions, progressPosition);
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

        AppendBounded(byteRates, 0);
        AppendBounded(itemRates, 0);
        AppendBounded(
            progressPositions,
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

    private void AppendBounded(IList<double> samples, double value)
    {
        while (samples.Count >= _capacity)
        {
            samples.RemoveAt(0);
        }
        samples.Add(value);
    }
}
