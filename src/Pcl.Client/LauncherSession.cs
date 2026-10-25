using System.Collections.Concurrent;
using System.Text.Json;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Sessions;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Client;
/// <summary>桌面连接的唯一所有者。Host 持有根锁及凭据；界面只缓存公开契约快照。</summary>
public sealed partial class LauncherSession : IAsyncDisposable, Pcl.Core.Contracts.Accounts.IAccountLogin
{
    public HostClient Client { get; }

    private readonly SemaphoreSlim gate = new(1, 1);
    public Pcl.Core.Contracts.Workspaces.DesktopSnapshot DesktopState { get; private set; } = null!;

    // Cache contains only public DTOs. Every completed command obtains authoritative Host state.
    public async Task RefreshDesktopAsync(CancellationToken ct = default)
    {
        var state = await Client.Call<Pcl.Core.Contracts.Workspaces.DesktopSnapshot>(
            "desktop.snapshot",
            new { },
            ct
        );
        DesktopState = state;
        credentials = state.Accounts.Accounts.Where(
            x => x.HasCredential
        )
            .Select(
                x => x.Id
            )
            .ToHashSet();
    }

    private volatile HashSet<string> credentials = [];
    private readonly ConcurrentDictionary<string, RemoteGameSession> games = new();
    private readonly object gameGate = new();
    private RemoteGameSession GetOrCreateGame(GameDetails details)
    {
        lock (gameGate)
        {
            if (games.TryGetValue(details.GameProcessId, out var existing))
            {
                return existing;
            }

            var session = new RemoteGameSession(this, details);
            games[details.GameProcessId] = session;
            return session;
        }
    }

    private readonly ConcurrentDictionary<string, byte> activeInstalls = new();
    private readonly ConcurrentDictionary<string, byte> activeLaunches = new();
    private volatile bool stopping;
    private int refreshAfterReconnect;
    private readonly CancellationTokenSource connectionStop = new();
    private Task connectionMonitor = Task.CompletedTask;
    public string? ConnectionError { get; private set; }
    public bool IsAvailable => !stopping && Client.IsConnected;
    public IReadOnlyList<ActiveGame> RecoveredGames { get; private set; } = Array.Empty<ActiveGame>();

    public event Action? AccessChanged;
    public InstallationTarget Target { get; private set; } = null!;
    public IVersionInstallService Installation { get; }
    public IGameLaunchOperations Launches { get; }

    public LauncherSession(HostClient client)
    {
        Client = client;
        Client.ConnectionLost += () => AccessChanged?.Invoke();
        Client.ConnectionRestored += _ => RestoreConnection();
        Installation = new RemoteInstallation(this);
        Launches = new RemoteLaunch(this);
    }

    public async Task InitializeAsync()
    {
        Target = (await Client.HelloAsync()).InstallationTarget ?? throw new InvalidDataException("HOST_TARGET_MISSING");
        if (!Client.Capabilities.Contains("desktop.contract-only.v1"))
            throw new OperationRejectedException("HOST_INCOMPATIBLE");
        RestoreConnection();
        await RefreshStateAfterOperationAsync();
        if (Client.CanReconnect)
        {
            connectionMonitor = MonitorConnectionAsync();
        }
    }

    // 空闲页面同样能自动重连；重试任务归会话持有并在释放前取消、等待，不泄漏后台异常。
    private async Task MonitorConnectionAsync()
    {
        try
        {
            while (!connectionStop.IsCancellationRequested && !stopping)
            {
                await Task.Delay(250, connectionStop.Token);
                if (stopping)
                {
                    return;
                }

                if (Client.IsConnected && Volatile.Read(ref refreshAfterReconnect) == 0)
                {
                    continue;
                }

                try
                {
                    if (!Client.IsConnected)
                        await Client.ReconnectAsync(connectionStop.Token);
                    await RefreshDesktopAsync(connectionStop.Token);
                    Interlocked.Exchange(ref refreshAfterReconnect, 0);
                    AccessChanged?.Invoke();
                    ConnectionError = null;
                }
                catch (OperationRejectedException ex)
                {
                    ConnectionError = ex.Code;
                    await Task.Delay(1000, connectionStop.Token);
                }
                catch (OperationCanceledException)when (!connectionStop.IsCancellationRequested)
                {
                    ConnectionError = "HOST_ATTACH_TIMEOUT";
                    await Task.Delay(1000, connectionStop.Token);
                }
                catch (IOException)
                {
                    ConnectionError = "HOST_DISCONNECTED";
                    await Task.Delay(1000, connectionStop.Token);
                }
            }
        }
        catch (OperationCanceledException)when (connectionStop.IsCancellationRequested)
        {
        }
    }

