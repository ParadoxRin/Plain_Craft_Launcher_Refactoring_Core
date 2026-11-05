namespace Pcl.Desktop;

// Thumbnail requests must leave Host page capacity available for navigation and business work.
// Admission happens here, before calling the API; each page generation owns its cancellation.
internal sealed class IconRequestQueue : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim slots = new(4, 4);
    private readonly HashSet<Task> pending = new();
    private CancellationTokenSource page = new();
    private bool stopped;
    private bool disposed;

    internal Task RunAsync(Func<CancellationToken, Task> action)
    {
        lock (sync)
        {
            if (stopped) return Task.CompletedTask;
            var task = ExecuteAsync(action, page.Token);
            pending.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (sync) pending.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        var entered = false;
        try
        {
            await slots.WaitAsync(ct);
            entered = true;
            ct.ThrowIfCancellationRequested();
            await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (entered) slots.Release();
        }
    }

    internal void CancelPage()
    {
        lock (sync)
        {
            if (stopped) return;
            page.Cancel();
            page.Dispose();
            page = new();
        }
    }

    internal Task StopAsync()
    {
        lock (sync)
        {
            stopped = true;
            if (!disposed) page.Cancel();
            return Task.WhenAll(pending.ToArray());
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            stopped = true;
            disposed = true;
            page.Cancel();
            page.Dispose();
            // Do not dispose the semaphore while a cancelled API call is still unwinding.
            // StopAsync is awaited by the window before releasing view resources.
        }
    }
}
