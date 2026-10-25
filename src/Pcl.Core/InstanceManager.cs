using System.IO.Compression;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：InstanceImportCandidate 汇集 Directory、JsonPath、Name、GameVersion、Loader、Supported、Reason，供该模块传递状态。
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
    // [实现] ToString 的实现：计算并返回 Name + (Supported ? " · 可导入" : " · " + Reason)。
    public override string ToString() => Name + (Supported
        ? " · 可导入"
        : " · " + Reason);
}

// [实现] 数据约定：DeletedInstance 汇集 Id、Name、DeletedAt、OriginalDirectory、TrashDirectory、Profile、DataMoved，供该模块传递状态。
public sealed record DeletedInstance(
    string Id,
    string Name,
    DateTimeOffset DeletedAt,
    string OriginalDirectory,
    string TrashDirectory,
    GameProfile Profile,
    bool DataMoved = true
);
// 实例管理：读取实例记录和版本配置，执行隔离、备份、恢复与组件变更并校验结果。
public sealed class InstanceManager(MinecraftService service)
{
    public LauncherSettingsStore Settings { get; } = new(service.Paths);
    private string Instances => Path.Combine(service.Paths.Root, "instances");
    private string Trash => Path.Combine(service.Paths.Root, "instance-trash");

    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher_accounts.json",
        "launcher_profiles.json",
        "accounts.json",
        "accounts",
        ".DS_Store",
        ".install.lock",
        "pcl-backup.json"
    };
    private static readonly HashSet<string> SharedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets",
        "libraries",
        "versions",
        "runtime"
    };
    // [实现] List 的实现：计算并返回 InstanceRegistry.List(service, includeHidden)。
    public IReadOnlyList<GameProfile> List(bool includeHidden = true) => InstanceRegistry.List(service, includeHidden);
    /// <summary>从实际存在的实例收集分组，包含隐藏项；空分组单独由界面显示，不产生虚构目录。</summary>
    public IReadOnlyList<string> Groups() => List()
        .Select(
            profile => Settings.Preferences(profile.GameDirectory)
                .Group
        )
        .Where(
            group => group.Length > 0
        )
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(
            group => group,
            StringComparer.CurrentCultureIgnoreCase
        )
        .ToArray();
    // [实现] Find 的实现：计算并返回 List(includeHidden) .Where( x => string.IsNullOrWhiteSpace(loader) || x.Loader == loader ) .Whe…。
    public IReadOnlyList<GameProfile> Find(
        string? query = null,
        string? loader = null,
        bool favoritesOnly = false,
        bool hiddenOnly = false,
        bool includeHidden = true,
        string? group = null
    ) => List(includeHidden)
        .Where(
            x => string.IsNullOrWhiteSpace(loader) || x.Loader == loader
        )
        .Where(
            x => group is null || string.Equals(
                Settings.Preferences(x.GameDirectory)
                    .Group,
                LauncherSettingsStore.NormalizeGroup(group),
                StringComparison.OrdinalIgnoreCase
            )
        )
        .Where(
            x => !favoritesOnly || Settings.Preferences(x.GameDirectory)
                .Favorite
        )
        .Where(
            x => !hiddenOnly || Settings.Preferences(x.GameDirectory)
                .Hidden
        )
        .Where(
            x => string.IsNullOrWhiteSpace(query) || new[]
            {
                x.Name,
                x.Id,
                x.GameVersion,
                x.Loader,
                Settings.Preferences(x.GameDirectory)
                    .Group
            }.Any(
                text => text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            )
        )
        .ToArray();
    // [实现] Scan 的实现：执行 directory = Path.GetFullPath(directory)；执行 RequirePlainDirectory(directory)；准备 var result = new List<InstanceImportCandidate>()。
    public IReadOnlyList<InstanceImportCandidate> Scan(string directory)
    {
        directory = Path.GetFullPath(directory);
        RequirePlainDirectory(directory);
        var result = new List<InstanceImportCandidate>();
        if (File.Exists(
            Path.Combine(directory, "profile.json")
        ))
        {
            // [实现] 条件成立（File.Exists( Path.Combine(directory, "profile.json") )）时：执行受异常处理保护的操作；返回 result。
            try
            {
                // [实现] 在异常保护范围内：准备 var receipt = JsonFiles.Read( Path.Combine(directory, "profile.json") )；准备 var loader = (string? )receipt["loader"] ?? "fabric"；准备 var version = (string? )receipt["gameVersion"] ?? ""。
                var receipt = JsonFiles.Read(
                    Path.Combine(directory, "profile.json")
                );
                var loader = (string? )receipt["loader"] ?? "fabric";
                var version = (string? )receipt["gameVersion"] ?? "";
                VersionCatalog.ValidateId(version);
                var metadata = Path.Combine(directory, "version.json");
                var valid = File.Exists(metadata) && loader is "vanilla" or "fabric" or "quilt" or "forge" or "neoforge" or "liteloader" or "optifine";
                result.Add(
                    new(
                        directory,
                        metadata,
                        (string? )receipt["displayName"] ?? Path.GetFileName(directory),
                        version,
                        loader,
                        valid,
                        valid
                            ? "本启动器实例；将复制到独立目录"
                            : "缺少版本文件或加载器尚未适配"
                    )
                );
            }
            catch (Exception ex)when (ex is IOException or Newtonsoft.Json.JsonException or ArgumentException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 result.Add( new( directory, "", Path.GetFileName(directory), "", "unknown", false, ex.Message )…。
                result.Add(
                    new(
                        directory,
                        "",
                        Path.GetFileName(directory),
                        "",
                        "unknown",
                        false,
                        ex.Message
                    )
                );
            }

            return result;
        }

        var versions = Path.Combine(directory, "versions");
        foreach (var folder in Directory.Exists(versions)
            ? Directory.EnumerateDirectories(versions)
                .Take(1000)
            : new[]
            {
                directory
            }

        )
        {
            // [实现] 对 Directory.Exists(versions) ? Directory.EnumerateDirectories(versions) .Take(1000) : new[] { dir… 中的每一项：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：执行 RequirePlainDirectory(folder)；准备 var path = Path.Combine( folder, Path.GetFileName(folder) + ".json" )；按 !File.Exists(path) 分支处理。
                RequirePlainDirectory(folder);
                var path = Path.Combine(
                    folder,
                    Path.GetFileName(folder) + ".json"
                );
                if (!File.Exists(path))
                    continue;
                var metadata = JsonFiles.Read(path);
                var version = (string? )metadata["id"] ?? "";
                VersionCatalog.ValidateId(version);
                var supported = metadata["inheritsFrom"] is null && (string? )metadata["mainClass"] == "net.minecraft.client.main.Main" && metadata["downloads"]?["client"] is JObject;
                result.Add(
                    new(
                        Directory.Exists(versions)
                            ? directory
                            : Path.GetDirectoryName(
                                Path.GetDirectoryName(folder)
                            )!,
                        path,
                        version,
                        version,
                        "vanilla",
                        supported,
                        supported
                            ? "原版数据将复制，客户端与依赖按官方清单重新校验"
                            : "第三方继承版本或修改客户端：请使用整合包导入，或重新安装对应加载器"
                    )
                );
            }
            catch (Exception ex)when (ex is IOException or Newtonsoft.Json.JsonException or ArgumentException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 result.Add( new( directory, "", Path.GetFileName(folder), "", "unknown", false, ex.Message ) )。
                result.Add(
                    new(
                        directory,
                        "",
                        Path.GetFileName(folder),
                        "",
                        "unknown",
                        false,
                        ex.Message
                    )
                );
            }
        }

        return result;
    }

    // [实现] ImportAsync 的实现：按 !candidate.Supported 分支处理；准备 var rescanned = Scan(candidate.Directory) .FirstOrDefault( x => Path.GetFullPath(x.JsonPath) ==…；按 rescanned is null || !rescanned.Supported || rescanned.GameVersion != candidate.GameVersion 分支处理。
    public async Task<GameProfile> ImportAsync(
        InstanceImportCandidate candidate,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (!candidate.Supported)
            throw new NotSupportedException(candidate.Reason);
        var rescanned = Scan(candidate.Directory)
            .FirstOrDefault(
                x => Path.GetFullPath(x.JsonPath) == Path.GetFullPath(candidate.JsonPath)
            );
        if (rescanned is null || !rescanned.Supported || rescanned.GameVersion != candidate.GameVersion)
            throw new InvalidDataException("导入目录已变化，请重新扫描。");
        using var parent = service.ForVersion(candidate.GameVersion);
        await parent.InstallAsync(progress, ct);
        var source = new GameProfile(
            "import",
            candidate.Name,
            candidate.GameVersion,
            candidate.Loader,
            candidate.Directory,
            candidate.JsonPath
        );
        return await CloneCoreAsync(source, candidate.Name, true, ct, progress);
    }

    // [实现] CopyAsync 的实现：计算并返回 CloneCoreAsync(source, name, false, ct)。
    public Task<GameProfile> CopyAsync(GameProfile source, string name, CancellationToken ct = default) => CloneCoreAsync(source, name, false, ct);
    // [实现] CloneCoreAsync 的实现：按 !Directory.Exists(source.GameDirectory) && !importing 分支处理；执行 RequirePlainDirectory(source.GameDirectory)；执行 Directory.CreateDirectory(Instances)。
    private async Task<GameProfile> CloneCoreAsync(
        GameProfile source,
        string name,
        bool importing,
        CancellationToken ct,
        IProgress<InstallProgress>? progress = null
    )
    {
        if (!Directory.Exists(source.GameDirectory) && !importing)
        {
            // [实现] 条件成立（!Directory.Exists(source.GameDirectory) && !importing）时：执行 RequireManaged(source)；执行 Directory.CreateDirectory(source.GameDirectory)。
            RequireManaged(source);
            Directory.CreateDirectory(source.GameDirectory);
        }

        RequirePlainDirectory(source.GameDirectory);
        Directory.CreateDirectory(Instances);
        var id = "copy-" + Guid.NewGuid()
            .ToString("N");
        var stage = Path.Combine(Instances, ".pending-" + id);
        var target = Path.Combine(Instances, id);
        Directory.CreateDirectory(stage);
        try
        {
            // [实现] 在异常保护范围内：执行 await Task.Run( () => CopyData( source.GameDirectory, stage, Shared(source) || (source.Loader =…；准备 JObject metadata；按 source.Loader == "vanilla" 分支处理。
            await Task.Run(
                () => CopyData(
                    source.GameDirectory,
                    stage,
                    Shared(source) || (source.Loader == "vanilla" && !File.Exists(
                        Path.Combine(source.GameDirectory, "profile.json")
                    )),
                    ct
                ),
                ct
            );
            JObject metadata;
            if (source.Loader == "vanilla")
            {
                // [实现] 条件成立（source.Loader == "vanilla"）时：准备 var parent = service.ForVersion(source.GameVersion)；执行 metadata = JsonFiles.Read( parent.LoadInstalled() .JsonPath )。
                using var parent = service.ForVersion(source.GameVersion);
                metadata = JsonFiles.Read(
                    parent.LoadInstalled()
                        .JsonPath
                );
            }
            else
                metadata = JsonFiles.Read(source.JsonPath);
            metadata["id"] = id;
            JsonFiles.Write(
                Path.Combine(stage, "version.json"),
                metadata
            );
            var sourceReceipt = Path.Combine(source.GameDirectory, "profile.json");
            var receipt = File.Exists(sourceReceipt)
                ? JsonFiles.Read(sourceReceipt)
                : new JObject();
            receipt["id"] = id;
            receipt["displayName"] = name;
            receipt["gameVersion"] = source.GameVersion;
            receipt["loader"] = source.Loader;
            receipt["loaderVersion"] ??= "";
            receipt["copiedAt"] = DateTimeOffset.UtcNow;
            JsonFiles.Write(
                Path.Combine(stage, "profile.json"),
                receipt
            );
            var staged = new GameProfile(
                id,
                name,
                source.GameVersion,
                source.Loader,
                stage,
                Path.Combine(stage, "version.json")
            );
            if (importing)
                await RepairAsync(staged, progress, ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            var result = staged with
            {
                GameDirectory = target,
                JsonPath = Path.Combine(target, "version.json")
            };
            Settings.SavePreferences(
                target,
                Settings.Preferences(source.GameDirectory)with
                {
                    Name = name,
                    Hidden = false,
                    Deleted = false
                }
            );
            Settings.SavePatch(
                target,
                Settings.Patch(source.GameDirectory)
            );
            return result;
        }
        catch
        {
            // [实现] 捕获 空 后：按 Directory.Exists(stage) 分支处理；重新抛出当前异常。
            if (Directory.Exists(stage))
                Directory.Delete(stage, true);
            throw;
        }
    }

    // [实现] RepairAsync 的实现：准备 var parent = service.ForVersion(profile.GameVersion)；执行 await parent.InstallAsync(progress, ct)；按 !File.Exists( Path.Combine(profile.GameDirectory, "profile.json") ) 分支处理。
    public async Task RepairAsync(
        GameProfile profile,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        using var parent = service.ForVersion(profile.GameVersion);
        await parent.InstallAsync(progress, ct);
        if (!File.Exists(
            Path.Combine(profile.GameDirectory, "profile.json")
        ))
            return;
        var installed = InstanceRegistry.Load(service, profile);
        foreach (var item in installed.Libraries)
        {
            // [实现] 对 installed.Libraries 中的每一项：按 DownloadService.IsValid(item) 分支处理；按 item.Url.Scheme != "https" 分支处理；执行 await service.Downloads.DownloadAsync(item, ct)。
            if (DownloadService.IsValid(item))
                continue;
            if (item.Url.Scheme != "https")
                throw new InvalidDataException(
                    "安装器生成文件缺失，需重新运行该加载器安装器：" + Path.GetFileName(item.Path)
                );
            await service.Downloads.DownloadAsync(item, ct);
        }

        var metadata = JsonFiles.Read(installed.JsonPath);
        if (metadata["downloads"]?["client"] is JObject client && !DownloadService.IsValid(
            new(
                new Uri((string)client["url"]!),
                installed.JarPath,
                (long? )client["size"] ?? -1,
                (string? )client["sha1"]
            )
        ))
            throw new InvalidDataException("自定义客户端损坏；父版已修复，请重新安装该加载器。");
    }

    // [实现] BackupAsync 的实现：执行 destination = Path.GetFullPath(destination)；按 Within(source.GameDirectory, destination) 分支处理；按 File.Exists(destination) 分支处理。
    public async Task BackupAsync(GameProfile source, string destination, CancellationToken ct = default)
    {
        destination = Path.GetFullPath(destination);
        if (Within(source.GameDirectory, destination))
            throw new InvalidDataException("备份文件应保存在实例之外。");
        if (File.Exists(destination))
            throw new IOException("备份目标已存在，请另选文件名。");
        var temporary = destination + ".partial-" + Guid.NewGuid()
            .ToString("N");
        Directory.CreateDirectory(
            Path.GetDirectoryName(destination)!
        );
        try
        {
            // [实现] 在异常保护范围内：执行 await Task.Run( async () => { using var archive = ZipFile.Open(temporary, ZipArchiveMode.Create…；执行 await InstanceBackupArchive.VerifyAsync(temporary, ct)；执行 ct.ThrowIfCancellationRequested()。
            await Task.Run(
                async () =>
                {
                    // [实现] 回调触发时：准备 var archive = ZipFile.Open(temporary, ZipArchiveMode.Create)；准备 var files = new JArray()；准备 long total = 0。
                    using var archive = ZipFile.Open(temporary, ZipArchiveMode.Create);
                    var files = new JArray();
                    long total = 0;
                    // [实现] Add 的实现：按 files.Count >= InstanceBackupArchive.MaximumFiles 分支处理；准备 var row = await InstanceBackupArchive.AddAsync(archive, name, input, ct)；执行 total = checked(total + (long)row["size"]!)。
                    async Task Add(string name, Stream input)
                    {
                        if (files.Count >= InstanceBackupArchive.MaximumFiles)
                            throw new InvalidDataException("备份文件数量超出限制。");
                        var row = await InstanceBackupArchive.AddAsync(archive, name, input, ct);
                        total = checked(total + (long)row["size"]!);
                        if (total > InstanceBackupArchive.MaximumBytes)
                            throw new InvalidDataException("备份体积超出限制。");
                        files.Add(row);
                    }

                    foreach (var file in DataFiles(
                        source.GameDirectory,
                        Shared(source),
                        ct
                    ))
                    {
                        // Shared vanilla version metadata is synthesized once, even when a loose version.json exists.
                        var relative = Path.GetRelativePath(source.GameDirectory, file)
                            .Replace('\\', '/');
                        if (!File.Exists(
                            Path.Combine(source.GameDirectory, "profile.json")
                        ) && relative == "version.json")
                            continue;
                        using var input = File.OpenRead(file);
                        await Add(relative, input);
                    }

                    if (!File.Exists(
                        Path.Combine(source.GameDirectory, "profile.json")
                    ))
                    {
                        // [实现] 条件成立（!File.Exists( Path.Combine(source.GameDirectory, "profile.json") )）时：准备 var metadata = JsonFiles.Read(source.JsonPath)；执行 metadata["id"] = source.Id；在作用域内使用并释放 var input = new MemoryStream( System.Text.Encoding.UTF8.GetBytes( metadata.ToString() ) )。
                        var metadata = JsonFiles.Read(source.JsonPath);
                        metadata["id"] = source.Id;
                        using (var input = new MemoryStream(
                            System.Text.Encoding.UTF8.GetBytes(
                                metadata.ToString()
                            )
                        ))
                            await Add("version.json", input);
                        using (var input = new MemoryStream(
                            System.Text.Encoding.UTF8.GetBytes(
                                JObject.FromObject(
                                    new
                                    {
                                        id = source.Id,
                                        displayName = source.Name,
                                        gameVersion = source.GameVersion,
                                        loader = source.Loader,
                                        loaderVersion = ""
                                    }
                                )
                                    .ToString()
                            )
                        ))
                            await Add("profile.json", input);
                    }

                    using var writer = new StreamWriter(
                        archive.CreateEntry("pcl-backup.json")
                            .Open()
                    );
                    await writer.WriteAsync(
                        JObject.FromObject(
                            new
                            {
                                format = "pcl-mac-instance-backup",
                                schemaVersion = 3,
                                profile = source,
                                preferences = Settings.Preferences(source.GameDirectory),
                                launchPatch = Settings.Patch(source.GameDirectory),
                                createdAt = DateTimeOffset.UtcNow,
                                files
                            }
                        )
                            .ToString()
                            .AsMemory(),
                        ct
                    );
                },
                ct
            );
            await InstanceBackupArchive.VerifyAsync(temporary, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 File.Exists(temporary) 分支处理。
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // [实现] RestoreBackupAsync 的实现：执行 Directory.CreateDirectory(Instances)；准备 var id = "restore-" + Guid.NewGuid() .ToString("N")；准备 var stage = Path.Combine(Instances, ".pending-" + id)。
    public async Task<GameProfile> RestoreBackupAsync(
        string archivePath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(Instances);
        var id = "restore-" + Guid.NewGuid()
            .ToString("N");
        var stage = Path.Combine(Instances, ".pending-" + id);
        var target = Path.Combine(Instances, id);
        Directory.CreateDirectory(stage);
        bool moved = false;
        try
        {
            // [实现] 在异常保护范围内：准备 JObject manifest；在作用域内使用并释放 var archive = ZipFile.OpenRead(archivePath)；准备 var candidate = Scan(stage) .SingleOrDefault( x => x.Supported ) ?? throw new InvalidDataExcept…。
            JObject manifest;
            using (var archive = ZipFile.OpenRead(archivePath))
                manifest = (await InstanceBackupArchive.ReadAsync(archive, stage, ct)).Manifest;
            var candidate = Scan(stage)
                .SingleOrDefault(
                    x => x.Supported
                ) ?? throw new InvalidDataException("备份缺少可恢复的实例定义。");
            var originalPreferences = manifest["preferences"]?.ToObject<InstancePreferences>() ?? new();
            var restoredName = string.IsNullOrWhiteSpace(originalPreferences.Name)
                ? candidate.Name
                : originalPreferences.Name;
            var preferences = originalPreferences with
            {
                Name = restoredName.Length <= 95
                    ? restoredName + " · 恢复"
                    : restoredName,
                Hidden = false,
                Deleted = false
            };
            var launch = manifest["launch"] is JObject legacy
                ? legacy.ToObject<LaunchSettings>()
                : null;
            var patch = manifest["launchPatch"] is JObject patchJson
                ? LaunchSettingsPatch.Parse(patchJson)
                : launch is not null
                    ? LaunchSettingsPatch.From(launch)
                    : null;
            patch = patch?.WithoutExecutableOverrides();
            Settings.ValidateInstance(preferences, patch);
            using var parent = service.ForVersion(candidate.GameVersion);
            await parent.InstallAsync(progress, ct);
            var metadata = candidate.Loader == "vanilla"
                ? JsonFiles.Read(
                    parent.LoadInstalled()
                        .JsonPath
                )
                : JsonFiles.Read(candidate.JsonPath);
            metadata["id"] = id;
            JsonFiles.Write(
                Path.Combine(stage, "version.json"),
                metadata
            );
            var receipt = JsonFiles.Read(
                Path.Combine(stage, "profile.json")
            );
            receipt["id"] = id;
            receipt["displayName"] = preferences.Name;
            JsonFiles.Write(
                Path.Combine(stage, "profile.json"),
                receipt
            );
            var staged = new GameProfile(
                id,
                preferences.Name,
                candidate.GameVersion,
                candidate.Loader,
                stage,
                Path.Combine(stage, "version.json")
            );
            await RepairAsync(staged, progress, ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            moved = true;
            var restored = staged with
            {
                GameDirectory = target,
                JsonPath = Path.Combine(target, "version.json")
            };
            Settings.SaveInstance(target, preferences, patch);
            return restored;
        }
        catch
        {
            // [实现] 捕获 空 后：按 moved && Directory.Exists(target) 分支处理；重新抛出当前异常。
            if (moved && Directory.Exists(target))
                Directory.Delete(target, true);
            throw;
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 Directory.Exists(stage) 分支处理。
            if (Directory.Exists(stage))
                Directory.Delete(stage, true);
        }
    }

    // [实现] DeleteToTrash 的实现：执行 RequireManaged(profile)；准备 var shared = Shared(profile)；准备 var source = profile.GameDirectory。
    public DeletedInstance DeleteToTrash(GameProfile profile)
    {
        RequireManaged(profile);
        var shared = Shared(profile);
        var source = profile.GameDirectory;
        if (!Directory.Exists(source))
            Directory.CreateDirectory(source);
        RequirePlainDirectory(source);
        Directory.CreateDirectory(
            ResourceInstaller.SafeTarget(service.Paths.Root, "instance-trash")
        );
        var id = Guid.NewGuid()
            .ToString("N");
        var folder = ResourceInstaller.SafeTarget(Trash, id);
        Directory.CreateDirectory(folder);
        var result = new DeletedInstance(
            id,
            profile.Name,
            DateTimeOffset.UtcNow,
            source,
            Path.Combine(folder, "data"),
            profile,
            !shared
        );
        JsonFiles.Write(
            Path.Combine(folder, "receipt.json"),
            result
        );
        try
        {
            // [实现] 在异常保护范围内：按 !shared 分支处理；执行 Settings.SavePreferences( profile.GameDirectory, Settings.Preferences(profile.GameDirectory)wit…。
            if (!shared)
                Directory.Move(source, result.TrashDirectory);
            Settings.SavePreferences(
                profile.GameDirectory,
                Settings.Preferences(profile.GameDirectory)with
                {
                    Deleted = true
                }
            );
        }
        catch
        {
            // [实现] 捕获 空 后：按 Directory.Exists(result.TrashDirectory) && !Directory.Exists(source) 分支处理；按 Directory.Exists(folder) 分支处理；重新抛出当前异常。
            if (Directory.Exists(result.TrashDirectory) && !Directory.Exists(source))
                Directory.Move(result.TrashDirectory, source);
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            throw;
        }

        return result;
    }

    // [实现] ListTrash 的实现：计算并返回 !Directory.Exists( ResourceInstaller.SafeTarget(service.Paths.Root, "instance-trash") ) ? [] : …。
    public IReadOnlyList<DeletedInstance> ListTrash() => !Directory.Exists(
        ResourceInstaller.SafeTarget(service.Paths.Root, "instance-trash")
    )
        ? []
        : Directory.EnumerateDirectories(Trash)
            .Where(
                x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0 && File.Exists(
                    Path.Combine(x, "receipt.json")
                )
            )
            .Select(
                x => JsonFiles.Read(
                    ResourceInstaller.SafeTarget(x, "receipt.json")
                )
                    .ToObject<DeletedInstance>()!
            )
            .OrderByDescending(
                x => x.DeletedAt
            )
            .ToArray();
    // [实现] RestoreTrash 的实现：按 !Guid.TryParseExact(id, "N", out _) 分支处理；准备 var folder = ResourceInstaller.SafeTarget(service.Paths.Root, "instance-trash/" + id)；准备 var receipt = JsonFiles.Read( ResourceInstaller.SafeTarget(folder, "receipt.json") ) .ToObject<…。
    public GameProfile RestoreTrash(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("回收记录无效。");
        var folder = ResourceInstaller.SafeTarget(service.Paths.Root, "instance-trash/" + id);
        var receipt = JsonFiles.Read(
            ResourceInstaller.SafeTarget(folder, "receipt.json")
        )
            .ToObject<DeletedInstance>()!;
        RequireManaged(receipt.Profile);
        var expected = receipt.Profile.GameDirectory;
        if (Path.GetFullPath(expected) != Path.GetFullPath(receipt.OriginalDirectory) || Path.GetFullPath(receipt.TrashDirectory) != Path.Combine(folder, "data"))
            throw new InvalidDataException("回收路径记录不匹配。");
        if (receipt.DataMoved && Shared(receipt.Profile))
            throw new InvalidDataException("回收记录不能覆盖共享目录。");
        if (receipt.DataMoved && Directory.Exists(expected))
            throw new IOException("原目录已有内容，无法覆盖恢复。");
        if (receipt.DataMoved)
        {
            // [实现] 条件成立（receipt.DataMoved）时：执行 RequirePlainDirectory( ResourceInstaller.SafeTarget(folder, "data") )；执行 Directory.CreateDirectory( Path.GetDirectoryName(expected)! )；执行 Directory.Move(receipt.TrashDirectory, expected)。
            RequirePlainDirectory(
                ResourceInstaller.SafeTarget(folder, "data")
            );
            Directory.CreateDirectory(
                Path.GetDirectoryName(expected)!
            );
            Directory.Move(receipt.TrashDirectory, expected);
        }

        try
        {
            // [实现] 在异常保护范围内：执行 Settings.SavePreferences( receipt.Profile.GameDirectory, Settings.Preferences(receipt.Profile.G…。
            Settings.SavePreferences(
                receipt.Profile.GameDirectory,
                Settings.Preferences(receipt.Profile.GameDirectory)with
                {
                    Deleted = false
                }
            );
        }
        catch
        {
            // [实现] 捕获 空 后：按 receipt.DataMoved 分支处理；重新抛出当前异常。
            if (receipt.DataMoved)
                Directory.Move(expected, receipt.TrashDirectory);
            throw;
        }

        Directory.Delete(folder, true);
        return receipt.Profile;
    }

    /// <summary>复用安装实例、Java 和启动计划；只生成离线身份，永不读取微软凭据或执行启动钩子。</summary>
    public async Task ExportOfflineScriptAsync(
        GameProfile profile,
        string destination,
        string playerName = "Player",
        CancellationToken ct = default,
        LauncherAccount? offlineAccount = null
    )
    {
        ct.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(destination);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException("目标已存在，请选择新的脚本文件名。");
        }

        ArgumentNullException.ThrowIfNull(profile);
        RequireManaged(profile);
        if (!OperatingSystem.IsMacOS() || service.Platform.Os != "osx")
        {
            throw new PlatformNotSupportedException("此导出功能用于本机 Mac 实例。");
        }

        playerName = AccountManager.ValidateOfflineName(playerName);
        if (offlineAccount is not null && (offlineAccount.Kind != AccountKind.Offline || offlineAccount.Name != playerName))
        {
            throw new InvalidDataException("只能导出同名离线账户的启动脚本。");
        }

        var skins = new OfflineSkinService(service.Paths);
        var session = AccountManager.OfflineSession(
            playerName,
            offlineAccount is null
                ? "auto"
                : skins.Get(offlineAccount)?.Model ?? "auto"
        );
        GameLaunchAuthorization.Require(session);
        var version = InstanceRegistry.Load(service, profile);
        var java = await Settings.ResolveJavaAsync(service, version, profile.GameDirectory, ct: ct);
        skins.Prepare(offlineAccount, version, profile.GameDirectory, profile.GameVersion, ct);
        new AppearanceService(service.Paths).PrepareGameLanguage(profile.GameDirectory, profile.GameVersion);
        var plan = await new LaunchService(service).PrepareAsync(version, java, session, gameDirectory: profile.GameDirectory, ct: ct);
        var script = OfflineLaunchScript.Render(plan, session);
        var temporary = target + ".pcl-" + Guid.NewGuid()
            .ToString("N");
        try
        {
            // 同目录临时文件先写完，再原子发布；取消或目标冲突均不留下半份脚本。
            await File.WriteAllTextAsync(
                temporary,
                script,
                new System.Text.UTF8Encoding(false),
                ct
            );
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // [实现] Shared 的实现：计算并返回 Path.GetFullPath(profile.GameDirectory) == Path.GetFullPath(service.Paths.Game)。
    private bool Shared(GameProfile profile) => Path.GetFullPath(profile.GameDirectory) == Path.GetFullPath(service.Paths.Game);
    // [实现] RequireManaged 的实现：按 !Shared(profile) && !Within(Instances, profile.GameDirectory) 分支处理；按 !Shared(profile) 分支处理；执行 VersionCatalog.ValidateId(profile.GameVersion)。
    private void RequireManaged(GameProfile profile)
    {
        if (!Shared(profile) && !Within(Instances, profile.GameDirectory))
            throw new InvalidDataException("只能管理启动器已导入的实例副本。");
        if (!Shared(profile))
            _ = ResourceInstaller.SafeTarget(
                Instances,
                Path.GetRelativePath(Instances, profile.GameDirectory)
            );
        VersionCatalog.ValidateId(profile.GameVersion);
    }

    // [实现] Within 的实现：计算并返回 Path.GetFullPath(path) .StartsWith( Path.TrimEndingDirectorySeparator( Path.GetFullPath(root) )…。
    private static bool Within(string root, string path) => Path.GetFullPath(path)
        .StartsWith(
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(root)
            ) + Path.DirectorySeparatorChar,
            StringComparison.Ordinal
        );
    // [实现] RequirePlainDirectory 的实现：按 !Directory.Exists(directory) 分支处理；按 (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 分支处理。
    private static void RequirePlainDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("实例目录不能是符号链接。");
    }

    // [实现] DataFiles 的实现：执行 RequirePlainDirectory(root)；准备 var pending = new Stack<string>()；执行 pending.Push(root)。
    private static IEnumerable<string> DataFiles(string root, bool skipShared, CancellationToken ct)
    {
        RequirePlainDirectory(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                // [实现] 对 new DirectoryInfo(folder).EnumerateFileSystemInfos() 中的每一项：执行 ct.ThrowIfCancellationRequested()；按 Excluded.Contains(entry.Name) 分支处理；按 (entry.Attributes & FileAttributes.ReparsePoint) != 0 分支处理。
                ct.ThrowIfCancellationRequested();
                if (Excluded.Contains(entry.Name))
                    continue;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("实例含符号链接，操作已停止：" + entry.Name);
                if (entry is DirectoryInfo)
                {
                    // [实现] 条件成立（entry is DirectoryInfo）时：按 !(skipShared && folder == root && SharedDirectories.Contains(entry.Name)) 分支处理。
                    if (!(skipShared && folder == root && SharedDirectories.Contains(entry.Name)))
                        pending.Push(entry.FullName);
                }
                else
                    yield return entry.FullName;
            }
    }

    // [实现] CopyData 的实现：遍历 DataFiles(source, skipShared, ct) 处理 file。
    private static void CopyData(string source, string destination, bool skipShared, CancellationToken ct)
    {
        foreach (var file in DataFiles(source, skipShared, ct))
        {
            // [实现] 对 DataFiles(source, skipShared, ct) 中的每一项：准备 var target = ResourceInstaller.SafeTarget( destination, Path.GetRelativePath(source, file) )；执行 Directory.CreateDirectory( Path.GetDirectoryName(target)! )；执行 File.Copy(file, target)。
            var target = ResourceInstaller.SafeTarget(
                destination,
                Path.GetRelativePath(source, file)
            );
            Directory.CreateDirectory(
                Path.GetDirectoryName(target)!
            );
            File.Copy(file, target);
        }
    }
}
