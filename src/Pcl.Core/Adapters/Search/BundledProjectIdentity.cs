using PCLCS;
using Pcl.Domain.Search;

namespace Pcl.Core.Adapters.Search;
/// <summary>将同一进程选定的签名数据包或内置词库投影为纯规则输入；身份规则不变。</summary>
public static class BundledProjectIdentity
{
    public static ProjectIdentityIndex Index { get; } = new(
        Pcl.Core.Adapters.Content.ActiveContentIndex.Entries.Select(
            x => new ProjectIdentityLink(
                x.Id,
                x.Slugs.GetValueOrDefault(ResourcePlatforms.Modrinth),
                x.Slugs.GetValueOrDefault(ResourcePlatforms.CurseForge)
            )
        )
    );

    // 只给可唯一对应的项目附加百科信息。类型也参与判断，资源包不能误接模组词库。
    public static ResourceSearchHit Enrich(ResourceSearchHit hit)
    {
        var link = hit.Modrinth is { Kind: ResourceKind.Mod } mr
            ? Index.Find("modrinth", mr.Slug)
            : hit.CurseForge is { Kind: ResourceKind.Mod } cf
                ? Index.Find("curseforge", cf.Slug)
                : hit.Mcmod is { } wiki
                    ? Index.FindWiki(wiki.Id)
                    : null;
        if (link is null)
        {
            return hit;
        }

        return hit with
        {
            Identity = "mcmod:" + link.WikiId,
            Mcmod = McmodCatalog.Find(link.WikiId)
        };
    }
}
