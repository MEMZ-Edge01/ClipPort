namespace ClipPort.Services;

/// <summary>
/// 串行任务队列调度器：同一时刻只允许一个任务执行，其余任务进入等待队列；
/// 优先任务总是排到普通任务前面，同优先级任务保持先来先执行。
/// 多个任务并行执行会带来极大的磁盘/内存开销并可能导致进程崩溃，
/// 因此任务必须串行化；优先级只决定排队顺序，不抢占正在执行的任务。
/// </summary>
public sealed class CopyJobScheduler
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _ordinaryQueue = new();
    private readonly LinkedList<Waiter> _priorityQueue = new();
    private Waiter? _active;

    /// <summary>
    /// 当前是否有任务正在执行（队列是否处于忙状态）。
    /// </summary>
    public bool HasActiveJob
    {
        get
        {
            lock (_sync)
            {
                return _active is not null;
            }
        }
    }

    /// <summary>
    /// 当前排队等待执行的任务数量。
    /// </summary>
    public int WaitingCount
    {
        get
        {
            lock (_sync)
            {
                return _priorityQueue.Count + _ordinaryQueue.Count;
            }
        }
    }

    public CopyJobScheduleRegistration Register(bool isPriority) =>
        new(this, isPriority);

    /// <summary>
    /// 等待获得执行权。返回的任务在轮到该任务时完成；
    /// 如果任务在排队期间被释放（取消），则以取消状态结束。
    /// </summary>
    public Task WaitForTurnAsync(
        CopyJobScheduleRegistration registration,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            Waiter waiter = registration.Waiter;
            if (waiter.IsActive)
            {
                return Task.CompletedTask;
            }

            if (!waiter.IsQueued)
            {
                waiter.IsQueued = true;
                (waiter.IsPriority ? _priorityQueue : _ordinaryQueue).AddLast(waiter);
                PromoteIfIdleLocked();
            }

            return waiter.Gate.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 返回该任务前面还有多少个任务在排队；不在队列中时返回 -1。
    /// 普通任务会看到它前面的全部优先任务和普通任务。
    /// </summary>
    public int GetWaitingPosition(CopyJobScheduleRegistration registration)
    {
        lock (_sync)
        {
            Waiter waiter = registration.Waiter;
            if (!waiter.IsQueued)
            {
                return -1;
            }

            int position = 0;
            foreach (Waiter other in _priorityQueue)
            {
                if (ReferenceEquals(other, waiter))
                {
                    return position;
                }
                position++;
            }

            foreach (Waiter other in _ordinaryQueue)
            {
                if (ReferenceEquals(other, waiter))
                {
                    return position;
                }
                position++;
            }

            return -1;
        }
    }

    private void PromoteIfIdleLocked()
    {
        if (_active is not null)
        {
            return;
        }

        Waiter? next = _priorityQueue.First?.Value ?? _ordinaryQueue.First?.Value;
        if (next is null)
        {
            return;
        }

        (next.IsPriority ? _priorityQueue : _ordinaryQueue).RemoveFirst();
        next.IsQueued = false;
        next.IsActive = true;
        _active = next;
        // RunContinuationsAsynchronously 保证等待方不会在锁内同步执行。
        next.Gate.TrySetResult();
    }

    private void Complete(CopyJobScheduleRegistration registration)
    {
        lock (_sync)
        {
            Waiter waiter = registration.Waiter;
            if (ReferenceEquals(_active, waiter))
            {
                // 正在执行的任务结束：立即把队首的下一个任务提升为活动任务。
                _active = null;
                waiter.IsActive = false;
                PromoteIfIdleLocked();
                return;
            }

            if (waiter.IsQueued)
            {
                // 排队中的任务被释放（例如取消）：从队列移除，避免占用队列位置。
                (waiter.IsPriority ? _priorityQueue : _ordinaryQueue).Remove(waiter);
                waiter.IsQueued = false;
            }

            if (!waiter.IsDone)
            {
                waiter.IsDone = true;
                waiter.Gate.TrySetCanceled();
            }
        }
    }

    public sealed class CopyJobScheduleRegistration : IDisposable
    {
        private CopyJobScheduler? _scheduler;
        private readonly Waiter _waiter;

        internal CopyJobScheduleRegistration(CopyJobScheduler scheduler, bool isPriority)
        {
            _scheduler = scheduler;
            _waiter = new Waiter(isPriority);
        }

        internal Waiter Waiter => _waiter;

        public void Dispose() =>
            Interlocked.Exchange(ref _scheduler, null)?.Complete(this);
    }

    // 该类型经 internal 属性（CopyJobScheduleRegistration.Waiter）对外暴露，
    // 访问级别必须与属性一致，否则 Release 构建会报 CS0053。
    internal sealed class Waiter
    {
        public Waiter(bool isPriority)
        {
            IsPriority = isPriority;
        }

        public bool IsPriority { get; }
        public bool IsQueued { get; set; }
        public bool IsActive { get; set; }
        public bool IsDone { get; set; }
        public TaskCompletionSource Gate { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
