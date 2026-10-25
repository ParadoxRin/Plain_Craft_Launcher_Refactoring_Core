using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：ComponentFile 汇集 RelativePath、Sha256、Size、ActiveMod，供该模块传递状态。
public sealed record ComponentFile(string RelativePath, string Sha256, long Size, bool ActiveMod);
// [实现] 数据约定：ComponentMigrationPlan 汇集 Source、TargetLoader、TargetVersion、DisableMods、SourceFingerprint、Files、ActiveMods、Conflicts，供该模块传递状态。
public sealed record ComponentMigrationPlan(
    GameProfile Source,
    string TargetLoader,
    string? TargetVersion,
    bool DisableMods,
    string SourceFingerprint,
    IReadOnlyList<ComponentFile> Files,
    IReadOnlyList<string> ActiveMods,
    IReadOnlyList<string> Conflicts
)
{
    // 预览阶段固定身份，界面展示与最终提交使用同一路径；旧回执允许为空。
    public string? TargetKey { get; init; }
    public bool CanApply => Conflicts.Count == 0;
}

// [实现] 数据约定：ComponentMigrationResult 汇集 Profile、Backup、Receipt、DisabledMods，供该模块传递状态。
public sealed record ComponentMigrationResult(GameProfile Profile, string Backup, string Receipt, int DisabledMods);
/// <summary>Rebuilds only the loader layer in a new instance. The source is never edited or removed.</summary>
public sealed class InstanceComponentService(
    MinecraftService service,
    Func<ComponentMigrationPlan, string, CancellationToken, Task<GameProfile>>? installer = null
)
{
    private static readonly HashSet<string> EngineDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets",
        "libraries",
        "versions",
        "runtime",
        "natives",
        "logs",
        "crash-reports",
        ".pcl-downloads",
        ".pcl-backups"
    };
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "version.json",
        "profile.json",
        ".DS_Store",
        "launcher_accounts.json",
        "launcher_profiles.json",
        "accounts.json",
        "accounts",
        "pcl-backup.json",
        "pcl-component-origin.json",
        "pcl-mac-setup.json",
        "pcl-optifabric.json",
        ".install.lock",
        ".resources.lock",
        ".component.lock",
        ".pcl-last-activation.json",
        ".pcl-activation-transaction.json",
        ".pcl-shader-removal.json",
        ".pcl-iris-environment.json",
        ".pcl-iris-uninstall.json"
    };
    public static readonly string[] Loaders = [
        "vanilla",
        "fabric",
        "quilt",
        "forge",
        "neoforge",
        "optifine",
        "liteloader"
    ];
    // [实现] VersionsAsync 的实现：执行 ValidateLoader(loader)；准备 var target = service.ForVersion(source.GameVersion)；按 loader == "vanilla" 分支处理。
    public async Task<IReadOnlyList<string>> VersionsAsync(GameProfile source, string loader, CancellationToken ct = default)
    {
        ValidateLoader(loader);
        using var target = service.ForVersion(source.GameVersion);
        if (loader == "vanilla")
            return[
                "原版（移除加载器）"
            ];
        if (loader is "fabric" or "quilt")
        {
            // [实现] 条件成立（loader is "fabric" or "quilt"）时：准备 var fabric = new FabricService(target, loader: loader)；返回 (await fabric.VersionsAsync(ct)).Where( x => x.Stable ) .Select( x => x.Version ) .ToArray()。
            using var fabric = new FabricService(target, loader: loader);
            return (await fabric.VersionsAsync(ct)).Where(
                x => x.Stable
            )
                .Select(
                    x => x.Version
                )
                .ToArray();
        }

        if (loader == "optifine")
        {
            // [实现] 条件成立（loader == "optifine"）时：准备 var optifine = new OptiFineService(target)；返回 (await optifine.VersionsAsync(ct)).Select( x => x.FileName ) .ToArray()。
            using var optifine = new OptiFineService(target);
            return (await optifine.VersionsAsync(ct)).Select(
                x => x.FileName
            )
                .ToArray();
        }

        if (loader == "liteloader")
        {
            // [实现] 条件成立（loader == "liteloader"）时：准备 var lite = new LiteLoaderService(target)；返回 (await lite.VersionsAsync(ct)).Select( x => x.Build ) .ToArray()。
            using var lite = new LiteLoaderService(target);
            return (await lite.VersionsAsync(ct)).Select(
                x => x.Build
            )
                .ToArray();
        }

        using var forge = new ForgeService(target);
        return (await forge.VersionsAsync(loader, ct)).Select(
            x => x.Version
        )
            .ToArray();
    }

    // [实现] Preview 的实现：执行 RequireManaged(source)；执行 ValidateLoader(loader)；按 loader != "vanilla" && string.IsNullOrWhiteSpace(version) 分支处理。
    public ComponentMigrationPlan Preview(
        GameProfile source,
        string loader,
        string? version,
        bool disableMods,
        CancellationToken ct = default
    )
    {
        RequireManaged(source);
        ValidateLoader(loader);
        if (loader != "vanilla" && string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("请选择目标加载器版本。");
        var files = DataFiles(source.GameDirectory, ct)
            .Select(
                file =>
                {
                    // [实现] 回调触发时：准备 var relative = Path.GetRelativePath(source.GameDirectory, file) .Replace('\\', '/')；准备 var input = File.OpenRead(file)；准备 var digest = Convert.ToHexString( SHA256.HashData(input) ) .ToLowerInvariant()。
                    var relative = Path.GetRelativePath(source.GameDirectory, file)
                        .Replace('\\', '/');
                    using var input = File.OpenRead(file);
                    var digest = Convert.ToHexString(
                        SHA256.HashData(input)
                    )
                        .ToLowerInvariant();
                    return new ComponentFile(
                        relative,
                        digest,
                        new FileInfo(file).Length,
                        relative.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) && (relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase))
                    );
                }
            )
            .OrderBy(
                x => x.RelativePath,
                StringComparer.Ordinal
            )
            .ToArray();
        var active = files.Where(
            x => x.ActiveMod
        )
            .Select(
                x => x.RelativePath
            )
            .ToArray();
        var conflicts = new List<string>();
        if (active.Length > 0 && !disableMods)
            conflicts.Add("已有 " + active.Length + " 个启用模组；请选择在新副本禁用，避免未经验证的加载器组合。");
        if (disableMods)
            foreach (var mod in active)
                if (files.Any(
                    x => x.RelativePath.Equals(mod + ".disabled", StringComparison.OrdinalIgnoreCase)
                ))
                    conflicts.Add("启用与禁用文件重名：" + mod);
        var fingerprint = Fingerprint(source, files);
        return new(
            source,
            loader,
            loader == "vanilla"
                ? null
                : version,
            disableMods,
            fingerprint,
            files,
            active,
            conflicts
        );
    }

    // [实现] ApplyAsync 的实现：按 !plan.CanApply 分支处理；执行 RequireManaged(plan.Source)；准备 var current = Preview(plan.Source, plan.TargetLoader, plan.TargetVersion, plan.DisableMods, ct)。
    public async Task<ComponentMigrationResult> ApplyAsync(
        ComponentMigrationPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        string? operationKey = null
    )
    {
        if (operationKey is not null && !Guid.TryParseExact(operationKey, "N", out _))
        {
            throw new InvalidDataException("COMPONENT_OPERATION_INVALID");
        }

        if (!plan.CanApply)
            throw new InvalidOperationException(
                string.Join("\n", plan.Conflicts)
            );
        RequireManaged(plan.Source);
        var current = Preview(plan.Source, plan.TargetLoader, plan.TargetVersion, plan.DisableMods, ct);
        if (current.SourceFingerprint != plan.SourceFingerprint || !current.CanApply)
            throw new InvalidDataException("源实例已变化，请重新预览。");
        operationKey ??= plan.TargetKey;
        if (operationKey is not null && !Guid.TryParseExact(operationKey, "N", out _))
        {
            throw new InvalidDataException("COMPONENT_OPERATION_INVALID");
        }

        var operation = operationKey ?? Guid.NewGuid()
            .ToString("N");
        var pendingName = "pending-pack-component-" + operation;
        var id = "modified-" + operation;
        var instances = Path.Combine(service.Paths.Root, "instances");
        var pending = Path.Combine(instances, pendingName);
        var target = Path.Combine(instances, id);
        var backupFolder = Path.Combine(service.Paths.Root, "instance-component-backups", operation);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException("目标副本路径已被占用，请重新预览。");
        }

        Directory.CreateDirectory(backupFolder);
        var backup = Path.Combine(backupFolder, "components.zip");
        var journal = Path.Combine(backupFolder, "migration.json");
        JsonFiles.Write(
            journal,
            new
            {
                status = "preparing",
                plan,
                createdAt = DateTimeOffset.UtcNow
            }
        );
        bool committed = false;
        try
        {
            // [实现] 在异常保护范围内：执行 progress?.Report( new("备份源实例组件", 0, 0, 0, 0) )；执行 await BackupComponentsAsync(plan, backup, ct)；准备 GameProfile fresh = installer is null ? await InstallFreshAsync(plan, pendingName, progress, ct…。
            progress?.Report(
                new("备份源实例组件", 0, 0, 0, 0)
            );
            await BackupComponentsAsync(plan, backup, ct);
            GameProfile fresh = installer is null
                ? await InstallFreshAsync(plan, pendingName, progress, ct)
                : await installer(plan, pendingName, ct);
            if (Path.GetFullPath(fresh.GameDirectory) != Path.GetFullPath(pending) || fresh.GameVersion != plan.Source.GameVersion || fresh.Loader != plan.TargetLoader || Path.GetFullPath(fresh.JsonPath) != Path.Combine(
                Path.GetFullPath(pending),
                "version.json"
            ))
                throw new InvalidDataException("安装器返回了不匹配的目标实例。");
            progress?.Report(
                new(
                    "复制存档与配置",
                    0,
                    plan.Files.Count,
                    0,
                    plan.Files.Sum(
                        x => x.Size
                    )
                )
            );
            long copied = 0;
            int count = 0;
            var totalBytes = plan.Files.Sum(
                x => x.Size
            );
            foreach (var entry in plan.Files)
            {
                // [实现] 对 plan.Files 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var source = ResourceInstaller.SafeTarget(plan.Source.GameDirectory, entry.RelativePath)；准备 var relative = entry.RelativePath + (entry.ActiveMod && plan.DisableMods ? ".disabled" : "")。
                ct.ThrowIfCancellationRequested();
                var source = ResourceInstaller.SafeTarget(plan.Source.GameDirectory, entry.RelativePath);
                var relative = entry.RelativePath + (entry.ActiveMod && plan.DisableMods
                    ? ".disabled"
                    : "");
                var destination = ResourceInstaller.SafeTarget(pending, relative);
                if (File.Exists(destination))
                    throw new InvalidDataException("新组件与源实例文件冲突：" + relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)!
                );
                await using (var input = File.OpenRead(source))
                    await using (var output = new FileStream(destination, FileMode.CreateNew))
                        await input.CopyToAsync(output, ct);
                using (var check = File.OpenRead(destination))
                    if (Convert.ToHexString(
                        SHA256.HashData(check)
                    )
                        .ToLowerInvariant() != entry.Sha256)
                        throw new InvalidDataException("复制时源文件发生变化：" + entry.RelativePath);
                copied += entry.Size;
                progress?.Report(
                    new("复制存档与配置", ++count, plan.Files.Count, copied, totalBytes)
                );
            }

            if (plan.DisableMods)
                DisableManagedModRecords(pending);
            var metadata = JsonFiles.Read(fresh.JsonPath);
            metadata["id"] = id;
            JsonFiles.Write(fresh.JsonPath, metadata);
            var receipt = JsonFiles.Read(
                Path.Combine(pending, "profile.json")
            );
            receipt["id"] = id;
            receipt["displayName"] = plan.Source.Name + " · " + (plan.TargetLoader == "vanilla"
                ? "原版副本"
                : plan.TargetLoader + " " + plan.TargetVersion);
            receipt["migrationSource"] = plan.Source.Id;
            receipt["migrationBackup"] = backup;
            JsonFiles.Write(
                Path.Combine(pending, "profile.json"),
                receipt
            );
            var staged = fresh with
            {
                Id = id,
                Name = (string)receipt["displayName"]!
            };
            var installed = InstanceRegistry.Load(service, staged);
            if (installed.Libraries.Any(
                x => !DownloadService.IsValid(x)
            ))
                throw new InvalidDataException("新实例组件校验失败。");
            if (service.Platform.Os == "osx" && service.Platform.Architecture == "aarch64")
                MacNativeAudit.Check(
                    installed,
                    new("inspection-by-installed-metadata", "aarch64", installed.JavaMajor)
                );
            if (Preview(plan.Source, plan.TargetLoader, plan.TargetVersion, plan.DisableMods, ct)
                .SourceFingerprint != plan.SourceFingerprint)
                throw new InvalidDataException("复制期间源实例有变动；已中止，请关闭游戏后重试。");
            var settings = new LauncherSettingsStore(service.Paths);
            var sourcePreferences = settings.Preferences(plan.Source.GameDirectory);
            var sourceLaunch = settings.Patch(plan.Source.GameDirectory);
            ct.ThrowIfCancellationRequested();
            JsonFiles.Write(
                ResourceInstaller.SafeTarget(pending, "pcl-component-origin.json"),
                new
                {
                    format = 1,
                    sourceDirectory = plan.Source.GameDirectory,
                    targetDirectory = target,
                    loader = plan.TargetLoader,
                    createdAt = DateTimeOffset.UtcNow
                }
            );
            Directory.Move(pending, target);
            committed = true;
            var result = staged with
            {
                GameDirectory = target,
                JsonPath = Path.Combine(target, "version.json")
            };
            settings.SavePreferences(
                target,
                sourcePreferences with
                {
                    Name = result.Name,
                    Hidden = false,
                    Deleted = false
                }
            );
            // Re-select Java for the new loader; custom agents and pre-launch arguments are not implicitly transferred.
            if (sourceLaunch is not null)
                settings.SavePatch(
                    target,
                    sourceLaunch.WithoutExecutableOverrides()
                );
            JsonFiles.Write(
                journal,
                new
                {
                    status = "committed",
                    plan,
                    target = result,
                    backup,
                    disabledMods = plan.DisableMods
                        ? plan.ActiveMods.Count
                        : 0,
                    sourcePreserved = true,
                    completedAt = DateTimeOffset.UtcNow
                }
            );
            return new(
                result,
                backup,
                journal,
                plan.DisableMods
                    ? plan.ActiveMods.Count
                    : 0
            );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：按 Directory.Exists(pending) 分支处理；按 committed && Directory.Exists(target) 分支处理；执行 JsonFiles.Write( journal, new { status = "rolled_back", plan.Source.Id, plan.TargetLoader, plan…。
            if (Directory.Exists(pending))
                Directory.Delete(pending, true);
            if (committed && Directory.Exists(target))
                Directory.Delete(target, true);
            JsonFiles.Write(
                journal,
                new
                {
                    status = "rolled_back",
                    plan.Source.Id,
                    plan.TargetLoader,
                    plan.TargetVersion,
                    backup,
                    sourcePreserved = true,
                    error = ex.Message,
                    completedAt = DateTimeOffset.UtcNow
                }
            );
            throw;
        }
    }

    // [实现] InstallFreshAsync 的实现：准备 var target = service.ForVersion(plan.Source.GameVersion)；准备 var parent = target.LoadInstalled()；执行 await new JavaService(target).EnsureForAsync(parent, progress, ct)。
    private async Task<GameProfile> InstallFreshAsync(
        ComponentMigrationPlan plan,
        string name,
        IProgress<InstallProgress>? progress,
        CancellationToken ct
    )
    {
        using var target = service.ForVersion(plan.Source.GameVersion);
        var parent = target.LoadInstalled();
        await new JavaService(target).EnsureForAsync(parent, progress, ct);
        if (plan.TargetLoader is "fabric" or "quilt")
        {
            // [实现] 条件成立（plan.TargetLoader is "fabric" or "quilt"）时：准备 var fabric = new FabricService(target, name, plan.TargetLoader)；返回 await fabric.InstallAsync(plan.TargetVersion, progress, ct)。
            using var fabric = new FabricService(target, name, plan.TargetLoader);
            return await fabric.InstallAsync(plan.TargetVersion, progress, ct);
        }

        if (plan.TargetLoader is "forge" or "neoforge")
        {
            // [实现] 条件成立（plan.TargetLoader is "forge" or "neoforge"）时：准备 var forge = new ForgeService(target)；返回 await forge.InstallAsync(plan.TargetLoader, plan.TargetVersion, name, progress: progress, ct: c…。
            using var forge = new ForgeService(target);
            return await forge.InstallAsync(plan.TargetLoader, plan.TargetVersion, name, progress: progress, ct: ct);
        }

        if (plan.TargetLoader == "optifine")
        {
            // [实现] 条件成立（plan.TargetLoader == "optifine"）时：准备 var optifine = new OptiFineService(target)；返回 await optifine.InstallAsync(plan.TargetVersion, progress, ct, name)。
            using var optifine = new OptiFineService(target);
            return await optifine.InstallAsync(plan.TargetVersion, progress, ct, name);
        }

        if (plan.TargetLoader == "liteloader")
        {
            // [实现] 条件成立（plan.TargetLoader == "liteloader"）时：准备 var lite = new LiteLoaderService(target)；返回 await lite.InstallAsync(plan.TargetVersion, progress, ct, name)。
            using var lite = new LiteLoaderService(target);
            return await lite.InstallAsync(plan.TargetVersion, progress, ct, name);
        }

        var folder = Path.Combine(service.Paths.Root, "instances", name);
        Directory.CreateDirectory(folder);
        var metadata = JsonFiles.Read(parent.JsonPath);
        metadata["id"] = name;
        JsonFiles.Write(
            Path.Combine(folder, "version.json"),
            metadata
        );
        JsonFiles.Write(
            Path.Combine(folder, "profile.json"),
            new
            {
                id = name,
                gameVersion = plan.Source.GameVersion,
                loader = "vanilla",
                loaderVersion = "",
                displayName = plan.Source.GameVersion + " · 原版副本"
            }
        );
        return new(
            name,
            plan.Source.GameVersion + " · 原版副本",
            plan.Source.GameVersion,
            "vanilla",
            folder,
            Path.Combine(folder, "version.json")
        );
    }

    // [实现] DisableManagedModRecords 的实现：准备 var manifest = Path.Combine(folder, ".pcl-resources.json")；按 !File.Exists(manifest) 分支处理；准备 var resources = JsonConvert.DeserializeObject<List<ManagedResource>>( File.ReadAllText(manifest…。
    private static void DisableManagedModRecords(string folder)
    {
        var manifest = Path.Combine(folder, ".pcl-resources.json");
        if (!File.Exists(manifest))
            return;
        var resources = JsonConvert.DeserializeObject<List<ManagedResource>>(
            File.ReadAllText(manifest)
        ) ?? throw new InvalidDataException("源实例资源清单无效。");
        JsonFiles.Write(
            manifest,
            resources.Select(
                x => x.Kind == ResourceKind.Mod
                    ? x with
                    {
                        Enabled = false
                    }
                    : x
            )
        );
    }

    // [实现] BackupComponentsAsync 的实现：准备 var temp = backup + ".partial"；执行受异常处理保护的操作。
    private async Task BackupComponentsAsync(ComponentMigrationPlan plan, string backup, CancellationToken ct)
    {
        var temp = backup + ".partial";
        try
        {
            // [实现] 在异常保护范围内：执行 await Task.Run( () => { using var zip = ZipFile.Open(temp, ZipArchiveMode.Create); zip.CreateEn…；执行 ct.ThrowIfCancellationRequested()；执行 File.Move(temp, backup)。
            await Task.Run(
                () =>
                {
                    // [实现] 回调触发时：准备 var zip = ZipFile.Open(temp, ZipArchiveMode.Create)；执行 zip.CreateEntryFromFile(plan.Source.JsonPath, "version.json", CompressionLevel.Fastest)；准备 var profile = Path.Combine(plan.Source.GameDirectory, "profile.json")。
                    using var zip = ZipFile.Open(temp, ZipArchiveMode.Create);
                    zip.CreateEntryFromFile(plan.Source.JsonPath, "version.json", CompressionLevel.Fastest);
                    var profile = Path.Combine(plan.Source.GameDirectory, "profile.json");
                    if (File.Exists(profile))
                        zip.CreateEntryFromFile(profile, "profile.json", CompressionLevel.Fastest);
                    foreach (var file in plan.Files.Where(
                        x => x.RelativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) || x.RelativePath == ".pcl-resources.json"
                    ))
                    {
                        // [实现] 对 plan.Files.Where( x => x.RelativePath.StartsWith("mods/", StringComparison.OrdinalIgnoreCase) |… 中的每一项：执行 ct.ThrowIfCancellationRequested()；执行 zip.CreateEntryFromFile( ResourceInstaller.SafeTarget(plan.Source.GameDirectory, file.RelativeP…。
                        ct.ThrowIfCancellationRequested();
                        zip.CreateEntryFromFile(
                            ResourceInstaller.SafeTarget(plan.Source.GameDirectory, file.RelativePath),
                            "data/" + file.RelativePath,
                            CompressionLevel.Fastest
                        );
                    }

                    var libraries = Path.Combine(plan.Source.GameDirectory, "libraries");
                    if (File.Exists(profile) && (string? )JsonFiles.Read(profile)["libraryRoot"] == "instance" && Directory.Exists(libraries))
                        foreach (var file in PlainFiles(libraries, ct))
                        {
                            // [实现] 对 PlainFiles(libraries, ct) 中的每一项：执行 ct.ThrowIfCancellationRequested()；执行 zip.CreateEntryFromFile( file, "libraries/" + Path.GetRelativePath(libraries, file) .Replace('\…。
                            ct.ThrowIfCancellationRequested();
                            zip.CreateEntryFromFile(
                                file,
                                "libraries/" + Path.GetRelativePath(libraries, file)
                                    .Replace('\\', '/'),
                                CompressionLevel.Fastest
                            );
                        }

                    using var writer = new StreamWriter(
                        zip.CreateEntry("source-manifest.json")
                            .Open()
                    );
                    writer.Write(
                        JObject.FromObject(
                            new
                            {
                                source = plan.Source,
                                plan.SourceFingerprint,
                                plan.Files,
                                sourcePreserved = true
                            }
                        )
                            .ToString()
                    );
                },
                ct
            );
            ct.ThrowIfCancellationRequested();
            File.Move(temp, backup);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 File.Exists(temp) 分支处理。
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    // [实现] Fingerprint 的实现：准备 var json = File.ReadAllText(source.JsonPath)；准备 var profile = Path.Combine(source.GameDirectory, "profile.json")；准备 var text = json + "\n" + (File.Exists(profile) ? File.ReadAllText(profile) : "") + "\n" + strin…。
    private static string Fingerprint(GameProfile source, IReadOnlyList<ComponentFile> files)
    {
        var json = File.ReadAllText(source.JsonPath);
        var profile = Path.Combine(source.GameDirectory, "profile.json");
        var text = json + "\n" + (File.Exists(profile)
            ? File.ReadAllText(profile)
            : "") + "\n" + string.Join(
            '\n',
            files.Select(
                x => x.RelativePath + "\0" + x.Sha256
            )
        );
        return Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(text)
            )
        )
            .ToLowerInvariant();
    }

    // [实现] RequireManaged 的实现：准备 var directory = Path.GetFullPath(source.GameDirectory)；准备 var instances = Path.GetFullPath( Path.Combine(service.Paths.Root, "instances") ) + Path.Direct…；按 directory != Path.GetFullPath(service.Paths.Game) && !directory.StartsWith(instances, StringCom… 分支处理。
    private void RequireManaged(GameProfile source)
    {
        var directory = Path.GetFullPath(source.GameDirectory);
        var instances = Path.GetFullPath(
            Path.Combine(service.Paths.Root, "instances")
        ) + Path.DirectorySeparatorChar;
        if (directory != Path.GetFullPath(service.Paths.Game) && !directory.StartsWith(instances, StringComparison.Ordinal))
            throw new InvalidDataException("请先将外部实例导入启动器，再调整组件。");
        _ = ResourceInstaller.SafeTarget(
            service.Paths.Root,
            Path.GetRelativePath(service.Paths.Root, source.JsonPath)
        );
        if (!File.Exists(source.JsonPath))
            throw new FileNotFoundException("源实例未安装完整。", source.JsonPath);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("源实例目录不存在。");
        _ = ResourceInstaller.SafeTarget(directory, ".component-check");
        VersionCatalog.ValidateId(source.GameVersion);
    }

    // [实现] ValidateLoader 的实现：按 !Loaders.Contains(loader) 分支处理。
    private static void ValidateLoader(string loader)
    {
        if (!Loaders.Contains(loader))
            throw new ArgumentException("加载器无效。");
    }

    // [实现] DataFiles 的实现：遍历 new DirectoryInfo(root).EnumerateFileSystemInfos() 处理 entry。
    private static IEnumerable<string> DataFiles(string root, CancellationToken ct)
    {
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            // [实现] 对 new DirectoryInfo(root).EnumerateFileSystemInfos() 中的每一项：执行 ct.ThrowIfCancellationRequested()；按 Excluded.Contains(entry.Name) || entry.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase… 分支处理；按 (entry.Attributes & FileAttributes.ReparsePoint) != 0 分支处理。
            ct.ThrowIfCancellationRequested();
            if (Excluded.Contains(entry.Name) || entry.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("源实例含符号链接，未复制：" + entry.Name);
            if (entry is DirectoryInfo)
            {
                // [实现] 条件成立（entry is DirectoryInfo）时：按 !EngineDirectories.Contains(entry.Name) 分支处理。
                if (!EngineDirectories.Contains(entry.Name))
                    foreach (var file in PlainFiles(entry.FullName, ct))
                        yield return file;
            }
            else
                yield return entry.FullName;
        }
    }

    // [实现] PlainFiles 的实现：准备 var pending = new Stack<string>()；执行 pending.Push(root)；在 pending.TryPop(out var folder) 条件下循环。
    private static IEnumerable<string> PlainFiles(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var folder))
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                // [实现] 对 new DirectoryInfo(folder).EnumerateFileSystemInfos() 中的每一项：执行 ct.ThrowIfCancellationRequested()；按 entry.Name is ".DS_Store" or ".install.lock" or ".resources.lock" or ".component.lock" 分支处理；按 (entry.Attributes & FileAttributes.ReparsePoint) != 0 分支处理。
                ct.ThrowIfCancellationRequested();
                if (entry.Name is ".DS_Store" or ".install.lock" or ".resources.lock" or ".component.lock")
                    continue;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("源实例含符号链接，未复制：" + entry.Name);
                if (entry is DirectoryInfo)
                    pending.Push(entry.FullName);
                else
                    yield return entry.FullName;
            }
    }
}
