namespace Pcl.Core.Contracts.Workspaces;
// 仅承载展示与用户输入，业务校验由 Host 内原服务执行。
public sealed record GameProfile(
    string Id,
    string Name,
    string GameVersion,
    string Loader,
    string GameDirectory,
    string JsonPath
)
{
    public override string ToString() => Name;
}

public sealed record InstancePreferences(
    string Name = "",
    string Description = "",
    bool Favorite = false,
    bool Hidden = false,
    bool Deleted = false,
    string Group = ""
);
public sealed record InstanceItem(
    GameProfile Profile,
    InstancePreferences Preferences,
    LaunchSettings Effective,
    string[] Fields,
    bool Shared
)
{
    public string? OriginDirectory { get; init; }
};
public sealed record InstanceSnapshot(InstanceItem[] Items, LaunchSettings Global, string Architecture);
public sealed record InstanceImportCandidate(
    string Directory,
    string JsonPath,
    string Name,
    string GameVersion,
    string Loader,
    bool Supported,
    string Reason
)
{
    public override string ToString() => Name + (Supported
        ? " · 可导入"
        : " · " + Reason);
}

public sealed record InstanceScan(string Handle, InstanceImportCandidate[] Items);
public sealed record DeletedInstance(
    string Id,
    string Name,
    DateTimeOffset DeletedAt,
    string OriginalDirectory,
    string TrashDirectory,
    GameProfile Profile,
    bool DataMoved = true
);
public sealed record BackupVerification(int SchemaVersion, int Files, long Bytes, bool HasFileHashes)
{
    public override string ToString() => HasFileHashes
        ? $"校验通过 · {Files} 个文件 · SHA-256 完整性一致"
        : $"旧版备份 · {Files} 个文件 · 无逐文件校验清单";
}

public sealed record JavaRuntime(string Executable, string Architecture, int MajorVersion);
public sealed record DiscoveredJava(string Executable, JavaRuntime? Runtime, string? Error)
{
    public override string ToString() => Runtime is null
        ? Executable + " · " + Error
        : $"Java {Runtime.MajorVersion} · {Runtime.Architecture} · {Executable}";
}

public sealed record LaunchSettings
{
    public int MinimumMemoryMb { get; init; } = 256;
    public int MaximumMemoryMb { get; init; } = 2048;
    public int Width { get; init; } = 1100;
    public int Height { get; init; } = 700;
    public bool Fullscreen { get; init; }
    public bool DisableLwjglUnsafeAgent { get; init; }
    public string GameWindowTitle { get; init; } = "";
    public string WindowMode { get; init; } = "custom";
    public string ProcessPriority { get; init; } = "normal";
    public bool AutomaticMemory { get; init; }
    public string VersionInformation { get; init; } = "";
    public string GarbageCollector { get; init; } = "default";
    public string JavaExecutable { get; init; } = "";
    public string JvmArguments { get; init; } = "";
    public string GameArguments { get; init; } = "";
    public string ServerAddress { get; init; } = "";
    public string QuickPlayWorld { get; init; } = "";
}

public sealed record PackImportOptions(
    string? Minecraft = null,
    string Loader = "vanilla",
    string LoaderVersion = "",
    bool IncludeOptional = false,
    IReadOnlyDictionary<string, string>? ManualFiles = null,
    IReadOnlyCollection<string>? SelectedOptionalFiles = null
);
public sealed record PackOptionalFile(string Id, string Name, long? Size);
public sealed record PackArchiveFile(string Entry, string Target);
public sealed record CursePackFile(long ProjectId, long FileId, bool Required)
{
    public string Key => ProjectId + ":" + FileId;
    public string Page => $"https://www.curseforge.com/projects/{ProjectId}/files/{FileId}";

    public override string ToString() => $"{(Required
            ? "必需"
            : "可选")} · {ProjectId} / {FileId}";
}

public sealed record PortablePackPlan(
    string Format,
    string Name,
    string Version,
    string Minecraft,
    string Loader,
    string LoaderVersion,
    IReadOnlyList<PackArchiveFile> Overrides,
    IReadOnlyList<CursePackFile> CurseFiles,
    IReadOnlyList<string> Warnings
)
{
    public IReadOnlyList<PackOptionalFile> OptionalFiles { get; init; } = [];
}

public sealed record PackInspection(string Handle, PortablePackPlan Plan, string Digest);
public sealed record PackChanges(string[] Changed, string[] Deleted);
public sealed record PackExportOptions(
    string Name,
    string Version = "1.0.0",
    bool IncludeSaves = false,
    bool IncludeOptions = false,
    IReadOnlyList<string>? IncludedFolders = null
);
public enum ResourceKind
{
    Mod,
    Shader,
    ResourcePack,
    Modpack,
    DataPack
}

public sealed record LocalResourceInfo(
    string Path,
    ResourceKind Kind,
    string Name,
    string Version,
    bool Enabled,
    string[] ModIds,
    string? Error
)
{
    public override string ToString() => $"{(Enabled
            ? "●"
            : "○")} {Name} · {Version}";
}

public sealed record ResourceScan(LocalResourceInfo[] Items, string ModNameStyle);
public sealed record LocalResourceUpdate(string SelectionId, LocalResourceInfo Current, string Version)
{
    public override string ToString() => $"{Current.Name}：{Current.Version} → {Version}";
}

public sealed record WorldDirectory(string Name, string Path)
{
    public override string ToString() => Name;
}

public sealed record WorldDataPack(string Name, string Path, bool Enabled)
{
    public override string ToString() => (Enabled
        ? "● "
        : "○ ") + Name;
}
