using System.Diagnostics;
using System.Text.Json;
using Pcl.Client.Updates;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Updates;

namespace Pcl.Client;

// 更新执行器归客户端部署层；业务仍在 Host。调用方可锁版、仅下次启动或空闲自动应用。
public sealed partial class HostClient
{
    private HostUpdateStore? updates;
    private readonly SemaphoreSlim updateGate = new(1, 1);
    private int updating;
    private bool startedUpdatedHost;
    private DateTimeOffset nextUpdateCheck;
    public HostUpdateStatus UpdateStatus { get; private set; } = new("disabled", "NO_POLICY");
    public bool UpdatesEnabled => updates is not null;

    /// <summary>只应在第一次连接之前配置；存储必须独立于安装包和游戏数据，信任根必须随客户端提供。</summary>
    public void ConfigureUpdates(HostUpdatePolicy policy, string storageDirectory, HttpClient? transport = null)
    {
        if (!reconnectable || ready || host is not null || updates is not null)
        {
            throw new InvalidOperationException("UPDATE_CONFIGURE_BEFORE_CONNECT");
        }
        updates = new HostUpdateStore(storageDirectory, dataRoot, policy, transport);
        UpdateStatus = new("ready", "POLICY_CONFIGURED");
    }

    public async Task<HostUpdateStatus> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        await updateGate.WaitAsync(ct);
        try
        {
            if (updates is null)
            {
                return UpdateStatus;
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromMinutes(5));
            UpdateStatus = await updates.StageAsync(budget.Token);
            return UpdateStatus;
        }
        finally
        {
            updateGate.Release();
        }
    }

    // 不强杀旧 Host、不取消业务；Host 拒绝时 pending 留给下次空闲或下次启动。
    public async Task<HostUpdateStatus> ApplyUpdateWhenIdleAsync(CancellationToken ct = default)
    {
        await updateGate.WaitAsync(ct);
        try
        {
            if (updates?.Pending is null)
            {
                return UpdateStatus;
            }
            await EnsureConnectedAsync(ct);
            if (!Capabilities.Contains("host.update.prepare.v1"))
            {
                return UpdateStatus = new("staged", "NEXT_START_REQUIRED");
            }
            Interlocked.Exchange(ref updating, 1);
            try
            {
                // 仅观察现有 OS 进程句柄；超时不根据 PID 强制终止。
                using var observed = Process.GetProcessById(HostPid);
                var result = await SendCore<HostUpdateReadiness>("host.update.prepare", new { }, ct);
                if (!result.Ready)
                {
                    return UpdateStatus = new("staged", "HOST_BUSY");
                }
                // 受理退出后有界等待不依赖调用方取消；迟到取消不能让旧版本被自动重启。
                using var transition = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await observed.WaitForExitAsync(transition.Token);
                await DisconnectAsync();
                host?.Dispose();
                host = null;
                await EnsureConnectedCoreAsync(transition.Token);
                return UpdateStatus;
            }
            catch
            {
                // 若新 Host 在握手前失败，回滚只关闭本次创建的候选进程；不碰旧游戏。
                if (startedUpdatedHost && updates.InTrial)
                {
                    await RollbackHostAsync();
                    using var retry = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await EnsureConnectedCoreAsync(retry.Token);
                    return UpdateStatus = new("rolled-back", "CANDIDATE_START_FAILED");
                }
                throw;
            }
            finally
            {
                Volatile.Write(ref updating, 0);
            }
        }
        finally
        {
            updateGate.Release();
        }
    }

    // LauncherSession 持有该循环的任务及取消源；无无人观察的后台 Task.Run。
    internal async Task RunUpdatesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (updates is null)
                {
                    return;
                }
                if (DateTimeOffset.UtcNow >= nextUpdateCheck)
                {
                    nextUpdateCheck = DateTimeOffset.UtcNow.AddSeconds(updates.Policy.CheckIntervalSeconds);
                    await CheckForUpdatesAsync(ct);
                }
                if (updates.Policy.ApplyWhenIdle)
                {
                    await ApplyUpdateWhenIdleAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // 不把远端正文/URL/系统路径写入面向用户的状态；离线更新失败不影响原 Host。
                UpdateStatus = new("deferred", "UPDATE_CHECK_OR_APPLY_FAILED");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PrepareHostUpdateAsync(CancellationToken ct)
    {
        if (updates?.Pending is not null)
        {
            try
            {
                await updates.BeginAsync(assembly.Length > 0 ? executable : "dotnet", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                updates.RejectPending();
                UpdateStatus = new("rolled-back", "CANDIDATE_PROBE_FAILED");
            }
        }
    }
    private async Task RollbackHostAsync()
    {
        ready = false;
        input?.Dispose();
        await reader;
        if (host is not null)
        {
            if (!host.HasExited)
            {
                host.Kill();
                await host.WaitForExitAsync();
            }
            await Task.WhenAll(diagnostics, bootstrapOutput);
            host.Dispose();
            host = null;
        }
        updates!.Rollback();
        startedUpdatedHost = false;
        UpdateStatus = new("rolled-back", "CANDIDATE_START_FAILED");
    }
}
