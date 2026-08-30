namespace ClipPort.FnOS.Tasks;

public sealed class TaskManagerException(
    string code,
    string message,
    object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}
