using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Host;

namespace Pcl.Client;
public sealed partial class LauncherSession
{
    private readonly SemaphoreSlim pageGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> activePages = new();
    private static readonly JsonSerializerOptions PageJson = new()
    {
        PropertyNameCaseInsensitive = true
    };
    public IPageTaskService PageTasks => new RemotePageTasks(this);
    public IWorkspaceOperations Workspaces => new RemoteWorkspaces(this);

    public IComponentPageService Components(string instanceId) => new RemoteComponents(this, instanceId);
    public IArtifactPageService Artifacts() => new RemoteArtifacts(this);
    public async Task<ComponentSource[]> ListInstancesAsync(CancellationToken ct = default) => Result<ComponentSource[]>(
        await RunPageAsync(
            new(
                Guid.NewGuid()
                    .ToString("N"),
                "instance.list"
            ),
            null,
            ct
        )
    );
    // UI 取消映射为 Host 的取消请求；连接或窗口消失不自动取消后端。
    private async Task<PageSnapshot> RunPageAsync(PageCommand command, IProgress<VersionInstallProgress>? progress, CancellationToken ct)
    {
        await pageGate.WaitAsync(ct);
        string? operationId = null;
        bool entered = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            await EnterHostAsync();
            entered = true;
            var operations = new HostPageOperations(Client);
            var accepted = await operations.AcceptAsync(command);
            operationId = accepted.OperationId;
            activePages[operationId] = 0;
            async Task<PageSnapshot> ObserveAsync()
            {
                await foreach (var row in operations.WatchAsync(accepted.OperationId))
                {
                    if (row.Progress is { } p)
                    {
                        progress?.Report(
                            new(p.Stage, p.Completed, p.Total ?? 0, p.Bytes, p.TotalBytes ?? 0)
                        );
                    }

                    if (row.Terminal)
                    {
                        return row;
                    }
                }

                throw new OperationRejectedException("SUBSCRIPTION_CLOSED");
            }

            var waiting = ObserveAsync();
            bool cancelSent = false;
            while (!waiting.IsCompleted)
            {
                if (ct.IsCancellationRequested && !cancelSent)
                {
                    await operations.CancelAsync(accepted.OperationId);
                    cancelSent = true;
                }

                await Task.WhenAny(
                    waiting,
                    Task.Delay(100)
                );
            }

            var terminal = await waiting;
            activePages.TryRemove(operationId, out _);
            return terminal;
        }
        finally
        {
            try
            {
                if (entered)
                {
                    await RefreshStateAfterOperationAsync();
                }
            }
            finally
            {
                pageGate.Release();
            }
        }
    }

    private static T Result<T>(PageSnapshot row)
    {
        if (row.State == "cancelled")
        {
            throw new OperationCanceledException();
        }

        if (row.State != "succeeded")
        {
            if (row.ErrorCode == "COMPONENT_PLAN_ALREADY_APPLIED")
            {
                throw new InvalidOperationException("此预览已执行，请重新预览后再调整组件。");
            }

            if (row.ErrorCode == "COMPONENT_SOURCE_CHANGED")
            {
                throw new InvalidOperationException("源实例已变化，请重新预览。");
            }

            if (row.ErrorCode == "WORKSPACE_REVIEW_REQUIRED")
            {
                throw new InvalidOperationException("上次任务在写入期间中断，无法确认是否已提交。已保留文件，请查看实例和任务记录，核对后重新发起操作。");
            }

            if (row.ErrorCode == "PACK_PREVIEW_CHANGED")
            {
                throw new OperationRejectedException("PACK_PREVIEW_CHANGED");
            }

            if (row.ErrorCode == "BATCH_PREVIEW_EXPIRED")
            {
                throw new InvalidOperationException("批量更新预览已失效，请重新检查更新并准备预览。");
            }

            throw new OperationRejectedException(row.ErrorCode ?? "PAGE_EXECUTION_FAILED");
        }

        return row.Result is { } value
            ? value.Deserialize<T>(PageJson)!
            : JsonSerializer.Deserialize<T>("null", PageJson)!;
    }

    // 显式工作区命令复用同一写入授权、操作订阅、取消与退出责任。
    private sealed class RemoteWorkspaces(LauncherSession owner) : IWorkspaceOperations, IPageTaskSource
    {
        public IPageTaskService Tasks => owner.PageTasks;

        public async Task<WorkspaceReply> ExecuteAsync(
            string method,
            string? instanceId,
            string payloadJson,
            IProgress<VersionInstallProgress>? progress = null,
            CancellationToken ct = default
        )
        {
            if (!WorkspaceMethods.Known.Contains(method))
            {
                throw new OperationRejectedException("WORKSPACE_METHOD_UNKNOWN");
            }

            var row = await owner.RunPageAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    method,
                    InstanceId: instanceId,
                    PayloadJson: payloadJson
                ),
                progress,
                ct
            );
            return new(
                row.OperationId,
                Result<JsonElement>(row)
            );
        }

        public Task<string> LinkInvitationAsync(string id, CancellationToken ct = default) => owner.Client.Call<string>(
            "multiplayer.invitation",
            new
            {
                id
            },
            ct
        );
        public async Task<string> RememberSecretAsync(string secret, CancellationToken ct = default)
        {
            var response = await owner.Client.Call<JsonElement>(
                "workspace.secret",
                new
                {
                    value = secret
                },
                ct
            );
            return response.GetProperty("handle")
                .GetString()!;
        }

        public Task ExportAsync(WorkspaceExport export, string destination, CancellationToken ct = default) => ExportFileAsync(owner.Client, export.OperationId, export.File, destination, ct);
    }

    private sealed class RemoteComponents(LauncherSession owner, string id) : IComponentPageService, IPageTaskSource
    {
        public IPageTaskService Tasks => owner.PageTasks;

        // 提示偏好由 Host 保存；此开关不影响 Host 的冲突、源指纹或运行中检查。
        public async Task SetConfirmationSuppressedAsync(bool suppressed, CancellationToken ct)
        {
            await owner.RunPageAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    "component.confirmation",
                    InstanceId: id,
                    DisableMods: suppressed
                ),
                null,
                ct
            );
        }

        public async Task<IReadOnlyList<string>> VersionsAsync(string loader, CancellationToken ct) => Result<string[]>(
            await owner.RunPageAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    "component.versions",
                    InstanceId: id,
                    Loader: loader
                ),
                null,
                ct
            )
        );
        public async Task<ComponentPreview> PreviewAsync(string loader, string? version, bool disable, CancellationToken ct) => Result<ComponentPreview>(
            await owner.RunPageAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    "component.preview",
                    InstanceId: id,
                    Loader: loader,
                    Version: version,
                    DisableMods: disable
                ),
                null,
                ct
            )
        );
        public async Task<ComponentResult> ApplyAsync(string planId, IProgress<VersionInstallProgress>? progress, CancellationToken ct) => Result<ComponentResult>(
            await owner.RunPageAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    "component.apply",
                    InstanceId: id,
                    PlanId: planId
                ),
                progress,
                ct
            )
        );
    }

    private sealed class RemoteArtifacts(LauncherSession owner) : IArtifactPageService, IPageTaskSource
    {
        public IPageTaskService Tasks => owner.PageTasks;

        public async Task<IReadOnlyList<ArtifactChoice>> ListAsync(string version, string kind, CancellationToken ct) => Result<ArtifactChoice[]>(
            await owner.RunPageAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    "artifact.list",
                    GameVersion: version,
                    Loader: kind
                ),
                null,
                ct
            )
        );
        public async Task<ArtifactNotes?> NotesAsync(string version, CancellationToken ct) => Result<ArtifactNotes?>(
            await owner.RunPageAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    "artifact.notes",
                    GameVersion: version
                ),
                null,
                ct
            )
        );
        public async Task<ArtifactSaved> SaveAsync(string selectionId, string destination, CancellationToken ct)
        {
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new IOException("目标已经存在，请使用其他文件名。");
            }

            var row = await owner.RunPageAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    "artifact.download",
                    SelectionId: selectionId
                ),
                null,
                ct
            );
            var export = Result<ArtifactExport>(row);
            await ExportFileAsync(owner.Client, row.OperationId, export, destination, ct);
            return new(destination, export.Verification);
        }
    }

    // 写用户选择的目标是 Shell 客户端职责。临时文件校验完成后原子发布，断线后续读同一校验对象。
    public static async Task ExportFileAsync(
        HostClient client,
        string operationId,
        ArtifactExport expected,
        string destination,
        CancellationToken ct = default
    )
    {
        var target = Path.GetFullPath(destination);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException("目标已经存在，请使用其他文件名。");
        }

        var temporary = target + ".pcl-" + Guid.NewGuid()
            .ToString("N");
        ResourceReference? handle = null;
        async Task OpenAsync()
        {
            handle = await client.OpenResourceAsync("artifact-export", operationId, ct);
            if (handle.Size != expected.Size || !handle.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("EXPORT_CHANGED");
            }
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            await OpenAsync();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    long offset = 0;
                    while (offset < expected.Size)
                    {
                        ct.ThrowIfCancellationRequested();
                        ResourceChunk chunk;
                        try
                        {
                            chunk = await client.ReadResourceAsync(handle!.ResourceId, offset, 262144, ct);
                        }
                        catch (OperationRejectedException ex)when (ex.Code is "RESOURCE_EXPIRED_OR_UNKNOWN" or "HOST_DISCONNECTED" or "RESPONSE_LOST_REQUERY")
                        {
                            await OpenAsync();
                            continue;
                        }

                        var bytes = Convert.FromBase64String(chunk.Base64);
                        if (chunk.Offset != offset || bytes.Length == 0 || bytes.Length > 262144 || offset + bytes.Length > expected.Size || chunk.Eof != (offset + bytes.Length == expected.Size))
                        {
                            throw new InvalidDataException("EXPORT_RANGE_INVALID");
                        }

                        await output.WriteAsync(bytes, ct);
                        hash.AppendData(bytes);
                        offset += bytes.Length;
                    }

                    if (!Convert.ToHexString(
                        hash.GetHashAndReset()
                    )
                        .Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("EXPORT_HASH_MISMATCH");
                    }

                    await output.FlushAsync(ct);
                    output.Flush(true);
                }

            ct.ThrowIfCancellationRequested();
            // 可执行权限在原子发布前设置；普通导出仍保留原权限策略。
            if (expected.Executable && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            if (handle is not null)
            {
                try
                {
                    await client.CloseResourceAsync(handle.ResourceId);
                }
                catch (Exception ex)when (ex is IOException or OperationRejectedException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private sealed class RemotePageTasks(LauncherSession owner) : IPageTaskService
    {
        public Task<IReadOnlyList<PageSnapshot>> ListAsync(CancellationToken ct = default) => new HostPageOperations(owner.Client).ListAsync(ct);
        public Task<PageSnapshot> CancelAsync(string id, CancellationToken ct = default) => new HostPageOperations(owner.Client).CancelAsync(id, ct);
        public async Task<ArtifactSaved> ExportAsync(string id, string destination, CancellationToken ct = default)
        {
            var row = await new HostPageOperations(owner.Client).GetAsync(id, ct);
            if (row.Command.Method != "artifact.download" && row.Command.Method is not ("workspace.instances.backup" or "workspace.browser.export" or "workspace.packs.export" or "workspace.settings.export" or "workspace.catalog.icon" or "workspace.account.skinExport"))
            {
                throw new InvalidOperationException("请选择已完成的文件下载任务。");
            }

            var export = Result<ArtifactExport>(row);
            await ExportFileAsync(owner.Client, id, export, destination, ct);
            return new(destination, export.Verification);
        }

        public async Task<PageSnapshot> ResumeAsync(string id, CancellationToken ct = default)
        {
            var row = await new HostPageOperations(owner.Client).GetAsync(id, ct);
            return await owner.RunPageAsync(
                row.Command with
                {
                    IdempotencyKey = Guid.NewGuid()
                        .ToString("N"),
                    ResumeOfOperationId = id
                },
                null,
                ct
            );
        }
    }
}
