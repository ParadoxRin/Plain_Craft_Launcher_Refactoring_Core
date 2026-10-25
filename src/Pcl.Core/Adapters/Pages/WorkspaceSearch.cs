using System.Text.Json;
using Newtonsoft.Json.Linq;
using SearchHit = Pcl.Core.Contracts.Workspaces.SearchHit;
using SearchPage = Pcl.Core.Contracts.Workspaces.SearchPage;
using SearchRelease = Pcl.Core.Contracts.Workspaces.SearchRelease;
using SearchVersions = Pcl.Core.Contracts.Workspaces.SearchVersions;
using SearchInstalled = Pcl.Core.Contracts.Workspaces.SearchInstalled;
using Pcl.Core.Ports.Pages;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Pages;
// 搜索会话只缓存有界分页。Host 重启后明确要求重搜；已返回选择则保存在私有记录中。
public sealed record SearchSelection(string Instance, ResourceKind Kind, ResourceSearchHit[] Hits);
public sealed record ReleaseSelection(
    string Instance,
    ResourceKind Kind,
    ResourceRelease[] Modrinth,
    CurseResourceRelease[] Curse
);
public sealed partial class PageExecutionAdapter
{
    private sealed record SearchState(string Instance, ResourceKind Kind, UnifiedSearchSession Session, DateTimeOffset Created);
    private readonly Dictionary<string, SearchState> searchSessions = new();
    private async Task<object> ExecuteSearchAsync(
        string id,
        PageInput request,
        JObject args,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    )
    {
        var profile = Profile(request.InstanceId);
        string Text(string name) => (string? )args[name] ?? "";
        var secretHandle = (string? )args["secretHandle"];
        if (request.Method == "workspace.search.page")
        {
            var sessionId = Text("session");
            foreach (var expired in searchSessions.Where(
                x => x.Value.Created < DateTimeOffset.UtcNow.AddMinutes(-30)
            )
                .Select(
                    x => x.Key
                )
                .ToArray())
            {
                searchSessions.Remove(expired);
            }

            SearchState state;
            if (sessionId.Length == 0)
            {
                if (searchSessions.Count >= 16)
                {
                    searchSessions.Remove(
                        searchSessions.MinBy(
                            x => x.Value.Created
                        )
                            .Key
                    );
                }

                var kind = (ResourceKind)((int? )args["kind"] ?? 0);
                if (kind is not (ResourceKind.Mod or ResourceKind.Shader or ResourceKind.ResourcePack or ResourceKind.Modpack))
                {
                    throw new OperationInstallException("SEARCH_KIND_INVALID");
                }

                var online = (bool? )args["online"] ?? true;
                var provider = Text("provider");
                var query = new ResourceSearchQuery(
                    kind,
                    Text("query"),
                    profile.GameVersion,
                    profile.Loader
                );
                // 只捕获密钥句柄；调用时再读取内存密钥，过期不能从 receipt 恢复明文。
                var session = new UnifiedSearchSession(
                    query,
                    async (q, token) =>
                    {
                        if (provider != "curseforge")
                        {
                            return await UnifiedResourceSearch.SearchAsync(
                                source,
                                profile,
                                q,
                                online,
                                Secret(secretHandle),
                                token,
                                provider == "mcmod"
                            );
                        }

                        if (string.IsNullOrWhiteSpace(
                            Secret(secretHandle)
                        ))
                        {
                            throw new OperationInstallException("CURSEFORGE_KEY_REQUIRED");
                        }

                        using var catalog = new CurseForgeCatalog(
                            Secret(secretHandle) ?? "",
                            paths: source.Paths
                        );
                        var rows = await catalog.SearchAsync(q.Kind, q.Text, q.GameVersion, q.Loader, q.Page * 20, token);
                        var hits = rows.Select(
                            x => new ResourceSearchHit(
                                "curse:" + x.Id,
                                x.Name,
                                x.Summary,
                                [
                                    "CurseForge"
                                ],
                                CurseForge: x
                            )
                        )
                            .ToArray();
                        return new UnifiedSearchResult(
                            hits,
                            [
                                new ResourceSourcePage("CurseForge", hits, rows.Count == 20)
                            ]
                        );
                    }
                );
                state = new(profile.Id, kind, session, DateTimeOffset.UtcNow);
                sessionId = id;
                searchSessions.Add(sessionId, state);
            }
            else if (!searchSessions.TryGetValue(sessionId, out state!) || state.Instance != profile.Id)
            {
                throw new OperationInstallException("SEARCH_SESSION_EXPIRED");
            }

            var page = (int? )args["page"] ?? 0;
            // 单窗口不允许无限积累搜索缓存；更深页通过新查询继续。
            if (page > 99)
            {
                throw new OperationInstallException("SEARCH_PAGE_LIMIT");
            }

            var result = await state.Session.GoToAsync(page, (bool? )args["retry"] ?? false, ct);
            Save(
                id,
                "search-hits",
                new SearchSelection(
                    profile.Id,
                    state.Kind,
                    result.Hits.ToArray()
                )
            );
            return new SearchPage(
                sessionId,
                state.Session.Page,
                result.HasNext,
                state.Session.FilteredDuplicates,
                result.Hits.Select(
                    (h, i) => new SearchHit(
                        id + ":" + i,
                        h.Title,
                        h.Description,
                        h.Sources.ToArray(),
                        h.LocalDirectory,
                        h.Modrinth?.WebUrl ?? h.CurseForge?.WebUrl ?? h.Mcmod?.WebUrl,
                        h.Mcmod?.WebUrl,
                        h.Mcmod?.DownloadPage
                    )
                )
                    .ToArray(),
                result.Sources.Select(
                    x => x.Source + ": " + (x.Error ?? x.Hits.Count + " 项")
                )
                    .ToArray(),
                result.Sources.Any(
                    x => x.Error is not null
                )
            );
        }

        if (request.Method == "workspace.search.import")
        {
            if (profile.Loader == "vanilla" || !string.Equals(
                Path.GetExtension(
                    Text("path")
                ),
                ".jar",
                StringComparison.OrdinalIgnoreCase
            ))
            {
                throw new InvalidDataException("请选择加载器实例及 JAR 模组文件。");
            }

            return await new ResourceInstaller(profile.GameDirectory, source.Downloads, profile.Loader, profile.GameVersion).ImportAsync(
                Text("path"),
                ResourceKind.Mod,
                ct
            );
        }

        var(selectionId, index) = Selection(
            Text("selection")
        );
        if (request.Method == "workspace.search.versions")
        {
            var selected = Read<SearchSelection>(selectionId, "search-hits");
            if (selected.Instance != profile.Id || index < 0 || index >= selected.Hits.Length)
            {
                throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
            }

            var hit = selected.Hits[index];
            ResourceRelease[] mr = [];
            CurseResourceRelease[] cf = [];
            var message = "";
            using var catalog = new ResourceCatalog(paths: source.Paths);
            if (hit.Mcmod is not null)
            {
                var found = await McmodCatalog.VersionsAsync(hit.Mcmod, catalog, profile.GameVersion, profile.Loader, ct);
                mr = found.Releases.ToArray();
                message = "百科索引对应的 Modrinth 版本；站内文件请下载后导入。" + (found.UnavailableProjects.Count > 0
                    ? " 部分索引项目不可用。"
                    : "");
            }
            else if (hit.Modrinth is not null)
            {
                mr = (await catalog.VersionsAsync(hit.Modrinth.Id, selected.Kind, profile.GameVersion, profile.Loader, ct)).ToArray();
            }
            else if (hit.CurseForge is not null)
            {
                using var curse = new CurseForgeCatalog(
                    Secret(secretHandle) ?? "",
                    paths: source.Paths
                );
                cf = (await curse.VersionsAsync(hit.CurseForge.Id, selected.Kind, profile.GameVersion, profile.Loader, ct)).ToArray();
            }

            // 有界结果：版本列表过大应显式拒绝，不能静默丢弃可选择的版本。
            Save(
                id,
                "search-releases",
                new ReleaseSelection(profile.Id, selected.Kind, mr, cf)
            );
            return new SearchVersions(
                mr.Select(
                    (x, i) => new SearchRelease(id + ":" + i, x.Name, "", "https://modrinth.com/project/" + x.ProjectId, false)
                )
                    .Concat(
                        cf.Select(
                            (x, i) => new SearchRelease(id + ":" + i, x.Name, x.FileName, x.Page, x.DownloadUrl is null)
                        )
                    )
                    .ToArray(),
                message
            );
        }

        var releases = Read<ReleaseSelection>(selectionId, "search-releases");
        if (releases.Instance != profile.Id || index < 0 || index >= Math.Max(releases.Modrinth.Length, releases.Curse.Length))
        {
            throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
        }

        var curseFile = releases.Curse.ElementAtOrDefault(index);
        var modrinth = releases.Modrinth.ElementAtOrDefault(index);
        string Cache(CurseResourceRelease f) => ResourceInstaller.SafeTarget(profile.GameDirectory, ".pcl-downloads/" + f.Sha1);
        bool Verified(CurseResourceRelease f) => DownloadService.IsValid(
            new(
                new Uri(f.Page),
                Cache(f),
                f.Size,
                f.Sha1
            )
        );
        if (request.Method == "workspace.search.match")
        {
            if (curseFile is null)
            {
                throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
            }

            var local = Text("path");
            if (!DownloadService.IsValid(
                new(
                    new Uri(curseFile.Page),
                    local,
                    curseFile.Size,
                    curseFile.Sha1
                )
            ))
            {
                throw new InvalidDataException("文件大小或 SHA-1 与官方记录不符。");
            }

            var target = Cache(curseFile);
            Directory.CreateDirectory(
                Path.GetDirectoryName(target)!
            );
            if (Path.GetFullPath(local) != Path.GetFullPath(target))
            {
                File.Copy(local, target, true);
            }

            if (!Verified(curseFile))
            {
                throw new InvalidDataException("复制后校验失败。");
            }

            return true;
        }

        if (request.Method != "workspace.search.install")
        {
            throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
        }

        if (curseFile is not null && curseFile.DownloadUrl is null && !Verified(curseFile))
        {
            throw new InvalidOperationException("作者未允许自动下载，请打开文件下载页并匹配手动文件。");
        }

        if (releases.Kind == ResourceKind.Modpack)
        {
            string path;
            if (modrinth is not null)
            {
                path = await new ModpackService(source).DownloadReleaseAsync(modrinth, progress, ct);
            }
            else
            {
                path = Cache(curseFile!);
                await source.Downloads.DownloadAsync(
                    new(
                        curseFile!.DownloadUrl ?? new Uri(curseFile.Page),
                        path,
                        curseFile.Size,
                        curseFile.Sha1
                    ),
                    ct
                );
            }

            var plan = PortableModpackService.Inspect(
                path,
                new(IncludeOptional: true)
            );
            var digest = HashFile(path);
            Save(
                id,
                "workspace-pack",
                new WorkspacePackSource(path, digest)
            );
            return new
            {
                Files = 0,
                Warnings = Array.Empty<string>(),
                Pack = new
                {
                    Handle = id,
                    Plan = plan,
                    Digest = digest
                }
            };
        }

        if (releases.Kind == ResourceKind.Mod && profile.Loader == "vanilla")
        {
            throw new InvalidOperationException("请先选择加载器实例。");
        }

        ResourceInstallPlan install;
        if (modrinth is not null)
        {
            using var catalog = new ResourceCatalog(paths: source.Paths);
            install = await ResourceDependencyPlanner.ResolveAsync(catalog, source.Downloads, modrinth, releases.Kind, profile, ct: ct);
        }
        else
        {
            using var catalog = new CurseForgeCatalog(
                Secret(secretHandle) ?? "",
                paths: source.Paths
            );
            install = await catalog.ResolveAsync(
                curseFile!,
                releases.Kind,
                profile.GameVersion,
                profile.Loader,
                records.Values.Where(
                    x => x.Kind == "search-releases"
                )
                    .Select(
                        x => x.Payload.Deserialize<ReleaseSelection>()!
                    )
                    .Where(
                        x => x.Instance == profile.Id
                    )
                    .SelectMany(
                        x => x.Curse
                    )
                    .Where(Verified)
                    .Select(
                        x => x.FileId
                    )
                    .ToHashSet(),
                ct
            );
        }

        await new ResourceInstaller(profile.GameDirectory, source.Downloads, profile.Loader, profile.GameVersion).InstallAsync(install, progress, ct);
        return new SearchInstalled(
            install.Releases.Count,
            releases.Kind == ResourceKind.Mod
                ? new LocalResourceService(profile.GameDirectory, profile.Loader, profile.GameVersion).ValidateEnabledResources()
                    .ToArray()
                : [],
            null
        );
    }
}
