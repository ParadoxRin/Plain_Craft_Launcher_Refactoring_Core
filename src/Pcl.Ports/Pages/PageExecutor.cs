namespace Pcl.Core.Ports.Pages;
// Application 映射输入到执行 Port；执行器仅处理受控用例，返回供公开结果序列化的字节。
public sealed record PageInput(
    string Method,
    string? InstanceId,
    string? GameVersion,
    string? Loader,
    string? Version,
    bool DisableMods,
    string? SelectionId,
    string? PlanId,
    string? PayloadJson = null
);
public interface IPageExecutor
{
    Task<byte[]> ExecuteAsync(
        string executionId,
        PageInput input,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    );
}
