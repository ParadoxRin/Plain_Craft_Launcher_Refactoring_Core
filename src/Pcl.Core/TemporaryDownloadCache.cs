using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：TemporaryCacheFile 汇集 RelativePath、Size、LastWriteUtc，供该模块传递状态。
public sealed record TemporaryCacheFile(string RelativePath, long Size, DateTimeOffset LastWriteUtc)
{
    // [实现] ToString 的实现：计算并返回 $"{Size / 1048576.0:F2} MiB · {LastWriteUtc.ToLocalTime():g} · {RelativePath}"。
    public override string ToString() => $"{Size / 1048576.0:F2} MiB · {LastWriteUtc.ToLocalTime():g} · {RelativePath}";
}

// [实现] 数据约定：CacheTrashEntry 汇集 RelativePath、StoredName、Size、LastWriteUtc，供该模块传递状态。
public sealed record CacheTrashEntry(string RelativePath, string StoredName, long Size, DateTimeOffset LastWriteUtc);
// [实现] 数据约定：CacheTrashBatch 汇集 Id、CreatedAt、Files，供该模块传递状态。
public sealed record CacheTrashBatch(string Id, DateTimeOffset CreatedAt, IReadOnlyList<CacheTrashEntry> Files)
{
    // [实现] ToString 的实现：计算并返回 $"{CreatedAt.ToLocalTime():g} · {Files.Count} 个文件 · {Files.Sum( x => x.Size ) / 1048576.0:F2} M…。
    public override string ToString() => $"{CreatedAt.ToLocalTime():g} · {Files.Count} 个文件 · {Files.Sum(
            x => x.Size
        ) / 1048576.0:F2} MiB";
}

// [实现] 数据约定：CacheCleanupResult 汇集 Batch、Skipped，供该模块传递状态。
public sealed record CacheCleanupResult(CacheTrashBatch? Batch, IReadOnlyList<string> Skipped);
/// <summary>Only incomplete downloads in explicit cache roots; never installed runtimes, game data or verified downloads.</summary>
public sealed class TemporaryDownloadCache(AppPaths paths)
{
    // [实现] Safe 的实现：计算并返回 ResourceInstaller.SafeTarget(paths.Root, relative)。
    private string Safe(string relative) => ResourceInstaller.SafeTarget(paths.Root, relative);
    // [实现] TemporaryName 的实现：计算并返回 name.EndsWith(".part", StringComparison.Ordinal) || name.EndsWith(".partial", StringComparison.…。
    private static bool TemporaryName(string name) => name.EndsWith(".part", StringComparison.Ordinal) || name.EndsWith(".partial", StringComparison.Ordinal) || name.Contains(".partial-", StringComparison.Ordinal);
    // [实现] Allowed 的实现：准备 var pieces = relative.Replace('\\', '/') .Split('/')；返回 !Path.IsPathRooted(relative) && !pieces.Any( x => x is "" or "." or ".." ) && TemporaryName(pie…。
    private static bool Allowed(string relative)
    {
        var pieces = relative.Replace('\\', '/')
            .Split('/');
        return !Path.IsPathRooted(relative) && !pieces.Any(
            x => x is "" or "." or ".."
        ) && TemporaryName(pieces[^1]) && (pieces.Length >= 2 && pieces[0] == "cache" || pieces.Length >= 4 && pieces[0] == "instances" && pieces[2] == ".pcl-downloads");
    }

