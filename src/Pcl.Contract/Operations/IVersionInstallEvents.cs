namespace Pcl.Core.Contracts.Operations;
/// <summary>安装展示订阅；快照水位递增，终态后结束。等待令牌只取消订阅，不取消后台任务。</summary>
public interface IVersionInstallEvents
{
    IAsyncEnumerable<OperationSnapshot> WatchAsync(string operationId, CancellationToken ct = default);
}
