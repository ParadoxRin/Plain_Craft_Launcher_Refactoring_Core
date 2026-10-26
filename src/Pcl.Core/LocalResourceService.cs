using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：LocalResourceInfo 汇集 Path、Kind、Name、Version、Sha1、Size、Enabled、ModIds、Error、Managed，供该模块传递状态。
public sealed record LocalResourceInfo(
    string Path,
    ResourceKind Kind,
    string Name,
    string Version,
    string Sha1,
    long Size,
    bool Enabled,
    IReadOnlyList<string> ModIds,
    string? Error,
    ManagedResource? Managed
)
{
    // [实现] ToString 的实现：计算并返回 $"{(Enabled ? "●" : "○")} {Name} · {Version}" + (Error is null ? "" : " · 文件异常")。
    public override string ToString() => $"{(Enabled
            ? "●"
            : "○")} {Name} · {Version}" + (Error is null
        ? ""
        : " · 文件异常");
}

// [实现] 数据约定：LocalResourceUpdate 汇集 Current、Available，供该模块传递状态。
public sealed record LocalResourceUpdate(LocalResourceInfo Current, ResourceRelease Available)
{
    // [实现] ToString 的实现：计算并返回 $"{Current.Name}：{Current.Version} → {Available.Number}"。
    public override string ToString() => $"{Current.Name}：{Current.Version} → {Available.Number}";
}