    // 重连恢复 Host 托管的逻辑游戏身份；有所有者的监控任务随后更新公开快照。
    private void RestoreConnection()
    {
        if (Client.Attachment is not { } attachment)
        {
            return;
        }

        Interlocked.Exchange(ref refreshAfterReconnect, 1);
        RecoveredGames = attachment.Games;
        foreach (var item in attachment.Games)
        {
            GetOrCreateGame(item.Details);
        }

        AccessChanged?.Invoke();
    }

    public bool HasMicrosoftCredential(string id) => credentials.Contains(id);
    // Host 是唯一写入者；显式取消仍需由持有 operationId 的应用协议提交。
    // The desktop is a pure client: it never obtains a legacy writer lease.
    private Task EnterHostAsync()
    {
        if (stopping)
            throw new OperationRejectedException("APPLICATION_STOPPING");
        return Task.CompletedTask;
    }

    // UI 背景查询与安装/启动/登录共用受理门，避免尚未结束的页面读操作触发 HOST_WRITER_ACTIVE。
    // 取消只中断等待进入；请求发出后仍完整等待受理结果，不把未知提交当成未执行。
    private async Task<T> AdmitCommandAsync<T>(Func<Task<T>> submit, CancellationToken wait = default)
    {
        await pageGate.WaitAsync(wait);
        try
        {
            await EnterHostAsync();
            return await submit();
        }
        finally
        {
            pageGate.Release();
        }
    }

    private async Task RefreshStateAfterOperationAsync()
    {
        if (stopping)
            return;
        await RefreshDesktopAsync();
        AccessChanged?.Invoke();
    }

    public IGameSession? BorrowGame(string id) => games.GetValueOrDefault(id);
    // 登录任务属于 Host，客户端只展示短时 user_code；取消后等待终态，才重新启用旧页面。
    public async Task<string> LoginAsync(bool remember, Action<LoginPrompt> prompt, CancellationToken ct)
    {
        await EnterHostAsync();
        LoginSnapshot? row = null;
        bool shown = false, cancelled = false;
        try
        {
            row = await AdmitCommandAsync(
                () => Client.Call<LoginSnapshot>(
                    "account.login.begin",
                    new
                    {
                        key = Guid.NewGuid()
                            .ToString("N"),
                        remember
                    }
                ),
                ct
            );
            while (row.State is not ("succeeded" or "failed" or "cancelled"))
            {
                if (ct.IsCancellationRequested && !cancelled)
                {
                    await Client.Call<JsonElement>(
                        "account.login.cancel",
                        new
                        {
                            operationId = row.OperationId
                        }
                    );
                    cancelled = true;
                }

                if (!shown && row.Prompt is { } challenge)
                {
                    prompt(challenge);
                    shown = true;
                }

                await Task.Delay(250);
                row = await Client.Call<LoginSnapshot>(
                    "account.login.get",
                    new
                    {
                        operationId = row.OperationId
                    }
                );
            }

            if (row.State == "cancelled")
            {
                throw new OperationCanceledException(ct);
            }

            return row.AccountId ?? throw new OperationRejectedException(row.ErrorCode ?? "LOGIN_FAILED");
        }
        finally
        {
            await RefreshStateAfterOperationAsync();
        }
    }

    public async Task LogoutAsync(string id, bool remove)
    {
        await EnterHostAsync();
        try
        {
            await AdmitCommandAsync(
                () => Client.Call<JsonElement>(
                    remove
                        ? "account.remove"
                        : "account.logout",
                    new
                    {
                        accountId = id
                    }
                ),
                CancellationToken.None
            );
        }
        finally
        {
            await RefreshStateAfterOperationAsync();
        }
    }

