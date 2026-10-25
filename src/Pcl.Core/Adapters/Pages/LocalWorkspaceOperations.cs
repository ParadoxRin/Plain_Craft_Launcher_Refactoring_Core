using System.Text.Json;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Ports.Pages;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Pages;
// 显式 --in-process 兼容测试由窗口持有此适配器；默认应用使用独立 Host。
public sealed class LocalWorkspaceOperations : IWorkspaceOperations, IDisposable
{
    private sealed class MemoryStore : IOperationStore
    {
        private readonly Dictionary<string, byte[]> values = new();
        public IReadOnlyList<byte[]> ReadAll() => values.Values.ToArray();
        public void Save(string id, byte[] value) => values[id] = value;
        public void Dispose()
        {
        }
    }

    private sealed class ProgressMap(IProgress<VersionInstallProgress>? progress) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress p) => progress?.Report(
            new(p.Stage, p.Completed, p.Total, p.Bytes, p.TotalBytes)
        );
    }

    private readonly PageExecutionAdapter adapter;
    public LocalWorkspaceOperations(MinecraftService source, AccountManager? accounts = null)
    {
        adapter = new(
            source,
            new MemoryStore(),
            accounts
        );
    }

    public IPageTaskService Tasks => throw new NotSupportedException("同进程兼容模式没有 Host 任务历史。");

    public async Task<WorkspaceReply> ExecuteAsync(
        string method,
        string? instanceId,
        string payloadJson,
        IProgress<VersionInstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var id = "op-" + Guid.NewGuid()
            .ToString("N");
        var bytes = await Task.Run(
            () => adapter.ExecuteAsync(
                id,
                new PageInput(method, instanceId, null, null, null, false, null, null, payloadJson),
                new ProgressMap(progress),
                ct
            ),
            ct
        );
        using var document = JsonDocument.Parse(bytes);
        return new(
            id,
            document.RootElement.Clone()
        );
    }

    public Task<string> LinkInvitationAsync(string id, CancellationToken ct = default) => Task.FromResult(adapter.LinkInvitation(id));

    public Task<string> RememberSecretAsync(string secret, CancellationToken ct = default) => Task.FromResult(
        adapter.RememberSecret(secret)
    );
    public async Task ExportAsync(WorkspaceExport export, string destination, CancellationToken ct = default)
    {
        var file = adapter.OpenExport(export.OperationId);
        var temporary = destination + ".pcl-" + Guid.NewGuid()
            .ToString("N");
        try
        {
            await using (var input = File.OpenRead(file.Path))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, ct);
                }

            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public void Dispose() => adapter.Dispose();
}
