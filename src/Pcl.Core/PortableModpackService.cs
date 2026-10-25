using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：PackImportOptions 汇集 Minecraft、Loader、LoaderVersion、IncludeOptional、CurseForgeApiKey、ManualFiles、SelectedOptionalFiles，供该模块传递状态。
public sealed record PackImportOptions(
    string? Minecraft = null,
    string Loader = "vanilla",
    string LoaderVersion = "",
    bool IncludeOptional = false,
    string? CurseForgeApiKey = null,
    IReadOnlyDictionary<string, string>? ManualFiles = null,
    IReadOnlyCollection<string>? SelectedOptionalFiles = null
);
// [实现] 数据约定：PackOptionalFile 汇集 Id、Name、Size，供该模块传递状态。
public sealed record PackOptionalFile(string Id, string Name, long? Size);
// [实现] 数据约定：PackArchiveFile 汇集 Entry、Target，供该模块传递状态。
public sealed record PackArchiveFile(string Entry, string Target);
// [实现] 数据约定：CursePackFile 汇集 ProjectId、FileId、Required，供该模块传递状态。
public sealed record CursePackFile(long ProjectId, long FileId, bool Required)
{
    public string Key => ProjectId + ":" + FileId;
    public string Page => $"https://www.curseforge.com/projects/{ProjectId}/files/{FileId}";

    // [实现] ToString 的实现：计算并返回 $"{(Required ? "必需" : "可选")} · {ProjectId} / {FileId}"。
    public override string ToString() => $"{(Required
            ? "必需"
            : "可选")} · {ProjectId} / {FileId}";
}

// [实现] 数据约定：PortablePackPlan 汇集 Format、Name、Version、Minecraft、Loader、LoaderVersion、Overrides、CurseFiles、Warnings，供该模块传递状态。
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
    public IReadOnlyList<EmbeddedPackMetadata> EmbeddedMetadata { get; init; } = [];
}

// [实现] 数据约定：PackExportOptions 汇集 Name、Version、IncludeSaves、IncludeOptions、IncludedFolders，供该模块传递状态。
public sealed record PackExportOptions(
    string Name,
    string Version = "1.0.0",
    bool IncludeSaves = false,
    bool IncludeOptions = false,
    IReadOnlyList<string>? IncludedFolders = null
);
/// <summary>Converts archive data to the audited mrpack installer. Archive launch commands are never imported or executed.</summary>
public sealed class PortableModpackService(MinecraftService service)
{
    private static readonly string[] DataFolders = [
        "mods",
        "config",
        "resourcepacks",
        "shaderpacks",
        "datapacks",
        "scripts",
        "kubejs",
        "defaultconfigs",
        "resources",
        "saves"
    ];
    public static IReadOnlyList<string> ExportFolders => DataFolders.Where(
        x => x != "saves"
    )
        .ToArray();

