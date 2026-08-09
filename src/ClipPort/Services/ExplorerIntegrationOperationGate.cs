namespace ClipPort.Services;

public sealed class ExplorerIntegrationOperationGate
{
    private long _nextOperationId;
    private long _activeOperationId;

    public bool IsBusy => Volatile.Read(ref _activeOperationId) != 0;

    public bool TryBegin(out long operationId)
    {
        long candidateId = Interlocked.Increment(ref _nextOperationId);
        if (Interlocked.CompareExchange(
                ref _activeOperationId,
                candidateId,
                comparand: 0) == 0)
        {
            operationId = candidateId;
            return true;
        }

        operationId = 0;
        return false;
    }

    public bool Complete(long operationId) =>
        operationId > 0 &&
        Interlocked.CompareExchange(
            ref _activeOperationId,
            value: 0,
            comparand: operationId) == operationId;
}
