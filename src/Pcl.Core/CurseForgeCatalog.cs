using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：CurseResourceProject 汇集 Id、Name、Summary、WebUrl、Downloads、Kind，供该模块传递状态。
public sealed record CurseResourceProject(long Id, string Name, string Summary, string WebUrl, long Downloads, ResourceKind Kind)
{
    // [实现] ToString 的实现：计算并返回 $"{Name} · {Downloads:N0}"。
    public override string ToString() => $"{Name} · {Downloads:N0}";
}

// [实现] 数据约定：CurseResourceRelease 汇集 ProjectId、FileId、Name、FileName、Sha1、Size、DownloadUrl、Published、Channel、GameVersions、Dependencies、Page，供该模块传递状态。
public sealed record CurseResourceRelease(
    long ProjectId,
    long FileId,
    string Name,
    string FileName,
    string Sha1,
    long Size,
    Uri? DownloadUrl,
    DateTimeOffset Published,
    string Channel,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<ResourceDependency> Dependencies,
    string Page
)
{
    public string ProjectTitle { get; init; } = "";

    // [实现] ToString 的实现：计算并返回 Name + (DownloadUrl is null ? " · 需手动下载" : "")。
    public override string ToString() => Name + (DownloadUrl is null
        ? " · 需手动下载"
        : "");
}

/// <summary>Official CurseForge v1. The caller supplies its own key; this client does not persist it.</summary>
public sealed class CurseForgeCatalog(string apiKey, HttpMessageHandler? handler = null, AppPaths? paths = null) : IDisposable
{
    private readonly CurseForgeClient client = new(apiKey, handler, paths);
    // [实现] ClassId 的实现：计算并返回 kind switch { ResourceKind.Mod => 6, ResourceKind.ResourcePack => 12, ResourceKind.Shader => 65…。
    public static int ClassId(ResourceKind kind) => kind switch
    {
        ResourceKind.Mod => 6,
        ResourceKind.ResourcePack => 12,
        ResourceKind.Shader => 6552,
        ResourceKind.Modpack => 4471,
        _ => throw new NotSupportedException("此来源暂不支持该资源类型。")
    };
    // [实现] LoaderId 的实现：计算并返回 loader switch { "forge" => 1, "liteloader" => 3, "fabric" => 4, "quilt" => 5, "neoforge" => 6, …。
    private static int LoaderId(string loader) => loader switch
    {
        "forge" => 1,
        "liteloader" => 3,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0
    };
    // [实现] ProjectId 的实现：计算并返回 id.StartsWith("curse-project-", StringComparison.Ordinal) && long.TryParse(id[14..], out var va…。
    private static long ProjectId(string id) => id.StartsWith("curse-project-", StringComparison.Ordinal) && long.TryParse(id[14..], out var value)
        ? value
        : throw new InvalidDataException("不是 CurseForge 项目标识。");
    // [实现] SearchAsync 的实现：按 long.TryParse( query.Trim(), out var projectId ) && projectId > 0 分支处理；执行 query = ResourceAliases.SearchQuery(query, "curseforge")；准备 var filter = kind == ResourceKind.Mod && LoaderId(loader) != 0 ? "&modLoaderType=" + LoaderId(l…。
    public async Task<IReadOnlyList<CurseResourceProject>> SearchAsync(
        ResourceKind kind,
        string query,
        string gameVersion,
        string loader,
        int offset = 0,
        CancellationToken ct = default
    )
    {
        if (long.TryParse(
            query.Trim(),
            out var projectId
        ) && projectId > 0)
        {
            // [实现] 条件成立（long.TryParse( query.Trim(), out var projectId ) && projectId > 0）时：准备 var project = await client.Get("mods/" + projectId, ct)；按 (int? )project["classId"] != ClassId(kind) 分支处理；返回 [ ParseProject(project, kind) ]。
            var project = await client.Get("mods/" + projectId, ct);
            if ((int? )project["classId"] != ClassId(kind))
                return[];
            return[
                ParseProject(project, kind)
            ];
        }

        query = ResourceAliases.SearchQuery(query, "curseforge");
        var filter = kind == ResourceKind.Mod && LoaderId(loader) != 0
            ? "&modLoaderType=" + LoaderId(loader)
            : "";
        var data = await client.Get(
            $"mods/search?gameId=432&classId={ClassId(kind)}&searchFilter={Uri.EscapeDataString(query)}&gameVersion={Uri.EscapeDataString(gameVersion)}&index={Math.Max(0, offset)}&pageSize=20&sortField=2&sortOrder=desc{filter}",
            ct
        );
        return data.Select(
            x => ParseProject(x, kind)
        )
            .ToArray();
    }

