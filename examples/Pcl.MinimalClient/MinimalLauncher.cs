using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Pages;

namespace Pcl.MinimalClient;

/// <summary>
/// 可替换 UI 的最小接线：只传公开契约，不读取账户文件、receipt 或安装目录。
/// 命名空间中的 Core.Contracts 实际来自 Pcl.Contract.dll，并非 Core 依赖。
/// </summary>
public sealed class MinimalLauncher : IAsyncDisposable
{
    public HostClient Client { get; }
    public HostHello Hello { get; private set; } = null!;

    public MinimalLauncher(string executable, string hostAssembly, string root)
    {
        Client = new HostClient(executable, hostAssembly, root, reconnectable: true);
    }

    // 用 Host 声明的能力和目标接线；客户端不自行推测 Java、加载器或 natives。
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        Hello = await Client.HelloAsync(ct);
        foreach (var capability in new[] { "version.install.v3", "session.reconnect.v1", "event.subscribe.v1", "game.launch.v2" })
        {
            if (!Hello.Capabilities.Contains(capability))
            {
                throw new OperationRejectedException("REQUIRED_CAPABILITY_MISSING");
            }
        }
        if (Hello.InstallationTarget is null)
        {
            throw new OperationRejectedException("HOST_TARGET_MISSING");
        }
    }

    // key 必须由调用者保留。超时后仍用原 key 查询/重试，不创建第二个副作用。
    public Task<OperationAcceptance> InstallAsync(string version, string loader, string key, CancellationToken ct = default)
    {
        return Client.Installations.AcceptAsync(
            new InstallCommand(key, new VersionInstallRequest(version, loader, Hello.InstallationTarget!)), ct
        );
    }

    // 续做从原公开快照取请求，不能让客户端重建 receipt 或修改原请求的目标。
    // 是否具备有效 receipt 和能否续做仍完全由 Host 决定，拒绝后不得退化成全新安装。
    public async Task<OperationAcceptance> ResumeAsync(string operationId, string key, CancellationToken ct = default)
    {
        var original = await Client.Installations.GetAsync(operationId, ct)
            ?? throw new OperationRejectedException("OPERATION_NOT_FOUND");
        return await Client.Installations.AcceptAsync(new(key, original.Request, operationId), ct);
    }

    // 只断开管道再连接同一会话；既有订阅由 Client 恢复，不主动取消后台操作。
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        await Client.DisconnectAsync();
        await Client.ReconnectAsync(ct);
        await ConnectAsync(ct);
    }

    // 正常重启是显式的 Host 退出：当前任务可能被取消。崩溃恢复由独立验收模拟。
    public async Task RestartHostAsync(CancellationToken ct = default)
    {
        await Client.ShutdownAsync(ct);
        await Client.DisconnectAsync();
        await Client.ReconnectAsync(ct);
        await ConnectAsync(ct);
    }

    public IAsyncEnumerable<OperationSnapshot> WatchAsync(string id, CancellationToken ct = default)
    {
        return ((IVersionInstallEvents)Client.Installations).WatchAsync(id, ct);
    }

    // 示例仅演示无凭据的账户创建。微软账户继续由既有 Host 登录契约管理。
    public async Task<PageSnapshot> AddOfflineAsync(string name, string key, CancellationToken ct = default)
    {
        var pages = new HostPageOperations(Client);
        var accepted = await pages.AcceptAsync(new(key, "workspace.account.addOffline",
            PayloadJson: JsonSerializer.Serialize(new { name })), ct);
        return await pages.WaitAsync(accepted.OperationId, ct);
    }

    public Task<AccountSnapshot> AccountsAsync(CancellationToken ct = default)
    {
        return Client.Call<AccountSnapshot>("account.snapshot", new { }, ct);
    }

    public Task<LaunchGuidance> GuidanceAsync(string accountId, CancellationToken ct = default)
    {
        return Client.Call<LaunchGuidance>("game.guidance", new LaunchGuidanceRequest(accountId, "zh-CN"), ct);
    }

    // 展示和确认由调用界面负责；Host 再验证许可提示和登录资格，不接受 Java/令牌参数。
    public Task<OperationAcceptance> LaunchAsync(string instanceId, string accountId, string key,
        bool detached, LaunchGuidanceConsent consent, CancellationToken ct = default)
    {
        return Client.Launches.AcceptAsync(new(key, accountId, instanceId, detached, consent), ct);
    }

    // 释放客户端只收回传输，不隐式停止 Host 或游戏。正常退出必须显式调用 ShutdownAsync。
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