    // [实现] ActivePaths 的实现：计算并返回 DownloadTasks.For(paths) .Snapshot() .Where( x => x.State is "排队中" or "下载中" ) .SelectMany( x =>…。
    private HashSet<string> ActivePaths() => DownloadTasks.For(paths)
        .Snapshot()
        .Where(
            x => x.State is "排队中" or "下载中"
        )
        .SelectMany(
            x => new[]
            {
                Path.GetFullPath(x.Item.Path),
                Path.GetFullPath(x.Item.Path + ".part")
            }
        )
        .ToHashSet(StringComparer.Ordinal);
    // [实现] Validate 的实现：计算并返回 Allowed(relative) ? Safe(relative) : throw new InvalidDataException("此文件不属于可清理的下载临时缓存。")。
    private string Validate(string relative) => Allowed(relative)
        ? Safe(relative)
        : throw new InvalidDataException("此文件不属于可清理的下载临时缓存。");
    // [实现] Lease 的实现：计算并返回 new( Safe(".cache-cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None )。
    private FileStream Lease() => new(
        Safe(".cache-cleanup.lock"),
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None
    );
    // [实现] Preview 的实现：准备 var cutoff = (now ?? DateTimeOffset.UtcNow).AddHours(-24)；准备 var roots = new List<string>()；准备 var result = new List<TemporaryCacheFile>()。
    public IReadOnlyList<TemporaryCacheFile> Preview(DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddHours(-24);
        var roots = new List<string>();
        var result = new List<TemporaryCacheFile>();
        var active = ActivePaths();
        var cache = Safe("cache");
        if (Directory.Exists(cache))
            roots.Add(cache);
        var instances = Safe("instances");
        if (Directory.Exists(instances))
            foreach (var instance in Directory.EnumerateDirectories(instances))
            {
                // [实现] 对 Directory.EnumerateDirectories(instances) 中的每一项：按 (File.GetAttributes(instance) & FileAttributes.ReparsePoint) != 0 分支处理；准备 var root = ResourceInstaller.SafeTarget(instance, ".pcl-downloads")；按 Directory.Exists(root) 分支处理。
                if ((File.GetAttributes(instance) & FileAttributes.ReparsePoint) != 0)
                    continue;
                var root = ResourceInstaller.SafeTarget(instance, ".pcl-downloads");
                if (Directory.Exists(root))
                    roots.Add(root);
            }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        foreach (var root in roots)
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                // [实现] 对 Directory.EnumerateFiles(root, "*", options) 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var relative = Path.GetRelativePath(paths.Root, file)；按 !Allowed(relative) || active.Contains(file) 分支处理。
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(paths.Root, file);
                if (!Allowed(relative) || active.Contains(file))
                    continue;
                var info = new FileInfo(
                    Validate(relative)
                );
                if (info.LastWriteTimeUtc > cutoff.UtcDateTime)
                    continue;
                result.Add(
                    new(relative, info.Length, info.LastWriteTimeUtc)
                );
                if (result.Count >= 10000)
                    throw new InvalidDataException("临时文件超过 10000 个，请缩小缓存范围后再检查。");
            }