    private static readonly HashSet<string> PrivateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "accounts.json",
        "launcher_accounts.json",
        "launcher_profiles.json",
        "servers.dat",
        "servers.dat_old",
        "usercache.json",
        "usernamecache.json",
        "session.lock",
        "auth.json",
        "credentials.json",
        "tokens.json"
    };
    // [实现] ValidateArchive 的实现：按 zip.Entries.Count > 100000 分支处理；准备 long total = 0；准备 var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)。
    public static void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count > 100000)
            throw new InvalidDataException("压缩包文件数量过多。");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            // [实现] 对 zip.Entries 中的每一项：准备 var name = entry.FullName.TrimEnd('/')；按 name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.A… 分支处理；按 (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000 分支处理。
            var name = entry.FullName.TrimEnd('/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) || name.Split('/')
                .Any(
                    x => x is ".." or "." or ""
                ))
                throw new InvalidDataException("压缩包含不安全路径：" + entry.FullName);
            if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                throw new InvalidDataException("压缩包含符号链接。");
            if (!names.Add(name))
                throw new InvalidDataException("压缩包含重复或大小写冲突路径：" + name);
            total = checked(total + entry.Length);
            if (entry.Length > 512L * 1024 * 1024 || total > 8L * 1024 * 1024 * 1024)
                throw new InvalidDataException("压缩包解压体积超出限制。");
        }
    }

    // [实现] ReadJson 的实现：按 entry.Length > 16 * 1024 * 1024 分支处理；准备 var reader = new StreamReader( entry.Open() )；返回 JObject.Parse( reader.ReadToEnd() )。
    private static JObject ReadJson(ZipArchiveEntry entry)
    {
        if (entry.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("整合包元数据过大。");
        using var reader = new StreamReader(
            entry.Open()
        );
        return JObject.Parse(
            reader.ReadToEnd()
        );
    }

    // [实现] Prefix 的实现：计算并返回 path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : ""。
    private static string Prefix(string path) => path.Contains('/')
        ? path[..(path.LastIndexOf('/') + 1)]
        : "";
    // [实现] AllowedData 的实现：准备 var parts = path.Split('/')；按 parts.Any( x => x.StartsWith('.') || PrivateNames.Contains(x) ) || path.EndsWith(".log", String… 分支处理；返回 DataFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || (privateData && path is "op…。
    private static bool AllowedData(string path, bool privateData = false)
    {
        var parts = path.Split('/');
        if (parts.Any(
            x => x.StartsWith('.') || PrivateNames.Contains(x)
        ) || path.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return false;
        return DataFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase) || (privateData && path is "options.txt" or "optionsof.txt" or "optionsshaders.txt");
    }

    // [实现] Inspect 的实现：执行 options ??= new()；准备 var zip = ZipFile.OpenRead(archive)；执行 ValidateArchive(zip)。
    public static PortablePackPlan Inspect(string archive, PackImportOptions? options = null)
    {
        options ??= new();
        using var zip = ZipFile.OpenRead(archive);
        ValidateArchive(zip);
        var embedded = new List<EmbeddedPackMetadata>();
        var warnings = new List<string>();
        var curse = new List<CursePackFile>();
        var optionalFiles = new List<PackOptionalFile>();
        var curseKeys = new HashSet<string>();
        string format = "普通 ZIP", name = Path.GetFileNameWithoutExtension(archive), version = "imported", minecraft = options.Minecraft ?? "", loader = options.Loader, loaderVersion = options.LoaderVersion, prefix = "";
        var manifests = zip.Entries.Where(
            x => Path.GetFileName(x.FullName)is "modrinth.index.json" or "mcbbs.packmeta" or "mmc-pack.json" or "modpack.json" or "manifest.json"
        )
            .OrderBy(
                x => x.FullName.Count(
                    c => c == '/'
                )
            )
            .ToArray();
        var candidateRoots = manifests.Where(
            x => x.FullName.Count(
                c => c == '/'
            ) <= 1
        )
            .Select(
                x => Prefix(x.FullName)
            )
            .Distinct()
            .ToArray();
        if (candidateRoots.Length > 1)
            throw new InvalidDataException("压缩包包含多个实例，请分别导出后导入。");
        var entry = manifests.FirstOrDefault(
            x => Path.GetFileName(x.FullName) == "modrinth.index.json"
        ) ?? manifests.FirstOrDefault(
            x => Path.GetFileName(x.FullName) == "mcbbs.packmeta"
        ) ?? manifests.FirstOrDefault();
        string contentPrefix;
        // [实现] SetLoader 的实现：准备 var normalized = id switch { "fabric-loader" or "net.fabricmc.fabric-loader" or "fabric" => "fa…；按 loader != "vanilla" && loader != normalized 分支处理；执行 loader = normalized。
        void SetLoader(string id, string value)
        {
            var normalized = id switch
            {
                "fabric-loader" or "net.fabricmc.fabric-loader" or "fabric" => "fabric",
                "quilt-loader" or "org.quiltmc.quilt-loader" or "quilt" => "quilt",
                "forge" or "net.minecraftforge" => "forge",
                "neoforge" or "net.neoforged" => "neoforge",
                _ => throw new NotSupportedException("整合包组件尚未适配：" + id)
            };
            if (loader != "vanilla" && loader != normalized)
                throw new NotSupportedException("整合包包含多个加载器，需要单独适配。");
            loader = normalized;
            loaderVersion = value;
        }

        if (entry is not null)
        {
            // [实现] 条件成立（entry is not null）时：执行 prefix = Prefix(entry.FullName)；准备 var json = ReadJson(entry)；执行 name = (string? )json["name"] ?? name。
            prefix = Prefix(entry.FullName);
            var json = ReadJson(entry);
            name = (string? )json["name"] ?? name;
            version = (string? )json["version"] ?? version;
            switch (Path.GetFileName(entry.FullName))
            {
                case "modrinth.index.json":
                    var all = ModpackService.Parse(json, true);
                    var mr = ModpackService.Parse(json, options.IncludeOptional, options.SelectedOptionalFiles);
                    return new(
                        "Modrinth",
                        mr.Name,
                        mr.Version,
                        mr.Minecraft,
                        mr.Loader,
                        mr.LoaderVersion,
                        Array.Empty<PackArchiveFile>(),
                        curse,
                        warnings
                    )
                    {
                        OptionalFiles = all.Files.Where(
                            x => x.Optional
                        )
                            .Select(
                                x => new PackOptionalFile(x.Path, x.Path, x.Size)
                            )
                            .ToArray()
                    };
                case "mmc-pack.json":
                    format = "MultiMC";
                    loader = "vanilla";
                    loaderVersion = "";
                    if ((int? )json["formatVersion"] != 1)
                        throw new NotSupportedException("不支持此 MultiMC 格式版本。");
                    foreach (var component in json["components"] ?? new JArray())
                    {
                        // [实现] 对 json["components"] ?? new JArray() 中的每一项：准备 var id = (string? )component["uid"] ?? ""；准备 var value = (string? )component["version"] ?? ""；按 id == "net.minecraft" 分支处理。
                        var id = (string? )component["uid"] ?? "";
                        var value = (string? )component["version"] ?? "";
                        if (id == "net.minecraft")
                            minecraft = value;
                        else if (id.StartsWith("org.lwjgl", StringComparison.Ordinal))
                            warnings.Add("LWJGL 将按 Mac 兼容性重新匹配。");
                        else if (id is "net.fabricmc.fabric-loader" or "org.quiltmc.quilt-loader" or "net.minecraftforge" or "net.neoforged")
                            SetLoader(id, value);
                        else
                        {
                            // [实现] 前一条件不成立时：执行 VersionCatalog.ValidateId(id)；准备 var custom = zip.GetEntry(prefix + "patches/" + id + ".json") ?? throw new NotSupportedExceptio…；执行 _ = PackMetadataCompatibility.MultiMc(custom, (JArray)json["components"]!)。
                            VersionCatalog.ValidateId(id);
                            var custom = zip.GetEntry(prefix + "patches/" + id + ".json") ?? throw new NotSupportedException("缺少自定义组件定义：" + id);
                            _ = PackMetadataCompatibility.MultiMc(custom, (JArray)json["components"]!);
                            warnings.Add("自定义组件：" + id + "；将保留额外校验库。");
                        }
                    }

                    if (zip.Entries.Any(
                        x => x.FullName.StartsWith(prefix + "jarmods/", StringComparison.Ordinal) && !x.FullName.EndsWith('/')
                    ))
                        throw new NotSupportedException("MultiMC jarmods 修改客户端，尚不能等效重建。");
                    foreach (var patch in zip.Entries.Where(
                        x => x.FullName.StartsWith(prefix + "patches/", StringComparison.Ordinal) && !x.FullName.EndsWith('/')
                    ))
                    {
                        // [实现] 对 zip.Entries.Where( x => x.FullName.StartsWith(prefix + "patches/", StringComparison.Ordinal) &&… 中的每一项：按 !patch.FullName.EndsWith(".json", StringComparison.Ordinal) 分支处理；执行 embedded.Add( PackMetadataCompatibility.MultiMc(patch, (JArray)json["components"]!) )。
                        if (!patch.FullName.EndsWith(".json", StringComparison.Ordinal))
                            throw new NotSupportedException("未知 MultiMC 补丁文件：" + patch.FullName);
                        embedded.Add(
                            PackMetadataCompatibility.MultiMc(patch, (JArray)json["components"]!)
                        );
                    }

                    if (embedded.Count > 0)
                        warnings.Add("标准组件与官方元数据核对；额外校验库、入口与限定属性保留，不支持的修改会停止。");
                    contentPrefix = zip.Entries.Any(
                        x => x.FullName.StartsWith(prefix + ".minecraft/", StringComparison.Ordinal)
                    )
                        ? prefix + ".minecraft/"
                        : prefix + "minecraft/";
                    warnings.Add("不导入 MultiMC 启动命令、Java 路径和 JVM 参数。");
                    break;
                case "modpack.json":
                    format = "HMCL";
                    minecraft = (string? )json["gameVersion"] ?? minecraft;
                    contentPrefix = prefix + "minecraft/";
                    var hmclPack = zip.GetEntry(contentPrefix + "pack.json");
                    if (hmclPack is not null && string.IsNullOrWhiteSpace(minecraft))
                        minecraft = (string? )PackMetadataCompatibility.Read(hmclPack)["jar"] ?? "";
                    var versionFiles = zip.Entries.Where(
                        x => x.FullName.StartsWith(contentPrefix + "versions/", StringComparison.Ordinal) && !x.FullName.EndsWith('/')
                    )
                        .ToArray();
                    if (hmclPack is not null && versionFiles.Length > 0)
                        throw new InvalidDataException("HMCL 同时包含 pack.json 和 versions，无法确定启动定义。");
                    if (hmclPack is not null)
                        versionFiles = [
                            hmclPack
                        ];
                    if (versionFiles.Length > 0)
                    {
                        // [实现] 条件成立（versionFiles.Length > 0）时：按 versionFiles.Length != 1 || !versionFiles[0].FullName.EndsWith(".json", StringComparison.Ordina… 分支处理；准备 var descriptor = PackMetadataCompatibility.Hmcl(versionFiles[0], minecraft, out var inferredLoa…；按 options.Loader != "vanilla" && (options.Loader != inferredLoader || options.LoaderVersion != in… 分支处理。
                        if (versionFiles.Length != 1 || !versionFiles[0].FullName.EndsWith(".json", StringComparison.Ordinal))
                            throw new NotSupportedException("HMCL 包含多个版本或自定义客户端文件，尚不能等效重建。");
                        var descriptor = PackMetadataCompatibility.Hmcl(versionFiles[0], minecraft, out var inferredLoader, out var inferredVersion);
                        if (options.Loader != "vanilla" && (options.Loader != inferredLoader || options.LoaderVersion != inferredVersion))
                            throw new InvalidDataException("手动组件与 HMCL 声明冲突。");
                        loader = inferredLoader;
                        loaderVersion = inferredVersion;
                        embedded.Add(descriptor);
                        warnings.Add("内置版本与官方定义核对；保留额外校验库、入口及限定属性，自定义客户端替换仍会停止。");
                    }

                    warnings.Add("HMCL 自定义启动设置不自动执行。若含模组，请在导入前指定对应加载器。");
                    break;
                default:
                    if (Path.GetFileName(entry.FullName) == "mcbbs.packmeta" || json["addons"] is JArray)
                    {
                        // [实现] 条件成立（Path.GetFileName(entry.FullName) == "mcbbs.packmeta" || json["addons"] is JArray）时：执行 format = "MCBBS"；执行 loader = "vanilla"；执行 loaderVersion = ""。
                        format = "MCBBS";
                        loader = "vanilla";
                        loaderVersion = "";
                        foreach (var addon in json["addons"] ?? new JArray())
                        {
                            // [实现] 对 json["addons"] ?? new JArray() 中的每一项：准备 var id = (string? )addon["id"] ?? ""；准备 var value = (string? )addon["version"] ?? ""；按 id == "game" 分支处理。
                            var id = (string? )addon["id"] ?? "";
                            var value = (string? )addon["version"] ?? "";
                            if (id == "game")
                                minecraft = value;
                            else
                                SetLoader(id, value);
                        }

                        contentPrefix = prefix + "overrides/";
                        warnings.Add("不导入 launchInfo 中的外部命令或 Java 参数。");
                    }
                    else
                    {
                        // [实现] 前一条件不成立时：执行 format = "CurseForge"；执行 loader = "vanilla"；执行 loaderVersion = ""。
                        format = "CurseForge";
                        loader = "vanilla";
                        loaderVersion = "";
                        if ((string? )json["manifestType"] != "minecraftModpack" || (int? )json["manifestVersion"] != 1)
                            throw new NotSupportedException("不支持此 CurseForge 清单格式。");
                        minecraft = (string? )json["minecraft"]?["version"] ?? "";
                        foreach (var component in json["minecraft"]?["modLoaders"] ?? new JArray())
                        {
                            // [实现] 对 json["minecraft"]?["modLoaders"] ?? new JArray() 中的每一项：准备 var id = (string? )component["id"] ?? ""；准备 var dash = id.IndexOf('-')；按 dash < 0 分支处理。
                            var id = (string? )component["id"] ?? "";
                            var dash = id.IndexOf('-');
                            if (dash < 0)
                                throw new InvalidDataException("加载器标识不完整。");
                            SetLoader(id[..dash], id[(dash + 1)..]);
                        }

                        var overrides = (string? )json["overrides"] ?? "overrides";
                        ModpackService.ValidatePath(overrides);
                        contentPrefix = prefix + overrides.TrimEnd('/') + "/";
                        foreach (var file in json["files"] ?? new JArray())
                        {
                            // [实现] 对 json["files"] ?? new JArray() 中的每一项：准备 var required = (bool? )file["required"] ?? true；准备 var project = (long? )file["projectID"] ?? 0；准备 var id = (long? )file["fileID"] ?? 0。
                            var required = (bool? )file["required"] ?? true;
                            var project = (long? )file["projectID"] ?? 0;
                            var id = (long? )file["fileID"] ?? 0;
                            if (project <= 0 || id <= 0)
                                throw new InvalidDataException("CurseForge 文件 ID 无效。");
                            var key = "curse:" + project + ":" + id;
                            if (!curseKeys.Add(key))
                                throw new InvalidDataException("整合包包含重复的 CurseForge 文件。");
                            if (!required)
                                optionalFiles.Add(
                                    new(key, $"CurseForge {project} / {id}", null)
                                );
                            if (required || (options.SelectedOptionalFiles is not null
                                ? options.SelectedOptionalFiles.Contains(key)
                                : options.IncludeOptional))
                                curse.Add(
                                    new(project, id, required)
                                );
                        }
                    }

                    break;
            }
        }
        else
        {
            // [实现] 前一条件不成立时：准备 var mc = zip.Entries.FirstOrDefault( x => x.FullName.Contains("/.minecraft/", StringComparison.…；执行 contentPrefix = mc is null ? "" : mc.FullName[..(mc.FullName.IndexOf(".minecraft/", StringCompa…；执行 warnings.Add("普通 ZIP 仅导入游戏数据；需指定游戏版本与加载器。启动器程序和脚本入口不会执行。")。
            var mc = zip.Entries.FirstOrDefault(
                x => x.FullName.Contains("/.minecraft/", StringComparison.Ordinal) || x.FullName.StartsWith(".minecraft/", StringComparison.Ordinal)
            );
            contentPrefix = mc is null
                ? ""
                : mc.FullName[..(mc.FullName.IndexOf(".minecraft/", StringComparison.Ordinal) + 11)];
            warnings.Add("普通 ZIP 仅导入游戏数据；需指定游戏版本与加载器。启动器程序和脚本入口不会执行。");
        }

        if (string.IsNullOrWhiteSpace(minecraft))
            throw new InvalidDataException("整合包未声明 Minecraft 版本，请在导入设置中填写。");
        VersionCatalog.ValidateId(minecraft);
        if (loader is not ("vanilla" or "fabric" or "quilt" or "forge" or "neoforge"))
            throw new NotSupportedException("此加载器尚未适配。");
        if (loader != "vanilla" && string.IsNullOrWhiteSpace(loaderVersion))
            throw new InvalidDataException("请指定加载器版本。");
        var files = new List<PackArchiveFile>();
        foreach (var item in zip.Entries.Where(
            x => x.FullName.StartsWith(contentPrefix, StringComparison.Ordinal) && !x.FullName.EndsWith('/')
        ))
        {
            // [实现] 对 zip.Entries.Where( x => x.FullName.StartsWith(contentPrefix, StringComparison.Ordinal) && !x.Fu… 中的每一项：准备 var target = item.FullName[contentPrefix.Length..]；按 !AllowedData(target, true) 分支处理；执行 ModpackService.ValidatePath(target)。
            var target = item.FullName[contentPrefix.Length..];
            if (!AllowedData(target, true))
                continue;
            ModpackService.ValidatePath(target);
            files.Add(
                new(item.FullName, target)
            );
        }

        if (files.GroupBy(
            x => x.Target,
            StringComparer.OrdinalIgnoreCase
        )
            .Any(
                x => x.Count() > 1
            ))
            throw new InvalidDataException("目标文件冲突。");
        if (loader == "vanilla" && files.Any(
            x => x.Target.StartsWith("mods/", StringComparison.Ordinal)
        ))
            throw new InvalidDataException("包中有模组但没有加载器信息，请先指定加载器。");
        if (optionalFiles.Select(
            x => x.Id
        )
            .Distinct()
            .Count() != optionalFiles.Count || curse.Select(
            x => x.Key
        )
            .Distinct()
            .Count() != curse.Count)
            throw new InvalidDataException("整合包包含重复的 CurseForge 文件。");
        if (options.SelectedOptionalFiles is not null && options.SelectedOptionalFiles.Any(
            x => !optionalFiles.Any(
                f => f.Id == x
            )
        ))
            throw new InvalidDataException("可选文件选择已失效，请重新检查整合包。");
        return new(format, name, version, minecraft, loader, loaderVersion, files, curse, warnings)
        {
            OptionalFiles = optionalFiles,
            EmbeddedMetadata = embedded
        };
    }

    // [实现] ConvertAsync 的实现：准备 var plan = Inspect(archive, options)；准备 var customChanges = await PackMetadataCompatibility.VerifyAsync(service, archive, plan, ct)；按 plan.Format == "Modrinth" 分支处理。
    public async Task<string> ConvertAsync(
        string archive,
        PackImportOptions options,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var plan = Inspect(archive, options);
        var customChanges = await PackMetadataCompatibility.VerifyAsync(service, archive, plan, ct);
        if (plan.Format == "Modrinth")
        {
            // Nested mrpack manifests are not valid per the format specification.
            ModpackService.Inspect(archive, options.IncludeOptional, options.SelectedOptionalFiles);
            return archive;
        }

        var cache = ResourceInstaller.SafeTarget(service.Paths.Root, "cache/modpacks/converted");
        Directory.CreateDirectory(cache);
        var output = Path.Combine(
            cache,
            Guid.NewGuid()
                .ToString("N") + ".mrpack"
        );
        try
        {
            // [实现] 在异常保护范围内：准备 var input = ZipFile.OpenRead(archive)；准备 var converted = ZipFile.Open(output, ZipArchiveMode.Create)；准备 var dependencies = new JObject { ["minecraft"] = plan.Minecraft }。
            using var input = ZipFile.OpenRead(archive);
            using var converted = ZipFile.Open(output, ZipArchiveMode.Create);
            var dependencies = new JObject
            {
                ["minecraft"] = plan.Minecraft
            };
            if (plan.Loader != "vanilla")
                dependencies[plan.Loader is "fabric" or "quilt"
                    ? plan.Loader + "-loader"
                    : plan.Loader] = plan.LoaderVersion;
            var index = new JObject
            {
                ["formatVersion"] = 1,
                ["game"] = "minecraft",
                ["versionId"] = plan.Version,
                ["name"] = plan.Name,
                ["dependencies"] = dependencies,
                ["files"] = new JArray()
            };
            await WriteJsonAsync(converted, "modrinth.index.json", index, ct);
            if (customChanges.Libraries.Count > 0 || customChanges.JvmArguments.Count > 0 || customChanges.MainClass is not null || (customChanges.Tweakers?.Count ?? 0) > 0)
                await WriteJsonAsync(
                    converted,
                    CustomPackMetadata.EntryName,
                    CustomPackMetadata.Serialize(customChanges),
                    ct
                );
            var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan.Overrides)
            {
                // [实现] 对 plan.Overrides 中的每一项：执行 ct.ThrowIfCancellationRequested()；执行 targetNames.Add(item.Target)；执行 await CopyEntry( input.GetEntry(item.Entry)!, converted.CreateEntry("overrides/" + item.Target)…。
                ct.ThrowIfCancellationRequested();
                targetNames.Add(item.Target);
                await CopyEntry(
                    input.GetEntry(item.Entry)!,
                    converted.CreateEntry("overrides/" + item.Target),
                    ct
                );
            }

            using var curse = string.IsNullOrWhiteSpace(options.CurseForgeApiKey) && (DownloadTasks.For(service.Paths)
                .Options.CurseForgeApiMirror.Length == 0 || DownloadTasks.For(service.Paths)
                .Options.ResourceSource == "official")
                ? null
                : new CurseForgeClient(options.CurseForgeApiKey ?? "", paths: service.Paths);
            var manualHashes = new JArray();
            for (var i = 0; i < plan.CurseFiles.Count; i++)
            {
                // [实现] 每次迭代（i < plan.CurseFiles.Count）：执行 ct.ThrowIfCancellationRequested()；准备 var file = plan.CurseFiles[i]；准备 string source。
                ct.ThrowIfCancellationRequested();
                var file = plan.CurseFiles[i];
                string source;
                string target;
                progress?.Report(
                    new(
                        $"读取 CurseForge {file.ProjectId}/{file.FileId}",
                        i,
                        plan.CurseFiles.Count,
                        0,
                        0
                    )
                );
                if (options.ManualFiles is not null && options.ManualFiles.TryGetValue(file.Key, out var local))
                {
                    // [实现] 条件成立（options.ManualFiles is not null && options.ManualFiles.TryGetValue(file.Key, out var local)）时：执行 source = Path.GetFullPath(local)；准备 var name = Path.GetFileName(source)；执行 ResourceInstaller.ValidateFileName(name)。
                    source = Path.GetFullPath(local);
                    var name = Path.GetFileName(source);
                    ResourceInstaller.ValidateFileName(name);
                    if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        throw new NotSupportedException("无 API 时仅可手动匹配 JAR 模组。");
                    using var jar = ZipFile.OpenRead(source);
                    ResourceInstaller.ValidateModArchive(jar, plan.Loader, plan.Minecraft);
                    target = "mods/" + name;
                    using var bytes = File.OpenRead(source);
                    manualHashes.Add(
                        new JObject
                        {
                            ["projectId"] = file.ProjectId,
                            ["fileId"] = file.FileId,
                            ["fileName"] = name,
                            ["sha256"] = Convert.ToHexString(
                                SHA256.HashData(bytes)
                            )
                                .ToLowerInvariant(),
                            ["source"] = "user-selected; remote identity not verified without API"
                        }
                    );
                }
                else
                {
                    // [实现] 前一条件不成立时：按 curse is null 分支处理；准备 var resolved = await curse.FileAsync(file.ProjectId, file.FileId, ct)；按 resolved.DownloadUrl is null 分支处理。
                    if (curse is null)
                        throw new InvalidOperationException($"缺少 CurseForge API Key 或手动文件映射：{file.Key}。官方页面：{file.Page}");
                    var resolved = await curse.FileAsync(file.ProjectId, file.FileId, ct);
                    if (resolved.DownloadUrl is null)
                        throw new InvalidOperationException("作者未允许自动下载，请从官方页面下载并手动匹配：" + file.Page);
                    target = resolved.Folder + "/" + resolved.Name;
                    source = Path.Combine(cache, resolved.Sha1);
                    await service.Downloads.DownloadAsync(
                        new(resolved.DownloadUrl, source, resolved.Size, resolved.Sha1),
                        ct
                    );
                }

                ModpackService.ValidatePath(target);
                if (!targetNames.Add(target))
                    throw new InvalidDataException("整合包文件目标冲突：" + target);
                using var from = File.OpenRead(source);
                using var to = converted.CreateEntry("overrides/" + target)
                    .Open();
                await from.CopyToAsync(to, ct);
            }

            await WriteJsonAsync(
                converted,
                "pcl-import-provenance.json",
                new JObject
                {
                    ["format"] = plan.Format,
                    ["warnings"] = new JArray(plan.Warnings),
                    ["manualFiles"] = manualHashes
                },
                ct
            );
            return output;
        }
        catch
        {
            // [实现] 捕获 空 后：执行 File.Delete(output)；重新抛出当前异常。
            File.Delete(output);
            throw;
        }
    }

    // [实现] InstallAsync 的实现：准备 var selectedPlan = Inspect(archive, options)；准备 string sourceHash；在作用域内使用并释放 var input = File.OpenRead(archive)。
    public async Task<GameProfile> InstallAsync(
        string archive,
        PackImportOptions options,
        string? instanceName = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var selectedPlan = Inspect(archive, options);
        string sourceHash;
        using (var input = File.OpenRead(archive))
            sourceHash = Convert.ToHexString(
                SHA256.HashData(input)
            )
                .ToLowerInvariant();
        var normalized = await ConvertAsync(archive, options, progress, ct);
        var result = await new ModpackService(service).InstallAsync(
            normalized,
            instanceName,
            options.IncludeOptional,
            progress,
            ct,
            Path.GetFullPath(normalized) == Path.GetFullPath(archive)
                ? options.SelectedOptionalFiles
                : null
        );
        var selected = options.SelectedOptionalFiles ?? (options.IncludeOptional
            ? selectedPlan.OptionalFiles.Select(
                x => x.Id
            )
                .ToArray()
            : []);
        JsonFiles.Write(
            Path.Combine(result.GameDirectory, "pcl-import-selection.json"),
            new
            {
                sourceSha256 = sourceHash,
                format = selectedPlan.Format,
                selectedOptionalFiles = selected.Order(StringComparer.Ordinal)
                    .ToArray()
            }
        );
        return result;
    }

    // [实现] CaptureDataHashes 的实现：准备 var hashes = new Dictionary<string, string>(StringComparer.Ordinal)；准备 var folders = new Stack<string>()；执行 folders.Push(root)。
    public static IReadOnlyDictionary<string, string> CaptureDataHashes(string root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.Count > 0)
            foreach (var path in Directory.EnumerateFileSystemEntries(
                folders.Pop()
            ))
            {
                // [实现] 对 Directory.EnumerateFileSystemEntries( folders.Pop() ) 中的每一项：准备 var relative = Path.GetRelativePath(root, path) .Replace('\\', '/')；按 !AllowedData(relative, true) 分支处理；执行 ResourceInstaller.SafeTarget(root, relative)。
                var relative = Path.GetRelativePath(root, path)
                    .Replace('\\', '/');
                if (!AllowedData(relative, true))
                    continue;
                ResourceInstaller.SafeTarget(root, relative);
                if (Directory.Exists(path))
                {
                    // [实现] 条件成立（Directory.Exists(path)）时：执行 folders.Push(path)；执行 continue;。
                    folders.Push(path);
                    continue;
                }

                using var input = File.OpenRead(path);
                hashes[relative] = Convert.ToHexString(
                    SHA256.HashData(input)
                )
                    .ToLowerInvariant();
            }

        return hashes;
    }

    // [实现] ChangedUserFiles 的实现：准备 var receipt = ResourceInstaller.SafeTarget(profile.GameDirectory, "pcl-modpack.json")；按 !File.Exists(receipt) || JsonFiles.Read(receipt)["packDataHashes"] is not JObject baseline 分支处理；返回 CaptureDataHashes(profile.GameDirectory) .Where( x => (string? )baseline[x.Key] != x.Value ) .S…。
    public static IReadOnlyList<string> ChangedUserFiles(GameProfile profile)
    {
        var receipt = ResourceInstaller.SafeTarget(profile.GameDirectory, "pcl-modpack.json");
        if (!File.Exists(receipt) || JsonFiles.Read(receipt)["packDataHashes"] is not JObject baseline)
            throw new InvalidOperationException("此实例没有原始文件基线。请先导出备份，再把新整合包安装为新实例；不能猜测哪些配置是用户修改。");
        return CaptureDataHashes(profile.GameDirectory)
            .Where(
                x => (string? )baseline[x.Key] != x.Value
            )
            .Select(
                x => x.Key
            )
            .ToArray();
    }

    // [实现] DeletedUserFiles 的实现：准备 var baseline = JsonFiles.Read( ResourceInstaller.SafeTarget(profile.GameDirectory, "pcl-modpack…；返回 baseline.Properties() .Select( x => x.Name ) .Where( x => AllowedData(x, true) && !File.Exists(…。
    public static IReadOnlyList<string> DeletedUserFiles(GameProfile profile)
    {
        var baseline = JsonFiles.Read(
            ResourceInstaller.SafeTarget(profile.GameDirectory, "pcl-modpack.json")
        )["packDataHashes"] as JObject ?? throw new InvalidOperationException("缺少文件基线。");
        return baseline.Properties()
            .Select(
                x => x.Name
            )
            .Where(
                x => AllowedData(x, true) && !File.Exists(
                    ResourceInstaller.SafeTarget(profile.GameDirectory, x)
                )
            )
            .ToArray();
    }

    /// <summary>Updates into a separate instance. The original remains the rollback copy, and changed user files are copied only after opt-in.</summary>
    public async Task<GameProfile> UpdateAsNewInstanceAsync(
        GameProfile current,
        string archive,
        PackImportOptions options,
        bool preserveUserChanges,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var changed = preserveUserChanges
            ? ChangedUserFiles(current)
            : Array.Empty<string>();
        var deleted = preserveUserChanges
            ? DeletedUserFiles(current)
            : Array.Empty<string>();
        var name = "pack-update-" + Guid.NewGuid()
            .ToString("N")[..12];
        var result = await InstallAsync(archive, options, name, progress, ct);
        try
        {
            // [实现] 在异常保护范围内：遍历 changed 处理 relative；遍历 deleted 处理 relative；执行 new LocalResourceService(result.GameDirectory, result.Loader, result.GameVersion).ValidateEnabl…。
            foreach (var relative in changed)
            {
                // [实现] 对 changed 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var from = ResourceInstaller.SafeTarget(current.GameDirectory, relative)；准备 var to = ResourceInstaller.SafeTarget(result.GameDirectory, relative)。
                ct.ThrowIfCancellationRequested();
                var from = ResourceInstaller.SafeTarget(current.GameDirectory, relative);
                var to = ResourceInstaller.SafeTarget(result.GameDirectory, relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(to)!
                );
                File.Copy(from, to, true);
            }

            foreach (var relative in deleted)
            {
                // [实现] 对 deleted 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var target = ResourceInstaller.SafeTarget(result.GameDirectory, relative)；按 File.Exists(target) 分支处理。
                ct.ThrowIfCancellationRequested();
                var target = ResourceInstaller.SafeTarget(result.GameDirectory, relative);
                if (File.Exists(target))
                    File.Delete(target);
            }

            new LocalResourceService(result.GameDirectory, result.Loader, result.GameVersion).ValidateEnabledResources();
            ModpackService.RegisterFiles(result.GameDirectory);
            JsonFiles.Write(
                Path.Combine(result.GameDirectory, "pcl-pack-update.json"),
                new
                {
                    originalInstance = current.Id,
                    copiedUserChanges = changed,
                    preservedDeletions = deleted,
                    timestamp = DateTimeOffset.UtcNow,
                    originalPreserved = true
                }
            );
            return result;
        }
        catch
        {
            // The newly created instance is quarantined, not mistaken for a committed update.
            Directory.Move(
                result.GameDirectory,
                Path.Combine(
                    Path.GetDirectoryName(result.GameDirectory)!,
                    "pending-pack-update-failed-" + Guid.NewGuid()
                        .ToString("N")
                )
            );
            throw;
        }
    }

    // [实现] ExportAsync 的实现：按 string.IsNullOrWhiteSpace(options.Name) || string.IsNullOrWhiteSpace(options.Version) 分支处理；按 File.Exists(destination) 分支处理；准备 var parent = Path.GetDirectoryName( Path.GetFullPath(destination) )!。
    public static async Task ExportAsync(
        GameProfile profile,
        string destination,
        PackExportOptions options,
        CancellationToken ct = default
    )
    {
        if (string.IsNullOrWhiteSpace(options.Name) || string.IsNullOrWhiteSpace(options.Version))
            throw new ArgumentException("请填写名称与版本。");
        if (File.Exists(destination))
            throw new IOException("导出目标已存在，请选择新文件名。");
        var parent = Path.GetDirectoryName(
            Path.GetFullPath(destination)
        )!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(
            parent,
            ".pack-" + Guid.NewGuid()
                .ToString("N")
        );
        try
        {
            // [实现] 在异常保护范围内：在作用域内使用并释放 var zip = ZipFile.Open(temporary, ZipArchiveMode.Create)；执行 ModpackService.Inspect(temporary)；执行 ct.ThrowIfCancellationRequested()。
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                // [实现] 本段流程：准备 var dependencies = new JObject { ["minecraft"] = profile.GameVersion }；按 profile.Loader != "vanilla" 分支处理；执行 await WriteJsonAsync( zip, "modrinth.index.json", new JObject { ["formatVersion"] = 1, ["game"]…。
                var dependencies = new JObject
                {
                    ["minecraft"] = profile.GameVersion
                };
                if (profile.Loader != "vanilla")
                {
                    // [实现] 条件成立（profile.Loader != "vanilla"）时：准备 var receipt = JsonFiles.Read( ResourceInstaller.SafeTarget(profile.GameDirectory, "profile.json…；执行 dependencies[profile.Loader is "fabric" or "quilt" ? profile.Loader + "-loader" : profile.Loade…。
                    var receipt = JsonFiles.Read(
                        ResourceInstaller.SafeTarget(profile.GameDirectory, "profile.json")
                    );
                    dependencies[profile.Loader is "fabric" or "quilt"
                        ? profile.Loader + "-loader"
                        : profile.Loader] = (string? )receipt["loaderVersion"] ?? throw new InvalidDataException("加载器版本缺失。");
                }

                await WriteJsonAsync(
                    zip,
                    "modrinth.index.json",
                    new JObject
                    {
                        ["formatVersion"] = 1,
                        ["game"] = "minecraft",
                        ["versionId"] = options.Version,
                        ["name"] = options.Name,
                        ["dependencies"] = dependencies,
                        ["files"] = new JArray()
                    },
                    ct
                );
                var customPath = ResourceInstaller.SafeTarget(profile.GameDirectory, CustomPackMetadata.EntryName);
                if (File.Exists(customPath))
                {
                    // [实现] 条件成立（File.Exists(customPath)）时：按 new FileInfo(customPath).Length > 2 * 1024 * 1024 分支处理；准备 var custom = CustomPackMetadata.Parse( JsonFiles.Read(customPath) )；执行 await WriteJsonAsync( zip, CustomPackMetadata.EntryName, CustomPackMetadata.Serialize(custom), …。
                    if (new FileInfo(customPath).Length > 2 * 1024 * 1024)
                        throw new InvalidDataException("自定义组件记录过大。");
                    var custom = CustomPackMetadata.Parse(
                        JsonFiles.Read(customPath)
                    );
                    await WriteJsonAsync(
                        zip,
                        CustomPackMetadata.EntryName,
                        CustomPackMetadata.Serialize(custom),
                        ct
                    );
                }

                var hashes = new JArray();
                long total = 0;
                foreach (var root in DataFolders.Where(
                    x => x == "saves"
                        ? options.IncludeSaves
                        : options.IncludedFolders is null || options.IncludedFolders.Contains(x)
                ))
                {
                    // [实现] 对 DataFolders.Where( x => x == "saves" ? options.IncludeSaves : options.IncludedFolders is null |… 中的每一项：准备 var folder = ResourceInstaller.SafeTarget(profile.GameDirectory, root)；按 !Directory.Exists(folder) 分支处理；执行 await AddDirectory(folder)。
                    var folder = ResourceInstaller.SafeTarget(profile.GameDirectory, root);
                    if (!Directory.Exists(folder))
                        continue;
                    await AddDirectory(folder);
                }

                if (options.IncludeOptions)
                    foreach (var name in new[]
                    {
                        "options.txt",
                        "optionsof.txt",
                        "optionsshaders.txt"
                    }

                    )
                    {
                        // [实现] 对 new[] { "options.txt", "optionsof.txt", "optionsshaders.txt" } 中的每一项：准备 var path = ResourceInstaller.SafeTarget(profile.GameDirectory, name)；按 File.Exists(path) 分支处理。
                        var path = ResourceInstaller.SafeTarget(profile.GameDirectory, name);
                        if (File.Exists(path))
                            await AddFile(path);
                    }

                await WriteJsonAsync(
                    zip,
                    "pcl-export.json",
                    new JObject
                    {
                        ["schemaVersion"] = 1,
                        ["privacy"] = "Accounts, tokens, logs, server lists and launcher state excluded; saves and options require opt-in.",
                        ["files"] = hashes
                    },
                    ct
                );
                // [实现] AddDirectory 的实现：遍历 Directory.EnumerateFileSystemEntries(folder) 处理 path。
                async Task AddDirectory(string folder)
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                    {
                        // [实现] 对 Directory.EnumerateFileSystemEntries(folder) 中的每一项：执行 ct.ThrowIfCancellationRequested()；执行 ResourceInstaller.SafeTarget( profile.GameDirectory, Path.GetRelativePath(profile.GameDirectory…；按 Directory.Exists(path) 分支处理。
                        ct.ThrowIfCancellationRequested();
                        ResourceInstaller.SafeTarget(
                            profile.GameDirectory,
                            Path.GetRelativePath(profile.GameDirectory, path)
                        );
                        if (Directory.Exists(path))
                            await AddDirectory(path);
                        else
                            await AddFile(path);
                    }
                }

                // [实现] AddFile 的实现：准备 var relative = Path.GetRelativePath(profile.GameDirectory, path) .Replace('\\', '/')；按 !AllowedData(relative, options.IncludeOptions) 分支处理；执行 ModpackService.ValidatePath(relative)。
                async Task AddFile(string path)
                {
                    var relative = Path.GetRelativePath(profile.GameDirectory, path)
                        .Replace('\\', '/');
                    if (!AllowedData(relative, options.IncludeOptions))
                        return;
                    ModpackService.ValidatePath(relative);
                    using var file = File.OpenRead(path);
                    total = checked(total + file.Length);
                    if (total > 2L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("导出超过 2 GiB，请减少包含内容。");
                    // Hash the exact bytes written, avoiding a hash/copy race when an external editor changes a configuration file.
                    using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                    using var output = zip.CreateEntry("overrides/" + relative)
                        .Open();
                    var buffer = new byte[131072];
                    long written = 0;
                    int count;
                    while ((count = await file.ReadAsync(buffer, ct)) > 0)
                    {
                        // [实现] 循环满足 (count = await file.ReadAsync(buffer, ct)) > 0 时：执行 sha1.AppendData(buffer, 0, count)；执行 sha512.AppendData(buffer, 0, count)；执行 await output.WriteAsync( buffer.AsMemory(0, count), ct )。
                        sha1.AppendData(buffer, 0, count);
                        sha512.AppendData(buffer, 0, count);
                        await output.WriteAsync(
                            buffer.AsMemory(0, count),
                            ct
                        );
                        written += count;
                    }

                    hashes.Add(
                        new JObject
                        {
                            ["path"] = relative,
                            ["size"] = written,
                            ["sha1"] = Convert.ToHexString(
                                sha1.GetHashAndReset()
                            )
                                .ToLowerInvariant(),
                            ["sha512"] = Convert.ToHexString(
                                sha512.GetHashAndReset()
                            )
                                .ToLowerInvariant()
                        }
                    );
                }
            }

            ModpackService.Inspect(temporary);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        catch
        {
            // [实现] 捕获 空 后：执行 File.Delete(temporary)；重新抛出当前异常。
            File.Delete(temporary);
            throw;
        }
    }

    // [实现] SaveExportOptions 的实现：按 options.IncludedFolders?.Any( x => !ExportFolders.Contains(x) ) == true || string.IsNullOrWhite… 分支处理；执行 JsonFiles.Write( ResourceInstaller.SafeTarget(profile.GameDirectory, ".pcl-export-options.json"…。
    public static void SaveExportOptions(GameProfile profile, PackExportOptions options)
    {
        if (options.IncludedFolders?.Any(
            x => !ExportFolders.Contains(x)
        ) == true || string.IsNullOrWhiteSpace(options.Name) || options.Name.Length > 200 || options.Version.Length > 100)
            throw new InvalidDataException("无效导出配置。");
        JsonFiles.Write(
            ResourceInstaller.SafeTarget(profile.GameDirectory, ".pcl-export-options.json"),
            options
        );
    }

    // [实现] LoadExportOptions 的实现：准备 var path = ResourceInstaller.SafeTarget(profile.GameDirectory, ".pcl-export-options.json")；按 !File.Exists(path) 分支处理；按 new FileInfo(path).Length > 1024 * 1024 分支处理。
    public static PackExportOptions LoadExportOptions(GameProfile profile)
    {
        var path = ResourceInstaller.SafeTarget(profile.GameDirectory, ".pcl-export-options.json");
        if (!File.Exists(path))
            return new(profile.Name);
        if (new FileInfo(path).Length > 1024 * 1024)
            throw new InvalidDataException("导出配置过大。");
        var value = Newtonsoft.Json.JsonConvert.DeserializeObject<PackExportOptions>(
            File.ReadAllText(path)
        ) ?? throw new InvalidDataException("导出配置损坏。");
        if (value.IncludedFolders?.Any(
            x => !ExportFolders.Contains(x)
        ) == true)
            throw new InvalidDataException("导出配置包含未知目录。");
        return value;
    }

    // [实现] WriteJsonAsync 的实现：准备 var stream = zip.CreateEntry(name) .Open()；准备 var writer = new StreamWriter(stream)；执行 await writer.WriteAsync( json.ToString() .AsMemory(), ct )。
    private static async Task WriteJsonAsync(ZipArchive zip, string name, JObject json, CancellationToken ct)
    {
        using var stream = zip.CreateEntry(name)
            .Open();
        using var writer = new StreamWriter(stream);
        await writer.WriteAsync(
            json.ToString()
                .AsMemory(),
            ct
        );
    }

    // [实现] CopyEntry 的实现：准备 var input = from.Open()；准备 var output = to.Open()；执行 await input.CopyToAsync(output, ct)。
    private static async Task CopyEntry(ZipArchiveEntry from, ZipArchiveEntry to, CancellationToken ct)
    {
        using var input = from.Open();
        using var output = to.Open();
        await input.CopyToAsync(output, ct);
    }
}

