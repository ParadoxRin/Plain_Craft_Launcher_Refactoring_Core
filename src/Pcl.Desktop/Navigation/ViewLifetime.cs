namespace Pcl.Desktop;
// 每个视图只拥有自己的异步工作与清理回调。关闭不销毁 Host；普通关页不会隐式取消已受理任务。
internal sealed class ViewLifetime
{
    private readonly object gate = new();
    private readonly Dictionary<Lease, Action?> active = new();
    private readonly List<Func<Task>> cleanup = new();
    private Task? stopping;
    internal bool IsStopping { get; private set; }

    internal bool IsIdle
    {
        get
        {
            lock (gate)
            {
                return active.Count == 0;
            }
        }
    }

    internal IDisposable Begin(Action? cancel = null)
    {
        lock (gate)
        {
            if (IsStopping)
            {
                throw new OperationCanceledException("视图正在关闭。");
            }

            var lease = new Lease(this);
            active.Add(lease, cancel);
            return lease;
        }
    }

    internal void OnStop(Func<Task> action)
    {
        lock (gate)
        {
            if (IsStopping)
            {
                throw new InvalidOperationException("视图已开始关闭。");
            }

            cleanup.Add(action);
        }
    }

    internal void PrepareClose(bool cancelWork)
    {
        lock (gate)
        {
            IsStopping = true;
            if (cancelWork)
            {
                foreach (var(lease, cancel)in active.ToArray())
                {
                    if (lease.CancelRequested)
                    {
                        continue;
                    }

                    lease.CancelRequested = true;
                    try
                    {
                        cancel?.Invoke();
                    }
                    catch (ObjectDisposedException)
                    {
                        // 操作已在 finally 中完成取消源释放，活动租约随后归还。
                    }
                }
            }
        }
    }

    internal Task StopAsync(bool cancelWork)
    {
        PrepareClose(cancelWork);
        lock (gate)
        {
            return stopping ??= DrainAsync(
                active.Keys.Select(
                    x => x.Completed.Task
                )
                    .ToArray()
            );
        }
    }

    private async Task DrainAsync(Task[] work)
    {
        await Task.WhenAll(work);
        // 所有清理都尝试一次，异常向关闭协调者传播；不让前一项失败漏掉后续释放。
        List<Exception> errors = [];
        foreach (var action in cleanup)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(errors);
        }
    }

    private sealed class Lease(ViewLifetime owner) : IDisposable
    {
        internal bool CancelRequested;
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()
        {
            lock (owner.gate)
            {
                if (owner.active.Remove(this))
                {
                    Completed.TrySetResult();
                }
            }
        }
    }
}
