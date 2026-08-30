namespace ClipPort.Services;

/// <summary>
/// Records only ClipPort-owned partial files so a host can clean them after an
/// ungraceful process exit without scanning or deleting unrelated user data.
/// </summary>
public interface IPartialFileJournal
{
    void Track(string path);

    void Untrack(string path);
}

internal sealed class NullPartialFileJournal : IPartialFileJournal
{
    public static NullPartialFileJournal Instance { get; } = new();

    public void Track(string path)
    {
    }

    public void Untrack(string path)
    {
    }
}
