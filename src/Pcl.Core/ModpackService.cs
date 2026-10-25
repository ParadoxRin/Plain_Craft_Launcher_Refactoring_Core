using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：ModpackFile 汇集 Path、Downloads、Size、Sha1、Sha512、Optional，供该模块传递状态。
public sealed record ModpackFile(
    string Path,
    IReadOnlyList<Uri> Downloads,
    long Size,
    string Sha1,
    string Sha512,
    bool Optional
);
// [实现] 数据约定：ModpackPlan 汇集 Name、Version、Minecraft、Loader、LoaderVersion、Files、SkippedServerFiles、SkippedOptionalFiles，供该模块传递状态。
public sealed record ModpackPlan(
    string Name,
    string Version,
    string Minecraft,
    string Loader,
    string LoaderVersion,
    IReadOnlyList<ModpackFile> Files,
    int SkippedServerFiles,
    int SkippedOptionalFiles
);
// 整合包兼容：检查清单及归档路径，转换受支持组件，下载校验内容并安装到隔离实例。
public sealed class ModpackService(MinecraftService service)
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com",
        "github.com",
        "raw.githubusercontent.com",
        "gitlab.com"
    };
    // [实现] ValidatePath 的实现：按 string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith… 分支处理；准备 var first = path.Split('/')[0].ToLowerInvariant()；按 first.StartsWith('.') || first is "libraries" or "versions" or "runtime" or "profile.json" or "… 分支处理。
    public static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Split('/')
            .Any(
                x => x is "." or ".." or ""
            ))
            throw new InvalidDataException("整合包路径不安全：" + path);
        var first = path.Split('/')[0].ToLowerInvariant();
        if (first.StartsWith('.') || first is "libraries" or "versions" or "runtime" or "profile.json" or "version.json" or "launcher_profiles.json" || first.StartsWith("pcl-") || (path.Split('/')
            .Length == 1 && first.EndsWith(".jar")))
            throw new InvalidDataException("整合包不能覆盖启动器或加载器内部文件：" + path);
    }

    // [实现] Parse 的实现：按 (int? )index["formatVersion"] != 1 || (string? )index["game"] != "minecraft" 分支处理；准备 var dependencies = (JObject? )index["dependencies"] ?? throw new InvalidDataException("缺少整合包依赖。…；准备 var minecraft = (string? )dependencies["minecraft"] ?? throw new InvalidDataException("缺少 Minec…。
    public static ModpackPlan Parse(
        JObject index,
        bool includeOptional = false,
        IReadOnlyCollection<string>? selectedOptionalFiles = null
    )
    {
        if ((int? )index["formatVersion"] != 1 || (string? )index["game"] != "minecraft")
            throw new NotSupportedException("只支持 Modrinth formatVersion 1 / Minecraft。");
        var dependencies = (JObject? )index["dependencies"] ?? throw new InvalidDataException("缺少整合包依赖。");
        var minecraft = (string? )dependencies["minecraft"] ?? throw new InvalidDataException("缺少 Minecraft 版本。");
        VersionCatalog.ValidateId(minecraft);
        foreach (var key in dependencies.Properties())
            if (key.Name is not ("minecraft" or "forge" or "neoforge" or "fabric-loader" or "quilt-loader"))
                throw new NotSupportedException("尚不支持整合包依赖：" + key.Name);
        var loaders = dependencies.Properties()
            .Where(
                x => x.Name != "minecraft"
            )
            .ToArray();
        if (loaders.Length > 1)
            throw new InvalidDataException("整合包不能同时指定多个加载器。");
        var loader = loaders.Length == 0
            ? "vanilla"
            : loaders[0].Name switch
            {
                "fabric-loader" => "fabric",
                "quilt-loader" => "quilt",
                _ => loaders[0].Name
            };
        var files = new List<ModpackFile>();
        var optionalPaths = new HashSet<string>(StringComparer.Ordinal);
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int server = 0, optional = 0;
        foreach (var file in index["files"] ?? new JArray())
        {
            // [实现] 对 index["files"] ?? new JArray() 中的每一项：准备 var path = (string)file["path"]!；执行 ValidatePath(path)；按 !allPaths.Add(path) 分支处理。
            var path = (string)file["path"]!;
            ValidatePath(path);
            if (!allPaths.Add(path))
                throw new InvalidDataException("整合包包含重复或大小写冲突路径：" + path);
            var client = (string? )file["env"]?["client"] ?? "required";
            if (client is not ("required" or "optional" or "unsupported"))
                throw new InvalidDataException("未知客户端环境标志。");
            if (client == "unsupported")
            {
                // [实现] 条件成立（client == "unsupported"）时：执行 server++；执行 continue;。
                server++;
                continue;
            }

            if (client == "optional")
            {
                // [实现] 条件成立（client == "optional"）时：执行 optionalPaths.Add(path)；按 selectedOptionalFiles is not null ? !selectedOptionalFiles.Contains(path, StringComparer.Ordina… 分支处理。
                optionalPaths.Add(path);
                if (selectedOptionalFiles is not null
                    ? !selectedOptionalFiles.Contains(path, StringComparer.Ordinal)
                    : !includeOptional)
                {
                    // [实现] 条件成立（selectedOptionalFiles is not null ? !selectedOptionalFiles.Contains(path, StringComparer.Ordina…）时：执行 optional++；执行 continue;。
                    optional++;
                    continue;
                }
            }

            var sha1 = (string? )file["hashes"]?["sha1"] ?? "";
            var sha512 = (string? )file["hashes"]?["sha512"];
            if (!Regex.IsMatch(sha1, "^[a-fA-F0-9]{40}$") || !Regex.IsMatch(sha512 ?? "", "^[a-fA-F0-9]{128}$"))
                throw new InvalidDataException("整合包哈希缺失或无效。");
            var urls = file["downloads"]?.Values<string>()
                .Select(
                    x => new Uri(x!)
                )
                .ToArray() ?? Array.Empty<Uri>();
            if (urls.Length == 0 || urls.Any(
                x => x.Scheme != "https" || !Hosts.Contains(x.Host) || !string.IsNullOrEmpty(x.UserInfo) || !x.IsDefaultPort
            ))
                throw new InvalidDataException("下载地址不在整合包允许的 HTTPS 来源中：" + path);
            var size = (long? )file["fileSize"] ?? -1;
            if (size < 0)
                throw new InvalidDataException("文件大小无效。");
            files.Add(
                new(
                    path,
                    urls,
                    size,
                    sha1.ToLowerInvariant(),
                    sha512!.ToLowerInvariant(),
                    client == "optional"
                )
            );
        }

        if (selectedOptionalFiles is not null && selectedOptionalFiles.Any(
            x => !optionalPaths.Contains(x)
        ))
            throw new InvalidDataException("选择包含不存在或非可选的文件，请重新检查整合包。");
        if (files.GroupBy(
            x => x.Path,
            StringComparer.OrdinalIgnoreCase
        )
            .Any(
                x => x.Count() > 1
            ))
            throw new InvalidDataException("整合包包含重复或大小写冲突路径。");
        return new(
            (string? )index["name"] ?? "Modpack",
            (string? )index["versionId"] ?? "unknown",
            minecraft,
            loader,
            loaders.Length == 0
                ? ""
                : loaders[0].Value.ToString(),
            files,
            server,
            optional
        );
    }

    // [实现] Inspect 的实现：准备 var zip = ZipFile.OpenRead(path)；准备 var entry = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("不是有效的 .mrpac…；按 entry.Length > 16 * 1024 * 1024 分支处理。
    public static ModpackPlan Inspect(
        string path,
        bool includeOptional = false,
        IReadOnlyCollection<string>? selectedOptionalFiles = null
    )
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("modrinth.index.json") ?? throw new InvalidDataException("不是有效的 .mrpack：缺少 modrinth.index.json。");
        if (entry.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("整合包清单过大。");
        using var reader = new StreamReader(
            entry.Open()
        );
        var plan = Parse(
            JObject.Parse(
                reader.ReadToEnd()
            ),
            includeOptional,
            selectedOptionalFiles
        );
        ValidateOverrides(zip);
        _ = CustomPackMetadata.ReadArchive(path);
        return plan;
    }

    // [实现] ValidateOverrides 的实现：准备 long total = 0；准备 var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)；遍历 zip.Entries 处理 entry。
    public static void ValidateOverrides(ZipArchive zip)
    {
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            // [实现] 对 zip.Entries 中的每一项：按 !entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) && !entry.FullName.StartsWit… 分支处理；按 (entry.ExternalAttributes >> 16 & 0xf000) == 0xa000 分支处理；准备 var split = entry.FullName.IndexOf('/')。
            if (!entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) && !entry.FullName.StartsWith("client-overrides/", StringComparison.Ordinal))
                continue;
            if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                throw new InvalidDataException("覆盖文件不能包含符号链接。");
            var split = entry.FullName.IndexOf('/');
            var relative = entry.FullName[(split + 1)..].TrimEnd('/');
            if (relative.Length == 0)
                continue;
            ValidatePath(relative);
            total += entry.Length;
            if (entry.Length > 512L * 1024 * 1024 || total > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("整合包覆盖内容过大。");
            var key = entry.FullName[..split] + "/" + relative;
            if (!paths.Add(key))
                throw new InvalidDataException("覆盖目录包含重复或大小写冲突路径。");
        }
    }

    // [实现] DownloadReleaseAsync 的实现：按 !Regex.IsMatch(release.File.Sha1, "^[0-9a-f]{40}$") 分支处理；按 !release.File.Name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) 分支处理；准备 var cache = Path.Combine(service.Paths.Root, "cache/modpacks")。
    public async Task<string> DownloadReleaseAsync(
        ResourceRelease release,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (!Regex.IsMatch(release.File.Sha1, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("整合包哈希无效。");
        if (!release.File.Name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("目前支持 Modrinth .mrpack 整合包。");
        var cache = Path.Combine(service.Paths.Root, "cache/modpacks");
        Directory.CreateDirectory(cache);
        var file = Path.Combine(cache, release.File.Sha1 + ".mrpack");
        progress?.Report(
            new("下载整合包", 0, 1, 0, release.File.Size)
        );
        await service.Downloads.DownloadAsync(
            new(release.File.Url, file, release.File.Size, release.File.Sha1),
            ct
        );
        return file;
    }

    // [实现] InstallReleaseAsync 的实现：计算并返回 await InstallAsync( await DownloadReleaseAsync(release, progress, ct), includeOptional: include…。
    public async Task<GameProfile> InstallReleaseAsync(
        ResourceRelease release,
        bool includeOptional = false,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    ) => await InstallAsync(
        await DownloadReleaseAsync(release, progress, ct),
        includeOptional: includeOptional,
        progress: progress,
        ct: ct
    );
    // [实现] ExtractOverridesAsync 的实现：准备 var zip = ZipFile.OpenRead(pack)；执行 ValidateOverrides(zip)；遍历 new[] { "overrides/", "client-overrides/" } 处理 prefix。
    public static async Task ExtractOverridesAsync(string pack, string targetRoot, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(pack);
        ValidateOverrides(zip);
        foreach (var prefix in new[]
        {
            "overrides/",
            "client-overrides/"
        }

        )
            foreach (var entry in zip.Entries.Where(
                x => x.FullName.StartsWith(prefix, StringComparison.Ordinal) && !x.FullName.EndsWith('/')
            ))
            {
                // [实现] 对 zip.Entries.Where( x => x.FullName.StartsWith(prefix, StringComparison.Ordinal) && !x.FullName.… 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var target = ResourceInstaller.SafeTarget(targetRoot, entry.FullName[prefix.Length..])；执行 Directory.CreateDirectory( Path.GetDirectoryName(target)! )。
                ct.ThrowIfCancellationRequested();
                var target = ResourceInstaller.SafeTarget(targetRoot, entry.FullName[prefix.Length..]);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(target)!
                );
                using var output = File.Create(target);
                using var content = entry.Open();
                await content.CopyToAsync(output, ct);
            }
    }

    // [实现] RegisterFiles 的实现：准备 var managed = new List<ManagedResource>()；遍历 new[] { ResourceKind.Mod, ResourceKind.Shader, ResourceKind.ResourcePack } 处理 kind；执行 JsonFiles.Write( Path.Combine(root, ".pcl-resources.json"), managed )。
    public static void RegisterFiles(string root)
    {
        var managed = new List<ManagedResource>();
        foreach (var kind in new[]
        {
            ResourceKind.Mod,
            ResourceKind.Shader,
            ResourceKind.ResourcePack
        }

        )
        {
            // [实现] 对 new[] { ResourceKind.Mod, ResourceKind.Shader, ResourceKind.ResourcePack } 中的每一项：准备 var folder = Path.Combine( root, ResourceInstaller.Folder(kind) )；按 !Directory.Exists(folder) 分支处理；遍历 Directory.EnumerateFiles(folder) 处理 file。
            var folder = Path.Combine(
                root,
                ResourceInstaller.Folder(kind)
            );
            if (!Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                // [实现] 对 Directory.EnumerateFiles(folder) 中的每一项：准备 var disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)；准备 var name = Path.GetFileName( disabled ? file[..^9] : file )；按 kind == ResourceKind.Mod ? !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !name.… 分支处理。
                var disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var name = Path.GetFileName(
                    disabled
                        ? file[..^9]
                        : file
                );
                if (kind == ResourceKind.Mod
                    ? !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase)
                    : !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var input = File.OpenRead(file);
                var hash = Convert.ToHexString(
                    SHA1.HashData(input)
                )
                    .ToLowerInvariant();
                var identity = Convert.ToHexString(
                    SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(
                            ResourceInstaller.Folder(kind) + "/" + name
                        )
                    )
                )
                    .ToLowerInvariant();
                managed.Add(
                    new(
                        "local-pack-" + identity[..16],
                        hash,
                        Path.GetFileNameWithoutExtension(name),
                        "整合包",
                        kind,
                        name,
                        hash,
                        input.Length,
                        !disabled,
                        DateTimeOffset.UtcNow,
                        Array.Empty<ResourceDependency>()
                    )
                );
            }
        }

        JsonFiles.Write(
            Path.Combine(root, ".pcl-resources.json"),
            managed
        );
    }

    // [实现] InstallAsync 的实现：准备 var plan = Inspect(pack, includeOptional, selectedOptionalFiles)；准备 var customChanges = CustomPackMetadata.ReadArchive(pack)；准备 var input = File.OpenRead(pack)。
    public async Task<GameProfile> InstallAsync(
        string pack,
        string? instanceName = null,
        bool includeOptional = false,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        IReadOnlyCollection<string>? selectedOptionalFiles = null
    )
    {
        var plan = Inspect(pack, includeOptional, selectedOptionalFiles);
        var customChanges = CustomPackMetadata.ReadArchive(pack);
        using var input = File.OpenRead(pack);
        var hash = Convert.ToHexString(
            SHA256.HashData(input)
        )
            .ToLowerInvariant();
        var selected = plan.Files.Where(
            x => x.Optional
        )
            .Select(
                x => x.Path
            )
            .Order(StringComparer.Ordinal)
            .ToArray();
        var selectionHash = Convert.ToHexString(
            SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    string.Join("\n", selected)
                )
            )
        )
            .ToLowerInvariant();
        instanceName ??= "pack-" + hash[..16] + (selectedOptionalFiles is null
            ? ""
            : "-" + selectionHash[..8]);
        ForgeService.ValidateName(instanceName);
        if (instanceName.StartsWith("pending-pack-"))
            throw new InvalidDataException("实例名称使用保留前缀。");
        var destination = AppPaths.SafeChild(
            Path.Combine(service.Paths.Root, "instances"),
            instanceName
        );
        if (Directory.Exists(destination))
        {
            // [实现] 条件成立（Directory.Exists(destination)）时：准备 var old = JsonFiles.Read( Path.Combine(destination, "pcl-modpack.json") )；准备 var oldSelected = old["plan"]?.ToObject<ModpackPlan>()?.Files.Where( x => x.Optional ) .Select(…；按 (string? )old["sourceSha256"] == hash && selected.SequenceEqual(oldSelected) 分支处理。
            var old = JsonFiles.Read(
                Path.Combine(destination, "pcl-modpack.json")
            );
            var oldSelected = old["plan"]?.ToObject<ModpackPlan>()?.Files.Where(
                x => x.Optional
            )
                .Select(
                    x => x.Path
                )
                .Order(StringComparer.Ordinal)
                .ToArray() ?? [];
            if ((string? )old["sourceSha256"] == hash && selected.SequenceEqual(oldSelected))
            {
                // [实现] 条件成立（(string? )old["sourceSha256"] == hash && selected.SequenceEqual(oldSelected)）时：准备 var existing = InstanceRegistry.ByFolder(service, instanceName)；准备 var installed = InstanceRegistry.Load(service, existing)；按 installed.Libraries.Any( x => !DownloadService.IsValid(x) ) 分支处理。
                var existing = InstanceRegistry.ByFolder(service, instanceName);
                var installed = InstanceRegistry.Load(service, existing);
                if (installed.Libraries.Any(
                    x => !DownloadService.IsValid(x)
                ))
                    throw new InvalidDataException("整合包加载器文件不完整，请换新名称重装。");
                if (!File.Exists(
                    Path.Combine(destination, ".pcl-resources.json")
                ))
                    RegisterFiles(destination);
                using var checker = new DownloadService();
                var store = new ResourceInstaller(destination, checker, plan.Loader);
                foreach (var item in store.List())
                {
                    // [实现] 对 store.List() 中的每一项：准备 var file = ResourceInstaller.SafeTarget( destination, ResourceInstaller.Folder(item.Kind) + "/"…；按 !DownloadService.IsValid( new( new Uri("https://localhost/unused"), file, item.Size, item.Sha1 … 分支处理。
                    var file = ResourceInstaller.SafeTarget(
                        destination,
                        ResourceInstaller.Folder(item.Kind) + "/" + item.FileName + (item.Enabled
                            ? ""
                            : ".disabled")
                    );
                    if (!DownloadService.IsValid(
                        new(
                            new Uri("https://localhost/unused"),
                            file,
                            item.Size,
                            item.Sha1
                        )
                    ))
                        throw new InvalidDataException("整合包文件已修改或缺失，请使用新实例安装。");
                }

                return existing;
            }

            throw new IOException("实例目录已存在，未覆盖现有整合包。请选择新名称。");
        }

        var pendingName = "pending-pack-" + Guid.NewGuid()
            .ToString("N");
        var pending = Path.Combine(service.Paths.Root, "instances", pendingName);
        using var gameService = service.ForVersion(plan.Minecraft);
        InstalledVersion parent;
        try
        {
            // [实现] 在异常保护范围内：执行 parent = gameService.LoadInstalled()。
            parent = gameService.LoadInstalled();
        }
        catch (Exception ex)when (ex is FileNotFoundException or InvalidDataException)
        {
            // [实现] 捕获 (Exception ex) 后：执行 parent = await gameService.InstallAsync(progress, ct)。
            parent = await gameService.InstallAsync(progress, ct);
        }

        await new JavaService(gameService).EnsureForAsync(parent, progress, ct);
        try
        {
            // [实现] 在异常保护范围内：按 plan.Loader == "vanilla" 分支处理；准备 int done = 0；准备 long bytes = 0。
            if (plan.Loader == "vanilla")
            {
                // [实现] 条件成立（plan.Loader == "vanilla"）时：执行 Directory.CreateDirectory(pending)；准备 var vanillaMetadata = JsonFiles.Read(parent.JsonPath)；执行 vanillaMetadata["id"] = pendingName。
                Directory.CreateDirectory(pending);
                var vanillaMetadata = JsonFiles.Read(parent.JsonPath);
                vanillaMetadata["id"] = pendingName;
                JsonFiles.Write(
                    Path.Combine(pending, "version.json"),
                    vanillaMetadata
                );
                JsonFiles.Write(
                    Path.Combine(pending, "profile.json"),
                    new
                    {
                        id = pendingName,
                        displayName = plan.Name,
                        gameVersion = plan.Minecraft,
                        loader = "vanilla",
                        loaderVersion = ""
                    }
                );
            }
            else if (plan.Loader is "fabric" or "quilt")
            {
                // [实现] 条件成立（plan.Loader is "fabric" or "quilt"）时：准备 var fabric = new FabricService(gameService, pendingName, plan.Loader)；执行 await fabric.InstallAsync(plan.LoaderVersion, progress, ct)。
                using var fabric = new FabricService(gameService, pendingName, plan.Loader);
                await fabric.InstallAsync(plan.LoaderVersion, progress, ct);
            }
            else
            {
                // [实现] 前一条件不成立时：准备 var forge = new ForgeService(gameService)；执行 await forge.InstallAsync(plan.Loader, plan.LoaderVersion, pendingName, progress: progress, ct: …。
                using var forge = new ForgeService(gameService);
                await forge.InstallAsync(plan.Loader, plan.LoaderVersion, pendingName, progress: progress, ct: ct);
            }

            int done = 0;
            long bytes = 0;
            await Parallel.ForEachAsync(
                plan.Files,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 6,
                    CancellationToken = ct
                },
                async (file, token) =>
                {
                    // [实现] 回调触发时：准备 var target = ResourceInstaller.SafeTarget(pending, file.Path)；准备 Exception? last = null；遍历 file.Downloads 处理 url。
                    var target = ResourceInstaller.SafeTarget(pending, file.Path);
                    Exception? last = null;
                    foreach (var url in file.Downloads)
                    {
                        // [实现] 对 file.Downloads 中的每一项：执行受异常处理保护的操作。
                        try
                        {
                            // [实现] 在异常保护范围内：执行 await service.Downloads.DownloadAsync( new(url, target, file.Size, file.Sha1), token )；执行 last = null；执行 break;。
                            await service.Downloads.DownloadAsync(
                                new(url, target, file.Size, file.Sha1),
                                token
                            );
                            last = null;
                            break;
                        }
                        catch (Exception ex)when (ex is HttpRequestException or IOException)
                        {
                            // [实现] 捕获 (Exception ex) 后：执行 token.ThrowIfCancellationRequested()；执行 last = ex。
                            token.ThrowIfCancellationRequested();
                            last = ex;
                        }
                    }

                    if (last is not null)
                        throw last;
                    if (file.Sha512 is not null)
                    {
                        // [实现] 条件成立（file.Sha512 is not null）时：准备 var content = File.OpenRead(target)；按 Convert.ToHexString( SHA512.HashData(content) ) .ToLowerInvariant() != file.Sha512 分支处理。
                        using var content = File.OpenRead(target);
                        if (Convert.ToHexString(
                            SHA512.HashData(content)
                        )
                            .ToLowerInvariant() != file.Sha512)
                            throw new InvalidDataException("SHA-512 不匹配：" + file.Path);
                    }

                    var count = Interlocked.Increment(ref done);
                    var current = Interlocked.Add(ref bytes, file.Size);
                    progress?.Report(
                        new(
                            "下载整合包文件",
                            count,
                            plan.Files.Count,
                            current,
                            plan.Files.Sum(
                                x => x.Size
                            )
                        )
                    );
                }
            );
            await ExtractOverridesAsync(pack, pending, ct);
            await CustomPackMetadata.ApplyAsync(service, pending, customChanges, ct);
            new LocalResourceService(pending, plan.Loader, plan.Minecraft).ValidateEnabledResources();
            RegisterFiles(pending);
            var receipt = JsonFiles.Read(
                Path.Combine(pending, "profile.json")
            );
            receipt["id"] = instanceName;
            receipt["displayName"] = plan.Name + " · " + plan.Version;
            if (receipt["clientJar"] is not null)
            {
                // [实现] 条件成立（receipt["clientJar"] is not null）时：执行 File.Move( ResourceInstaller.SafeTarget(pending, (string)receipt["clientJar"]!), Path.Combine(p…；执行 receipt["clientJar"] = instanceName + ".jar"。
                File.Move(
                    ResourceInstaller.SafeTarget(pending, (string)receipt["clientJar"]!),
                    Path.Combine(pending, instanceName + ".jar")
                );
                receipt["clientJar"] = instanceName + ".jar";
            }

            var metadata = JsonFiles.Read(
                Path.Combine(pending, "version.json")
            );
            metadata["id"] = instanceName;
            JsonFiles.Write(
                Path.Combine(pending, "version.json"),
                metadata
            );
            JsonFiles.Write(
                Path.Combine(pending, "profile.json"),
                receipt
            );
            JsonFiles.Write(
                Path.Combine(pending, "pcl-modpack.json"),
                new
                {
                    sourceSha256 = hash,
                    plan,
                    includeOptional,
                    selectedOptionalFiles = selected,
                    selectionHash,
                    installedAt = DateTimeOffset.UtcNow,
                    packDataHashes = PortableModpackService.CaptureDataHashes(pending),
                    integrity = "file size, SHA-1 and declared SHA-512 checked before overrides; client overrides applied last"
                }
            );
            ct.ThrowIfCancellationRequested();
            Directory.Move(pending, destination);
            var profile = InstanceRegistry.ByFolder(service, instanceName);
            JsonFiles.Write(
                Path.Combine(service.Paths.Evidence, instanceName + "-modpack-install.json"),
                new
                {
                    status = "passed",
                    profile,
                    plan,
                    sourceSha256 = hash
                }
            );
            return profile;
        }
        catch
        {
            // [实现] 捕获 空 后：按 Directory.Exists(pending) 分支处理；重新抛出当前异常。
            if (Directory.Exists(pending))
                Directory.Move(pending, pending + ".failed");
            throw;
        }
    }
}