        return result.OrderByDescending(
            x => x.Size
        )
            .ToArray();
    }

    // [实现] MoveToTrash 的实现：准备 var lease = Lease()；准备 var id = Guid.NewGuid() .ToString("N")；准备 var folder = Safe("cache-trash/" + id)。
    public CacheCleanupResult MoveToTrash(IEnumerable<TemporaryCacheFile> selected, CancellationToken ct = default)
    {
        using var lease = Lease();
        var id = Guid.NewGuid()
            .ToString("N");
        var folder = Safe("cache-trash/" + id);
        var moved = new List<CacheTrashEntry>();
        var skipped = new List<string>();
        foreach (var item in selected.DistinctBy(
            x => x.RelativePath
        )
            .Take(10000))
        {
            // [实现] 对 selected.DistinctBy( x => x.RelativePath ) .Take(10000) 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var source = Validate(item.RelativePath)；按 ActivePaths() .Contains(source) 分支处理。
            ct.ThrowIfCancellationRequested();
            var source = Validate(item.RelativePath);
            if (ActivePaths()
                .Contains(source))
            {
                // [实现] 条件成立（ActivePaths() .Contains(source)）时：执行 skipped.Add(item.RelativePath + "：正在下载")；执行 continue;。
                skipped.Add(item.RelativePath + "：正在下载");
                continue;
            }

            try
            {
                // [实现] 在异常保护范围内：准备 var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)；准备 var info = new FileInfo(source)；按 info.Length != item.Size || info.LastWriteTimeUtc != item.LastWriteUtc.UtcDateTime || info.Last… 分支处理。
                using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var info = new FileInfo(source);
                if (info.Length != item.Size || info.LastWriteTimeUtc != item.LastWriteUtc.UtcDateTime || info.LastWriteTimeUtc > DateTime.UtcNow.AddHours(-24) || ActivePaths()
                    .Contains(source))
                {
                    // [实现] 条件成立（info.Length != item.Size || info.LastWriteTimeUtc != item.LastWriteUtc.UtcDateTime || info.Last…）时：执行 skipped.Add(item.RelativePath + "：文件已变化或正在使用")；执行 continue;。
                    skipped.Add(item.RelativePath + "：文件已变化或正在使用");
                    continue;
                }

                Directory.CreateDirectory(folder);
                var stored = Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(item.RelativePath)
                    )
                )
                    .ToLowerInvariant() + ".cache";
                // Receipt is written first. A crash can leave a missing cache entry, but never orphan moved bytes.
                var entry = new CacheTrashEntry(item.RelativePath, stored, item.Size, item.LastWriteUtc);
                var pending = moved.Append(entry)
                    .ToArray();
                JsonFiles.Write(
                    Path.Combine(folder, "receipt.json"),
                    new CacheTrashBatch(id, DateTimeOffset.UtcNow, pending)
                );
                File.Move(
                    source,
                    ResourceInstaller.SafeTarget(folder, stored)
                );
                moved.Add(entry);
            }
            catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 skipped.Add(item.RelativePath + "：被占用或无法移动")。
                skipped.Add(item.RelativePath + "：被占用或无法移动");
            }
        }

        if (moved.Count == 0)
        {
            // [实现] 条件成立（moved.Count == 0）时：按 Directory.Exists(folder) 分支处理；返回 new(null, skipped)。
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            return new(null, skipped);
        }

        var batch = new CacheTrashBatch(id, DateTimeOffset.UtcNow, moved);
        JsonFiles.Write(
            Path.Combine(folder, "receipt.json"),
            batch
        );
        return new(batch, skipped);
    }

    // [实现] ListTrash 的实现：准备 var root = Safe("cache-trash")；按 !Directory.Exists(root) 分支处理；返回 Directory.EnumerateDirectories(root) .Where( x => (File.GetAttributes(x) & FileAttributes.Repar…。
    public IReadOnlyList<CacheTrashBatch> ListTrash()
    {
        var root = Safe("cache-trash");
        if (!Directory.Exists(root))
            return[];
        return Directory.EnumerateDirectories(root)
            .Where(
                x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0 && Guid.TryParseExact(
                    Path.GetFileName(x),
                    "N",
                    out _
                )
            )
            .Select(
                x => JsonFiles.Read(
                    ResourceInstaller.SafeTarget(x, "receipt.json")
                )
                    .ToObject<CacheTrashBatch>()!
            )
            .OrderByDescending(
                x => x.CreatedAt
            )
            .ToArray();
    }

    // [实现] Restore 的实现：按 !Guid.TryParseExact(id, "N", out _) 分支处理；准备 var lease = Lease()；准备 var folder = Safe("cache-trash/" + id)。
    public CacheCleanupResult Restore(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("缓存回收记录无效。");
        using var lease = Lease();
        var folder = Safe("cache-trash/" + id);
        var batch = JsonFiles.Read(
            ResourceInstaller.SafeTarget(folder, "receipt.json")
        )
            .ToObject<CacheTrashBatch>()!;
        if (batch.Id != id || batch.Files.Count > 10000)
            throw new InvalidDataException("缓存回收记录不匹配。");
        var remaining = new List<CacheTrashEntry>();
        var skipped = new List<string>();
        foreach (var item in batch.Files)
        {
            // [实现] 对 batch.Files 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var target = Validate(item.RelativePath)；准备 var stored = ResourceInstaller.SafeTarget(folder, item.StoredName)。
            ct.ThrowIfCancellationRequested();
            var target = Validate(item.RelativePath);
            var stored = ResourceInstaller.SafeTarget(folder, item.StoredName);
            var expectedName = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(item.RelativePath)
                )
            )
                .ToLowerInvariant() + ".cache";
            if (item.StoredName != expectedName)
                throw new InvalidDataException("缓存回收文件名与原路径不匹配。");
            if (!File.Exists(stored))
                continue;
            if (File.Exists(target) || Directory.Exists(target) || ActivePaths()
                .Contains(target))
            {
                // [实现] 条件成立（File.Exists(target) || Directory.Exists(target) || ActivePaths() .Contains(target)）时：执行 remaining.Add(item)；执行 skipped.Add(item.RelativePath + "：目标已有文件或正在下载")；执行 continue;。
                remaining.Add(item);
                skipped.Add(item.RelativePath + "：目标已有文件或正在下载");
                continue;
            }

            try
            {
                // [实现] 在异常保护范围内：准备 var exclusive = new FileStream(stored, FileMode.Open, FileAccess.ReadWrite, FileShare.None)；按 exclusive.Length != item.Size 分支处理；执行 Directory.CreateDirectory( Path.GetDirectoryName(target)! )。
                using var exclusive = new FileStream(stored, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (exclusive.Length != item.Size)
                    throw new InvalidDataException("回收文件大小已变化，停止恢复。");
                Directory.CreateDirectory(
                    Path.GetDirectoryName(target)!
                );
                File.Move(stored, target);
            }
            catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 remaining.Add(item)；执行 skipped.Add(item.RelativePath + "：被占用或无法恢复")。
                remaining.Add(item);
                skipped.Add(item.RelativePath + "：被占用或无法恢复");
            }
        }

        var next = batch with
        {
            Files = remaining
        };
        if (remaining.Count == 0)
            Directory.Delete(folder, true);
        else
            JsonFiles.Write(
                Path.Combine(folder, "receipt.json"),
                next
            );
        return new(
            remaining.Count == 0
                ? null
                : next,
            skipped
        );
    }
}
