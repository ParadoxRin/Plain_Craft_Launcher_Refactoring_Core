using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 状态集合：ResourceKind 限定为 Mod、Shader、ResourcePack、Modpack、DataPack。
public enum ResourceKind
{
    Mod,
    Shader,
    ResourcePack,
    Modpack,
    DataPack
}

// [实现] 数据约定：ResourceProject 汇集 Id、Slug、Title、Description、Author、Downloads、Kind，供该模块传递状态。
public sealed record ResourceProject(
    string Id,
    string Slug,
    string Title,
    string Description,
    string Author,
    long Downloads,
    ResourceKind Kind
)
{
    // 图标仅用于目录展示，不参与资源身份、校验或安装。
    public string? IconUrl { get; init; }
    public string WebUrl => $"https://modrinth.com/{ResourceCatalog.TypeName(Kind)}/{Uri.EscapeDataString(Slug)}";

    // [实现] ToString 的实现：计算并返回 Title。
    public override string ToString() => Title;
}

// [实现] 数据约定：ResourceSearch 汇集 Projects、Offset、Total，供该模块传递状态。
public sealed record ResourceSearch(IReadOnlyList<ResourceProject> Projects, int Offset, int Total);
// [实现] 数据约定：ResourceFile 汇集 Name、Url、Size、Sha1，供该模块传递状态。
public sealed record ResourceFile(string Name, Uri Url, long Size, string Sha1);
// [实现] 数据约定：ResourceDependency 汇集 ProjectId、VersionId、Type，供该模块传递状态。
public sealed record ResourceDependency(string? ProjectId, string? VersionId, string Type);
// [实现] 数据约定：ResourceRelease 汇集 Id、ProjectId、Name、Number、Channel、Published、GameVersions、Loaders、File、Dependencies，供该模块传递状态。
public sealed record ResourceRelease(
    string Id,
    string ProjectId,
    string Name,
    string Number,
    string Channel,
    DateTimeOffset Published,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    ResourceFile File,
    IReadOnlyList<ResourceDependency> Dependencies
)
{
    public string ProjectTitle { get; init; } = "";
    public string DisplayName => ProjectTitle.Length > 0
        ? ProjectTitle
        : Name;

    // [实现] ToString 的实现：计算并返回 $"{Name} · {Channel} · {Published:yyyy-MM-dd}"。
    public override string ToString() => $"{Name} · {Channel} · {Published:yyyy-MM-dd}";
}

// [实现] 数据约定：ResourceInstallPlan 汇集 Kind、RootProjectId、Releases，供该模块传递状态。
public sealed record ResourceInstallPlan(ResourceKind Kind, string RootProjectId, IReadOnlyList<ResourceRelease> Releases)
{
    public long Bytes => Releases.Sum(
        r => r.File.Size
    );
}

