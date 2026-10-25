using PCLCS;

namespace Pcl.Core;
// [实现] 数据约定：ResourceAlias 汇集 WikiId、ChineseName、ModrinthSlug、CurseForgeSlug，供该模块传递状态。
public sealed record ResourceAlias(int WikiId, string? ChineseName, string? ModrinthSlug, string? CurseForgeSlug)
{
    public string WikiUrl => $"https://www.mcmod.cn/class/{WikiId}.html";
}

/// <summary>Uses the fixed PCLCS WikiEntries data shipped with the source baseline; no guessed cross-platform identity.</summary>
public static class ResourceAliases
{
    public static IReadOnlyList<ResourceAlias> Entries => WikiEntry.All.Value.Select(
        x => new ResourceAlias(
            x.Id,
            x.ChineseName,
            x.Slugs.GetValueOrDefault(ResourcePlatforms.Modrinth),
            x.Slugs.GetValueOrDefault(ResourcePlatforms.CurseForge)
        )
    )
        .ToArray();

    // [实现] Find 的实现：准备 var platform = source == "curseforge" ? ResourcePlatforms.CurseForge : ResourcePlatforms.Modrin…；准备 var item = WikiEntry.All.Value.FirstOrDefault( x => x.Slugs.GetValueOrDefault(platform) == slug…；返回 item is null ? null : new( item.Id, item.ChineseName, item.Slugs.GetValueOrDefault(ResourcePlat…。
    public static ResourceAlias? Find(string slug, string source = "modrinth")
    {
        var platform = source == "curseforge"
            ? ResourcePlatforms.CurseForge
            : ResourcePlatforms.Modrinth;
        var item = WikiEntry.All.Value.FirstOrDefault(
            x => x.Slugs.GetValueOrDefault(platform) == slug
        );
        return item is null
            ? null
            : new(
                item.Id,
                item.ChineseName,
                item.Slugs.GetValueOrDefault(ResourcePlatforms.Modrinth),
                item.Slugs.GetValueOrDefault(ResourcePlatforms.CurseForge)
            );
    }

    // [实现] SearchQuery 的实现：按 string.IsNullOrWhiteSpace(query) || !query.Any( x => x is >= '\u3400' and <= '\u9fff' ) 分支处理；准备 var platform = source == "curseforge" ? ResourcePlatforms.CurseForge : ResourcePlatforms.Modrin…；准备 var match = WikiEntry.All.Value.Where( x => x.ChineseName?.Contains( query.Trim(), StringCompar…。
    public static string SearchQuery(string query, string source = "modrinth")
    {
        if (string.IsNullOrWhiteSpace(query) || !query.Any(
            x => x is >= '\u3400' and <= '\u9fff'
        ))
            return query;
        var platform = source == "curseforge"
            ? ResourcePlatforms.CurseForge
            : ResourcePlatforms.Modrinth;
        var match = WikiEntry.All.Value.Where(
            x => x.ChineseName?.Contains(
                query.Trim(),
                StringComparison.OrdinalIgnoreCase
            ) == true && x.Slugs.ContainsKey(platform)
        )
            .OrderByDescending(
                x => x.ChineseName!.StartsWith(
                    query.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ThenByDescending(
                x => x.Popularity
            )
            .FirstOrDefault();
        return match?.Slugs[platform].Replace('-', ' ') ?? query;
    }

    // [实现] DisplayName 的实现：计算并返回 Find(slug, source)?.ChineseName ?? original。
    public static string DisplayName(string slug, string original, string source = "modrinth") => Find(slug, source)?.ChineseName ?? original;
    // [实现] WikiUrl 的实现：计算并返回 Find(slug, source)?.WikiUrl ?? "https://www.mcmod.cn/s?key=" + Uri.EscapeDataString(original) +…。
    public static string WikiUrl(string slug, string original, string source = "modrinth") => Find(slug, source)?.WikiUrl ?? "https://www.mcmod.cn/s?key=" + Uri.EscapeDataString(original) + "&site=all&filter=0";
    // [实现] CanonicalIdentity 的实现：计算并返回 Find(slug, source)is { } match ? "mcmod:" + match.WikiId : source + ":" + slug。
    public static string CanonicalIdentity(string source, string slug) => Find(slug, source)is { } match
        ? "mcmod:" + match.WikiId
        : source + ":" + slug;
}
