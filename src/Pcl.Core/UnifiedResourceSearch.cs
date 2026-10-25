namespace Pcl.Core;
// [实现] 数据约定：ResourceSearchQuery 汇集 Kind、Text、GameVersion、Loader、Page，供该模块传递状态。
public sealed record ResourceSearchQuery(ResourceKind Kind, string Text, string GameVersion, string Loader, int Page = 0);
// [实现] 数据约定：ResourceSearchHit 汇集 Identity、Title、Description、Sources、Modrinth、CurseForge、LocalDirectory、Mcmod，供该模块传递状态。
public sealed record ResourceSearchHit(
    string Identity,
    string Title,
    string Description,
    IReadOnlyList<string> Sources,
    ResourceProject? Modrinth = null,
    CurseResourceProject? CurseForge = null,
    string? LocalDirectory = null,
    McmodProject? Mcmod = null
)
{
    // [实现] ToString 的实现：计算并返回 Title + " · " + string.Join(" + ", Sources)。
    public override string ToString() => Title + " · " + string.Join(" + ", Sources);
}

// [实现] 数据约定：ResourceSourcePage 汇集 Source、Hits、HasNext、Error，供该模块传递状态。
public sealed record ResourceSourcePage(string Source, IReadOnlyList<ResourceSearchHit> Hits, bool HasNext, string? Error = null);
// [实现] 数据约定：UnifiedSearchResult 汇集 Hits、Sources，供该模块传递状态。
public sealed record UnifiedSearchResult(IReadOnlyList<ResourceSearchHit> Hits, IReadOnlyList<ResourceSourcePage> Sources)
{
    public bool HasNext => Sources.Any(
        x => x.HasNext
    );
}

// 资源发现：查询并转换下载平台元数据，按游戏版本和加载器筛选资源，保留来源与可用性信息。
public static class UnifiedResourceSearch
{
    // [实现] QueryAsync 的实现：按 query.Page < 0 || query.Page > 5000 分支处理；准备 var tasks = providers.Select( async p => { try { return await p.Value(query, ct); } catch (Oper…；准备 var pages = await Task.WhenAll(tasks)。
    public static async Task<UnifiedSearchResult> QueryAsync(
        ResourceSearchQuery query,
        IReadOnlyDictionary<string, Func<ResourceSearchQuery, CancellationToken, Task<ResourceSourcePage>>> providers,
        CancellationToken ct = default
    )
    {
        if (query.Page < 0 || query.Page > 5000)
            throw new ArgumentOutOfRangeException(
                nameof(query.Page)
            );
        var tasks = providers.Select(
            async p =>
            {
                // [实现] 回调触发时：执行受异常处理保护的操作。
                try
                {
                    // [实现] 在异常保护范围内：返回 await p.Value(query, ct)。
                    return await p.Value(query, ct);
                }
                catch (OperationCanceledException)when (ct.IsCancellationRequested)
                {
                    // [实现] 捕获 (OperationCanceledException) 后：重新抛出当前异常。
                    throw;
                }
                catch (Exception ex)when (ex is HttpRequestException or IOException or TaskCanceledException or Newtonsoft.Json.JsonException)
                {
                    // [实现] 捕获 (Exception ex) 后：返回 new ResourceSourcePage( p.Key, [], false, "来源暂不可用，请重试" )。
                    return new ResourceSourcePage(
                        p.Key,
                        [],
                        false,
                        "来源暂不可用，请重试"
                    );
                }
            }
        );
        var pages = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        return new(
            Merge(
                pages.SelectMany(
                    p => p.Hits
                )
            ),
            pages
        );
    }

    // [实现] Merge 的实现：准备 var order = new List<string>()；准备 var rows = new Dictionary<string, ResourceSearchHit>(StringComparer.Ordinal)；遍历 input 处理 hit。
    public static IReadOnlyList<ResourceSearchHit> Merge(IEnumerable<ResourceSearchHit> input)
    {
        var order = new List<string>();
        var rows = new Dictionary<string, ResourceSearchHit>(StringComparer.Ordinal);
        foreach (var hit in input)
        {
            // [实现] 对 input 中的每一项：按 !rows.TryGetValue(hit.Identity, out var prior) 分支处理。
            if (!rows.TryGetValue(hit.Identity, out var prior))
            {
                // [实现] 条件成立（!rows.TryGetValue(hit.Identity, out var prior)）时：执行 rows[hit.Identity] = hit；执行 order.Add(hit.Identity)。
                rows[hit.Identity] = hit;
                order.Add(hit.Identity);
            }
            else
                rows[hit.Identity] = prior with
                {
                    Title = hit.Sources.Contains("Modrinth")
                        ? hit.Title
                        : prior.Title,
                    Description = hit.Sources.Contains("Modrinth")
                        ? hit.Description
                        : prior.Description,
                    Sources = prior.Sources.Concat(hit.Sources)
                        .Distinct()
                        .ToArray(),
                    Modrinth = hit.Sources.Contains("Modrinth")
                        ? hit.Modrinth
                        : prior.Modrinth ?? hit.Modrinth,
                    CurseForge = prior.CurseForge ?? hit.CurseForge,
                    LocalDirectory = prior.LocalDirectory ?? hit.LocalDirectory,
                    Mcmod = prior.Mcmod ?? hit.Mcmod
                };
        }

        return order.Select(
            x => rows[x]
        )
            .ToArray();
    }