// Official Modrinth v2, no launcher-specific proxy or bundled credentials.
public sealed class ResourceCatalog : IDisposable
{
    private readonly HttpClient http;
    private readonly AppPaths? paths;
    // [实现] ResourceCatalog 构造 的实现：执行 this.paths = paths；执行 http = handler is null ? new HttpClient() : new HttpClient(handler)；执行 http.BaseAddress = new Uri("https://api.modrinth.com/v2/")。
    public ResourceCatalog(HttpMessageHandler? handler = null, AppPaths? paths = null)
    {
        this.paths = paths;
        http = handler is null
            ? new HttpClient()
            : new HttpClient(handler);
        http.BaseAddress = new Uri("https://api.modrinth.com/v2/");
        http.Timeout = TimeSpan.FromSeconds(45);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PCL-MacPort/0.2 (https://github.com/Meloong-Git/PCL)");
    }

    // [实现] TypeName 的实现：计算并返回 kind switch { ResourceKind.Mod => "mod", ResourceKind.Shader => "shader", ResourceKind.Resource…。
    public static string TypeName(ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => "mod",
        ResourceKind.Shader => "shader",
        ResourceKind.ResourcePack => "resourcepack",
        ResourceKind.DataPack => "datapack",
        _ => "modpack"
    };
    // [实现] EscapeArray 的实现：计算并返回 Uri.EscapeDataString( JsonConvert.SerializeObject(values) )。
    private static string EscapeArray(params string[] values) => Uri.EscapeDataString(
        JsonConvert.SerializeObject(values)
    );
    // [实现] Get 的实现：准备 var options = paths is null ? new NetworkOptions() : DownloadTasks.For(paths) .Options；准备 var official = new Uri(http.BaseAddress!, path).AbsoluteUri；准备 var mirror = options.ResourceApiMirror.Length == 0 ? "" : options.ResourceApiMirror.TrimEnd('/'…。
    private async Task<JToken> Get(string path, CancellationToken ct)
    {
        var options = paths is null
            ? new NetworkOptions()
            : DownloadTasks.For(paths)
                .Options;
        var official = new Uri(http.BaseAddress!, path).AbsoluteUri;
        var mirror = options.ResourceApiMirror.Length == 0
            ? ""
            : options.ResourceApiMirror.TrimEnd('/') + "/" + path;
        return JToken.Parse(
            await MetadataSourcePolicy.ReadAsync(http, official, mirror, options.ResourceSource, ct)
        );
    }

    // [实现] SearchAsync 的实现：准备 var facets = new List<string[]> { new[] { "project_type:" + TypeName(kind) } }；按 !string.IsNullOrWhiteSpace(gameVersion) 分支处理；按 kind == ResourceKind.Mod && !string.IsNullOrWhiteSpace(loader) 分支处理。
    public async Task<ResourceSearch> SearchAsync(
        ResourceKind kind,
        string query,
        string gameVersion,
        string? loader = null,
        int offset = 0,
        string sort = "relevance",
        CancellationToken ct = default,
        string? category = null
    )
    {
        var facets = new List<string[]>
        {
            new[]
            {
                "project_type:" + TypeName(kind)
            }
        };
        // 空版本表示原版目录中的“全部”，安装时仍必须校验目标实例版本。
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            // [实现] 条件成立（!string.IsNullOrWhiteSpace(gameVersion)）时：执行 facets.Add( new[] { "versions:" + gameVersion } )。
            facets.Add(
                new[]
                {
                    "versions:" + gameVersion
                }
            );
        }

        if (kind == ResourceKind.Mod && !string.IsNullOrWhiteSpace(loader))
            facets.Add(
                new[]
                {
                    "categories:" + loader
                }
            );
        if (!string.IsNullOrWhiteSpace(category))
            facets.Add(
                new[]
                {
                    "categories:" + category
                }
            );
        query = ResourceAliases.SearchQuery(query);
        if (sort is not ("relevance" or "downloads" or "updated" or "newest"))
            throw new ArgumentException("无效排序。");
        var json = await Get(
            $"search?query={Uri.EscapeDataString(query)}&facets={Uri.EscapeDataString(
                    JsonConvert.SerializeObject(facets)
                )}&offset={Math.Max(0, offset)}&limit=20&index={sort}",
            ct
        );
        return new(
            json["hits"]!.Select(
                x => new ResourceProject(
                    (string)x["project_id"]!,
                    (string)x["slug"]!,
                    ResourceAliases.DisplayName((string)x["slug"]!, (string)x["title"]!),
                    (string? )x["description"] ?? "",
                    (string? )x["author"] ?? "",
                    (long? )x["downloads"] ?? 0,
                    kind
                )
                {
                    IconUrl = (string? )x["icon_url"]
                }
            )
                .ToArray(),
            (int)json["offset"]!,
            (int)json["total_hits"]!
        );
    }

    // [实现] ProjectAsync 的实现：准备 var x = await Get( "project/" + Uri.EscapeDataString(id), ct )；准备 var kind = (string? )x["project_type"] switch { "shader" => ResourceKind.Shader, "resourcepack"…；返回 new( (string)x["id"]!, (string)x["slug"]!, (string)x["title"]!, (string? )x["description"] ?? "…。
    public async Task<ResourceProject> ProjectAsync(string id, CancellationToken ct = default)
    {
        var x = await Get(
            "project/" + Uri.EscapeDataString(id),
            ct
        );
        if (x is not JObject)
        {
            throw new InvalidDataException("资源来源返回的项目详情格式无效。");
        }

        var kind = (string? )x["project_type"] switch
        {
            "shader" => ResourceKind.Shader,
            "datapack" => ResourceKind.DataPack,
            "resourcepack" => ResourceKind.ResourcePack,
            "modpack" => ResourceKind.Modpack,
            _ => ResourceKind.Mod
        };
        return new(
            (string)x["id"]!,
            (string)x["slug"]!,
            ResourceAliases.DisplayName((string)x["slug"]!, (string)x["title"]!),
            (string? )x["description"] ?? "",
            "",
            (long? )x["downloads"] ?? 0,
            kind
        )
        {
            IconUrl = (string? )x["icon_url"]
        };
    }

    // [实现] VersionsAsync 的实现：准备 var filter = kind == ResourceKind.Mod && !string.IsNullOrWhiteSpace(loader) ? "&loaders=" + Esc…；准备 var versionFilter = string.IsNullOrWhiteSpace(gameVersion) ? "" : "game_versions=" + EscapeArra…；准备 var json = await Get( $"project/{Uri.EscapeDataString(id)}/version?{versionFilter}{filter}", ct…。
    public async Task<IReadOnlyList<ResourceRelease>> VersionsAsync(
        string id,
        ResourceKind kind,
        string gameVersion,
        string loader = "fabric",
        CancellationToken ct = default
    )
    {
        var filter = kind == ResourceKind.Mod && !string.IsNullOrWhiteSpace(loader)
            ? "&loaders=" + EscapeArray(loader)
            : kind == ResourceKind.Shader && !string.IsNullOrWhiteSpace(loader)
                ? "&loaders=" + EscapeArray("iris")
                : kind == ResourceKind.DataPack
                    ? "&loaders=" + EscapeArray("datapack")
                    : "";
        // 浏览可查询所有游戏版本；依赖解析调用仍传入实际版本和加载器。
        var versionFilter = string.IsNullOrWhiteSpace(gameVersion)
            ? ""
            : "game_versions=" + EscapeArray(gameVersion);
        var json = await Get(
            $"project/{Uri.EscapeDataString(id)}/version?{versionFilter}{filter}",
            ct
        );
        var project = await ProjectAsync(id, ct);
        return json.Select(
            x => ParseRelease(x)with
            {
                ProjectTitle = project.Title
            }
        )
            .OrderByDescending(
                x => x.Published
            )
            .ToArray();
    }

    // [实现] VersionAsync 的实现：计算并返回 ParseRelease( await Get( "version/" + Uri.EscapeDataString(id), ct ) )。
    // 单版本与哈希查找共用来源策略，再读取项目名称；发布名只用于版本列表。
    public async Task<ResourceRelease> VersionAsync(string id, CancellationToken ct = default)
    {
        return await WithTitle(
            ParseRelease(
                await Get(
                    "version/" + Uri.EscapeDataString(id),
                    ct
                )
            ),
            ct
        );
    }

    private async Task<ResourceRelease> WithTitle(ResourceRelease release, CancellationToken ct)
    {
        var project = await ProjectAsync(release.ProjectId, ct);
        return release with
        {
            ProjectTitle = project.Title
        };
    }

    public async Task<ResourceRelease?> VersionFromHashAsync(string sha1, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(sha1, "^[a-fA-F0-9]{40}$"))
        {
            throw new ArgumentException("无效 SHA-1。");
        }

        try
        {
            return await WithTitle(
                ParseRelease(
                    await Get(
                        "version_file/" + sha1.ToLowerInvariant() + "?algorithm=sha1",
                        ct
                    ),
                    sha1
                ),
                ct
            );
        }
        catch (IOException ex)when (ex.InnerException is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
        {
            return null;
        }
    }

    // [实现] ProjectDescriptionAsync 的实现：计算并返回 (string? )((await Get( "project/" + Uri.EscapeDataString(id), ct ))["body"]) ?? ""。
    public async Task<string> ProjectDescriptionAsync(string id, CancellationToken ct = default) => (string? )((await Get(
        "project/" + Uri.EscapeDataString(id),
        ct
    ))["body"]) ?? "";
    // [实现] ChangelogAsync 的实现：计算并返回 (string? )((await Get( "version/" + Uri.EscapeDataString(versionId), ct ))["changelog"]) ?? ""。
    public async Task<string> ChangelogAsync(string versionId, CancellationToken ct = default) => (string? )((await Get(
        "version/" + Uri.EscapeDataString(versionId),
        ct
    ))["changelog"]) ?? "";
    // [实现] CategoriesAsync 的实现：准备 var categories = await Get("tag/category", ct)；返回 categories.Where( x => (string? )x["project_type"] == TypeName(kind) ) .Select( x => (string)x[…。
    public async Task<IReadOnlyList<string>> CategoriesAsync(ResourceKind kind, CancellationToken ct = default)
    {
        var categories = await Get("tag/category", ct);
        return categories.Where(
            x => (string? )x["project_type"] == TypeName(kind)
        )
            .Select(
                x => (string)x["name"]!
            )
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    // [实现] ParseRelease 的实现：准备 var files = json["files"]!.ToArray()；准备 var f = files.FirstOrDefault( x => (bool? )x["primary"] == true ) ?? files.FirstOrDefault() ?? …；准备 var name = (string)f["filename"]!。
    public static ResourceRelease ParseRelease(JToken json, string? expectedSha1 = null)
    {
        var files = json["files"]!.ToArray();
        // Hash lookup must keep the exact pack file, including non-primary files in a multi-file release.
        var f = expectedSha1 is null
            ? files.FirstOrDefault(
                x => (bool? )x["primary"] == true
            ) ?? files.FirstOrDefault()
            : files.FirstOrDefault(
                x => string.Equals((string? )x["hashes"]?["sha1"], expectedSha1, StringComparison.OrdinalIgnoreCase)
            );
        if (f is null)
        {
            throw new InvalidDataException("项目没有匹配的可下载文件。");
        }

        var name = (string)f["filename"]!;
        ResourceInstaller.ValidateFileName(name);
        var hash = (string? )f["hashes"]?["sha1"] ?? throw new InvalidDataException("下载缺少 SHA-1。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-fA-F]{40}$"))
            throw new InvalidDataException("无效文件哈希。");
        var uri = new Uri((string)f["url"]!);
        if (uri.Scheme != "https")
            throw new InvalidDataException("资源下载必须使用 HTTPS。");
        return new(
            (string)json["id"]!,
            (string)json["project_id"]!,
            (string)json["name"]!,
            (string)json["version_number"]!,
            (string)json["version_type"]!,
            (DateTimeOffset)json["date_published"]!,
            json["game_versions"]!.Values<string>()
                .ToArray()!,
            json["loaders"]!.Values<string>()
                .ToArray()!,
            new(
                name,
                uri,
                (long)f["size"]!,
                hash.ToLowerInvariant()
            ),
            json["dependencies"]!.Select(
                x => new ResourceDependency(
                    (string? )x["project_id"],
                    (string? )x["version_id"],
                    (string)x["dependency_type"]!
                )
            )
                .ToArray()
        );
    }

    // [实现] ResolveAsync 的实现：准备 var byProject = new Dictionary<string, ResourceRelease>()；准备 var order = new List<ResourceRelease>()；执行 async Task Visit(ResourceRelease current) { ct.ThrowIfCancellationRequested(); discovered?.Invo…。
    public Task<ResourceInstallPlan> ResolveAsync(
        ResourceRelease release,
        ResourceKind kind,
        string gameVersion,
        string loader = "fabric",
        CancellationToken ct = default,
        IReadOnlyDictionary<string, ResourceRelease>? preferred = null,
        Action<ResourceRelease>? discovered = null
    ) => ResolveManyAsync(
        [
            release
        ],
        kind,
        gameVersion,
        loader,
        ct,
        preferred,
        discovered
    );
    /// <summary>所有根项目共用同一项目版本表；显式选择的根版本锁定，循环边只访问一次。</summary>
    public async Task<ResourceInstallPlan> ResolveManyAsync(
        IReadOnlyList<ResourceRelease> roots,
        ResourceKind kind,
        string gameVersion,
        string loader = "fabric",
        CancellationToken ct = default,
        IReadOnlyDictionary<string, ResourceRelease>? preferred = null,
        Action<ResourceRelease>? discovered = null
    )
    {
        if (roots.Count is 0 or > 512 || roots.GroupBy(
            x => x.ProjectId
        )
            .Any(
                g => g.Select(
                    x => x.Id
                )
                    .Distinct()
                    .Count() != 1
            ))
        {
            throw new InvalidDataException("根项目为空、重复版本冲突或数量过多。");
        }

        var choices = preferred is null
            ? new Dictionary<string, ResourceRelease>()
            : new Dictionary<string, ResourceRelease>(preferred);
        foreach (var root in roots)
        {
            choices[root.ProjectId] = root;
        }

        var byProject = new Dictionary<string, ResourceRelease>();
        var order = new List<ResourceRelease>();
        // [实现] Visit 的实现：执行 ct.ThrowIfCancellationRequested()；执行 discovered?.Invoke(current)；按 !current.GameVersions.Contains(gameVersion) || (kind == ResourceKind.Mod && !current.Loaders.Co… 分支处理。
        async Task Visit(ResourceRelease current)
        {
            ct.ThrowIfCancellationRequested();
            discovered?.Invoke(current);
            if (!current.GameVersions.Contains(gameVersion) || (kind == ResourceKind.Mod && !current.Loaders.Contains(loader)) || (kind == ResourceKind.DataPack && !current.Loaders.Contains("datapack")))
                throw new InvalidDataException($"{current.Name} 不兼容 Minecraft {gameVersion} / {loader}。");
            if (byProject.TryGetValue(current.ProjectId, out var previous))
            {
                // [实现] 条件成立（byProject.TryGetValue(current.ProjectId, out var previous)）时：按 previous.Id != current.Id 分支处理；结束当前过程。
                if (previous.Id != current.Id)
                    throw new InvalidDataException($"前置版本冲突：{previous.Name} / {current.Name}");
                return;
            }

            byProject.Add(current.ProjectId, current);
            if (byProject.Count > 1024)
                throw new InvalidDataException("前置关系超过限制。");
            foreach (var dependency in current.Dependencies.Where(
                x => x.Type == "required"
            ))
            {
                // [实现] 对 current.Dependencies.Where( x => x.Type == "required" ) 中的每一项：准备 ResourceRelease child；按 dependency.VersionId is not null 分支处理；按 dependency.ProjectId is not null && child.ProjectId != dependency.ProjectId 分支处理。
                ResourceRelease child;
                if (dependency.VersionId is not null)
                    child = await VersionAsync(dependency.VersionId, ct);
                else if (dependency.ProjectId is not null && choices.TryGetValue(dependency.ProjectId, out var chosen))
                    child = chosen;
                else if (dependency.ProjectId is not null)
                {
                    // [实现] 条件成立（dependency.ProjectId is not null）时：准备 var versions = await VersionsAsync(dependency.ProjectId, kind, gameVersion, loader, ct)；执行 child = versions.FirstOrDefault( x => x.Channel == "release" ) ?? versions.FirstOrDefault() ?? …。
                    var versions = await VersionsAsync(dependency.ProjectId, kind, gameVersion, loader, ct);
                    child = versions.FirstOrDefault(
                        x => x.Channel == "release"
                    ) ?? versions.FirstOrDefault() ?? throw new InvalidDataException("没有适用的前置模组：" + dependency.ProjectId);
                }
                else
                    throw new InvalidDataException("前置模组没有项目或版本 ID。");
                if (dependency.ProjectId is not null && child.ProjectId != dependency.ProjectId)
                    throw new InvalidDataException("前置项目标识不匹配。");
                await Visit(child);
            }

            order.Add(current);
        }

        foreach (var root in roots)
        {
            await Visit(root);
        }

        foreach (var current in order)
            foreach (var dependency in current.Dependencies.Where(
                x => x.Type == "incompatible"
            ))
                if (byProject.Values.Any(
                    x => dependency.VersionId is not null
                        ? x.Id == dependency.VersionId
                        : x.ProjectId == dependency.ProjectId
                ))
                    throw new InvalidDataException("模组互不兼容：" + current.Name);
        return new(kind, roots[0].ProjectId, order);
    }

    // [实现] Dispose 的实现：执行 http.Dispose()。
    public void Dispose() => http.Dispose();
}
