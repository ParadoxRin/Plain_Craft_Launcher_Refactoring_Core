using Pcl.Core.Contracts.Installation;

namespace Pcl.Core.Contracts.Operations;
// v3 操作契约独立于旧等待完成接口；所有 DTO 可序列化，不携带 Task、令牌或原生句柄。
public interface IVersionInstallOperations
{
    const int ContractVersion = 3;
    Task<OperationAcceptance> AcceptAsync(InstallCommand command, CancellationToken wait = default);
    Task<OperationSnapshot?> GetAsync(string operationId, CancellationToken wait = default);
    Task<IReadOnlyList<OperationSnapshot>> ListAsync(CancellationToken wait = default);
    Task<OperationSnapshot> CancelAsync(string operationId, CancellationToken wait = default);
    Task<OperationSnapshot> WaitAsync(string operationId, CancellationToken wait = default);
}

public sealed record InstallCommand(
    string IdempotencyKey,
    VersionInstallRequest Installation,
    string? ResumeOfOperationId = null
);
// 未知总量使用 null；阶段名不与伪造百分比混在一起。
public sealed record OperationProgress(string Stage, int Completed, int? Total, long Bytes, long? TotalBytes);
public sealed record OperationAcceptance(string OperationId, string State, bool Replayed);
public sealed record OperationSnapshot(
    string OperationId,
    string State,
    string Stage,
    long Sequence,
    DateTimeOffset Updated,
    VersionInstallRequest Request,
    string? ResumeOfOperationId = null,
    OperationProgress? Progress = null,
    InstalledInstance? Result = null,
    string? ErrorCode = null
)
{
    public override string ToString() => $"{State} · Minecraft {Request.GameVersion} · {Request.Loader} · {OperationId}";
    public bool Terminal => State is "succeeded" or "failed" or "cancelled" or "interrupted";
}

// 拒绝受理与受理后失败分开；只返回稳定错误码，不公开原始异常。
public sealed class OperationRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
