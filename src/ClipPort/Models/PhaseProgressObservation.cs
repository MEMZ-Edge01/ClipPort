using System.Diagnostics;

namespace ClipPort.Models;

/// <summary>
/// Keeps a phase progress event and the monotonic time at which the UI observed
/// it together, preventing progress and timestamp state from drifting apart.
/// </summary>
public sealed record PhaseProgressObservation(
    CopyProgressInfo Progress,
    long ObservedTimestamp)
{
    public static PhaseProgressObservation Capture(CopyProgressInfo progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return new PhaseProgressObservation(progress, Stopwatch.GetTimestamp());
    }

    public TimeSpan ProjectElapsed(long currentTimestamp, bool isActive) =>
        DisplayFormatting.ProjectLiveElapsed(
            Progress.Elapsed,
            ObservedTimestamp,
            currentTimestamp,
            isActive);
}
