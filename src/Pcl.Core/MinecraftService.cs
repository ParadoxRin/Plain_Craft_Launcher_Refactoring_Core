using System.IO.Compression;
using System.Text.RegularExpressions;
using MeloongCore;
using Newtonsoft.Json.Linq;
using Pcl.Legacy;
using Pcl.Core.Ports.Processes;
using Pcl.Core.Adapters.Processes;

namespace Pcl.Core;
// 游戏安装：根据官方版本与加载器元数据规划组件，校验下载文件，写入可复现的实例配置。
public sealed class MinecraftService : IDisposable
{
    public const string Version = "1.21.1";
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public string GameVersion { get; }
    public string VanillaGameDirectory => GameVersion == Version
        ? Paths.Game
        : Path.Combine(Paths.Root, "instances", "vanilla-" + GameVersion);

    // [实现] ForVersion 的实现：计算并返回 new(Paths, Platform, version)。
    public MinecraftService ForVersion(string version) => new(Paths, Platform, version, commands: Commands, gameProcesses: GameProcesses);
    public AppPaths Paths { get; }
    public TargetPlatform Platform { get; }
    public DownloadService Downloads { get; private set; }
    public IShellCommandRunner Commands { get; }
    public IGameProcessHost GameProcesses { get; }

    private readonly bool ownsGameProcesses;
    private HttpClient metadata;
    private NetworkOptions handlerOptions;
    // [实现] MinecraftService 构造 的实现：执行 GameVersion = gameVersion ?? Version；执行 VersionCatalog.ValidateId(GameVersion)；执行 Paths = paths。
    public MinecraftService(
        AppPaths paths,
        TargetPlatform? platform = null,
        string? gameVersion = null,
        HttpMessageHandler? metadataHandler = null,
        IShellCommandRunner? commands = null,
        IGameProcessHost? gameProcesses = null
    )
    {
        Commands = commands ?? new SystemShellCommandRunner();
        ownsGameProcesses = gameProcesses is null;
        GameProcesses = gameProcesses ?? new SystemGameProcessHost();
        GameVersion = gameVersion ?? Version;
        VersionCatalog.ValidateId(GameVersion);
        Paths = paths;
        Platform = platform ?? TargetPlatform.Current;
        handlerOptions = DownloadTasks.For(paths)
            .Options;
        Downloads = new(paths: paths);
        metadata = new HttpClient(
            metadataHandler ?? DownloadTasks.For(paths)
                .Options.CreateHandler()
        )
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        metadata.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MacPort/0.8");
    }

    // 只在无活动工作且兼容写入租约归还后调用，重建代理 Handler 并重新读取网络选项。
    public void RefreshNetwork()
    {
        var options = NetworkOptions.Load(Paths);
        var tasks = DownloadTasks.For(Paths);
        var changed = handlerOptions != options;
        tasks.ReloadIdle();
        if (!changed)
        {
            return;
        }

        Downloads.ReconfigureIdle(options);
        metadata.Dispose();
        metadata = new HttpClient(
            options.CreateHandler()
        )
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
        metadata.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MacPort/0.8");
        handlerOptions = options;
    }