// [实现] 数据约定：CurseFile 汇集 Name、Folder、Size、Sha1、DownloadUrl，供该模块传递状态。
public sealed record CurseFile(string Name, string Folder, long Size, string Sha1, Uri? DownloadUrl);
// 整合包兼容：检查清单及归档路径，转换受支持组件，下载校验内容并安装到隔离实例。
public sealed class CurseForgeClient : IDisposable
{
    private readonly HttpClient http;
    private readonly HttpClient mirrorHttp = new(
        new HttpClientHandler
        {
            AllowAutoRedirect = false
        }
    );
    private readonly AppPaths? paths;
    private readonly bool hasKey;
    // [实现] CurseForgeClient 构造 的实现：按 string.IsNullOrWhiteSpace(apiKey) 分支处理；执行 http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler)；执行 http.BaseAddress = new Uri("https://api.curseforge.com/v1/")。
    public CurseForgeClient(string apiKey, HttpMessageHandler? handler = null, AppPaths? paths = null)
    {
        this.paths = paths;
        hasKey = !string.IsNullOrWhiteSpace(apiKey);
        http = handler is null
            ? new HttpClient(
                new HttpClientHandler
                {
                    AllowAutoRedirect = false
                }
            )
            : new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.curseforge.com/v1/");
        if (hasKey)
        {
            http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        }

        http.Timeout = TimeSpan.FromSeconds(45);
    }

