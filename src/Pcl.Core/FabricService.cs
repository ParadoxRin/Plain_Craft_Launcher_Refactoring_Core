using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：LoaderVersion 汇集 Version、Stable，供该模块传递状态。
public sealed record LoaderVersion(string Version, bool Stable)
{
    // [实现] ToString 的实现：计算并返回 Version + (Stable ? "" : " · 预览")。
    public override string ToString() => Version + (Stable
        ? ""
        : " · 预览");
}

// [实现] 数据约定：GameProfile 汇集 Id、Name、GameVersion、Loader、GameDirectory、JsonPath，供该模块传递状态。
public sealed record GameProfile(
    string Id,
    string Name,
    string GameVersion,
    string Loader,
    string GameDirectory,
    string JsonPath
)
{
    // [实现] ToString 的实现：计算并返回 Name。
    public override string ToString() => Name;
}

// 游戏安装：根据官方版本与加载器元数据规划组件，校验下载文件，写入可复现的实例配置。
public sealed class FabricService(
    MinecraftService service,
    string? instanceName = null,
    string loader = "fabric",
    HttpMessageHandler? handler = null
) : IDisposable
{
    private readonly HttpClient http = new(
        handler ?? new HttpClientHandler()
    )
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private string ApiRoot => loader switch
    {
        "fabric" => "https://meta.fabricmc.net/v2",
        "quilt" => "https://meta.quiltmc.org/v3",
        _ => throw new ArgumentException("加载器必须是 Fabric 或 Quilt。")
    };
    public string ProfileRoot => AppPaths.SafeChild(
        Path.Combine(service.Paths.Root, "instances"),
        instanceName ?? loader + "-" + service.GameVersion
    );
    public string ProfileFile => Path.Combine(ProfileRoot, "profile.json");

    // [实现] Profiles 的实现：计算并返回 InstanceRegistry.List(service)。
    public IReadOnlyList<GameProfile> Profiles() => InstanceRegistry.List(service);
    // [实现] FabricProfile 的实现：计算并返回 Profiles() .FirstOrDefault( x => x.Loader == loader && Path.GetFullPath(x.GameDirectory) == Pat…。
    public GameProfile FabricProfile() => Profiles()
        .FirstOrDefault(
            x => x.Loader == loader && Path.GetFullPath(x.GameDirectory) == Path.GetFullPath(ProfileRoot)
        ) ?? throw new InvalidOperationException("请先安装此 " + loader + " 实例。");
    // [实现] LoaderFor 的实现：计算并返回 (string)JsonFiles.Read( Path.Combine(profile.GameDirectory, "profile.json") )["loaderVersion"]!。
    public static string LoaderFor(GameProfile profile) => (string)JsonFiles.Read(
        Path.Combine(profile.GameDirectory, "profile.json")
    )["loaderVersion"]!;
    // [实现] VersionsAsync 的实现：准备 var json = JArray.Parse( await MetadataRequest.GetAsync(http, $"{ApiRoot}/versions/loader/{serv…；返回 json.Select( x => new LoaderVersion( (string)x["loader"]!["version"]!, (bool? )x["loader"]!["st…。
    public async Task<IReadOnlyList<LoaderVersion>> VersionsAsync(CancellationToken ct = default)
    {
        var json = JArray.Parse(
            await MetadataRequest.GetAsync(http, $"{ApiRoot}/versions/loader/{service.GameVersion}", ct)
        );
        return json.Select(
            x => new LoaderVersion(
                (string)x["loader"]!["version"]!,
                (bool? )x["loader"]!["stable"] ?? !((string)x["loader"]!["version"]!).Contains('-')
            )
        )
            .OrderByDescending(
                x => System.Version.TryParse(
                    x.Version.Split('-')[0],
                    out var v
                )
                    ? v
                    : new System.Version(0, 0)
            )
            .ThenByDescending(
                x => x.Stable
            )
            .ToArray();
    }

    // [实现] InstallAsync 的实现：执行 service.LoadInstalled()；执行 loaderVersion ??= (await VersionsAsync(ct)).FirstOrDefault( x => x.Stable )?.Version ?? throw n…；按 !Regex.IsMatch(loaderVersion, @"^[0-9A-Za-z.+_-]+$") 分支处理。
    public async Task<GameProfile> InstallAsync(
        string? loaderVersion = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        LoaderRecoveryContext? recovery = null
    )
    {
        service.LoadInstalled();
        // Parent must be complete before writing an inherited profile.
        loaderVersion ??= (await VersionsAsync(ct)).FirstOrDefault(
            x => x.Stable
        )?.Version ?? throw new NotSupportedException("此 Minecraft 版本没有可用的稳定 " + loader + " 加载器。");
        if (!Regex.IsMatch(loaderVersion, @"^[0-9A-Za-z.+_-]+$"))
            throw new InvalidDataException("无效 Fabric 版本。");
        if (File.Exists(ProfileFile))
        {
            // [实现] 条件成立（File.Exists(ProfileFile)）时：准备 var existing = JsonFiles.Read(ProfileFile)；按 (string? )existing["loader"] != loader || (string? )existing["loaderVersion"] != loaderVersion … 分支处理。
            var existing = JsonFiles.Read(ProfileFile);
            if ((string? )existing["loader"] != loader || (string? )existing["loaderVersion"] != loaderVersion || (string? )existing["gameVersion"] != service.GameVersion)
                throw new InvalidOperationException("同名实例使用不同加载器版本。请先备份，或使用新的实例名称安装。");
        }

        Directory.CreateDirectory(ProfileRoot);
        using var lease = new FileStream(
            Path.Combine(service.Paths.Game, ".install.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        progress?.Report(
            new("安装 " + loader + " " + loaderVersion, 0, 0, 0, 0)
        );
        var profile = JObject.Parse(
            await MetadataRequest.GetAsync(
                http,
                $"{ApiRoot}/versions/loader/{service.GameVersion}/{Uri.EscapeDataString(loaderVersion)}/profile/json",
                ct
            )
        );
        if ((string? )profile["inheritsFrom"] != service.GameVersion)
            throw new InvalidDataException("Fabric 继承版本不匹配。");
        var libraries = (JArray)profile["libraries"]!;
        foreach (var library in libraries.OfType<JObject>())
        {
            // [实现] 对 libraries.OfType<JObject>() 中的每一项：准备 var path = MavenPath((string)library["name"]!)；准备 var baseUrl = (string? )library["url"] ?? "https://maven.fabricmc.net/"；准备 var uri = new Uri( new Uri( baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/" ), path )。
            var path = MavenPath((string)library["name"]!);
            var baseUrl = (string? )library["url"] ?? "https://maven.fabricmc.net/";
            var uri = new Uri(
                new Uri(
                    baseUrl.EndsWith('/')
                        ? baseUrl
                        : baseUrl + "/"
                ),
                path
            );
            if (uri.Scheme != "https")
                throw new InvalidDataException("Maven 下载必须使用 HTTPS。");
            var sha1 = (string? )library["sha1"];
            if (sha1 is null)
                sha1 = (await MetadataRequest.GetAsync(http, uri.AbsoluteUri + ".sha1", ct)).Trim()
                    .Split(' ', '\t', '\n')[0];
            if (!Regex.IsMatch(sha1, "^[0-9a-fA-F]{40}$"))
                throw new InvalidDataException("Maven 校验值无效。");
            library["downloads"] = new JObject
            {
                ["artifact"] = new JObject
                {
                    ["path"] = path,
                    ["url"] = uri.AbsoluteUri,
                    ["size"] = (long? )library["size"] ?? -1,
                    ["sha1"] = sha1.ToLowerInvariant()
                }
            };
        }

        if (instanceName is not null)
            profile["id"] = instanceName + "-" + (string)profile["id"]!;
        var merged = Merge(
            JsonFiles.Read(
                service.LoadInstalled()
                    .JsonPath
            ),
            profile
        );
        var plan = MinecraftService.BuildLibraries(merged, service.Paths.Game, service.Platform);
        recovery?.Report(
            "loader-planned",
            "",
            LoaderRecoveryContext.Hash(merged),
            plan
        );
        await service.Downloads.DownloadManyAsync(plan, progress, ct);
        ct.ThrowIfCancellationRequested();
        recovery?.Report("loader-committing", "", "", plan);
        JsonFiles.Write(
            Path.Combine(ProfileRoot, "version.json"),
            merged
        );
        JsonFiles.Write(
            ProfileFile,
            new
            {
                id = (string)profile["id"]!,
                loaderVersion,
                loader,
                gameVersion = service.GameVersion,
                displayName = instanceName == "mac-shaders-1.21.1"
                    ? "1.21.1 · Mac 光影（实验）"
                    : instanceName == "mac-base-1.21.1"
                        ? "1.21.1 · Mac 基础"
                        : (string? )null,
                installedAt = DateTimeOffset.UtcNow
            }
        );
        recovery?.Report("loader-committed", "", "");
        var saved = JsonFiles.Read(ProfileFile);
        return new(
            (string)profile["id"]!,
            (string? )saved["displayName"] ?? $"{service.GameVersion} · {loader} {loaderVersion}",
            service.GameVersion,
            loader,
            ProfileRoot,
            Path.Combine(ProfileRoot, "version.json")
        );
    }

    // [实现] LoadInstalled 的实现：计算并返回 InstanceRegistry.Load(service, profile)。
    public InstalledVersion LoadInstalled(GameProfile profile) => InstanceRegistry.Load(service, profile);
    // [实现] MavenPath 的实现：准备 var pieces = coordinate.Split(':')；按 pieces.Length is < 3 or > 4 || pieces.Any( x => !Regex.IsMatch(x, @"^[A-Za-z0-9_.+\-]+$") || x … 分支处理；返回 $"{pieces[0].Replace('.', '/')}/{pieces[1]}/{pieces[2]}/{pieces[1]}-{pieces[2]}{(pieces.Length …。
    public static string MavenPath(string coordinate)
    {
        var pieces = coordinate.Split(':');
        if (pieces.Length is < 3 or > 4 || pieces.Any(
            x => !Regex.IsMatch(x, @"^[A-Za-z0-9_.+\-]+$") || x is "." or ".."
        ))
            throw new InvalidDataException("无效 Maven 坐标。");
        return $"{pieces[0].Replace('.', '/')}/{pieces[1]}/{pieces[2]}/{pieces[1]}-{pieces[2]}{(pieces.Length == 4
                ? "-" + pieces[3]
                : "")}.jar";
    }

    // [实现] Merge 的实现：准备 var result = (JObject)parent.DeepClone()；执行 result["id"] = child["id"]!.DeepClone()；执行 result["mainClass"] = child["mainClass"]!.DeepClone()。
    public static JObject Merge(JObject parent, JObject child)
    {
        var result = (JObject)parent.DeepClone();
        result["id"] = child["id"]!.DeepClone();
        result["mainClass"] = child["mainClass"]!.DeepClone();
        // Legacy Forge/OptiFine descriptors supply a complete command line, not appended arguments.
        if (child["minecraftArguments"] is JValue legacy && child["arguments"] is not JObject)
        {
            // [实现] 条件成立（child["minecraftArguments"] is JValue legacy && child["arguments"] is not JObject）时：执行 result["minecraftArguments"] = legacy.DeepClone()；执行 result["arguments"]!["game"] = new JArray( LegacyLaunchAdapter.SplitArguments((string)legacy!) …；执行 result["pclLegacyArguments"] = true。
            result["minecraftArguments"] = legacy.DeepClone();
            result["arguments"]!["game"] = new JArray(
                LegacyLaunchAdapter.SplitArguments((string)legacy!)
            );
            result["pclLegacyArguments"] = true;
        }

        foreach (var kind in new[]
        {
            "game",
            "jvm"
        }

        )
        {
            // [实现] 对 new[] { "game", "jvm" } 中的每一项：准备 var args = (JArray)result["arguments"]![kind]!；遍历 child["arguments"]?[kind] ?? new JArray() 处理 arg。
            var args = (JArray)result["arguments"]![kind]!;
            foreach (var arg in child["arguments"]?[kind] ?? new JArray())
                args.Add(
                    arg.DeepClone()
                );
        }

        // Maven group/artifact/classifier is the override key; newer child libraries replace parent versions.
        string Key(JToken lib)
        {
            var p = ((string)lib["name"]!).Split(':');
            return string.Join(
                ':',
                p.Take(2)
            ) + (p.Length > 3
                ? ":" + p[3]
                : "");
        }

        var childLibraries = child["libraries"]!.ToArray();
        var keys = childLibraries.Select(Key)
            .ToHashSet();
        result["libraries"] = new JArray(
            parent["libraries"]!.Where(
                x => !keys.Contains(
                    Key(x)
                )
            )
                .Concat(childLibraries)
                .Select(
                    x => x.DeepClone()
                )
        );
        return result;
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
