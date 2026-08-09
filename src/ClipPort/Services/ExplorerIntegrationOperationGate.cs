namespace ClipPort.Services;

public sealed class ExplorerIntegrationOperationGate
{
    private int _operationInProgress;

    public bool TryBegin() =>
        Interlocked.CompareExchange(ref _operationInProgress, 1, 0) == 0;

    public void Complete() =>
        Interlocked.Exchange(ref _operationInProgress, 0);
}