    // [实现] Get 的实现：准备 var response = await http.GetAsync(path, ct)；执行 response.EnsureSuccessStatusCode()；返回 JObject.Parse( await response.Content.ReadAsStringAsync(ct) )["data"] ?? throw new InvalidDataE…。
    internal async Task<JToken> Get(string path, CancellationToken ct)
    {
        var options = paths is null
            ? new NetworkOptions()
            : DownloadTasks.For(paths)
                .Options;
        var official = new Uri(http.BaseAddress!, path).AbsoluteUri;
        var mirror = options.CurseForgeApiMirror.Length == 0
            ? ""
            : options.CurseForgeApiMirror.TrimEnd('/') + "/" + path;
        Exception? error = null;
        foreach (var url in MetadataSourcePolicy.Sources(official, mirror, options.ResourceSource))
        {
            try
            {
                var isOfficial = url == official;
                if (isOfficial && !hasKey)
                {
                    throw new InvalidOperationException("CurseForge 官方 API 需要自己的密钥；可在来源设置配置授权的兼容镜像。");
                }

                // 镜像使用没有 API Key、Cookie、授权头的独立客户端，禁止自动跨站重定向。
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(
                    TimeSpan.FromSeconds(12)
                );
                var text = await MetadataRequest.GetAsync(
                    isOfficial
                        ? http
                        : mirrorHttp,
                    url,
                    timeout.Token
                );
                return JObject.Parse(text)["data"] ?? throw new InvalidDataException("CurseForge 返回空数据。");
            }
            catch (Exception ex)when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or InvalidOperationException or Newtonsoft.Json.JsonException)
            {
                error = ex;
            }
        }

