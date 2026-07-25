namespace EZDIT.Services;

public sealed class CopyJobScheduler
{
    private readonly object _sync = new();
    private int _activePriorityJobs;
    private TaskCompletionSource _priorityGate = CreateCompletedGate();

    public bool HasActivePriorityJobs
    {
        get
        {
            lock (_sync)
            {
                return _activePriorityJobs > 0;
            }
        }
    }

    public CopyJobScheduleRegistration Register(bool isPriority)
    {
        if (isPriority)
        {
            lock (_sync)
            {
                if (_activePriorityJobs++ == 0)
                {
                    _priorityGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        return new CopyJobScheduleRegistration(this, isPriority);
    }

    public Task WaitForTurnAsync(bool isPriority, CancellationToken cancellationToken = default)
    {
        if (isPriority)
        {
            return Task.CompletedTask;
        }

        Task gate;
        lock (_sync)
        {
            gate = _priorityGate.Task;
        }
        return gate.WaitAsync(cancellationToken);
    }

    private void Complete(bool isPriority)
    {
        if (!isPriority)
        {
            return;
        }

        TaskCompletionSource? gateToOpen = null;
        lock (_sync)
        {
            if (_activePriorityJobs > 0 && --_activePriorityJobs == 0)
            {
                gateToOpen = _priorityGate;
            }
        }
        gateToOpen?.TrySetResult();
    }

    private static TaskCompletionSource CreateCompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    public sealed class CopyJobScheduleRegistration : IDisposable
    {
        private CopyJobScheduler? _scheduler;
        private readonly bool _isPriority;

        internal CopyJobScheduleRegistration(CopyJobScheduler scheduler, bool isPriority)
        {
            _scheduler = scheduler;
            _isPriority = isPriority;
        }

        public void Dispose() => Interlocked.Exchange(ref _scheduler, null)?.Complete(_isPriority);
    }
}