    public async Task<AccountAppearance?> AppearanceAsync(
        string id,
        string action,
        string? value = null,
        byte[]? png = null,
        bool slim = false,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        await EnterHostAsync();
        try
        {
            var row = await AdmitCommandAsync(
                () => Client.Call<AppearanceSnapshot>(
                    "account.appearance.begin",
                    new
                    {
                        accountId = id,
                        action,
                        value,
                        png,
                        slim
                    }
                ),
                ct
            );
            bool cancelled = false;
            while (row.State is not ("succeeded" or "failed" or "cancelled"))
            {
                if (ct.IsCancellationRequested && !cancelled)
                {
                    await Client.Call<JsonElement>(
                        "account.appearance.cancel",
                        new
                        {
                            operationId = row.OperationId
                        }
                    );
                    cancelled = true;
                }

                await Task.Delay(150);
                row = await Client.Call<AppearanceSnapshot>(
                    "account.appearance.get",
                    new
                    {
                        operationId = row.OperationId
                    }
                );
            }

            if (row.State == "cancelled")
            {
                throw new OperationCanceledException(ct);
            }

            if (row.State != "succeeded")
            {
                throw new OperationRejectedException(row.ErrorCode ?? "ACCOUNT_APPEARANCE_FAILED");
            }

            if (row.Profile is not { } profile)
            {
                return null;
            }

            return profile;
        }
        finally
        {
            await RefreshStateAfterOperationAsync();
        }
    }

    // 主窗口正常退出先显式取消已受理任务；断连只关闭管道，Host 保留尚未收尾任务和游戏的所有权。
    public async Task ShutdownAsync()
    {
        stopping = true;
        // 先收回重连任务所有权，防止正常关闭后由旧监视器重启一个新的 Host。
        // 取消仅作用于客户端重连；游戏的托管/独立运行仍由 Host 原关闭策略处理。
        await connectionStop.CancelAsync();
        await connectionMonitor;
        foreach (var id in activePages.Keys.ToArray())
        {
            await new HostPageOperations(Client).CancelAsync(id);
        }

        foreach (var id in activeInstalls.Keys.ToArray())
        {
            await Client.Installations.CancelAsync(id);
        }

        foreach (var id in activeLaunches.Keys.ToArray())
        {
            await Client.Launches.CancelAsync(id);
        }

        await Client.ShutdownAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await connectionStop.CancelAsync();
        await connectionMonitor;
        await Client.DisposeAsync();
        connectionStop.Dispose();
    }

    private sealed class RemoteInstallation(LauncherSession owner) : IVersionInstallService, IVersionInstallOperations, IVersionInstallEvents
    {
        public IVersionInstallOperations Operations => this;

        public IAsyncEnumerable<OperationSnapshot> WatchAsync(string id, CancellationToken ct = default) => ((IVersionInstallEvents)owner.Client.Installations).WatchAsync(id, ct);
        public async Task<VersionInstallResult> InstallAsync(
            VersionInstallRequest request,
            IProgress<VersionInstallProgress>? progress = null,
            CancellationToken cancellationToken = default
        ) => await OperationInstallAdapter.InstallAsync(this, request, progress, cancellationToken);
        public async Task<OperationAcceptance> AcceptAsync(InstallCommand command, CancellationToken wait = default)
        {
            wait.ThrowIfCancellationRequested();
            await owner.EnterHostAsync();
            try
            {
                var row = await owner.AdmitCommandAsync(
                    () => owner.Client.Installations.AcceptAsync(command),
                    wait
                );
                owner.activeInstalls.TryAdd(row.OperationId, 0);
                return row;
            }
            catch
            {
                await owner.RefreshStateAfterOperationAsync();
                throw;
            }
        }

        public Task<OperationSnapshot?> GetAsync(string id, CancellationToken wait = default) => owner.Client.Installations.GetAsync(id, wait);
        public Task<IReadOnlyList<OperationSnapshot>> ListAsync(CancellationToken wait = default) => owner.Client.Installations.ListAsync(wait);
        public Task<OperationSnapshot> CancelAsync(string id, CancellationToken wait = default) => owner.Client.Installations.CancelAsync(id, wait);
        public async Task<OperationSnapshot> WaitAsync(string id, CancellationToken wait = default)
        {
            var row = await owner.Client.Installations.WaitAsync(id, wait);
            owner.activeInstalls.TryRemove(id, out _);
            await owner.RefreshStateAfterOperationAsync();
            return row;
        }
    }

