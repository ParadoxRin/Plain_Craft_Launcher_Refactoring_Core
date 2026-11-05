using System.Runtime.ExceptionServices;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Client;

// Protocol observation only. Host owns authentication, credentials and the authoritative terminal state.
internal sealed class LoginMonitor(
    Func<string, bool, CancellationToken, Task<LoginSnapshot>> begin,
    Func<string, CancellationToken, Task<LoginSnapshot>> get,
    Func<string, CancellationToken, Task> cancel,
    bool reconnectable,
    TimeSpan? pollInterval = null)
{
    private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromMilliseconds(250);
    private static bool Terminal(LoginSnapshot row) => row.State is "succeeded" or "failed" or "cancelled" or "interrupted";

    internal async Task<string> RunAsync(bool remember, Action<LoginPrompt> prompt,
        CancellationToken cancelIntent, CancellationToken abort)
    {
        cancelIntent.ThrowIfCancellationRequested();
        // Reconnect/retry must retain the key even if the admission response was lost.
        var key = Guid.NewGuid().ToString("N");
        LoginSnapshot? row = null;
        ExceptionDispatchInfo? promptFailure = null;
        bool shown = false, cancelSent = false;
        try
        {
            row = await RecoverAsync(ct => begin(key, remember, ct), abort);
            while (!Terminal(row))
            {
                if ((cancelIntent.IsCancellationRequested || promptFailure is not null) && !cancelSent)
                {
                    await RecoverAsync(async ct => { await cancel(row.OperationId, ct); return true; }, abort);
                    cancelSent = true;
                }
                if (!cancelSent && !shown && row.Prompt is { } challenge)
                {
                    shown = true;
                    try { prompt(challenge); }
                    catch (Exception ex)
                    {
                        promptFailure = ExceptionDispatchInfo.Capture(ex);
                        continue; // Explicitly cancel before polling or refreshing any page.
                    }
                }
                await Task.Delay(poll, abort);
                row = await RecoverAsync(ct => get(row.OperationId, ct), abort);
            }
            promptFailure?.Throw();
            if (row.State == "cancelled") throw new OperationCanceledException(cancelIntent);
            if (row.State != "succeeded") throw new OperationRejectedException(row.ErrorCode ?? "LOGIN_FAILED");
            return row.AccountId ?? throw new OperationRejectedException("LOGIN_FAILED");
        }
        catch
        {
            // A presentation failure or permanent transport failure must not leave a known
            // authorization polling invisibly. Cleanup is bounded; no fabricated Host success.
            if (row is not null && !Terminal(row) && !abort.IsCancellationRequested)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await cancel(row.OperationId, cleanup.Token);
                    while (!Terminal(await get(row.OperationId, cleanup.Token)))
                        await Task.Delay(poll, cleanup.Token);
                }
                catch { /* Preserve the original error; Host still enforces conflicting operations. */ }
            }
            throw;
        }
    }

    private async Task<T> RecoverAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken abort)
    {
        while (true)
        {
            abort.ThrowIfCancellationRequested();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(abort);
            request.CancelAfter(TimeSpan.FromSeconds(30));
            try { return await call(request.Token); }
            catch (Exception ex) when (reconnectable && !abort.IsCancellationRequested &&
                ex is IOException or OperationCanceledException or OperationRejectedException { Code: "HOST_DISCONNECTED" or "HOST_ATTACH_TIMEOUT" })
            {
                // HostClient reconnects on the next call. The operation/key remains unchanged.
                await Task.Delay(poll, abort);
            }
        }
    }
}
