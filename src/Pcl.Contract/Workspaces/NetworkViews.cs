namespace Pcl.Core.Contracts.Workspaces;
// 诊断与缓存展示快照；选择清单在 Host 私有存储中重新校验。
public sealed record TemporaryCacheFile(string RelativePath, long Size, DateTimeOffset LastWriteUtc)
{
    public override string ToString() => $"{Size / 1048576.0:F2} MiB · {LastWriteUtc.ToLocalTime():g} · {RelativePath}";
}

public sealed record CacheTrashEntry(string RelativePath, string StoredName, long Size, DateTimeOffset LastWriteUtc);
public sealed record CacheTrashBatch(string Id, DateTimeOffset CreatedAt, IReadOnlyList<CacheTrashEntry> Files)
{
    public override string ToString() => $"{CreatedAt.ToLocalTime():g} · {Files.Count} 个文件 · {Files.Sum(
            x => x.Size
        ) / 1048576.0:F2} MiB";
}

public sealed record CacheCleanupResult(CacheTrashBatch? Batch, IReadOnlyList<string> Skipped);
public sealed record NetworkProbeTarget(string Name, string Url)
{
    public override string ToString() => Name + " · " + Url;
}

public sealed record NetworkProbeResult(
    string Name,
    string Url,
    bool Success,
    int? StatusCode,
    long Bytes,
    double ResponseMilliseconds,
    double ElapsedMilliseconds,
    double KiBPerSecond,
    bool RangeHonored,
    string? Error = null
)
{
    public override string ToString() => Success
        ? $"{Name} · 响应 {ResponseMilliseconds:F0} ms · {Bytes / 1024.0:F1} KiB / {KiBPerSecond:F1} KiB/s · HTTP {StatusCode}"
        : $"{Name} · {Error}";
}

public sealed record CachePreview(string Handle, TemporaryCacheFile[] Items);
