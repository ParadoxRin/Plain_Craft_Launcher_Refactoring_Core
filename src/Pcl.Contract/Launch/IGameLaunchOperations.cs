using Pcl.Core.Contracts.Operations;

namespace Pcl.Core.Contracts.Launch;
// 跨 Shell/Host 只传稳定账户和实例 ID；不允许客户端提交 Java 路径、完整参数或任何令牌。
public sealed record GameLaunchCommand(string IdempotencyKey, string AccountId, string InstanceId, bool Detached = false, LaunchGuidanceConsent? Guidance = null);
public sealed record StartedGame(string GameProcessId, int Pid, bool Detached);
public sealed record GameLaunchSnapshot(
    string OperationId,
    string State,
    string Stage,
    long Sequence,
    DateTimeOffset Updated,
    GameLaunchCommand Command,
    StartedGame? Result = null,
    string? ErrorCode = null
)
{
    public bool Terminal => State is "succeeded" or "failed" or "cancelled" or "interrupted";
}

public sealed record GameProcessSnapshot(string GameProcessId, string State, int? ExitCode, bool Detached);
public interface IGameLaunchOperations
{
    Task<OperationAcceptance> AcceptAsync(GameLaunchCommand command, CancellationToken wait = default);
    Task<GameLaunchSnapshot?> GetAsync(string operationId, CancellationToken wait = default);
    Task<GameLaunchSnapshot> WaitAsync(string operationId, CancellationToken wait = default);
    Task<GameLaunchSnapshot> CancelAsync(string operationId, CancellationToken wait = default);
    Task<GameProcessSnapshot> GetGameAsync(string gameProcessId, CancellationToken wait = default);
    Task StopGameAsync(string gameProcessId, CancellationToken wait = default);
}
