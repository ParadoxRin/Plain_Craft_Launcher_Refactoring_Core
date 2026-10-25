using System.Text.Json;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Core.Contracts.Pages;
// 只接受已登记用例；工作区参数可带用户选择的输入路径，不接受任意 Host 输出路径。
public sealed record PageCommand(
    string IdempotencyKey,
    string Method,
    string? InstanceId = null,
    string? GameVersion = null,
    string? Loader = null,
    string? Version = null,
    bool DisableMods = false,
    string? SelectionId = null,
    string? PlanId = null,
    string? ResumeOfOperationId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? PayloadJson = null
);
public sealed record PageSnapshot(
    string OperationId,
    string State,
    string Stage,
    long Sequence,
    DateTimeOffset Updated,
    PageCommand Command,
    OperationProgress? Progress = null,
    JsonElement? Result = null,
    string? ErrorCode = null
)
{
    public bool Terminal => State is "succeeded" or "failed" or "cancelled" or "interrupted";

    public override string ToString() => Command.Method + " · " + State + " · " + OperationId;
}

public sealed record ArtifactExport(string FileName, long Size, string Sha256, string Verification, bool Executable = false);
public interface IPageOperations
{
    Task<OperationAcceptance> AcceptAsync(PageCommand command, CancellationToken ct = default);
    Task<PageSnapshot> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PageSnapshot>> ListAsync(CancellationToken ct = default);
    Task<PageSnapshot> CancelAsync(string id, CancellationToken ct = default);
    Task<PageSnapshot> WaitAsync(string id, CancellationToken ct = default);
}

// 任务页可查询、显式取消或创建续做尝试；原操作历史不会被覆盖。
public interface IPageTaskService
{
    Task<IReadOnlyList<PageSnapshot>> ListAsync(CancellationToken ct = default);
    Task<PageSnapshot> CancelAsync(string id, CancellationToken ct = default);
    Task<PageSnapshot> ResumeAsync(string id, CancellationToken ct = default);
    Task<ArtifactSaved> ExportAsync(string id, string destination, CancellationToken ct = default);
}

public interface IPageTaskSource
{
    IPageTaskService Tasks { get; }
}
