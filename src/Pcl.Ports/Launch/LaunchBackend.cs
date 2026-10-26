using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Ports.Launch;
// 探测和读取属于适配器，购买/试玩规则由纯 Domain 依据这些显式输入计算。
public sealed record LaunchAccountState(bool Exists, bool Offline, bool VerifiedBefore);
public interface ILaunchGuidanceState
{
    LaunchAccountState Read(string accountId);
}

// 借用只读进程观察能力；应用入口不得访问 Process、钥匙串或含令牌的命令行。
public interface IGameObservation : IAsyncDisposable
{
    int Pid { get; }

    string LogFile { get; }

    bool HasExited { get; }

    Task<int> Completion { get; }

    event Action<string>? Output;
    Task StopAsync(CancellationToken ct = default);
    void Stop();
}

public sealed record GameProcessDetails(string Id, int Pid, bool Detached, string LogFile);
public interface IGameLaunchBackend : IDisposable
{
    bool HasRunning { get; }

    bool HasManagedRunning { get; }

    Task WaitForManagedGamesAsync();
    Task<StartedGameProcess> StartAsync(GameLaunchInput input, bool trial, Action beforeSpawn, CancellationToken ct);
    ManagedGameState Get(string id);
    GameProcessDetails Details(string id);
    IGameObservation? Borrow(string id);
    Task StopAsync(string id, CancellationToken ct);
}
