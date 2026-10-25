namespace Pcl.Core.Ports.Operations;
public sealed record GameLaunchInput(string AccountId, string InstanceId, bool Detached, string Culture = "zh-CN", bool SponsorRead = false, bool PurchaseRead = false, bool Demo = false);
public sealed record StartedGameProcess(string Id, int Pid, bool Detached);
public sealed record ManagedGameState(string Id, bool Running, int? ExitCode, bool Detached);
public interface IGameLaunchExecutor
{
    Task<StartedGameProcess> StartAsync(GameLaunchInput input, Action beforeSpawn, CancellationToken ct);
    ManagedGameState Get(string gameId);
    Task StopAsync(string gameId, CancellationToken ct);
}