    // [实现] GetMetadataAsync 的实现：按 sha1 is not null 分支处理；准备 var network = Downloads.Tasks?.Options ?? new NetworkOptions()；准备 var text = url == ManifestUrl ? await MetadataSourcePolicy.ReadAsync(metadata, url, network.Man…。
    public async Task<JObject> GetMetadataAsync(
        string url,
        string path,
        string? sha1 = null,
        long size = -1,
        CancellationToken cancellationToken = default
    )
    {
        if (sha1 is not null)
        {
            // [实现] 条件成立（sha1 is not null）时：执行 await Downloads.DownloadAsync( new( new Uri(url), path, size, sha1 ), cancellationToken )；返回 JsonFiles.Read(path)。
            await Downloads.DownloadAsync(
                new(
                    new Uri(url),
                    path,
                    size,
                    sha1
                ),
                cancellationToken
            );
            return JsonFiles.Read(path);
        }

        var network = Downloads.Tasks?.Options ?? new NetworkOptions();
        var text = url == ManifestUrl
            ? await MetadataSourcePolicy.ReadAsync(metadata, url, network.ManifestMirror, network.ManifestSource, cancellationToken)
            : await MetadataRequest.GetAsync(metadata, url, cancellationToken);
        var json = JObject.Parse(text);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!
        );
        FileUtils.WriteAtomic(path, text);
        return json;
    }

    // [实现] InstallAsync 的实现：执行 Directory.CreateDirectory(Paths.Game)；准备 var lease = new FileStream( Path.Combine(Paths.Game, ".install.lock"), FileMode.OpenOrCreate, F…；执行 progress?.Report( new("读取官方版本清单", 0, 0, 0, 0) )。
    public async Task<InstalledVersion> InstallAsync(
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<VanillaInstallPlan, string>? checkpoint = null,
        Func<InstalledVersion, CancellationToken, Task>? beforePublish = null
    )
    {
        Directory.CreateDirectory(Paths.Game);
        using var lease = new FileStream(
            Path.Combine(Paths.Game, ".install.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        progress?.Report(
            new("读取官方版本清单", 0, 0, 0, 0)
        );
        var manifest = await GetMetadataAsync(
            ManifestUrl,
            Path.Combine(Paths.Root, "cache/version_manifest_v2.json"),
            cancellationToken: cancellationToken
        );
        var entry = manifest["versions"]!.OfType<JObject>()
            .Single(
                x => (string? )x["id"] == GameVersion
            );
        var folder = Paths.VersionFolder(GameVersion);
        var jsonPath = Path.Combine(folder, GameVersion + ".json");
        var originalPath = Path.Combine(folder, GameVersion + ".official.json");
        var original = await GetMetadataAsync(
            (string)entry["url"]!,
            originalPath,
            (string)entry["sha1"]!,
            cancellationToken: cancellationToken
        );
        var version = LegacyMacLibraries.Adapt(
            LegacyLaunchAdapter.NormalizeMetadata(original),
            Platform
        );
        JsonFiles.Write(jsonPath, version);
        if (version["inheritsFrom"] is not null)
            throw new NotSupportedException("首版仅支持完整原版元数据。");
        var jarPath = Path.Combine(folder, GameVersion + ".jar");
        var libraryPlan = BuildLibraries(version, Paths.Game, Platform);
        var clientItem = Artifact((JObject)version["downloads"]!["client"]!, jarPath);
        var index = (JObject)version["assetIndex"]!;
        var indexName = (string)index["id"]!;
        var indexPath = AppPaths.SafeChild(
            Path.Combine(Paths.Game, "assets/indexes"),
            indexName + ".json"
        );
        progress?.Report(
            new("读取资源索引", 0, 0, 0, 0)
        );
        var indexJson = await GetMetadataAsync(
            (string)index["url"]!,
            indexPath,
            (string)index["sha1"]!,
            (long)index["size"]!,
            cancellationToken
        );
        var assets = new List<DownloadItem>();
        foreach (var item in ((JObject)indexJson["objects"]!).Properties())
        {
            // [实现] 对 ((JObject)indexJson["objects"]!).Properties() 中的每一项：准备 var hash = (string)item.Value["hash"]!；按 !Regex.IsMatch(hash, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant) 分支处理；执行 assets.Add( new( new Uri($"https://resources.download.minecraft.net/{hash[..2]}/{hash}"), Path.…。
            var hash = (string)item.Value["hash"]!;
            if (!Regex.IsMatch(hash, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("资源哈希不正确。");
            assets.Add(
                new(
                    new Uri($"https://resources.download.minecraft.net/{hash[..2]}/{hash}"),
                    Path.Combine(Paths.Game, "assets/objects", hash[..2], hash),
                    (long)item.Value["size"]!,
                    hash
                )
            );
        }

        var files = new[]
        {
            clientItem
        }.Concat(libraryPlan)
            .Concat(assets)
            .ToArray();
        // 操作 receipt 在下载前持久化可信计划；续做必须先通过回调核对元数据身份，不能盲用旧清单。
        var recoveryPlan = new VanillaInstallPlan((string)entry["sha1"]!, (string)index["sha1"]!, files);
        checkpoint?.Invoke(recoveryPlan, "planned");
        cancellationToken.ThrowIfCancellationRequested();
        await Downloads.DownloadManyAsync(files, progress, cancellationToken);
        checkpoint?.Invoke(recoveryPlan, "verified");
        cancellationToken.ThrowIfCancellationRequested();
        var natives = Path.Combine(folder, "natives");
        Directory.CreateDirectory(natives);
        var installed = new InstalledVersion(
            GameVersion,
            jsonPath,
            jarPath,
            indexName,
            libraryPlan,
            VersionCatalog.JavaMajor(version)
        );
        LegacyLaunchAdapter.PrepareFiles(
            installed,
            Path.Combine(Paths.Game, "assets"),
            VanillaGameDirectory,
            cancellationToken
        );
        // v3 原版操作在发布安装标识前完成 Java 准备，避免已提交后再因 Java 阶段的迟到取消报告取消。
        if (beforePublish is not null)
        {
            await beforePublish(installed, cancellationToken)
                .ConfigureAwait(false);
        }

        // 准备阶段允许取消；发布安装标识的短提交区间不响应迟到取消，随后返回真实成功。
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint?.Invoke(recoveryPlan, "committing");
        JsonFiles.Write(
            Path.Combine(folder, "pcl-mac-install.json"),
            new
            {
                version = GameVersion,
                platform = Platform,
                installedAt = DateTimeOffset.UtcNow,
                source = ManifestUrl,
                client = clientItem,
                libraries = libraryPlan.Count,
                assetObjects = assets.Count,
                uniqueObjects = assets.Select(
                    x => x.Path
                )
                    .Distinct()
                    .Count(),
                downloadedOrValidatedBytes = files.DistinctBy(
                    x => x.Path
                )
                    .Sum(
                        x => x.Size
                    ),
                integrity = "Every downloaded client/library/asset was checked by MeloongCore.FileChecker against official size and SHA-1."
            }
        );
        checkpoint?.Invoke(recoveryPlan, "committed");
        progress?.Report(
            new(
                "安装完成",
                files.Length,
                files.Length,
                files.Sum(
                    x => x.Size
                ),
                files.Sum(
                    x => x.Size
                )
            )
        );
        return installed;
    }

    // [实现] LoadInstalled 的实现：准备 var folder = Paths.VersionFolder(GameVersion)；准备 var jsonPath = Path.Combine(folder, GameVersion + ".json")；按 !File.Exists( Path.Combine(folder, "pcl-mac-install.json") ) 分支处理。
    public InstalledVersion LoadInstalled()
    {
        var folder = Paths.VersionFolder(GameVersion);
        var jsonPath = Path.Combine(folder, GameVersion + ".json");
        if (!File.Exists(
            Path.Combine(folder, "pcl-mac-install.json")
        ))
            throw new FileNotFoundException("请先完成游戏安装。");
        var metadata = JsonFiles.Read(jsonPath);
        var jar = Path.Combine(folder, GameVersion + ".jar");
        if (!DownloadService.IsValid(
            Artifact((JObject)metadata["downloads"]!["client"]!, jar)
        ))
            throw new InvalidDataException("客户端校验失败，请重新安装修复。");
        return new(
            GameVersion,
            jsonPath,
            jar,
            (string)metadata["assetIndex"]!["id"]!,
            BuildLibraries(metadata, Paths.Game, Platform),
            VersionCatalog.JavaMajor(metadata)
        );
    }

    // [实现] BuildLibraries 的实现：准备 var result = new List<DownloadItem>()；遍历 version["libraries"]!.OfType<JObject>() 处理 library；返回 result.DistinctBy( x => x.Path ) .ToArray()。
    public static IReadOnlyList<DownloadItem> BuildLibraries(JObject version, string gameRoot, TargetPlatform platform)
    {
        var result = new List<DownloadItem>();
        foreach (var library in version["libraries"]!.OfType<JObject>())
        {
            // [实现] 对 version["libraries"]!.OfType<JObject>() 中的每一项：按 !MinecraftRules.Check( library["rules"], platform.Os, platform.Architecture, platform.OsVersion… 分支处理；按 library["downloads"]?["artifact"] is JObject artifact 分支处理；按 library["natives"]?[platform.Os] is JToken classifier 分支处理。
            if (!MinecraftRules.Check(
                library["rules"],
                platform.Os,
                platform.Architecture,
                platform.OsVersion,
                new Dictionary<string, bool>()
            ))
                continue;
            if (library["downloads"]?["artifact"] is JObject artifact)
                result.Add(
                    Artifact(
                        artifact,
                        AppPaths.SafeChild(
                            Path.Combine(gameRoot, "libraries"),
                            (string)artifact["path"]!
                        )
                    )
                );
            if (library["natives"]?[platform.Os] is JToken classifier)
            {
                // [实现] 条件成立（library["natives"]?[platform.Os] is JToken classifier）时：准备 var name = classifier.ToString() .Replace( "${arch}", platform.Architecture is "x86" ? "32" : "…；按 library["downloads"]?["classifiers"]?[name] is not JObject native 分支处理；执行 result.Add( Artifact( native, AppPaths.SafeChild( Path.Combine(gameRoot, "libraries"), (string)…。
                var name = classifier.ToString()
                    .Replace(
                        "${arch}",
                        platform.Architecture is "x86"
                            ? "32"
                            : "64"
                    );
                if (library["downloads"]?["classifiers"]?[name] is not JObject native)
                    throw new InvalidDataException("缺少原生库元数据：" + name);
                result.Add(
                    Artifact(
                        native,
                        AppPaths.SafeChild(
                            Path.Combine(gameRoot, "libraries"),
                            (string)native["path"]!
                        ),
                        true
                    )
                );
            }
        }

        return result.DistinctBy(
            x => x.Path
        )
            .ToArray();
    }

    // [实现] Artifact 的实现：准备 var hash = (string? )json["sha1"]；按 hash is null || !Regex.IsMatch(hash, "^[0-9a-f]{40}$") 分支处理；准备 var url = (string? )json["url"]。
    private static DownloadItem Artifact(JObject json, string path, bool native = false)
    {
        var hash = (string? )json["sha1"];
        if (hash is null || !Regex.IsMatch(hash, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("缺少有效的官方 SHA-1。");
        var url = (string? )json["url"];
        var uri = string.IsNullOrWhiteSpace(url) && (bool? )json["pclInstallerGenerated"] == true
            ? new Uri(
                Path.GetFullPath(path)
            )
            : new Uri(
                url ?? throw new InvalidDataException("缺少下载 URL。")
            );
        return new(uri, path, (long)json["size"]!, hash, native);
    }

    // [实现] Dispose 的实现：执行 metadata.Dispose()；执行 Downloads.Dispose()。
    // 远程桌面的兼容服务在归还写授权后只能释放资源，不能再 Flush 共享任务文件。
    public void DisposeWithoutFlush()
    {
        metadata.Dispose();
        Downloads.DisposeWithoutFlush();
        if (ownsGameProcesses)
        {
            GameProcesses.Dispose();
        }
    }

    public void Dispose()
    {
        metadata.Dispose();
        Downloads.Dispose();
        if (ownsGameProcesses)
        {
            GameProcesses.Dispose();
        }
    }
}

// 内部原版计划：从本次重新校验的官方元数据生成，不由恢复文件提供下载地址。
public sealed record VanillaInstallPlan(string MetadataSha1, string AssetIndexSha1, IReadOnlyList<DownloadItem> Files);
