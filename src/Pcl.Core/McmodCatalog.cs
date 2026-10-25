using PCLCS;

namespace Pcl.Core;
// [实现] 数据约定：McmodProject 汇集 Id、Title、ModrinthSlugs、CurseForgeSlugs、Popularity，供该模块传递状态。
public sealed record McmodProject(
    int Id,
    string Title,
    IReadOnlyList<string> ModrinthSlugs,
    IReadOnlyList<string> CurseForgeSlugs,
    int Popularity
)
{
    public string WebUrl => $"https://www.mcmod.cn/class/{Id}.html";
    public string DownloadPage => $"https://www.mcmod.cn/download/{Id}.html";
}

// [实现] 数据约定：McmodSearch 汇集 Projects、Total、HasNext，供该模块传递状态。
public sealed record McmodSearch(IReadOnlyList<McmodProject> Projects, int Total, bool HasNext);
// [实现] 数据约定：McmodVersions 汇集 Releases、UnavailableProjects，供该模块传递状态。
public sealed record McmodVersions(IReadOnlyList<ResourceRelease> Releases, IReadOnlyList<string> UnavailableProjects);
/// <summary>
/// Offline MC百科 index already shipped by the fixed PCLCS baseline. Never presented as a live crawl.
/// Explicit upstream slugs are resolved through Modrinth; no fuzzy-name identity or CAPTCHA endpoint.
/// </summary>
public static class McmodCatalog
{
    public const string Source = "MC 百科（内置索引）";
    public const string IndexProvenance = "PCL 6b9a50df911c9d68b69b24c979efba477fb14b67 / PCLCS Resource/WikiEntries.txt";
    private static readonly Lazy<IReadOnlyList<McmodProject>> index = new(
        () => WikiEntry.All.Value.GroupBy(
            x => x.Id
        )
            .Select(
                g => new McmodProject(
                    g.Key,
                    g.Select(
                        x => x.ChineseName
                    )
                        .FirstOrDefault(
                            x => !string.IsNullOrWhiteSpace(x)
                        ) ?? g.SelectMany(
                        x => x.Slugs.Values
                    )
                        .FirstOrDefault() ?? $"模组 {g.Key}",
                    g.Select(
                        x => x.Slugs.GetValueOrDefault(ResourcePlatforms.Modrinth)
                    )
                        .Where(ValidSlug)
                        .Cast<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    g.Select(
                        x => x.Slugs.GetValueOrDefault(ResourcePlatforms.CurseForge)
                    )
                        .Where(ValidSlug)
                        .Cast<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    g.Max(
                        x => x.Popularity
                    )
                )
            )
            .OrderByDescending(
                x => x.Popularity
            )
            .ThenBy(
                x => x.Id
            )
            .ToArray()
    );
    public static IReadOnlyList<McmodProject> Entries => index.Value;

    // [实现] ValidSlug 的实现：计算并返回 value is { Length: > 0 and <= 128 } && value.All( c => char.IsAsciiLetterOrDigit(c) || c is '-'…。
    private static bool ValidSlug(string? value) => value is { Length: > 0 and <= 128 } && value.All(
        c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'
    );
    // [实现] Find 的实现：计算并返回 Entries.FirstOrDefault( x => x.Id == id )。
    public static McmodProject? Find(int id) => Entries.FirstOrDefault(
        x => x.Id == id
    );
    // [实现] Search 的实现：按 page is < 0 or > 5000 || pageSize is < 1 or > 100 分支处理；执行 query = query.Trim()；按 query.Length > 200 分支处理。
    public static McmodSearch Search(string query, int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        if (page is < 0 or > 5000 || pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(page)
            );
        query = query.Trim();
        if (query.Length > 200)
            throw new ArgumentException("搜索词最多 200 字。");
        var id = IdFromQuery(query);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = new List<McmodProject>();
        foreach (var entry in Entries)
        {
            // [实现] 对 Entries 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var text = entry.Title + " " + string.Join( " ", entry.ModrinthSlugs.Concat(entry.CurseForgeSlu…；按 id.HasValue ? entry.Id == id : words.All( w => text.Contains(w, StringComparison.OrdinalIgnoreC… 分支处理。
            ct.ThrowIfCancellationRequested();
            var text = entry.Title + " " + string.Join(
                " ",
                entry.ModrinthSlugs.Concat(entry.CurseForgeSlugs)
            );
            if (id.HasValue
                ? entry.Id == id
                : words.All(
                    w => text.Contains(w, StringComparison.OrdinalIgnoreCase) || text.Replace('-', ' ')
                        .Contains(w, StringComparison.OrdinalIgnoreCase)
                ))
                hits.Add(entry);
        }

        var ordered = hits.OrderByDescending(
            x => string.Equals(x.Title, query, StringComparison.OrdinalIgnoreCase) || x.ModrinthSlugs.Contains(query, StringComparer.OrdinalIgnoreCase) || x.CurseForgeSlugs.Contains(query, StringComparer.OrdinalIgnoreCase)
        )
            .ThenByDescending(
                x => x.Popularity
            )
            .ThenBy(
                x => x.Id
            )
            .ToArray();
        return new(
            ordered.Skip(page * pageSize)
                .Take(pageSize)
                .ToArray(),
            ordered.Length,
            (page + 1) * pageSize < ordered.Length
        );
    }