    // [实现] ParseProject 的实现：准备 var id = (long)json["id"]!；准备 var url = (string? )json["links"]?["websiteUrl"]；按 !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host is not … 分支处理。
    private static CurseResourceProject ParseProject(JToken json, ResourceKind kind)
    {
        if (json is not JObject)
        {
            throw new InvalidDataException("CurseForge 返回的项目详情格式无效。");
        }

        var id = (long)json["id"]!;
        var url = (string? )json["links"]?["websiteUrl"];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host is not ("www.curseforge.com" or "curseforge.com"))
            url = "https://www.curseforge.com/projects/" + id;
        var slug = (string? )json["slug"] ?? uri?.Segments.LastOrDefault()?.Trim('/') ?? "";
        return new(
            id,
            ResourceAliases.DisplayName(slug, (string? )json["name"] ?? "", "curseforge"),
            (string? )json["summary"] ?? "",
            url!,
            (long? )json["downloadCount"] ?? 0,
            kind
        );
    }

    // [实现] VersionsAsync 的实现：按 project <= 0 分支处理；准备 var filter = kind == ResourceKind.Mod && LoaderId(loader) != 0 ? "&modLoaderType=" + LoaderId(l…；准备 var data = await client.Get( $"mods/{project}/files?gameVersion={Uri.EscapeDataString(gameVersi…。
    public async Task<IReadOnlyList<CurseResourceRelease>> VersionsAsync(
        long project,
        ResourceKind kind,
        string gameVersion,
        string loader,
        CancellationToken ct = default
    )
    {
        if (project <= 0)
            throw new ArgumentException("无效项目 ID。");
        var filter = kind == ResourceKind.Mod && LoaderId(loader) != 0
            ? "&modLoaderType=" + LoaderId(loader)
            : "";
        var data = await client.Get(
            $"mods/{project}/files?gameVersion={Uri.EscapeDataString(gameVersion)}&pageSize=50{filter}",
            ct
        );
        var title = ParseProject(
            await client.Get("mods/" + project, ct),
            kind
        )
            .Name;
        return data.Select(
            x => ParseRelease(x, project)with
            {
                ProjectTitle = title
            }
        )
            .Where(
                x => x.GameVersions.Contains(gameVersion)
            )
            .OrderByDescending(
                x => x.Published
            )
            .ToArray();
    }

    // [实现] ParseRelease 的实现：按 (long? )json["modId"] != project || (long? )json["id"] is not > 0 分支处理；准备 var id = (long)json["id"]!；准备 var name = (string? )json["fileName"] ?? ""。
    public static CurseResourceRelease ParseRelease(JToken json, long project)
    {
        if ((long? )json["modId"] != project || (long? )json["id"] is not > 0)
            throw new InvalidDataException("CurseForge 文件身份不匹配。");
        var id = (long)json["id"]!;
        var name = (string? )json["fileName"] ?? "";
        ResourceInstaller.ValidateFileName(name);
        var sha1 = (string? )json["hashes"]?.FirstOrDefault(
            x => (int? )x["algo"] == 1
        )?["value"] ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(sha1, "^[a-fA-F0-9]{40}$"))
            throw new InvalidDataException("文件缺少可验证的 SHA-1。");
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
            project,
            id,
            (string? )json["displayName"] ?? name,
            name,
            sha1.ToLowerInvariant(),
            size,
            uri,
            (DateTimeOffset? )json["fileDate"] ?? DateTimeOffset.MinValue,
            (int? )json["releaseType"] switch
            {
                1 => "release",
                2 => "beta",
                _ => "alpha"
            },
            json["gameVersions"]?.Values<string>()
                .OfType<string>()
                .ToArray() ?? [],
            (json["dependencies"] ?? new JArray()).Where(
                x => (int? )x["relationType"] is 3 or 5
            )
                .Select(
                    x => new ResourceDependency(
                        "curse-project-" + (long)x["modId"]!,
                        null,
                        (int? )x["relationType"] == 3
                            ? "required"
                            : "incompatible"
                    )
                )
                .ToArray(),
            $"https://www.curseforge.com/projects/{project}/files/{id}"
        );
    }

    // [实现] ResolveAsync 的实现：准备 var seen = new Dictionary<long, CurseResourceRelease>()；准备 var order = new List<ResourceRelease>()；执行 async Task Visit(CurseResourceRelease current) { ct.ThrowIfCancellationRequested(); if (seen.Tr…。
    public async Task<ResourceInstallPlan> ResolveAsync(
        CurseResourceRelease root,
        ResourceKind kind,
        string gameVersion,
        string loader,
        IReadOnlySet<long>? manualVerifiedFiles = null,
        CancellationToken ct = default
    )
    {
        var seen = new Dictionary<long, CurseResourceRelease>();
        var order = new List<ResourceRelease>();
        // [实现] Visit 的实现：执行 ct.ThrowIfCancellationRequested()；按 seen.TryGetValue(current.ProjectId, out var earlier) 分支处理；按 seen.Count >= 150 分支处理。
        async Task Visit(CurseResourceRelease current)
        {
            ct.ThrowIfCancellationRequested();
            if (seen.TryGetValue(current.ProjectId, out var earlier))
            {
                // [实现] 条件成立（seen.TryGetValue(current.ProjectId, out var earlier)）时：按 earlier.FileId != current.FileId 分支处理；结束当前过程。
                if (earlier.FileId != current.FileId)
                    throw new InvalidDataException("前置版本冲突。");
                return;
            }

            if (seen.Count >= 150)
                throw new InvalidDataException("依赖数量超过限制。");
            if (!current.GameVersions.Contains(gameVersion))
                throw new InvalidDataException("资源与目标游戏版本不兼容。");
            if (current.DownloadUrl is null && manualVerifiedFiles?.Contains(current.FileId) != true)
                throw new InvalidOperationException("此文件需手动下载并匹配：" + current.Page);
            seen.Add(current.ProjectId, current);
            foreach (var dependency in current.Dependencies.Where(
                x => x.Type == "required"
            ))
            {
                // [实现] 对 current.Dependencies.Where( x => x.Type == "required" ) 中的每一项：准备 var children = await VersionsAsync( ProjectId(dependency.ProjectId!), kind, gameVersion, loader…；准备 var chosen = children.FirstOrDefault( x => x.Channel == "release" ) ?? children.FirstOrDefault(…；执行 await Visit(chosen)。
                var children = await VersionsAsync(
                    ProjectId(dependency.ProjectId!),
                    kind,
                    gameVersion,
                    loader,
                    ct
                );
                var chosen = children.FirstOrDefault(
                    x => x.Channel == "release"
                ) ?? children.FirstOrDefault() ?? throw new InvalidDataException("没有适用的前置版本：" + dependency.ProjectId);
                await Visit(chosen);
            }

            order.Add(
                ToRelease(current, loader)
            );
        }

        await Visit(root);
        foreach (var current in order)
            foreach (var dependency in current.Dependencies.Where(
                x => x.Type == "incompatible"
            ))
                if (order.Any(
                    x => x.ProjectId == dependency.ProjectId
                ))
                    throw new InvalidDataException("资源互不兼容：" + current.Name);
        return new(kind, "curse-project-" + root.ProjectId, order);
    }

    // [实现] ToRelease 的实现：计算并返回 new( "curse-file-" + file.FileId, "curse-project-" + file.ProjectId, file.Name, file.Name, file…。
    public static ResourceRelease ToRelease(CurseResourceRelease file, string loader) => new(
        "curse-file-" + file.FileId,
        "curse-project-" + file.ProjectId,
        file.Name,
        file.Name,
        file.Channel,
        file.Published,
        file.GameVersions,
        [
            loader
        ],
        new(
            file.FileName,
            file.DownloadUrl ?? new Uri(file.Page),
            file.Size,
            file.Sha1
        ),
        file.Dependencies
    )
    {
        ProjectTitle = file.ProjectTitle
    };
    // [实现] Dispose 的实现：执行 client.Dispose()。
    public void Dispose() => client.Dispose();
}
