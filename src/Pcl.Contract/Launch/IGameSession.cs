namespace Pcl.Core.Contracts.Launch;
/// <summary>Shell 借用的本地会话接口；不暴露 ProcessSpec、Process 或平台进程 Port，也不作为 RPC DTO 序列化。</summary>
public interface IGameSession : IAsyncDisposable
{
    string Id { get; }

    int Pid { get; }

    string LogFile { get; }

    bool HasExited { get; }

    Task<int> Completion { get; }

    event Action<string>? Output;
    Task StopAsync(CancellationToken ct = default);
}
