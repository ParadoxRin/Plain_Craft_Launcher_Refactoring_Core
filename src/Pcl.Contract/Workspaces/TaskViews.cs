namespace Pcl.Core.Contracts.Workspaces;
// Editable network draft only. Host performs validation and persistence.
public sealed record NetworkOptions(
    int ConcurrentFiles = 8,
    int SpeedKiB = 0,
    string Proxy = "",
    string MirrorBase = "",
    string MirrorHost = "",
    bool PreferMirror = false,
    bool SegmentedDownloads = false,
    int SegmentWorkers = 4
)
{
    public string ManifestMirror { get; init; } = "";
    public string ManifestSource { get; init; } = "official";
    public string ResourceApiMirror { get; init; } = "";
    public string CurseForgeApiMirror { get; init; } = "";
    public string ResourceSource { get; init; } = "official";
    public string FileNameStyle { get; init; } = "original";
    public string ModNameStyle { get; init; } = "name";
    public bool NotifyRelease { get; init; }
    public bool NotifySnapshot { get; init; }
    public bool DisableLocalCopy { get; init; }
    public bool DebugMode { get; init; }
    public int ArtificialDelayMs { get; init; }
}

public sealed record DownloadFile(string Path, long Size);
public sealed record DownloadTaskRow(
    string Id,
    DownloadFile Item,
    string State,
    long Bytes,
    DateTimeOffset Updated,
    string? Error
)
{
    public override string ToString() => $"{State} · {System.IO.Path.GetFileName(Item.Path)} · {Bytes / 1048576.0:F1}/{Math.Max(0, Item.Size) / 1048576.0:F1} MiB";
}

public sealed record InstallOperation(
    string Id,
    string Version,
    string Loader,
    string State,
    DateTimeOffset Updated,
    string? Error
)
{
    public override string ToString() => $"{State} · Minecraft {Version} · {Loader}";
}

public sealed record DownloadSnapshot(
    DownloadTaskRow[] Rows,
    bool IsPaused,
    NetworkOptions Options,
    InstallOperation[] Installations,
    string[] Logs
);
