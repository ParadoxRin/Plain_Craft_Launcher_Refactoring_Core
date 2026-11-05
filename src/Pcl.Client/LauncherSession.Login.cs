using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Operations;
using System.Text.Json;

namespace Pcl.Client;
public sealed partial class LauncherSession
{
    private readonly object loginGate = new();
    private CancellationTokenSource? loginCancellation, loginAbort;
    private Task loginCompletion = Task.CompletedTask;

    // The caller owns presentation and its cancel intent, never a page-scoped Host lease.
    // Session disposal also observes this operation so a forgotten UI task cannot outlive it.
    public Task<string> LoginAsync(bool remember, Action<LoginPrompt> prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ct.ThrowIfCancellationRequested();
        lock (loginGate)
        {
            if (stopping) throw new OperationRejectedException("APPLICATION_STOPPING");
            if (!loginCompletion.IsCompleted) throw new OperationRejectedException("LOGIN_BUSY");
            var intent = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var abort = new CancellationTokenSource();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loginCancellation = intent;
            loginAbort = abort;
            loginCompletion = completion.Task;
            var task = RunLoginAsync(remember, prompt, intent, abort, completion);
            // A closed/removed view may stop awaiting its task; still observe its failure.
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<string> RunLoginAsync(bool remember, Action<LoginPrompt> prompt,
        CancellationTokenSource intent, CancellationTokenSource abort, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            var monitor = new LoginMonitor(
                (key, keep, token) => AdmitCommandAsync(() => Client.Call<LoginSnapshot>(
                    "account.login.begin", new { key, remember = keep }, token), token),
                (id, token) => Client.Call<LoginSnapshot>("account.login.get", new { operationId = id }, token),
                async (id, token) => { await Client.Call<JsonElement>("account.login.cancel", new { operationId = id }, token); },
                Client.CanReconnect);
            return await monitor.RunAsync(remember, prompt, intent.Token, abort.Token);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            try
            {
                // Serialize with background/reconnect reads; return only after authoritative refresh.
                if (!stopping)
                {
                    using var refresh = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                    refresh.CancelAfter(TimeSpan.FromSeconds(30));
                    await RefreshDesktopAsync(refresh.Token);
                    AccessChanged?.Invoke();
                }
            }
            catch when (failure is not null) { }
            finally
            {
                lock (loginGate)
                {
                    loginCancellation = loginAbort = null;
                    intent.Dispose();
                    abort.Dispose();
                    completion.TrySetResult();
                }
            }
        }
    }

    private async Task StopLoginAsync()
    {
        Task pending;
        lock (loginGate)
        {
            loginCancellation?.Cancel();
            pending = loginCompletion;
        }
        try { await pending.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException)
        {
            lock (loginGate) loginAbort?.Cancel();
            await pending;
        }
    }
}
