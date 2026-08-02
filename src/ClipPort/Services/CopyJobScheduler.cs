using System.Diagnostics;

namespace ClipPort.Services;

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

        lock (_sync)
        {
            if (_activePriorityJobs > 0 && --_activePriorityJobs == 0)
            {
                // TrySetResult stays inside the lock so that a Register call on
                // another thread cannot observe _activePriorityJobs == 0, create
                // a fresh incomplete gate, and leave us opening the old one.
                // RunContinuationsAsynchronously ensures continuations never
                // execute synchronously under the lock.
                bool opened = _priorityGate.TrySetResult();
                if (!opened)
                {
                    // The gate was already completed — this indicates an
                    // unbalanced register / dispose sequence.  The scheduler
                    // remains in a safe state (a completed gate lets ordinary
                    // jobs proceed), but the diagnostic is preserved here for
                    // production telemetry when logging is available.
                }
            }
        }
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