    private sealed class RemoteLaunch(LauncherSession owner) : IGameLaunchOperations
    {
        public async Task<OperationAcceptance> AcceptAsync(GameLaunchCommand command, CancellationToken wait = default)
        {
            wait.ThrowIfCancellationRequested();
            await owner.EnterHostAsync();
            try
            {
                var row = await owner.AdmitCommandAsync(
                    () => owner.Client.Launches.AcceptAsync(command),
                    wait
                );
                owner.activeLaunches.TryAdd(row.OperationId, 0);
                return row;
            }
            catch
            {
                await owner.RefreshStateAfterOperationAsync();
                throw;
            }
        }

        public Task<GameLaunchSnapshot?> GetAsync(string id, CancellationToken wait = default) => owner.Client.Launches.GetAsync(id, wait);
        public Task<GameLaunchSnapshot> CancelAsync(string id, CancellationToken wait = default) => owner.Client.Launches.CancelAsync(id, wait);
        public async Task<GameLaunchSnapshot> WaitAsync(string id, CancellationToken wait = default)
        {
            var row = await owner.Client.Launches.WaitAsync(id, wait);
            owner.activeLaunches.TryRemove(id, out _);
            if (row.Result is { Detached: false } result)
            {
                var details = await owner.Client.Call<GameDetails>(
                    "game.details",
                    new
                    {
                        gameProcessId = result.GameProcessId
                    }
                );
                owner.GetOrCreateGame(details);
            }
            else if (row.Result is null)
            {
                await owner.RefreshStateAfterOperationAsync();
            }

            return row;
        }

        public Task<GameProcessSnapshot> GetGameAsync(string id, CancellationToken wait = default) => owner.Client.Launches.GetGameAsync(id, wait);
        public Task StopGameAsync(string id, CancellationToken wait = default) => owner.Client.Launches.StopGameAsync(id, wait);
    }

    // 会话观察只通过 Host 逻辑身份操作，Client 不引用 Core 的进程 Port 或平台对象。
    private sealed class RemoteGameSession : IGameSession
    {
        private readonly LauncherSession owner;
        public string Id { get; }
        public int Pid { get; }
        public string LogFile { get; }
        public bool HasExited => Completion.IsCompleted;

        public event Action<string>? Output;
        public Task<int> Completion { get; }

        public RemoteGameSession(LauncherSession owner, GameDetails details)
        {
            this.owner = owner;
            Id = details.GameProcessId;
            Pid = details.Pid;
            LogFile = details.LogFile;
            Completion = ObserveAsync();
        }

        // 退出查询与日志泵都归 Completion 持有；日志不可读也不能丢掉真实游戏退出的观察。
        private async Task<int> ObserveAsync()
        {
            var exit = WaitForExitAsync();
            try
            {
                while (!File.Exists(LogFile) && !exit.IsCompleted)
                {
                    await Task.Delay(150);
                }

                if (File.Exists(LogFile))
                {
                    using var file = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(file);
                    while (!exit.IsCompleted)
                    {
                        for (int count = 0; count < 256 && await reader.ReadLineAsync()is { } line; count++)
                        {
                            try
                            {
                                Output?.Invoke(line);
                            }
                            catch (Exception)
                            {
                            }
                        }

                        await Task.Delay(150);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return await exit;
        }

        private async Task<int> WaitForExitAsync()
        {
            if (owner.Client.Capabilities.Contains("event.subscribe.v1"))
            {
                await using var subscription = await owner.Client.SubscribeAsync("game", Id);
                await foreach (var item in subscription.ReadAsync())
                {
                    var snapshot = item.Snapshot.Deserialize<GameProcessSnapshot>(
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        }
                    )!;
                    if (snapshot.State == "exited")
                    {
                        await owner.RefreshStateAfterOperationAsync();
                        return snapshot.ExitCode ?? -1;
                    }
                }

                throw new OperationRejectedException("SUBSCRIPTION_CLOSED");
            }

            while (true)
            {
                var row = await owner.Client.Launches.GetGameAsync(Id);
                if (row.State == "exited")
                {
                    await owner.RefreshStateAfterOperationAsync();
                    return row.ExitCode ?? -1;
                }

                await Task.Delay(250);
            }
        }

        public Task StopAsync(CancellationToken ct = default) => owner.Client.Launches.StopGameAsync(Id, ct);
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Completion;
            }
            finally
            {
                owner.games.TryRemove(Id, out _);
            }
        }
    }
}
