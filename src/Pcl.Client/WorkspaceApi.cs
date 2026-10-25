using System.Text.Json;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Client;
/// <summary>工作区的类型化传输辅助；只序列化 Contract 数据，不读取或更改游戏目录。</summary>
public sealed class WorkspaceApi(IWorkspaceOperations operations)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public IPageTaskService Tasks => operations.Tasks;

    public async Task<T> CallAsync<T>(
        string method,
        string? id,
        object? args = null,
        CancellationToken ct = default,
        IProgress<VersionInstallProgress>? progress = null
    )
    {
        var row = await operations.ExecuteAsync(
            method,
            id,
            JsonSerializer.Serialize(
                args ?? new { }
            ),
            progress,
            ct
        );
        return row.Value.Deserialize<T>(Json)!;
    }

    public Task<string> LinkInvitationAsync(string id, CancellationToken ct = default) => operations.LinkInvitationAsync(id, ct);
    public Task<string> SecretAsync(string value, CancellationToken ct = default) => operations.RememberSecretAsync(value, ct);
    public async Task ExportAsync(string method, string? id, object args, string destination, CancellationToken ct = default)
    {
        var row = await operations.ExecuteAsync(
            method,
            id,
            JsonSerializer.Serialize(args),
            ct: ct
        );
        await operations.ExportAsync(
            new(
                row.OperationId,
                row.Value.Deserialize<ArtifactExport>(Json)!
            ),
            destination,
            ct
        );
    }

    public async Task<byte[]?> ReadBinaryAsync(string method, string? id, object args, CancellationToken ct = default)
    {
        var row = await operations.ExecuteAsync(
            method,
            id,
            JsonSerializer.Serialize(args),
            ct: ct
        );
        if (row.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var file = Path.Combine(
            Path.GetTempPath(),
            "pcl-preview-" + Guid.NewGuid()
                .ToString("N")
        );
        try
        {
            await operations.ExportAsync(
                new(
                    row.OperationId,
                    row.Value.Deserialize<ArtifactExport>(Json)!
                ),
                file,
                ct
            );
            return await File.ReadAllBytesAsync(file, ct);
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    public async Task<byte[]?> ReadIconAsync(string id, CancellationToken ct = default)
    {
        var row = await operations.ExecuteAsync("workspace.instances.icon", id, "{}", ct: ct);
        if (row.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            "pcl-icon-" + Guid.NewGuid()
                .ToString("N") + ".png"
        );
        try
        {
            await operations.ExportAsync(
                new(
                    row.OperationId,
                    row.Value.Deserialize<ArtifactExport>(Json)!
                ),
                path,
                ct
            );
            return await File.ReadAllBytesAsync(path, ct);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