// 本地资源管理：扫描模组、光影和资源包，在文件变更前后校验依赖并保留事务恢复信息。
public sealed class LocalResourceService(
    string gameDirectory,
    string loader,
    string gameVersion,
    string? libraryContextDirectory = null
)
{
    public string GameDirectory { get; } = Path.GetFullPath(gameDirectory);
    private string Manifest => ResourceInstaller.SafeTarget(GameDirectory, ".pcl-resources.json");

    // [实现] 功能：列出实例中的模组、光影和资源包。实现：识别启停后缀，计算文件身份，按当前加载器读取元数据，并把损坏或不支持的文件原因返回界面。
    public IReadOnlyList<LocalResourceInfo> Scan()
    {
        var managed = File.Exists(Manifest)
            ? JsonConvert.DeserializeObject<List<ManagedResource>>(
                File.ReadAllText(Manifest)
            ) ?? []
            : [];
        var result = new List<LocalResourceInfo>();
        foreach (var kind in new[]
        {
            ResourceKind.Mod,
            ResourceKind.Shader,
            ResourceKind.ResourcePack
        }

        )
        {
            // [实现] 对 new[] { ResourceKind.Mod, ResourceKind.Shader, ResourceKind.ResourcePack } 中的每一项：准备 var folder = ResourceInstaller.SafeTarget( GameDirectory, ResourceInstaller.Folder(kind) )；按 !Directory.Exists(folder) 分支处理；遍历 Directory.EnumerateFiles(folder) 处理 path。
            var folder = ResourceInstaller.SafeTarget(
                GameDirectory,
                ResourceInstaller.Folder(kind)
            );
            if (!Directory.Exists(folder))
                continue;
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                // [实现] 对 Directory.EnumerateFiles(folder) 中的每一项：准备 var enabled = !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)；准备 var name = Path.GetFileName( enabled ? path : path[..^9] )；按 kind == ResourceKind.Mod ? !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !name.… 分支处理。
                var enabled = !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var name = Path.GetFileName(
                    enabled
                        ? path
                        : path[..^9]
                );
                if (kind == ResourceKind.Mod
                    ? !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase)
                    : !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                string display = Path.GetFileNameWithoutExtension(name), version = "本地", hash = "";
                long size = 0;
                var ids = new List<string>();
                string? error = null;
                try
                {
                    // [实现] 在异常保护范围内：执行 ResourceInstaller.SafeTarget( GameDirectory, Path.GetRelativePath(GameDirectory, path) )；准备 var input = File.OpenRead(path)；执行 size = input.Length。
                    ResourceInstaller.SafeTarget(
                        GameDirectory,
                        Path.GetRelativePath(GameDirectory, path)
                    );
                    using var input = File.OpenRead(path);
                    size = input.Length;
                    hash = Convert.ToHexString(
                        SHA1.HashData(input)
                    )
                        .ToLowerInvariant();
                    using var zip = ZipFile.OpenRead(path);
                    if (kind == ResourceKind.Mod)
                    {
                        // [实现] 条件成立（kind == ResourceKind.Mod）时：按 loader == "quilt" 分支处理；准备 var fabric = zip.GetEntry("fabric.mod.json")；准备 var forge = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml")。
                        if (loader == "quilt")
                            QuiltMetadataJson.CheckProductionArchive(zip);
                        var fabric = zip.GetEntry("fabric.mod.json");
                        var forge = zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml");
                        var legacy = zip.GetEntry("mcmod.info");
                        var quilt = zip.GetEntry("quilt.mod.json");
                        var lite = zip.GetEntry("litemod.json");
                        if (fabric is not null && !(loader == "quilt" && quilt is not null))
                        {
                            // [实现] 条件成立（fabric is not null && !(loader == "quilt" && quilt is not null)）时：准备 var reader = Reader(fabric)；准备 var json = JObject.Parse( reader.ReadToEnd() )；执行 ids.Add((string? )json["id"] ?? display)。
                            using var reader = Reader(fabric);
                            var json = JObject.Parse(
                                reader.ReadToEnd()
                            );
                            ids.Add((string? )json["id"] ?? display);
                            display = (string? )json["name"] ?? ids[0];
                            version = (string? )json["version"] ?? version;
                        }
                        else if (quilt is not null)
                        {
                            // [实现] 条件成立（quilt is not null）时：准备 var reader = Reader(quilt)；准备 var json = QuiltMetadataJson.Parse( reader.ReadToEnd() )["quilt_loader"] ?? throw new InvalidDa…；执行 ids.Add((string? )json["id"] ?? display)。
                            using var reader = Reader(quilt);
                            var json = QuiltMetadataJson.Parse(
                                reader.ReadToEnd()
                            )["quilt_loader"] ?? throw new InvalidDataException("Quilt 元数据缺失。");
                            ids.Add((string? )json["id"] ?? display);
                            display = (string? )json["metadata"]?["name"] ?? ids[0];
                            version = (string? )json["version"] ?? version;
                        }
                        else if (lite is not null)
                        {
                            // [实现] 条件成立（lite is not null）时：准备 var reader = Reader(lite)；准备 var json = JObject.Parse( reader.ReadToEnd() )；执行 ids.Add((string? )json["name"] ?? display)。
                            using var reader = Reader(lite);
                            var json = JObject.Parse(
                                reader.ReadToEnd()
                            );
                            ids.Add((string? )json["name"] ?? display);
                            display = (string? )json["displayName"] ?? ids[0];
                            version = (string? )json["version"] ?? version;
                        }
                        else if (forge is not null)
                        {
                            // [实现] 条件成立（forge is not null）时：准备 var reader = Reader(forge)；准备 var text = reader.ReadToEnd()；准备 var modBlocks = Regex.Matches(text, @"(?ms)^\s*\[\[mods\]\](.*?)(?=^\s*\[|\z)") .Select( x => x…。
                            using var reader = Reader(forge);
                            var text = reader.ReadToEnd();
                            var modBlocks = Regex.Matches(text, @"(?ms)^\s*\[\[mods\]\](.*?)(?=^\s*\[|\z)")
                                .Select(
                                    x => x.Groups[1].Value
                                );
                            ids.AddRange(
                                modBlocks.SelectMany(
                                    x => Regex.Matches(x, "(?m)^\\s*modId\\s*=\\s*\"([^\"]+)\"")
                                        .Select(
                                            m => m.Groups[1].Value
                                        )
                                )
                                    .Distinct()
                            );
                            var title = Regex.Match(text, "(?m)^\\s*displayName\\s*=\\s*\"([^\"]+)\"");
                            if (title.Success)
                                display = title.Groups[1].Value;
                            var v = Regex.Match(text, "(?m)^\\s*version\\s*=\\s*\"([^\"]+)\"");
                            if (v.Success)
                                version = v.Groups[1].Value;
                        }
                        else if (legacy is not null)
                        {
                            // [实现] 条件成立（legacy is not null）时：准备 var reader = Reader(legacy)；准备 var json = JToken.Parse( reader.ReadToEnd() )；准备 var info = json is JArray ? json.First : json["modList"]?.First。
                            using var reader = Reader(legacy);
                            var json = JToken.Parse(
                                reader.ReadToEnd()
                            );
                            var info = json is JArray
                                ? json.First
                                : json["modList"]?.First;
                            if (info is not null)
                            {
                                // [实现] 条件成立（info is not null）时：执行 ids.Add((string? )info["modid"] ?? display)；执行 display = (string? )info["name"] ?? display；执行 version = (string? )info["version"] ?? version。
                                ids.Add((string? )info["modid"] ?? display);
                                display = (string? )info["name"] ?? display;
                                version = (string? )info["version"] ?? version;
                            }
                        }
                        else if (Path.GetFileName(path) == OptiFabricService.OptiFineFile && OptiFabricService.IsPreset(GameDirectory))
                        {
                            // [实现] 条件成立（Path.GetFileName(path) == OptiFabricService.OptiFineFile && OptiFabricService.IsPreset(GameDire…）时：执行 OptiFabricService.ValidatePreset(GameDirectory)；执行 ids.Add("optifine")；执行 display = "OptiFine"。
                            OptiFabricService.ValidatePreset(GameDirectory);
                            ids.Add("optifine");
                            display = "OptiFine";
                            version = "1.20.1 HD U I6";
                        }
                        else
                            error = "缺少可识别的模组元数据";
                    }
                }
                catch (Exception ex)when (ex is IOException or InvalidDataException or JsonException)
                {
                    // [实现] 捕获 (Exception ex) 后：执行 error = ex.Message。
                    error = ex.Message;
                }

                var known = managed.FirstOrDefault(
                    x => x.Kind == kind && string.Equals(x.FileName, name, StringComparison.Ordinal)
                );
                if (known is not null)
                {
                    // [实现] 条件成立（known is not null）时：执行 display = known.Name；执行 version = known.Version。
                    display = known.Name;
                    version = known.Version;
                }

                result.Add(
                    new(path, kind, display, version, hash, size, enabled, ids, error, known)
                );
            }
        }

        return result.OrderBy(
            x => x.Kind
        )
            .ThenBy(
                x => x.Name,
                StringComparer.OrdinalIgnoreCase
            )
            .ToArray();
    }

    // [实现] Reader 的实现：按 entry.Length > 2 * 1024 * 1024 分支处理；返回 new( entry.Open() )。
    private static StreamReader Reader(ZipArchiveEntry entry)
    {
        if (entry.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("模组元数据过大。");
        return new(
            entry.Open()
        );
    }

    // [实现] Lease 的实现：计算并返回 new( ResourceInstaller.SafeTarget(GameDirectory, ".resources.lock"), FileMode.OpenOrCreate, Fil…。
    private FileStream Lease()
    {
        var lease = new FileStream(
            ResourceInstaller.SafeTarget(GameDirectory, ".resources.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        try
        {
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

    // [实现] CurrentSelection 的实现：准备 var wanted = paths.Select(Path.GetFullPath) .ToHashSet(StringComparer.Ordinal)；准备 var all = Scan()；准备 var selected = all.Where( x => wanted.Contains(x.Path) ) .ToArray()。
    private IReadOnlyList<LocalResourceInfo> CurrentSelection(IEnumerable<string> paths)
    {
        var wanted = paths.Select(Path.GetFullPath)
            .ToHashSet(StringComparer.Ordinal);
        var all = Scan();
        var selected = all.Where(
            x => wanted.Contains(x.Path)
        )
            .ToArray();
        if (selected.Length != wanted.Count)
            throw new IOException("所选资源已发生变化，请刷新。");
        return selected;
    }

    // [实现] 功能：批量切换本地资源启停。实现：取得目录锁后改名，再检查整个启用集合；若缺失前置或发生冲突，按逆序恢复全部改名。
    public void SetEnabled(IEnumerable<string> paths, bool enabled)
    {
        using var lease = Lease();
        var selected = CurrentSelection(paths)
            .Where(
                x => x.Enabled != enabled
            )
            .ToArray();
        if (selected.Any(
            x => x.Error is not null
        ))
            throw new InvalidDataException("所选资源中有损坏文件，请先检查。");
        var moves = new List<(string From, string To)>();
        try
        {
            // [实现] 在异常保护范围内：遍历 selected 处理 item；执行 ValidateEnabledResources()；执行 SynchronizeManifest()。
            foreach (var item in selected)
            {
                // [实现] 对 selected 中的每一项：准备 var destination = enabled ? item.Path[..^9] : item.Path + ".disabled"；执行 ResourceInstaller.SafeTarget( GameDirectory, Path.GetRelativePath(GameDirectory, destination) )；执行 File.Move(item.Path, destination, false)。
                var destination = enabled
                    ? item.Path[..^9]
                    : item.Path + ".disabled";
                ResourceInstaller.SafeTarget(
                    GameDirectory,
                    Path.GetRelativePath(GameDirectory, destination)
                );
                File.Move(item.Path, destination, false);
                moves.Add((item.Path, destination));
            }

            ValidateEnabledResources();
            SynchronizeManifest();
            ResourceHistory.Record(
                GameDirectory,
                enabled
                    ? "批量启用"
                    : "批量停用",
                selected.Select(
                    x => Path.GetFileName(x.Path)
                )
            );
        }
        catch
        {
            // [实现] 捕获 空 后：遍历 moves.AsEnumerable() .Reverse() 处理 move；重新抛出当前异常。
            foreach (var move in moves.AsEnumerable()
                .Reverse())
                File.Move(move.To, move.From);
            throw;
        }
    }

    // [实现] 功能：移除资源并保留恢复副本。实现：先移动到唯一备份目录，再检查剩余模组依赖并写恢复清单；失败则还原原路径。
    public string Remove(IEnumerable<string> paths, CancellationToken ct = default, string? operationId = null)
    {
        using var lease = Lease();
        var selected = CurrentSelection(paths);
        if (selected.Any(
            x => x.Kind == ResourceKind.Shader
        ))
        {
            if (selected.Any(
                x => x.Kind != ResourceKind.Shader || x.Managed is null
            ))
            {
                throw new IOException("请单独选择受管理的光影包卸载；未修改任何文件。");
            }

            return new ShaderRemovalTransaction(GameDirectory).RemoveManaged(
                selected.Select(
                    x => x.Managed!
                )
                    .ToArray(),
                ct,
                operationId
            );
        }

        var backup = ResourceInstaller.SafeTarget(
            GameDirectory,
            ".pcl-backups/resources-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(backup);
        var moved = new List<(string From, string To)>();
        try
        {
            // [实现] 在异常保护范围内：遍历 selected 处理 item；执行 ValidateEnabledResources()；执行 JsonFiles.Write( Path.Combine(backup, "rollback.json"), moved.Select( x => new { target = Path.…。
            foreach (var item in selected)
            {
                // [实现] 对 selected 中的每一项：准备 var target = Path.Combine( backup, moved.Count.ToString() )；执行 File.Move(item.Path, target)；执行 moved.Add((item.Path, target))。
                var target = Path.Combine(
                    backup,
                    moved.Count.ToString()
                );
                File.Move(item.Path, target);
                moved.Add((item.Path, target));
            }

            ValidateEnabledResources();
            JsonFiles.Write(
                Path.Combine(backup, "rollback.json"),
                moved.Select(
                    x => new
                    {
                        target = Path.GetRelativePath(GameDirectory, x.From),
                        backup = Path.GetFileName(x.To)
                    }
                )
            );
            SynchronizeManifest();
            ResourceHistory.Record(
                GameDirectory,
                "批量移至备份",
                selected.Select(
                    x => Path.GetFileName(x.Path)
                ),
                backup
            );
            return backup;
        }
        catch
        {
            // [实现] 捕获 空 后：遍历 moved.AsEnumerable() .Reverse() 处理 move；重新抛出当前异常。
            foreach (var move in moved.AsEnumerable()
                .Reverse())
                File.Move(move.To, move.From);
            throw;
        }
    }

    // [实现] SynchronizeManifest 的实现：准备 var rows = Scan() .Where( x => x.Managed is not null ) .Select( x => x.Managed!with { Enabled =…；执行 JsonFiles.Write(Manifest, rows)。
    private void SynchronizeManifest()
    {
        var rows = Scan()
            .Where(
                x => x.Managed is not null
            )
            .Select(
                x => x.Managed!with
                {
                    Enabled = x.Enabled
                }
            )
            .ToArray();
        JsonFiles.Write(Manifest, rows);
    }

    // [实现] ValidateFabricSet 的实现：按 loader != "fabric" 分支处理；准备 var optifabricPreset = OptiFabricService.IsPreset(GameDirectory)；按 optifabricPreset 分支处理。
    private IReadOnlyList<string> ValidateFabricSet(CancellationToken ct)
    {
        if (loader != "fabric")
            return[];
        var optifabricPreset = OptiFabricService.IsPreset(GameDirectory);
        if (optifabricPreset)
            OptiFabricService.ValidatePreset(GameDirectory);
        var files = Scan()
            .Where(
                x => x.Kind == ResourceKind.Mod && x.Enabled && (!optifabricPreset || Path.GetFileName(x.Path) != OptiFabricService.OptiFineFile)
            )
            .Select(
                x => x.Path
            )
            .ToArray();
        var available = new Dictionary<string, string>
        {
            ["minecraft"] = gameVersion,
            ["java"] = File.Exists(
                Path.Combine(GameDirectory, "version.json")
            )
                ? VersionCatalog.JavaMajor(
                    JsonFiles.Read(
                        Path.Combine(GameDirectory, "version.json")
                    )
                )
                    .ToString()
                : "21",
            ["fabricloader"] = File.Exists(
                Path.Combine(GameDirectory, "profile.json")
            )
                ? (string? )JsonFiles.Read(
                    Path.Combine(GameDirectory, "profile.json")
                )["loaderVersion"] ?? "0"
                : "0"
        };
        return FabricDependencySet.Require(
            FabricDependencySet.Resolve(files, GameDirectory, available, ct)
        );
    }

    // [实现] 功能：统一执行本地依赖预检。实现：根据加载器分派到 Fabric、Quilt 或 Forge/NeoForge 分析器，使用实例游戏、Java 和加载器版本返回诊断。
    public IReadOnlyList<string> ValidateEnabledResources(CancellationToken ct = default)
    {
        if (loader == "fabric")
        {
            return ValidateFabricSet(ct);
        }

        if (loader == "quilt")
        {
            // [实现] 条件成立（loader == "quilt"）时：准备 var profile = JsonFiles.Read( ResourceInstaller.SafeTarget(GameDirectory, "profile.json") )；准备 var quiltMetadata = JsonFiles.Read( ResourceInstaller.SafeTarget(GameDirectory, "version.json")…；返回 QuiltDependencies.Validate( GameDirectory, gameVersion, (string? )profile["loaderVersion"] ?? t…。
            var profile = JsonFiles.Read(
                ResourceInstaller.SafeTarget(GameDirectory, "profile.json")
            );
            var quiltMetadata = JsonFiles.Read(
                ResourceInstaller.SafeTarget(GameDirectory, "version.json")
            );
            return QuiltDependencies.Validate(
                GameDirectory,
                gameVersion,
                (string? )profile["loaderVersion"] ?? throw new InvalidDataException("Quilt 版本缺失。"),
                VersionCatalog.JavaMajor(quiltMetadata),
                libraryContextDirectory: libraryContextDirectory
            )
                .Warnings;
        }

        if (loader is not ("forge" or "neoforge"))
            return[];
        var files = Scan()
            .Where(
                x => x.Kind == ResourceKind.Mod && x.Enabled
            )
            .Select(
                x => x.Path
            )
            .ToArray();
        if (files.Length == 0)
            return[];
        var receipt = JsonFiles.Read(
            ResourceInstaller.SafeTarget(GameDirectory, "profile.json")
        );
        var loaderVersion = (string? )receipt["loaderVersion"] ?? throw new InvalidDataException("实例加载器版本缺失。");
        var metadata = JsonFiles.Read(
            ResourceInstaller.SafeTarget(GameDirectory, "version.json")
        );
        var fmlVersion = metadata["libraries"]?.Select(
            x => (string? )x["name"] ?? ""
        )
            .FirstOrDefault(
                x => x.StartsWith("net.neoforged.fancymodloader:loader:", StringComparison.Ordinal)
            )?.Split(':')[2];
        if (fmlVersion is null && (loader == "forge" || gameVersion == "1.20.1"))
            fmlVersion = loaderVersion;
        return ForgeModReader.Validate(
            files,
            loader,
            gameVersion,
            loaderVersion,
            VersionCatalog.JavaMajor(metadata),
            fmlVersion
        );
    }

    // [实现] CheckUpdatesAsync 的实现：准备 var result = new List<LocalResourceUpdate>()；准备 var ignored = ReadIgnored()；遍历 Scan() .Where( x => x.Error is null && x.Sha1.Length == 40 ) 处理 item。
    public async Task<IReadOnlyList<LocalResourceUpdate>> CheckUpdatesAsync(ResourceCatalog catalog, CancellationToken ct = default)
    {
        var result = new List<LocalResourceUpdate>();
        var ignored = ReadIgnored();
        foreach (var item in Scan()
            .Where(
                x => x.Error is null && x.Sha1.Length == 40
            ))
        {
            // [实现] 对 Scan() .Where( x => x.Error is null && x.Sha1.Length == 40 ) 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var current = await catalog.VersionFromHashAsync(item.Sha1, ct)；按 current is null 分支处理。
            ct.ThrowIfCancellationRequested();
            var current = await catalog.VersionFromHashAsync(item.Sha1, ct);
            if (current is null)
                continue;
            var releases = await catalog.VersionsAsync(current.ProjectId, item.Kind, gameVersion, loader, ct);
            var latest = releases.FirstOrDefault(
                x => x.Channel == "release"
            ) ?? releases.FirstOrDefault();
            if (latest is not null && latest.Id != current.Id && latest.Published > current.Published && !ignored.Contains(latest.Id))
                result.Add(
                    new(item, latest)
                );
        }

        return result;
    }

    // [实现] ReadIgnored 的实现：准备 var path = ResourceInstaller.SafeTarget(GameDirectory, ".pcl-ignored-updates.json")；返回 File.Exists(path) ? JsonConvert.DeserializeObject<HashSet<string>>( File.ReadAllText(path) ) ??…。
    private HashSet<string> ReadIgnored()
    {
        var path = ResourceInstaller.SafeTarget(GameDirectory, ".pcl-ignored-updates.json");
        return File.Exists(path)
            ? JsonConvert.DeserializeObject<HashSet<string>>(
                File.ReadAllText(path)
            ) ?? []
            : [];
    }

    // [实现] IgnoreUpdate 的实现：准备 var ids = ReadIgnored()；执行 ids.Add(versionId)；执行 JsonFiles.Write( ResourceInstaller.SafeTarget(GameDirectory, ".pcl-ignored-updates.json"), ids …。
    public void IgnoreUpdate(string versionId)
    {
        var ids = ReadIgnored();
        ids.Add(versionId);
        JsonFiles.Write(
            ResourceInstaller.SafeTarget(GameDirectory, ".pcl-ignored-updates.json"),
            ids
        );
    }

    // [实现] ApplyUpdateAsync 的实现：准备 var current = Scan() .SingleOrDefault( x => x.Path == update.Current.Path && x.Sha1 == update.C…；按 !current.Enabled 分支处理；在作用域内使用并释放 var lease = Lease()。
    public async Task ApplyUpdateAsync(
        LocalResourceUpdate update,
        ResourceCatalog catalog,
        DownloadService downloads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var current = Scan()
            .SingleOrDefault(
                x => x.Path == update.Current.Path && x.Sha1 == update.Current.Sha1
            ) ?? throw new IOException("文件已变化，请重新检查更新。");
        if (!current.Enabled)
            throw new InvalidOperationException("请先启用资源后再更新。");
        // Adopt only the selected file, preserving its measured hash so the installer can perform a transactional replacement.
        using (var lease = Lease())
        {
            // [实现] 本段流程：准备 var list = File.Exists(Manifest) ? JsonConvert.DeserializeObject<List<ManagedResource>>( File.R…；执行 list.RemoveAll( x => x.Kind == current.Kind && x.FileName == Path.GetFileName(current.Path) )；执行 list.Add( new( update.Available.ProjectId, "imported-" + current.Sha1, current.Name, current.Ve…。
            var list = File.Exists(Manifest)
                ? JsonConvert.DeserializeObject<List<ManagedResource>>(
                    File.ReadAllText(Manifest)
                ) ?? []
                : [];
            list.RemoveAll(
                x => x.Kind == current.Kind && x.FileName == Path.GetFileName(current.Path)
            );
            list.Add(
                new(
                    update.Available.ProjectId,
                    "imported-" + current.Sha1,
                    current.Name,
                    current.Version,
                    current.Kind,
                    Path.GetFileName(current.Path),
                    current.Sha1,
                    current.Size,
                    current.Enabled,
                    DateTimeOffset.UtcNow,
                    []
                )
            );
            JsonFiles.Write(Manifest, list);
        }

        var target = new GameProfile(
            "update",
            "update",
            gameVersion,
            loader,
            GameDirectory,
            Path.Combine(GameDirectory, "version.json")
        );
        var plan = await ResourceDependencyPlanner.ResolveAsync(catalog, downloads, update.Available, current.Kind, target, progress, ct);
        await new ResourceInstaller(GameDirectory, downloads, loader, gameVersion).InstallAsync(plan, progress, ct);
    }
}