    // [实现] IdFromQuery 的实现：按 int.TryParse(query, out var number) && number > 0 分支处理；按 Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host is "ww… 分支处理；返回 null。
    private static int? IdFromQuery(string query)
    {
        if (int.TryParse(query, out var number) && number > 0)
            return number;
        if (Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host is "www.mcmod.cn" or "mcmod.cn" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo))
        {
            // [实现] 条件成立（Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host is "ww…）时：准备 var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)；按 parts.Length == 2 && parts[0] is "class" or "download" && parts[1].EndsWith(".html", StringComp… 分支处理。
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] is "class" or "download" && parts[1].EndsWith(".html", StringComparison.Ordinal) && int.TryParse(parts[1][..^5], out number) && number > 0)
                return number;
        }

        return null;
    }

    // [实现] SearchPage 的实现：计算并返回 string.IsNullOrWhiteSpace(query) ? "https://www.mcmod.cn/modlist.html" : "https://search.mcmod.…。
    public static string SearchPage(string query) => string.IsNullOrWhiteSpace(query)
        ? "https://www.mcmod.cn/modlist.html"
        : "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(
            query.Trim()
        );
    // [实现] VersionsAsync 的实现：准备 var versions = new List<ResourceRelease>()；准备 var unavailable = new List<string>()；遍历 entry.ModrinthSlugs 处理 slug。
    public static async Task<McmodVersions> VersionsAsync(
        McmodProject entry,
        ResourceCatalog catalog,
        string gameVersion,
        string loader,
        CancellationToken ct = default
    )
    {
        var versions = new List<ResourceRelease>();
        var unavailable = new List<string>();
        foreach (var slug in entry.ModrinthSlugs)
        {
            // [实现] 对 entry.ModrinthSlugs 中的每一项：执行 ct.ThrowIfCancellationRequested()；按 !ValidSlug(slug) 分支处理；执行受异常处理保护的操作。
            ct.ThrowIfCancellationRequested();
            if (!ValidSlug(slug))
                throw new InvalidDataException("无效发布项目标识。");
            try
            {
                // [实现] 在异常保护范围内：准备 var project = await catalog.ProjectAsync(slug, ct)；按 project.Kind != ResourceKind.Mod || !string.Equals(project.Slug, slug, StringComparison.Ordinal… 分支处理；准备 var releases = await catalog.VersionsAsync(project.Id, ResourceKind.Mod, gameVersion, loader, c…。
                var project = await catalog.ProjectAsync(slug, ct);
                // A stale mapping must not silently resolve to a renamed or different kind of project.
                if (project.Kind != ResourceKind.Mod || !string.Equals(project.Slug, slug, StringComparison.OrdinalIgnoreCase))
                {
                    // [实现] 条件成立（project.Kind != ResourceKind.Mod || !string.Equals(project.Slug, slug, StringComparison.Ordinal…）时：执行 unavailable.Add(slug)；执行 continue;。
                    unavailable.Add(slug);
                    continue;
                }

                var releases = await catalog.VersionsAsync(project.Id, ResourceKind.Mod, gameVersion, loader, ct);
                versions.AddRange(
                    releases.Where(
                        x => x.ProjectId == project.Id && x.GameVersions.Contains(gameVersion) && x.Loaders.Contains(loader)
                    )
                );
            }
            catch (OperationCanceledException)when (ct.IsCancellationRequested)
            {
                // [实现] 捕获 (OperationCanceledException) 后：重新抛出当前异常。
                throw;
            }
            catch (Exception ex)when (ex is HttpRequestException or IOException or TaskCanceledException or Newtonsoft.Json.JsonException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 unavailable.Add(slug)。
                unavailable.Add(slug);
            }
        }

        return new(
            versions.DistinctBy(
                x => x.Id
            )
                .OrderByDescending(
                    x => x.Published
                )
                .ToArray(),
            unavailable
        );
    }
}
