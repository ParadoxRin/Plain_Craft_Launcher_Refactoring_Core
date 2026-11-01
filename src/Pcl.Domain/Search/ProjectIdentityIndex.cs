namespace Pcl.Domain.Search;
/// <summary>明确的跨站记录；百科的一页可能含多个移植项目，不能仅按百科 ID 合并。</summary>
public sealed record ProjectIdentityLink(int WikiId, string? Modrinth, string? CurseForge);
/// <summary>纯数据索引：仅接受一对一映射，歧义记录保留来源身份，不按名称推断。</summary>
public sealed class ProjectIdentityIndex
{
    private readonly Dictionary<(string Source, string Slug), ProjectIdentityLink> links = new();
    private readonly Dictionary<int, ProjectIdentityLink> wiki = new();
    public ProjectIdentityIndex(IEnumerable<ProjectIdentityLink> input)
    {
        var rows = input.Distinct()
            .ToArray();
        var mrCounts = rows.Where(
            x => !string.IsNullOrEmpty(x.Modrinth)
        )
            .GroupBy(
                x => x.Modrinth!
            )
            .ToDictionary(
                g => g.Key,
                g => g.Count()
            );
        var cfCounts = rows.Where(
            x => !string.IsNullOrEmpty(x.CurseForge)
        )
            .GroupBy(
                x => x.CurseForge!
            )
            .ToDictionary(
                g => g.Key,
                g => g.Count()
            );
        var candidates = rows.GroupBy(
            x => x.WikiId
        )
            .Where(
                g => g.Count() == 1
            )
            .Select(
                g => g.Single()
            )
            .ToArray();
        foreach (var row in candidates)
        {
            bool Unique(string source, string? slug)
            {
                return string.IsNullOrEmpty(slug) || (source == "modrinth"
                    ? mrCounts
                    : cfCounts)[slug] == 1;
            }

            if (!Unique("modrinth", row.Modrinth) || !Unique("curseforge", row.CurseForge))
            {
                continue;
            }

            wiki[row.WikiId] = row;
            if (!string.IsNullOrEmpty(row.Modrinth))
            {
                links[("modrinth", row.Modrinth)] = row;
            }

            if (!string.IsNullOrEmpty(row.CurseForge))
            {
                links[("curseforge", row.CurseForge)] = row;
            }
        }
    }

    public ProjectIdentityLink? Find(string source, string slug) => links.GetValueOrDefault((source, slug));
    public ProjectIdentityLink? FindWiki(int id) => wiki.GetValueOrDefault(id);
    public string Identity(string source, string id, string slug) => Find(source, slug)is { } match
        ? "mcmod:" + match.WikiId
        : source + ":" + id;
}
