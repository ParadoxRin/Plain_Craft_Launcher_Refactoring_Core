using Newtonsoft.Json;

namespace Pcl.Core;
// [实现] 数据约定：InstallOperation 汇集 Id、Version、Loader、State、Updated、Error，供该模块传递状态。
public sealed record InstallOperation(
    string Id,
    string Version,
    string Loader,
    string State,
    DateTimeOffset Updated,
    string? Error = null
)
{
    // [实现] ToString 的实现：计算并返回 $"{State} · Minecraft {Version} · {Loader}"。
    public override string ToString() => $"{State} · Minecraft {Version} · {Loader}";
}

// 游戏安装：根据官方版本与加载器元数据规划组件，校验下载文件，写入可复现的实例配置。
public sealed class InstallOperations(AppPaths paths)
{
    private static readonly object gate = new();
    private string FilePath => Path.Combine(paths.Root, "install-operations.json");

    // [实现] Read 的实现：锁定 gate 后访问共享状态。
    public IReadOnlyList<InstallOperation> Read()
    {
        lock (gate)
        {
            // [实现] 本段流程：准备 var rows = File.Exists(FilePath) ? JsonConvert.DeserializeObject<InstallOperation[]>( File.Read…；返回 rows.OrderByDescending( x => x.Updated ) .ToArray()。
            var rows = File.Exists(FilePath)
                ? JsonConvert.DeserializeObject<InstallOperation[]>(
                    File.ReadAllText(FilePath)
                ) ?? []
                : [];
            return rows.OrderByDescending(
                x => x.Updated
            )
                .ToArray();
        }
    }

    // [实现] MarkInterrupted 的实现：锁定 gate 后访问共享状态。
    public void MarkInterrupted()
    {
        lock (gate)
        {
            // [实现] 本段流程：准备 var rows = Read() .Select( x => x.State == "安装中" ? x with { State = "已中断", Error = "选择继续安装可复用已校…；按 rows.Length > 0 分支处理。
            var rows = Read()
                .Select(
                    x => x.State == "安装中"
                        ? x with
                        {
                            State = "已中断",
                            Error = "选择继续安装可复用已校验文件，并重新执行安装器。"
                        }
                        : x
                )
                .ToArray();
            if (rows.Length > 0)
                JsonFiles.Write(FilePath, rows);
        }
    }

    // [实现] Save 的实现：锁定 gate 后访问共享状态。
    private void Save(InstallOperation row)
    {
        lock (gate)
            JsonFiles.Write(
                FilePath,
                Read()
                    .Where(
                        x => x.Id != row.Id
                    )
                    .Prepend(row)
                    .Take(100)
                    .ToArray()
            );
    }

    // [实现] InstallAsync 的实现：执行 VersionCatalog.ValidateId(version)；按 !new[] { "auto", "vanilla", "fabric", "quilt", "forge", "neoforge", "optifine", "liteloader" }.… 分支处理；准备 var task = new InstallOperation( taskId ?? Guid.NewGuid() .ToString("N"), version, loader, "安装中…。
    public async Task<GameProfile> InstallAsync(
        MinecraftService source,
        string version,
        string loader,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        string? taskId = null
    )
    {
        VersionCatalog.ValidateId(version);
        if (!new[]
        {
            "auto",
            "vanilla",
            "fabric",
            "quilt",
            "forge",
            "neoforge",
            "optifine",
            "liteloader"
        }.Contains(loader))
            throw new InvalidDataException("安装任务的加载器无效。");
        var task = new InstallOperation(
            taskId ?? Guid.NewGuid()
                .ToString("N"),
            version,
            loader,
            "安装中",
            DateTimeOffset.UtcNow
        );
        Save(task);
        try
        {
            // [实现] 在异常保护范围内：准备 var target = source.ForVersion(version)；准备 var result = await new VersionInstaller(target).InstallAsync(loader, progress, ct)；执行 Save( task with { State = "已完成", Updated = DateTimeOffset.UtcNow } )。
            using var target = source.ForVersion(version);
            var result = await new VersionInstaller(target).InstallAsync(loader, progress, ct);
            Save(
                task with
                {
                    State = "已完成",
                    Updated = DateTimeOffset.UtcNow
                }
            );
            return result;
        }
        catch (OperationCanceledException)
        {
            // [实现] 捕获 (OperationCanceledException) 后：执行 Save( task with { State = "已取消", Updated = DateTimeOffset.UtcNow } )；重新抛出当前异常。
            Save(
                task with
                {
                    State = "已取消",
                    Updated = DateTimeOffset.UtcNow
                }
            );
            throw;
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 Save( task with { State = "失败", Error = DiagnosticsService.Redact(ex.Message), Updated = DateTi…；重新抛出当前异常。
            Save(
                task with
                {
                    State = "失败",
                    Error = DiagnosticsService.Redact(ex.Message),
                    Updated = DateTimeOffset.UtcNow
                }
            );
            throw;
        }
    }
}