        throw new IOException("配置的 CurseForge 来源均不可用。", error);
    }

    // [实现] FileAsync 的实现：按 project <= 0 || file <= 0 分支处理；准备 var json = await Get($"mods/{project}/files/{file}", ct)；准备 var mod = await Get($"mods/{project}", ct)。
    public async Task<CurseFile> FileAsync(long project, long file, CancellationToken ct = default)
    {
        if (project <= 0 || file <= 0)
            throw new ArgumentException("无效文件标识。");
        var json = await Get($"mods/{project}/files/{file}", ct);
        var mod = await Get($"mods/{project}", ct);
        if ((long? )json["modId"] != project || (long? )json["id"] != file)
            throw new InvalidDataException("文件标识不匹配。");
        var name = (string? )json["fileName"] ?? "";
        ResourceInstaller.ValidateFileName(name);
        var folder = (int? )mod["classId"] switch
        {
            6 => "mods",
            12 => "resourcepacks",
            6552 => "shaderpacks",
            _ => throw new NotSupportedException("此 CurseForge 资源类型需手动选择目标目录。")
        };
        var hash = (string? )json["hashes"]?.FirstOrDefault(
            x => (int? )x["algo"] == 1
        )?["value"] ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-fA-F]{40}$"))
            throw new InvalidDataException("CurseForge 文件缺少 SHA-1。");
        var url = (string? )json["downloadUrl"];
        Uri? uri = string.IsNullOrWhiteSpace(url)
            ? null
            : new Uri(url);
        if (uri is not null && (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)))
            throw new InvalidDataException("不安全下载地址。");
        var size = (long? )json["fileLength"] ?? -1;
        if (size < 0)
            throw new InvalidDataException("文件大小无效。");
        return new(
            name,
            folder,
            size,
            hash.ToLowerInvariant(),
            uri
        );
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose()
    {
        http.Dispose();
        mirrorHttp.Dispose();
    }
}
