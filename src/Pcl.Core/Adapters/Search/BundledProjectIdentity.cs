using PCLCS;
using Pcl.Domain.Search;

namespace Pcl.Core.Adapters.Search;
/// <summary>把 PCLCS 原始词库投影为纯规则输入；不修改 vendor 数据，也不在词库中加入网络或 UI。</summary>
public static class BundledProjectIdentity
{
    public static ProjectIdentityIndex Index { get; } = new(
        WikiEntry.All.Value.Select(
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
