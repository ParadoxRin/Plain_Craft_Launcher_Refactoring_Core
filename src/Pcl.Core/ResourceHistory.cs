using Newtonsoft.Json;

namespace Pcl.Core;
// [实现] 数据约定：ResourceOperation 汇集 Timestamp、Action、Files、BackupDirectory，供该模块传递状态。
public sealed record ResourceOperation(
    DateTimeOffset Timestamp,
    string Action,
    IReadOnlyList<string> Files,
    string? BackupDirectory
)
{
    // [实现] ToString 的实现：计算并返回 $"{Timestamp.LocalDateTime:MM-dd HH:mm} · {Action} · {string.Join(", ", Files)}"。
    public override string ToString() => $"{Timestamp.LocalDateTime:MM-dd HH:mm} · {Action} · {string.Join(", ", Files)}";
}

// 本地资源管理：扫描模组、光影和资源包，在文件变更前后校验依赖并保留事务恢复信息。
public static class ResourceHistory
{
    // [实现] Read 的实现：准备 var path = ResourceInstaller.SafeTarget(gameDirectory, ".pcl-resource-history.json")；按 !File.Exists(path) 分支处理；按 new FileInfo(path).Length > 4 * 1024 * 1024 分支处理。
    public static IReadOnlyList<ResourceOperation> Read(string gameDirectory)
    {
        var path = ResourceInstaller.SafeTarget(gameDirectory, ".pcl-resource-history.json");
        if (!File.Exists(path))
            return[];
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("资源历史过大。");
        return JsonConvert.DeserializeObject<List<ResourceOperation>>(
            File.ReadAllText(path)
        ) ?? [];
    }

    // [实现] Record 的实现：执行受异常处理保护的操作。
    internal static void Record(string gameDirectory, string action, IEnumerable<string> files, string? backup = null)
    {
        // History is secondary metadata; a disk error here must not turn a committed installation into a false failure.
        try
        {
            // [实现] 在异常保护范围内：执行 JsonFiles.Write( ResourceInstaller.SafeTarget(gameDirectory, ".pcl-resource-history.json"), Rea…。
            JsonFiles.Write(
                ResourceInstaller.SafeTarget(gameDirectory, ".pcl-resource-history.json"),
                Read(gameDirectory)
                    .Prepend(
                        new ResourceOperation(
                            DateTimeOffset.UtcNow,
                            action,
                            files.ToArray(),
                            backup
                        )
                    )
                    .Take(200)
            );
        }
        catch (Exception ex)when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // [实现] 捕获 (Exception ex) 后：执行 System.Diagnostics.Trace.WriteLine("Resource history: " + ex.Message)。
            System.Diagnostics.Trace.WriteLine("Resource history: " + ex.Message);
        }
    }
}
