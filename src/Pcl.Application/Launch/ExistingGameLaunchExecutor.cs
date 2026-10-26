using Pcl.Core.Ports.Launch;
using Pcl.Core.Ports.Operations;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;

namespace Pcl.Core.Application.Launch;
// 同一用例编排服务供 Host 和 CLI 使用；不组装 HTTP、文件、账户或进程实现。
public sealed class ExistingGameLaunchExecutor(IGameLaunchBackend backend, LaunchGuidanceService guidance) : IGameLaunchExecutor, IDisposable
{
    public bool HasRunning => backend.HasRunning;
    public bool HasManagedRunning => backend.HasManagedRunning;

    public Task WaitForManagedGamesAsync() => backend.WaitForManagedGamesAsync();
    public LaunchGuidance GetGuidance(string accountId, string culture) => guidance.Get(accountId, culture);
    public GameDetails Details(string id)
    {
        var value = backend.Details(id);
        return new(value.Id, value.Pid, value.Detached, value.LogFile);
    }

    public IGameObservation? BorrowLocalSession(string id) => backend.Borrow(id);
    public Task<StartedGameProcess> StartAsync(GameLaunchInput input, Action beforeSpawn, CancellationToken ct)
    {
        if (backend.HasRunning)
        {
            throw new OperationInstallException("GAME_ALREADY_RUNNING");
        }

        var trial = guidance.Require(input.AccountId, input.Culture, input.SponsorRead, input.PurchaseRead, input.Demo);
        return backend.StartAsync(input, trial, beforeSpawn, ct);
    }

    public ManagedGameState Get(string id) => backend.Get(id);
    public Task StopAsync(string id, CancellationToken ct) => backend.StopAsync(id, ct);
    // 组合根将 backend 的所有权交给此入口，关闭可重入而不能重复释放。
    private int disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            backend.Dispose();
        }
    }
}
