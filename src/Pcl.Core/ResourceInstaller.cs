using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Pcl.Core;
// [实现] 数据约定：ManagedResource 汇集 ProjectId、VersionId、Name、Version、Kind、FileName、Sha1、Size、Enabled、InstalledAt、Dependencies，供该模块传递状态。
public sealed record ManagedResource(
    string ProjectId,
    string VersionId,
    string Name,
    string Version,
    ResourceKind Kind,
    string FileName,
    string Sha1,
    long Size,
    bool Enabled,
    DateTimeOffset InstalledAt,
    IReadOnlyList<ResourceDependency> Dependencies
)
{
    // [实现] ToString 的实现：计算并返回 $"{(Enabled ? "●" : "○")} {Name} · {Version}"。
    public override string ToString() => $"{(Enabled
            ? "●"
            : "○")}  {Name} · {Version}";
}

// 本地资源管理：扫描模组、光影和资源包，在文件变更前后校验依赖并保留事务恢复信息。
public sealed class ResourceInstaller(
    string gameDirectory,
    DownloadService downloads,
    string loader = "fabric",
    string gameVersion = MinecraftService.Version,
    string? libraryContextDirectory = null
)
{
    public string GameDirectory { get; } = Path.GetFullPath(gameDirectory);
    private string Manifest => Path.Combine(GameDirectory, ".pcl-resources.json");

    // [实现] ValidateModArchive 的实现：按 loader == "quilt" 分支处理；准备 var legacyForge = loader == "forge" && Version.TryParse(gameVersion, out var parsed) && parsed …；准备 var metadata = loader switch { "fabric" => "fabric.mod.json", "forge" => legacyForge ? "mcmod.i…。
    public static void ValidateModArchive(ZipArchive archive, string loader, string gameVersion = MinecraftService.Version)
    {
        if (loader == "quilt")
        {
            // [实现] 条件成立（loader == "quilt"）时：执行 QuiltMetadataJson.CheckProductionArchive(archive)；按 archive.GetEntry("quilt.mod.json")is null && archive.GetEntry("fabric.mod.json")is null 分支处理；结束当前过程。
            QuiltMetadataJson.CheckProductionArchive(archive);
            if (archive.GetEntry("quilt.mod.json")is null && archive.GetEntry("fabric.mod.json")is null)
                throw new InvalidDataException("文件缺少 Quilt / Fabric 模组元数据。");
            return;
        }

        var legacyForge = loader == "forge" && Version.TryParse(gameVersion, out var parsed) && parsed <= new Version(1, 12, 2);
        var metadata = loader switch
        {
            "fabric" => "fabric.mod.json",
            "forge" => legacyForge
                ? "mcmod.info"
                : "META-INF/mods.toml",
            "neoforge" => gameVersion is "1.20.1" or "1.20.2" or "1.20.3" or "1.20.4"
                ? "META-INF/mods.toml"
                : "META-INF/neoforge.mods.toml",
            "liteloader" => "litemod.json",
            _ => throw new NotSupportedException("尚不支持此模组加载器。")
        };
        if (archive.GetEntry(metadata)is null)
            throw new InvalidDataException($"文件缺少 {loader} 模组元数据：{metadata}");
    }

    // [实现] ModExtension 的实现：计算并返回 loader == "liteloader" ? ".litemod" : ".jar"。
    public static string ModExtension(string loader) => loader == "liteloader"
        ? ".litemod"
        : ".jar";
    // [实现] Folder 的实现：计算并返回 kind switch { ResourceKind.Mod => "mods", ResourceKind.Shader => "shaderpacks", ResourceKind.Re…。
    public static string Folder(ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => "mods",
        ResourceKind.Shader => "shaderpacks",
        ResourceKind.ResourcePack => "resourcepacks",
        _ => throw new NotSupportedException("整合包需要独立实例安装。")
    };
    // [实现] ValidateFileName 的实现：按 string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != Path.GetFileName(name) || nam… 分支处理。
    public static void ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name != Path.GetFileName(name) || name.IndexOfAny(
            new[]
            {
                '/',
                '\\',
                ':',
                '\0'
            }
        ) >= 0 || name.Any(char.IsControl))
            throw new InvalidDataException("资源文件名不安全。");
    }

    // [实现] SafeTarget 的实现：准备 var path = AppPaths.SafeChild(root, relative)；准备 var cursor = path；在 cursor is not null 条件下循环。
    public static string SafeTarget(string root, string relative)
    {
        var path = AppPaths.SafeChild(root, relative);
        var cursor = path;
        while (cursor is not null)
        {
            // [实现] 循环满足 cursor is not null 时：按 (File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttribut… 分支处理；按 cursor == Path.GetFullPath(root) 分支处理；执行 cursor = Path.GetDirectoryName(cursor)。
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("资源目录含符号链接：" + cursor);
            if (cursor == Path.GetFullPath(root))
                break;
            cursor = Path.GetDirectoryName(cursor);
        }

        return path;
    }

    // [实现] FilePath 的实现：计算并返回 SafeTarget( GameDirectory, Folder(item.Kind) + "/" + item.FileName + (item.Enabled ? "" : ".dis…。
    private string FilePath(ManagedResource item) => SafeTarget(
        GameDirectory,
        Folder(item.Kind) + "/" + item.FileName + (item.Enabled
            ? ""
            : ".disabled")
    );
    // [实现] List 的实现：准备 var recorded = File.Exists(Manifest) ? JsonConvert.DeserializeObject<List<ManagedResource>>( Fi…；遍历 new LocalResourceService(GameDirectory, loader, gameVersion).Scan() .Where( x => x.Managed is n… 处理 external；返回 recorded。
    public IReadOnlyList<ManagedResource> List()
    {
        var recorded = File.Exists(Manifest)
            ? JsonConvert.DeserializeObject<List<ManagedResource>>(
                File.ReadAllText(Manifest)
            ) ?? throw new InvalidDataException("资源记录损坏。")
            : new List<ManagedResource>();
        // External imports and pack files must participate in dependency checks, even before being adopted.
        foreach (var external in new LocalResourceService(GameDirectory, loader, gameVersion).Scan()
            .Where(
                x => x.Managed is null && x.Error is null
            ))
        {
            // [实现] 对 new LocalResourceService(GameDirectory, loader, gameVersion).Scan() .Where( x => x.Managed is n… 中的每一项：准备 var name = Path.GetFileName( external.Enabled ? external.Path : external.Path[..^9] )；执行 recorded.Add( new( "local-scan-" + external.Kind + "-" + external.Sha1, external.Sha1, external…。
            var name = Path.GetFileName(
                external.Enabled
                    ? external.Path
                    : external.Path[..^9]
            );
            recorded.Add(
                new(
                    "local-scan-" + external.Kind + "-" + external.Sha1,
                    external.Sha1,
                    external.Name,
                    external.Version,
                    external.Kind,
                    name,
                    external.Sha1,
                    external.Size,
                    external.Enabled,
                    File.GetLastWriteTimeUtc(external.Path),
                    Array.Empty<ResourceDependency>()
                )
            );
        }

        return recorded;
    }

    // [实现] Lease 的实现：执行 Directory.CreateDirectory(GameDirectory)；执行 SafeTarget(GameDirectory, ".pcl-resources.json")；返回 new FileStream( SafeTarget(GameDirectory, ".resources.lock"), FileMode.OpenOrCreate, FileAccess…。
    private FileStream Lease()
    {
        Directory.CreateDirectory(GameDirectory);
        var lease = new FileStream(
            SafeTarget(GameDirectory, ".resources.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        try
        {
            // 所有资源写入都先收尾中断事务，避免后来操作破坏恢复前提。
            new ActivationTransaction(GameDirectory).Recover();
            new ShaderRemovalTransaction(GameDirectory).Recover();
            IrisEnvironmentService.RecoverUnderLease(GameDirectory);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    // [实现] ValidateConflicts 的实现：准备 var all = retained.Where( x => !plan.Releases.Any( r => r.ProjectId == x.ProjectId ) ) .Select(…；遍历 all 处理 item。
    public static void ValidateConflicts(IEnumerable<ManagedResource> retained, ResourceInstallPlan plan)
    {
        var all = retained.Where(
            x => !plan.Releases.Any(
                r => r.ProjectId == x.ProjectId
            )
        )
            .Select(
                x => (x.ProjectId, Id: x.VersionId, x.Name, x.Dependencies)
            )
            .Concat(
                plan.Releases.Select(
                    x => (x.ProjectId, Id: x.Id, x.Name, x.Dependencies)
                )
            )
            .ToArray();
        foreach (var item in all)
            foreach (var dependency in item.Dependencies)
            {
                // [实现] 对 item.Dependencies 中的每一项：按 dependency.Type == "incompatible" && all.Any( x => dependency.VersionId is not null ? x.Id == d… 分支处理；按 dependency.Type == "required" && !all.Any( x => (dependency.ProjectId is null || x.ProjectId ==… 分支处理。
                if (dependency.Type == "incompatible" && all.Any(
                    x => dependency.VersionId is not null
                        ? x.Id == dependency.VersionId
                        : x.ProjectId == dependency.ProjectId
                ))
                    throw new InvalidDataException("模组冲突：" + item.Name);
                if (dependency.Type == "required" && !all.Any(
                    x => (dependency.ProjectId is null || x.ProjectId == dependency.ProjectId) && (dependency.VersionId is null || x.Id == dependency.VersionId)
                ))
                    throw new InvalidDataException("缺少或不兼容的前置：" + item.Name);
            }
    }

    // [实现] 功能：下载并安装受管理资源。实现：先验证声明冲突和发布信息，校验缓存与归档结构，再检查同名文件和外部修改，最后提交带恢复能力的文件事务。
    public async Task<IReadOnlyList<ManagedResource>> InstallAsync(
        ResourceInstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        string? owner = null
    )
    {
        if (plan.Kind == ResourceKind.Shader && loader != "fabric")
            throw new NotSupportedException("当前光影方案需要 Fabric / Iris 实例。");
        using var lease = Lease();
        var existing = List();
        ValidateConflicts(existing, plan);
        // 用途在资源变更前持久化；安装失败最多留下保守保留理由，不能导致误删共享组件。
        ResourceOwnership.Claim(
            GameDirectory,
            plan.Releases.Select(
                x => x.ProjectId
            ),
            owner ?? "resource:" + plan.RootProjectId
        );
        var cache = SafeTarget(GameDirectory, ".pcl-downloads");
        Directory.CreateDirectory(cache);
        var incoming = new List<ManagedResource>();
        foreach (var release in plan.Releases)
        {
            // [实现] 对 plan.Releases 中的每一项：准备 var file = release.File；执行 ValidateFileName(file.Name)；按 !Regex.IsMatch(file.Sha1, "^[0-9a-f]{40}$") 分支处理。
            var file = release.File;
            ValidateFileName(file.Name);
            if (!Regex.IsMatch(file.Sha1, "^[0-9a-f]{40}$"))
                throw new InvalidDataException("无效 SHA-1。");
            if (Path.GetExtension(file.Name)
                .ToLowerInvariant() != (plan.Kind == ResourceKind.Mod
                ? ModExtension(loader)
                : ".zip"))
                throw new InvalidDataException("资源类型与文件扩展名不匹配。");
            var cached = SafeTarget(cache, file.Sha1);
            progress?.Report(
                new(
                    "下载 " + release.Name,
                    incoming.Count,
                    plan.Releases.Count,
                    incoming.Sum(
                        x => x.Size
                    ),
                    plan.Bytes
                )
            );
            try
            {
                await downloads.DownloadAsync(
                    new(file.Url, cached, file.Size, file.Sha1),
                    ct
                );
            }
            catch (InvalidDataException ex)
            {
                // 传输完整性失败不是版本不兼容；让联合求解停止，不能悄悄尝试旧版依赖。
                throw new IOException("资源下载校验失败：" + file.Name, ex);
            }

            // Packages are kept zipped; verify they are readable archives, without executing or extracting them.
            using (var archive = ZipFile.OpenRead(cached))
            {
                // [实现] 本段流程：按 archive.Entries.Count == 0 分支处理；按 plan.Kind == ResourceKind.Mod 分支处理；按 plan.Kind == ResourceKind.Shader && !archive.Entries.Any( x => x.FullName.StartsWith("shaders/"… 分支处理。
                if (archive.Entries.Count == 0)
                    throw new InvalidDataException("空资源包。");
                if (plan.Kind == ResourceKind.Mod)
                    ValidateModArchive(archive, loader, gameVersion);
                if (plan.Kind == ResourceKind.Shader && !archive.Entries.Any(
                    x => x.FullName.StartsWith("shaders/", StringComparison.Ordinal)
                ))
                    throw new InvalidDataException("光影包中没有 shaders 目录。");
                if (plan.Kind == ResourceKind.ResourcePack)
                {
                    // [实现] 条件成立（plan.Kind == ResourceKind.ResourcePack）时：准备 var info = archive.GetEntry("pack.mcmeta") ?? throw new InvalidDataException("资源包中没有 pack.mcmet…；按 info.Length > 2 * 1024 * 1024 分支处理；准备 var reader = new StreamReader( info.Open() )。
                    var info = archive.GetEntry("pack.mcmeta") ?? throw new InvalidDataException("资源包中没有 pack.mcmeta。");
                    if (info.Length > 2 * 1024 * 1024)
                        throw new InvalidDataException("资源包元数据过大。");
                    using var reader = new StreamReader(
                        info.Open()
                    );
                    Newtonsoft.Json.Linq.JObject.Parse(
                        reader.ReadToEnd()
                    );
                }
            }

            incoming.Add(
                new(
                    release.ProjectId,
                    release.Id,
                    release.DisplayName,
                    release.Number,
                    plan.Kind,
                    LocalResourcePreferences.FileName(
                        file.Name,
                        release.DisplayName,
                        downloads.Tasks?.Options.FileNameStyle ?? "original"
                    ),
                    file.Sha1,
                    file.Size,
                    true,
                    DateTimeOffset.UtcNow,
                    release.Dependencies
                )
            );
        }

        ct.ThrowIfCancellationRequested();
        if (incoming.GroupBy(
            x => (x.Kind, x.FileName.ToUpperInvariant())
        )
            .Any(
                x => x.Count() > 1
            ))
            throw new InvalidDataException("资源文件名冲突。");
        var replaced = existing.Where(
            x => incoming.Any(
                n => n.ProjectId == x.ProjectId
            )
        )
            .ToArray();
        foreach (var item in incoming)
        {
            // [实现] 对 incoming 中的每一项：准备 var target = FilePath(item)；按 File.Exists(target) && !replaced.Any( x => FilePath(x) == target ) 分支处理。
            var target = FilePath(item);
            if (File.Exists(target) && !replaced.Any(
                x => FilePath(x) == target
            ))
                throw new IOException("同名文件已存在，请先在管理页处理：" + item.FileName);
        }

        foreach (var old in replaced)
            VerifyUnmodified(old);
        var next = existing.Except(replaced)
            .Concat(incoming)
            .ToArray();
        Commit(replaced, incoming, next, cache, ct);
        progress?.Report(
            new("安装完成", incoming.Count, incoming.Count, plan.Bytes, plan.Bytes)
        );
        return incoming;
    }

    // [实现] VerifyUnmodified 的实现：准备 var path = FilePath(item)；按 File.Exists(path) && !DownloadService.IsValid( new( new Uri("https://localhost/unused"), path, … 分支处理。
    private void VerifyUnmodified(ManagedResource item)
    {
        var path = FilePath(item);
        if (File.Exists(path) && !DownloadService.IsValid(
            new(
                new Uri("https://localhost/unused"),
                path,
                item.Size,
                item.Sha1
            )
        ))
            throw new IOException("文件已被外部修改，未覆盖：" + item.FileName);
    }

    // [实现] Commit 的实现：准备 var backup = SafeTarget( GameDirectory, ".pcl-backups/" + Guid.NewGuid() .ToString("N") )；执行 Directory.CreateDirectory(backup)；准备 var moved = new List<(string Target, string Backup)>()。
    private void Commit(
        IReadOnlyList<ManagedResource> removed,
        IReadOnlyList<ManagedResource> incoming,
        IReadOnlyList<ManagedResource> next,
        string cache,
        CancellationToken ct = default
    )
    {
        var backup = SafeTarget(
            GameDirectory,
            ".pcl-backups/" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(backup);
        var moved = new List<(string Target, string Backup)>();
        var created = new List<string>();
        try
        {
            // [实现] 在异常保护范围内：遍历 removed 处理 old；遍历 incoming 处理 item；按 removed.Any( x => x.Kind == ResourceKind.Mod ) || incoming.Any( x => x.Kind == ResourceKind.Mod… 分支处理。
            foreach (var old in removed)
            {
                // [实现] 对 removed 中的每一项：准备 var path = FilePath(old)；按 !File.Exists(path) 分支处理；准备 var b = Path.Combine( backup, moved.Count.ToString() )。
                var path = FilePath(old);
                if (!File.Exists(path))
                    continue;
                var b = Path.Combine(
                    backup,
                    moved.Count.ToString()
                );
                File.Move(path, b);
                moved.Add((path, b));
            }

            foreach (var item in incoming)
            {
                // [实现] 对 incoming 中的每一项：准备 var target = FilePath(item)；执行 Directory.CreateDirectory( Path.GetDirectoryName(target)! )；执行 File.Copy( SafeTarget(cache, item.Sha1), target, false )。
                var target = FilePath(item);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(target)!
                );
                File.Copy(
                    SafeTarget(cache, item.Sha1),
                    target,
                    false
                );
                created.Add(target);
            }

            if (removed.Any(
                x => x.Kind == ResourceKind.Mod
            ) || incoming.Any(
                x => x.Kind == ResourceKind.Mod
            ))
                new LocalResourceService(GameDirectory, loader, gameVersion, libraryContextDirectory).ValidateEnabledResources(ct);
            JsonFiles.Write(
                Path.Combine(backup, "rollback.json"),
                moved.Select(
                    x => new
                    {
                        target = Path.GetRelativePath(GameDirectory, x.Target),
                        backup = Path.GetFileName(x.Backup)
                    }
                )
            );
            JsonFiles.Write(Manifest, next);
            ResourceHistory.Record(
                GameDirectory,
                incoming.Count > 0
                    ? "安装 / 更新"
                    : "移至备份",
                incoming.Count > 0
                    ? incoming.Select(
                        x => x.FileName
                    )
                    : removed.Select(
                        x => x.FileName
                    ),
                backup
            );
        }
        catch
        {
            // [实现] 捕获 空 后：遍历 created 处理 path；遍历 moved 处理 move；重新抛出当前异常。
            foreach (var path in created)
                File.Delete(path);
            foreach (var move in moved)
                File.Move(move.Backup, move.Target, true);
            throw;
        }
    }

    // [实现] SetEnabled 的实现：准备 var lease = Lease()；准备 var all = List() .ToArray()；准备 var old = all.Single( x => x.ProjectId == projectId )。
    public void SetEnabled(string projectId, bool enabled)
    {
        using var lease = Lease();
        var all = List()
            .ToArray();
        var old = all.Single(
            x => x.ProjectId == projectId
        );
        if (old.Enabled == enabled)
            return;
        if (!enabled && all.Any(
            x => x.Enabled && x.Dependencies.Any(
                d => d.Type == "required" && (d.ProjectId == projectId || (d.ProjectId is null && d.VersionId == old.VersionId))
            )
        ))
            throw new InvalidOperationException("其他已启用模组依赖此文件，请先禁用它们。");
        var next = old with
        {
            Enabled = enabled
        };
        if (enabled && old.Dependencies.Any(
            d => d.Type == "required" && !all.Any(
                x => x.Enabled && (d.ProjectId is null || x.ProjectId == d.ProjectId) && (d.VersionId is null || x.VersionId == d.VersionId)
            )
        ))
            throw new InvalidOperationException("请先启用所需前置。");
        File.Move(
            FilePath(old),
            FilePath(next),
            false
        );
        try
        {
            // [实现] 在异常保护范围内：执行 new LocalResourceService(GameDirectory, loader, gameVersion, libraryContextDirectory).ValidateE…；执行 JsonFiles.Write( Manifest, all.Select( x => x.ProjectId == projectId ? next : x ) )；执行 ResourceHistory.Record( GameDirectory, enabled ? "启用" : "停用", [ old.FileName ] )。
            new LocalResourceService(GameDirectory, loader, gameVersion, libraryContextDirectory).ValidateEnabledResources();
            JsonFiles.Write(
                Manifest,
                all.Select(
                    x => x.ProjectId == projectId
                        ? next
                        : x
                )
            );
            ResourceHistory.Record(
                GameDirectory,
                enabled
                    ? "启用"
                    : "停用",
                [
                    old.FileName
                ]
            );
        }
        catch
        {
            // [实现] 捕获 空 后：执行 File.Move( FilePath(next), FilePath(old) )；重新抛出当前异常。
            File.Move(
                FilePath(next),
                FilePath(old)
            );
            throw;
        }
    }

    // [实现] Remove 的实现：准备 var lease = Lease()；准备 var all = List()；准备 var old = all.Single( x => x.ProjectId == projectId )。
    public void Remove(string projectId, CancellationToken ct = default, string? operationId = null)
    {
        using var lease = Lease();
        var removal = new ShaderRemovalTransaction(GameDirectory);
        if (removal.Completed(operationId))
        {
            return;
        }

        var all = List();
        var old = all.Single(
            x => x.ProjectId == projectId
        );
        if (all.Any(
            x => x.ProjectId != projectId && x.Dependencies.Any(
                d => d.Type == "required" && (d.ProjectId == projectId || (d.ProjectId is null && d.VersionId == old.VersionId))
            )
        ))
            throw new InvalidOperationException("其他模组依赖此文件，请先移除它们。");
        VerifyUnmodified(old);
        if (old.Kind == ResourceKind.Shader)
        {
            removal.RemoveManaged(
                [
                    old
                ],
                ct,
                operationId
            );
            return;
        }

        ct.ThrowIfCancellationRequested();
        Commit(
            new[]
            {
                old
            },
            Array.Empty<ManagedResource>(),
            all.Where(
                x => x.ProjectId != projectId
            )
                .ToArray(),
            ""
        );
    }

    // [实现] Activate 的实现：准备 var lease = Lease()；准备 var item = List() .Single( x => x.ProjectId == projectId )；按 !item.Enabled || !File.Exists( FilePath(item) ) 分支处理。
    public void Activate(string projectId)
    {
        using var lease = Lease();
        new ActivationTransaction(GameDirectory).Recover();
        var item = List()
            .Single(
                x => x.ProjectId == projectId
            );
        if (!item.Enabled || !File.Exists(
            FilePath(item)
        ))
            throw new InvalidOperationException("资源未启用或文件缺失。");
        if (item.Kind == ResourceKind.Shader)
        {
            // [实现] 条件成立（item.Kind == ResourceKind.Shader）时：按 loader != "fabric" 分支处理；按 OperatingSystem.IsMacOS() 分支处理；准备 var config = SafeTarget(GameDirectory, "config/iris.properties")。
            if (loader != "fabric")
                throw new NotSupportedException("当前光影方案需要 Fabric / Iris 实例。");
            if (OperatingSystem.IsMacOS())
                MacShaderCompatibility.EnsureCanActivate(
                    FilePath(item)
                );
            var config = SafeTarget(GameDirectory, "config/iris.properties");
            Directory.CreateDirectory(
                Path.GetDirectoryName(config)!
            );
            var lines = File.Exists(config)
                ? File.ReadAllLines(config)
                    .Where(
                        x => !x.StartsWith("shaderPack=") && !x.StartsWith("enableShaders=")
                    )
                    .ToList()
                : new List<string>();
            // Java Properties uses escapes for non-ASCII file names and separators.
            string Escape(string value) => string.Concat(
                value.Select(
                    c => c > 127
                        ? "\\u" + ((int)c).ToString("x4")
                        : c is '\\' or '=' or ':'
                            ? "\\" + c
                            : c.ToString()
                )
            );
            lines.Add(
                "shaderPack=" + Escape(item.FileName)
            );
            lines.Add("enableShaders=true");
            WriteActivation(
                config,
                string.Join(Environment.NewLine, lines) + Environment.NewLine
            );
        }
        else if (item.Kind == ResourceKind.ResourcePack)
        {
            // [实现] 条件成立（item.Kind == ResourceKind.ResourcePack）时：准备 var config = SafeTarget(GameDirectory, "options.txt")；准备 var lines = File.Exists(config) ? File.ReadAllLines(config) .ToList() : new List<string>()；准备 var previous = lines.FirstOrDefault( x => x.StartsWith("resourcePacks:") )。
            var config = SafeTarget(GameDirectory, "options.txt");
            var lines = File.Exists(config)
                ? File.ReadAllLines(config)
                    .ToList()
                : new List<string>();
            var previous = lines.FirstOrDefault(
                x => x.StartsWith("resourcePacks:")
            );
            var packs = previous is null
                ? new List<string>
                {
                    "vanilla"
                }

                : JsonConvert.DeserializeObject<List<string>>(previous[14..]) ?? new List<string>
                {
                    "vanilla"
                };
            var value = "file/" + item.FileName;
            if (!packs.Contains(value))
                packs.Add(value);
            lines.RemoveAll(
                x => x.StartsWith("resourcePacks:")
            );
            lines.Add(
                "resourcePacks:" + JsonConvert.SerializeObject(packs)
            );
            WriteActivation(
                config,
                string.Join(Environment.NewLine, lines) + Environment.NewLine
            );
        }
        else
            throw new InvalidOperationException("此操作用于光影或资源包。");
        ResourceHistory.Record(
            GameDirectory,
            "应用",
            [
                item.FileName
            ]
        );
    }

    // 配置变更先写持久化事务，再原子替换；资源锁由 Activate / UndoActivation 持有。
    private void WriteActivation(string target, string text)
    {
        new ActivationTransaction(GameDirectory).Apply(
            Path.GetRelativePath(GameDirectory, target)
                .Replace('\\', '/'),
            text
        );
    }

    public void UndoActivation(CancellationToken ct = default, string? operationId = null)
    {
        using var lease = Lease();
        new ActivationTransaction(GameDirectory).Undo(ct, operationId);
    }

    // [实现] ImportAsync 的实现：按 kind == ResourceKind.Modpack 分支处理；准备 var name = Path.GetFileName(file)；执行 ValidateFileName(name)。
    public async Task<ManagedResource> ImportAsync(string file, ResourceKind kind, CancellationToken ct = default)
    {
        if (kind == ResourceKind.Modpack)
            throw new NotSupportedException("整合包导入需要独立实例。");
        var name = Path.GetFileName(file);
        ValidateFileName(name);
        var bytes = await File.ReadAllBytesAsync(file, ct);
        var hash = Convert.ToHexString(
            SHA1.HashData(bytes)
        )
            .ToLowerInvariant();
        var cache = SafeTarget(GameDirectory, ".pcl-downloads");
        Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(
            SafeTarget(cache, hash),
            bytes,
            ct
        );
        var projectId = "local-" + kind.ToString()
            .ToLowerInvariant() + "-" + hash;
        var displayName = Path.GetFileNameWithoutExtension(name);
        var displayVersion = "本地";
        var dependencies = new List<ResourceDependency>();
        if (kind == ResourceKind.Mod && loader == "fabric")
        {
            // Use the same final-set validator as remote installs. Do not duplicate raw metadata checks here.
            var imported = FabricModReader.Read(
                SafeTarget(cache, hash),
                GameDirectory
            );
            var main = imported[0];
            projectId = "local-" + main.Id;
            displayName = main.Id;
            displayVersion = main.Version;
        }

        var release = new ResourceRelease(
            "local-" + hash,
            projectId,
            displayName,
            displayVersion,
            "release",
            DateTimeOffset.UtcNow,
            new[]
            {
                gameVersion
            },
            new[]
            {
                loader
            },
            new(
                name,
                new Uri("https://localhost/unused"),
                bytes.LongLength,
                hash
            ),
            dependencies
        );
        return (await InstallAsync(
            new(
                kind,
                release.ProjectId,
                new[]
                {
                    release
                }
            ),
            ct: ct
        )).Single();
    }
}
