using Pcl.Core.Adapters.Search;

namespace Pcl.Core;
public sealed record UnifiedReleaseCandidates(ResourceRelease[] Modrinth, CurseResourceRelease[] Curse, string[] Messages);
/// <summary>按目标实例读取每个来源的独立文件候选；项目关联不意味着文件或依赖可互换。</summary>
public sealed class UnifiedReleaseResolver(ResourceCatalog modrinth, CurseForgeCatalog? curse)
{
    public async Task<UnifiedReleaseCandidates> ResolveAsync(
        ResourceSearchHit hit,
        ResourceKind kind,
        string gameVersion,
        string loader,
        string source,
        CancellationToken ct
    )
    {
        var mr = new List<ResourceRelease>();
        var cf = new List<CurseResourceRelease>();
        var messages = new List<string>();
        var link = hit.Modrinth is { Kind: ResourceKind.Mod } m
            ? BundledProjectIdentity.Index.Find("modrinth", m.Slug)
            : hit.CurseForge is { Kind: ResourceKind.Mod } c
                ? BundledProjectIdentity.Index.Find("curseforge", c.Slug)
                : hit.Mcmod is { } w
                    ? BundledProjectIdentity.Index.FindWiki(w.Id)
                    : null;
        bool Include(string site) => source is "" or "all" || source == site;
        // 来源故障单独展示；用户取消传播到整个查询，不能转成“没有版本”。
        async Task Read(string site, Func<Task> read)
        {
            try
            {
                await read();
            }
            catch (OperationCanceledException)when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)when (ex is HttpRequestException or IOException or TaskCanceledException or Newtonsoft.Json.JsonException)
            {
                messages.Add(site + " 版本查询失败，请重试；未自动切换版本");
            }
        }

        if (Include("modrinth"))
        {
            var project = hit.Modrinth?.Id ?? link?.Modrinth;
            if (project is not null)
            {
                await Read(
                    "Modrinth",
                    async () => mr.AddRange(
                        await modrinth.VersionsAsync(project, kind, gameVersion, loader, ct)
                    )
                );
            }
            else if (hit.Mcmod is not null)
            {
                // 多项目百科页不作为跨站同一身份，但保留原有百科入口的明确项目列表。
                await Read(
                    "Modrinth",
                    async () =>
                    {
                        var found = await McmodCatalog.VersionsAsync(hit.Mcmod, modrinth, gameVersion, loader, ct);
                        mr.AddRange(found.Releases);
                        if (found.UnavailableProjects.Count > 0)
                        {
                            messages.Add("部分百科关联项目不可用");
                        }
                    }
                );
            }
        }

        if (Include("curseforge"))
        {
            if (curse is null)
            {
                messages.Add("CurseForge 未配置密钥或可用镜像");
            }
            else
            {
                await Read(
                    "CurseForge",
                    async () =>
                    {
                        var project = hit.CurseForge;
                        if (project is null && link?.CurseForge is { } slug)
                        {
                            project = await curse.FindBySlugAsync(slug, kind, ct);
                            if (project is null)
                            {
                                messages.Add("CurseForge 关联未能精确确认，未使用模糊搜索替代");
                            }
                        }

                        if (project is not null)
                        {
                            cf.AddRange(
                                await curse.VersionsAsync(project.Id, kind, gameVersion, loader, ct)
                            );
                        }
                    }
                );
            }
        }

        ct.ThrowIfCancellationRequested();
        // 防止不同版本各自满足一个筛选条件而形成虚假的兼容组合。
        return new(
            mr.Where(
                x => x.GameVersions.Contains(gameVersion) && (kind != ResourceKind.Mod || (x.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase) || (loader == "quilt" && x.Loaders.Contains("fabric"))))
            )
                .DistinctBy(
                    x => x.Id
                )
                .OrderByDescending(
                    x => x.Published
                )
                .ToArray(),
            cf.Where(
                x => x.GameVersions.Contains(gameVersion) && (kind != ResourceKind.Mod || x.GameVersions.Contains(loader, StringComparer.OrdinalIgnoreCase))
            )
                .DistinctBy(
                    x => x.FileId
                )
                .OrderByDescending(
                    x => x.Published
                )
                .ToArray(),
            messages.ToArray()
        );
    }
}