    // [实现] SearchAsync 的实现：准备 var catalog = new ResourceCatalog(paths: service.Paths)；准备 var curse = string.IsNullOrWhiteSpace(curseKey) ? null : new CurseForgeCatalog(curseKey ?? "", paths: service.Paths)；准备 var providers = new Dictionary<string, Func<ResourceSearchQuery, CancellationToken, Task<Resour…。
    public static async Task<UnifiedSearchResult> SearchAsync(
        MinecraftService service,
        GameProfile profile,
        ResourceSearchQuery query,
        bool online = true,
        string? curseKey = null,
        CancellationToken ct = default,
        bool mcmodOnly = false
    )
    {
        using var catalog = new ResourceCatalog(paths: service.Paths);
        using var curse = string.IsNullOrWhiteSpace(curseKey) && (DownloadTasks.For(service.Paths)
            .Options.CurseForgeApiMirror.Length == 0 || DownloadTasks.For(service.Paths)
            .Options.ResourceSource == "official")
            ? null
            : new CurseForgeCatalog(curseKey ?? "", paths: service.Paths);
        var providers = new Dictionary<string, Func<ResourceSearchQuery, CancellationToken, Task<ResourceSourcePage>>>();
        if (!mcmodOnly)
            providers["本地"] = async (q, token) =>
            {
                // [实现] 回调触发时：准备 var installer = new ResourceInstaller(profile.GameDirectory, service.Downloads, profile.Loader,…；准备 var text = ResourceAliases.SearchQuery(q.Text)；准备 var rows = installer.List() .Where( x => x.Kind == q.Kind && (string.IsNullOrWhiteSpace(q.Text)…。
                var installer = new ResourceInstaller(profile.GameDirectory, service.Downloads, profile.Loader, profile.GameVersion);
                var text = ResourceAliases.SearchQuery(q.Text);
                var rows = installer.List()
                    .Where(
                        x => x.Kind == q.Kind && (string.IsNullOrWhiteSpace(q.Text) || x.Name.Contains(q.Text, StringComparison.OrdinalIgnoreCase) || x.FileName.Contains(q.Text, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || x.FileName.Contains(text, StringComparison.OrdinalIgnoreCase))
                    )
                    .OrderBy(
                        x => x.Name,
                        StringComparer.Ordinal
                    )
                    .ToArray();
                var hits = new List<ResourceSearchHit>();
                foreach (var row in rows.Skip(q.Page * 20)
                    .Take(20))
                {
                    // [实现] 对 rows.Skip(q.Page * 20) .Take(20) 中的每一项：执行 token.ThrowIfCancellationRequested()；准备 var identity = "local:" + row.ProjectId；准备 ResourceProject? remote = null。
                    token.ThrowIfCancellationRequested();
                    var identity = "local:" + row.ProjectId;
                    ResourceProject? remote = null;
                    var file = ResourceInstaller.SafeTarget(
                        profile.GameDirectory,
                        ResourceInstaller.Folder(row.Kind) + "/" + row.FileName + (row.Enabled
                            ? ""
                            : ".disabled")
                    );
                    if (!DownloadService.IsValid(
                        new(
                            new Uri("https://localhost/unused"),
                            file,
                            row.Size,
                            row.Sha1
                        )
                    ))
                        continue;
                    if (row.ProjectId.StartsWith("curse-project-"))
                        identity = "curseforge:" + row.ProjectId[14..];
                    else if (!row.ProjectId.StartsWith("local-"))
                    {
                        // [实现] 条件成立（!row.ProjectId.StartsWith("local-")）时：执行 identity = "modrinth:" + row.ProjectId；执行 remote = new(row.ProjectId, row.ProjectId, row.Name, "本地已安装", "", 0, row.Kind)。
                        identity = "modrinth:" + row.ProjectId;
                        remote = new(row.ProjectId, row.ProjectId, row.Name, "本地已安装", "", 0, row.Kind);
                    }
                    else if (online)
                    {
                        // [实现] 条件成立（online）时：执行受异常处理保护的操作。
                        try
                        {
                            // [实现] 在异常保护范围内：准备 var version = await catalog.VersionFromHashAsync(row.Sha1, token)；按 version is not null && version.GameVersions.Contains(q.GameVersion) 分支处理。
                            var version = await catalog.VersionFromHashAsync(row.Sha1, token);
                            if (version is not null && version.GameVersions.Contains(q.GameVersion))
                            {
                                // [实现] 条件成立（version is not null && version.GameVersions.Contains(q.GameVersion)）时：执行 identity = "modrinth:" + version.ProjectId；执行 remote = new( version.ProjectId, version.ProjectId, row.Name, "本地文件 SHA-1 匹配 Modrinth", "", 0, …。
                                identity = "modrinth:" + version.ProjectId;
                                remote = new(
                                    version.ProjectId,
                                    version.ProjectId,
                                    row.Name,
                                    "本地文件 SHA-1 匹配 Modrinth",
                                    "",
                                    0,
                                    row.Kind
                                );
                            }
                        }
                        catch (HttpRequestException)
                        {
                            // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                        }
                        catch (TaskCanceledException)when (!token.IsCancellationRequested)
                        {
                            // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                        }
                    }

                    hits.Add(
                        new(
                            identity,
                            row.Name,
                            row.FileName,
                            [
                                "本地"
                            ],
                            remote,
                            LocalDirectory: profile.GameDirectory
                        )
                    );
                }

                return new("本地", hits, (q.Page + 1) * 20 < rows.Length);
            };
        if (online && !mcmodOnly)
            providers["Modrinth"] = async (q, token) =>
            {
                // [实现] 回调触发时：准备 var result = await catalog.SearchAsync(q.Kind, q.Text, q.GameVersion, q.Loader, q.Page * 20, ct…；返回 new( "Modrinth", result.Projects.Select( x => new ResourceSearchHit( "modrinth:" + x.Id, x.Titl…。
                var result = await catalog.SearchAsync(q.Kind, q.Text, q.GameVersion, q.Loader, q.Page * 20, ct: token);
                return new(
                    "Modrinth",
                    result.Projects.Select(
                        x => new ResourceSearchHit(
                            "modrinth:" + x.Id,
                            x.Title,
                            x.Description,
                            [
                                "Modrinth"
                            ],
                            x
                        )
                    )
                        .ToArray(),
                    q.Page * 20 + 20 < result.Total
                );
            };
        if (online && !mcmodOnly && curse is not null && query.Kind != ResourceKind.DataPack)
            providers["CurseForge"] = async (q, token) =>
            {
                // [实现] 回调触发时：准备 var result = await curse.SearchAsync(q.Kind, q.Text, q.GameVersion, q.Loader, q.Page * 20, toke…；返回 new( "CurseForge", result.Select( x => new ResourceSearchHit( "curseforge:" + x.Id, x.Name, x.S…。
                var result = await curse.SearchAsync(q.Kind, q.Text, q.GameVersion, q.Loader, q.Page * 20, token);
                return new(
                    "CurseForge",
                    result.Select(
                        x => new ResourceSearchHit(
                            "curseforge:" + x.Id,
                            x.Name,
                            x.Summary,
                            [
                                "CurseForge"
                            ],
                            CurseForge: x
                        )
                    )
                        .ToArray(),
                    result.Count == 20
                );
            };
        if (query.Kind == ResourceKind.Mod)
            providers[McmodCatalog.Source] = (q, token) =>
            {
                // [实现] 回调触发时：准备 var result = McmodCatalog.Search(q.Text, q.Page, ct: token)；返回 Task.FromResult( new ResourceSourcePage( McmodCatalog.Source, result.Projects.Select( x => new …。
                var result = McmodCatalog.Search(q.Text, q.Page, ct: token);
                return Task.FromResult(
                    new ResourceSourcePage(
                        McmodCatalog.Source,
                        result.Projects.Select(
                            x => new ResourceSearchHit(
                                "mcmod:" + x.Id,
                                x.Title,
                                "内置索引；选择后联网检查兼容版本。",
                                [
                                    McmodCatalog.Source
                                ],
                                Mcmod: x
                            )
                        )
                            .ToArray(),
                        result.HasNext
                    )
                );
            };
        return await QueryAsync(query, providers, ct);
    }
}
