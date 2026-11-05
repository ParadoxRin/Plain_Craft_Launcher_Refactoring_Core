namespace Pcl.Desktop;

// Deterministic, network-free regression for the same queue used by both resource layouts.
internal static class IconRequestQueueChecks
{
    internal static async Task RunAsync()
    {
        using var queue = new IconRequestQueue();
        var admitted = 0;
        var active = 0;
        var peak = 0;
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Thumbnail(CancellationToken ct)
        {
            var count = Interlocked.Increment(ref active);
            Interlocked.Increment(ref admitted);
            peak = Math.Max(peak, count);
            if (count == 4) fourStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { Interlocked.Decrement(ref active); }
        }
        var burst = Enumerable.Range(0, 24).Select(_ => queue.RunAsync(Thumbnail)).ToArray();
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (admitted != 4 || peak != 4) throw new InvalidOperationException("Thumbnail admission exceeded four requests.");
        queue.CancelPage();
        await Task.WhenAll(burst).WaitAsync(TimeSpan.FromSeconds(5));
        if (admitted != 4 || active != 0) throw new InvalidOperationException("Cancelled queued thumbnails reached the API or remained active.");
        try
        {
            await queue.RunAsync(_ => throw new IOException("test failure"));
            throw new InvalidOperationException("Expected thumbnail failure.");
        }
        catch (IOException) { }
        fourStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = Enumerable.Range(0, 24).Select(_ => queue.RunAsync(Thumbnail)).ToArray();
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(next);
        if (admitted != 8 || active != 0) throw new InvalidOperationException("Shutdown failed to drain thumbnails.");
        await queue.RunAsync(_ => throw new InvalidOperationException("Stopped queue admitted work."));
    }
}
