namespace ClipPort.Services;

/// <summary>
/// 可抢占的串行任务队列调度器：普通任务依次执行；优先任务到达时会请求
/// 当前普通任务在安全检查点停稳，再获得执行权。多个优先任务之间保持
/// 先来先执行，全部完成后恢复被暂挂的普通任务。
/// </summary>
public sealed class CopyJobScheduler
{
    private readonly object _sync = new();
    private readonly HashSet<Waiter> _waiters = [];
    private readonly LinkedList<Waiter> _ordinaryQueue = new();
    private readonly LinkedList<Waiter> _priorityQueue = new();
    private Waiter? _active;
    private Waiter? _preemptedOrdinary;

    /// <summary>
    /// 当前是否有任务正在执行（队列是否处于忙状态）。
    /// </summary>
    public bool HasActiveJob
    {
        get
        {
            lock (_sync)
            {
                return _active is not null || _preemptedOrdinary is not null;
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

    public CopyJobScheduleRegistration Register(bool isPriority)
    {
        var registration = new CopyJobScheduleRegistration(this, isPriority);
        lock (_sync)
        {
            _waiters.Add(registration.Waiter);
        }
        return registration;
    }

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
                return waiter.IsPaused
                    ? waiter.Gate.Task.WaitAsync(cancellationToken)
                    : Task.CompletedTask;
            }

            if (waiter.IsPreempted)
            {
                return waiter.Gate.Task.WaitAsync(cancellationToken);
            }

            if (!waiter.IsQueued)
            {
                waiter.IsQueued = true;
                (waiter.IsPriority ? _priorityQueue : _ordinaryQueue).AddLast(waiter);
                PromoteNextLocked();
            }

            return waiter.Gate.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 在实际 I/O 的安全检查点确认当前任务仍拥有执行权。优先任务到达时，
    /// 普通任务会先在这里确认停稳，随后调度器才会放行优先任务。
    /// </summary>
    public Task WaitForExecutionAsync(
        CopyJobScheduleRegistration registration,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            Waiter waiter = registration.Waiter;
            if (waiter.IsDone)
            {
                return Task.FromCanceled(new CancellationToken(canceled: true));
            }

            if (ReferenceEquals(_active, waiter))
            {
                // 当前安全区域持有租约时必须先把这一段 I/O 做完。租约释放
                // 后会负责完成真正的调度交接。
                if (waiter.ActiveExecutionLeaseCount > 0)
                {
                    return Task.CompletedTask;
                }

                LinkedListNode<Waiter>? priorityNode =
                    FindFirstRunnableLocked(_priorityQueue);
                if (!waiter.IsPriority &&
                    waiter.PreemptionRequested &&
                    priorityNode is not null)
                {
                    YieldOrdinaryToPriorityLocked(waiter);
                }
                else if (waiter.IsPriority && waiter.IsPaused)
                {
                    YieldPausedPriorityLocked(waiter);
                }
                else if (!waiter.IsPaused)
                {
                    return Task.CompletedTask;
                }
                else
                {
                    ResetGateLocked(waiter);
                }
            }

            return waiter.Gate.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 为一段不可拆分的实际 I/O 获取执行租约。抢占请求会阻止新租约，
    /// 并等待当前任务的全部租约释放后再启动优先任务。
    /// </summary>
    public async ValueTask<IDisposable> AcquireExecutionLeaseAsync(
        CopyJobScheduleRegistration registration,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task waitTask;
            lock (_sync)
            {
                Waiter waiter = registration.Waiter;
                if (waiter.IsDone)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (ReferenceEquals(_active, waiter) &&
                    !waiter.IsPaused &&
                    !waiter.PreemptionRequested)
                {
                    waiter.ActiveExecutionLeaseCount++;
                    return new ExecutionLease(this, registration);
                }

                waitTask = waiter.Gate.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 指示普通任务是否正被优先任务自动暂挂。手动暂停由调用方单独维护。
    /// </summary>
    public bool IsPreempted(CopyJobScheduleRegistration registration)
    {
        lock (_sync)
        {
            return registration.Waiter.IsPreempted;
        }
    }

    /// <summary>
    /// 指示任务当前是否拥有实际执行权。已开始的任务在暂停、恢复或被抢占后
    /// 会继续复用同一个注册，因此复制循环必须在安全检查点读取此状态。
    /// </summary>
    public bool CanExecute(CopyJobScheduleRegistration registration)
    {
        lock (_sync)
        {
            return ReferenceEquals(_active, registration.Waiter);
        }
    }

    public bool IsPaused(CopyJobScheduleRegistration registration)
    {
        lock (_sync)
        {
            return registration.Waiter.IsPaused;
        }
    }

    /// <summary>
    /// 同步任务的手动暂停状态。运行中的任务会在下一个安全检查点停稳，
    /// 调度器不会在仍可能存在 I/O 时提前移交执行权。
    /// </summary>
    public void SetPaused(CopyJobScheduleRegistration registration, bool isPaused)
    {
        lock (_sync)
        {
            Waiter waiter = registration.Waiter;
            if (waiter.IsDone || waiter.IsPaused == isPaused)
            {
                return;
            }

            waiter.IsPaused = isPaused;
            if (isPaused && ReferenceEquals(_active, waiter))
            {
                ResetGateLocked(waiter);
            }
            if (!isPaused)
            {
                if (ReferenceEquals(_active, waiter))
                {
                    waiter.Gate.TrySetResult();
                }
                PromoteNextLocked();
            }
        }
    }

    /// <summary>
    /// 强制恢复一个普通任务，并把所有尚未结束的优先任务转为手动暂停。
    /// </summary>
    public void ForceResumeOrdinary(CopyJobScheduleRegistration registration)
    {
        lock (_sync)
        {
            Waiter ordinary = registration.Waiter;
            if (ordinary.IsPriority || ordinary.IsDone)
            {
                return;
            }

            foreach (Waiter priority in _waiters.Where(candidate =>
                         candidate.IsPriority && !candidate.IsDone))
            {
                priority.IsPaused = true;
                if (ReferenceEquals(_active, priority))
                {
                    ResetGateLocked(priority);
                }
            }

            ordinary.IsPaused = false;
            if (_active is null && ReferenceEquals(_preemptedOrdinary, ordinary))
            {
                _preemptedOrdinary = null;
                ordinary.IsPreempted = false;
                ActivateLocked(ordinary);
            }
        }
    }

    private void PromoteNextLocked()
    {
        LinkedListNode<Waiter>? priorityNode = FindFirstRunnableLocked(_priorityQueue);
        if (_active is { IsPriority: false } && priorityNode is not null)
        {
            // 普通任务仍可能有尚未完成的 I/O。这里只记录抢占请求，等它在
            // WaitForExecutionAsync 确认到达安全检查点后再移交执行权。
            _active.PreemptionRequested = true;
            ResetGateLocked(_active);
            return;
        }

        if (_active is not null)
        {
            return;
        }

        Waiter? next = priorityNode?.Value;
        if (priorityNode is not null)
        {
            _priorityQueue.Remove(priorityNode);
        }
        else if (_preemptedOrdinary is not null)
        {
            next = _preemptedOrdinary;
            _preemptedOrdinary = null;
            next.IsPreempted = false;
        }
        else
        {
            LinkedListNode<Waiter>? ordinaryNode = FindFirstRunnableLocked(_ordinaryQueue);
            next = ordinaryNode?.Value;
            if (ordinaryNode is not null)
            {
                _ordinaryQueue.Remove(ordinaryNode);
            }
        }

        if (next is null)
        {
            return;
        }

        ActivateLocked(next);
    }

    private void YieldOrdinaryToPriorityLocked(Waiter ordinary)
    {
        ResetGateLocked(ordinary);
        ordinary.IsActive = false;
        ordinary.IsPreempted = true;
        ordinary.PreemptionRequested = false;
        _preemptedOrdinary = ordinary;
        _active = null;
        PromoteNextLocked();
    }

    private void YieldPausedPriorityLocked(Waiter priority)
    {
        ResetGateLocked(priority);
        _active = null;
        priority.IsActive = false;
        EnqueueAtFrontLocked(priority);

        // 用户暂停优先任务的意图是恢复此前被抢占的普通任务。
        if (!PromotePreemptedOrdinaryLocked())
        {
            PromoteNextLocked();
        }
    }

    private bool PromotePreemptedOrdinaryLocked()
    {
        if (_active is not null || _preemptedOrdinary is null)
        {
            return false;
        }

        Waiter ordinary = _preemptedOrdinary;
        _preemptedOrdinary = null;
        ordinary.IsPreempted = false;
        ActivateLocked(ordinary);
        return true;
    }

    private static LinkedListNode<Waiter>? FindFirstRunnableLocked(
        LinkedList<Waiter> queue)
    {
        LinkedListNode<Waiter>? node = queue.First;
        while (node is not null && node.Value.IsPaused)
        {
            node = node.Next;
        }
        return node;
    }

    private void EnqueueAtFrontLocked(Waiter waiter)
    {
        if (waiter.IsQueued)
        {
            return;
        }

        waiter.IsQueued = true;
        (waiter.IsPriority ? _priorityQueue : _ordinaryQueue).AddFirst(waiter);
    }

    private void ActivateLocked(Waiter waiter)
    {
        waiter.IsQueued = false;
        waiter.IsActive = true;
        waiter.IsPreempted = false;
        waiter.PreemptionRequested = false;
        _active = waiter;
        // RunContinuationsAsynchronously 保证等待方不会在锁内同步执行。
        if (!waiter.IsPaused)
        {
            waiter.Gate.TrySetResult();
        }
    }

    private static void ResetGateLocked(Waiter waiter)
    {
        if (waiter.Gate.Task.IsCompleted)
        {
            waiter.Gate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void ReleaseExecutionLease(CopyJobScheduleRegistration registration)
    {
        lock (_sync)
        {
            Waiter waiter = registration.Waiter;
            if (waiter.ActiveExecutionLeaseCount <= 0)
            {
                return;
            }

            waiter.ActiveExecutionLeaseCount--;
            if (waiter.ActiveExecutionLeaseCount > 0 ||
                !ReferenceEquals(_active, waiter))
            {
                return;
            }

            if (!waiter.IsPriority &&
                waiter.PreemptionRequested &&
                FindFirstRunnableLocked(_priorityQueue) is not null)
            {
                YieldOrdinaryToPriorityLocked(waiter);
            }
            else if (waiter.IsPriority && waiter.IsPaused)
            {
                YieldPausedPriorityLocked(waiter);
            }
        }
    }

    private void Complete(CopyJobScheduleRegistration registration)
    {
        lock (_sync)
        {
            Waiter waiter = registration.Waiter;
            _waiters.Remove(waiter);
            if (ReferenceEquals(_active, waiter))
            {
                // 正在执行的任务结束：立即把队首的下一个任务提升为活动任务。
                _active = null;
                waiter.IsActive = false;
                waiter.IsDone = true;
                PromoteNextLocked();
                return;
            }

            if (ReferenceEquals(_preemptedOrdinary, waiter))
            {
                // 被抢占的普通任务仍可能被用户取消，此时不能在优先任务结束后恢复它。
                _preemptedOrdinary = null;
                waiter.IsPreempted = false;
                waiter.IsDone = true;
                waiter.Gate.TrySetCanceled();
                PromoteNextLocked();
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

            if (_active is null)
            {
                PromoteNextLocked();
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

    private sealed class ExecutionLease : IDisposable
    {
        private CopyJobScheduler? _scheduler;
        private CopyJobScheduleRegistration? _registration;

        public ExecutionLease(
            CopyJobScheduler scheduler,
            CopyJobScheduleRegistration registration)
        {
            _scheduler = scheduler;
            _registration = registration;
        }

        public void Dispose()
        {
            CopyJobScheduler? scheduler = Interlocked.Exchange(ref _scheduler, null);
            CopyJobScheduleRegistration? registration =
                Interlocked.Exchange(ref _registration, null);
            if (scheduler is not null && registration is not null)
            {
                scheduler.ReleaseExecutionLease(registration);
            }
        }
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
        public bool IsPreempted { get; set; }
        public bool IsPaused { get; set; }
        public bool PreemptionRequested { get; set; }
        public int ActiveExecutionLeaseCount { get; set; }
        public bool IsDone { get; set; }
        public TaskCompletionSource Gate { get; set; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
