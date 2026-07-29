using EZDIT.Models;

namespace EZDIT.Services;

internal static class HistoryRetentionPolicy
{
    public static int FindOldestRemovableIndex(
        IReadOnlyList<JobHistoryItem> history,
        Func<string, bool> isActive)
    {
        for (int index = history.Count - 1; index >= 0; index--)
        {
            if (!isActive(history[index].Id))
            {
                return index;
            }
        }

        return -1;
    }
}
