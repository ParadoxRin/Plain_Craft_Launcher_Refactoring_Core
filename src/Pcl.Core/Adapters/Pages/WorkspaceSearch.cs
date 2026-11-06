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
)
{
    // 扁平索引唯一指向一个来源文件，两个数组不能从0开始复用下载句柄。
    public (ResourceRelease? Modrinth, CurseResourceRelease? Curse) At(int index)
    {
        if (index < 0 || index >= Modrinth.Length + Curse.Length)
        {
            throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
        }

        return index < Modrinth.Length
            ? (Modrinth[index], null)
            : (null, Curse[index - Modrinth.Length]);
    }
}

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
                if (provider is not ("" or "all" or "modrinth" or "curseforge" or "mcmod" or "local"))
                {
                    throw new OperationInstallException("SEARCH_PROVIDER_INVALID");
                }

                var gameVersion = (string? )args["gameVersion"] ?? profile.GameVersion;
                var loader = (string? )args["loader"] ?? (profile.Loader == "vanilla"
                    ? ""
                    : profile.Loader);
                if (gameVersion.Length > 80 || loader is not ("" or "fabric" or "forge" or "neoforge" or "quilt" or "liteloader" or "vanilla"))
                {
                    throw new OperationInstallException("SEARCH_FILTER_INVALID");
                }

                var query = new ResourceSearchQuery(
                    kind,
                    Text("query"),
                    gameVersion,
                    loader
                )
                {
                    Provider = provider.Length == 0
                        ? "all"
                        : provider
                };
                // 会话固定筛选快照；仅捕获临时密钥句柄，不把密钥写入持久记录。
                var session = new UnifiedSearchSession(
                    query,
                    (q, token) => UnifiedResourceSearch.SearchAsync(
                        source,
                        profile,
                        q,
                        online,
                        null,
                        token,
                        provider == "mcmod",
                        () => Secret(secretHandle)
                    )
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
                result.Sources.Any(x => x.Error is not null)
            )
            {
                Availability = result.Sources.Select(x => new Pcl.Core.Contracts.Workspaces.SearchSourceAvailability(
                    x.Source, x.Error is null, x.ErrorCode ?? (x.Error is null ? "AVAILABLE" : "SOURCE_UNAVAILABLE"),
                    x.Error ?? "来源可用", x.Retryable || (x.Error is not null && x.ErrorCode is null))).ToArray()
            };
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
            // 浏览选择可以用于另一目标实例；本次版本快照仍绑定目标实例，安装时必须一致。
            if (index < 0 || index >= selected.Hits.Length)
            {
                throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
            }

            var hit = selected.Hits[index];
            // 下载候选始终按目标实例重新过滤，不能复用“全部版本”的浏览条件进行安装。
            var selectedSource = Text("source");
            if (selectedSource is not ("" or "all" or "modrinth" or "curseforge"))
            {
                throw new OperationInstallException("SEARCH_PROVIDER_INVALID");
            }

            using var catalog = new ResourceCatalog(paths: source.Paths);
            using var curse = new CurseForgeCatalog("", paths: source.Paths, keyProvider: () => Secret(secretHandle));
            var candidates = await new UnifiedReleaseResolver(catalog, curse).ResolveAsync(hit, selected.Kind, profile.GameVersion, profile.Loader, selectedSource, ct);
            var mr = candidates.Modrinth;
            var cf = candidates.Curse;
            var message = string.Join("；", candidates.Messages);
            // 有界结果：版本列表过大应显式拒绝，不能静默丢弃可选择的版本。
            Save(
                id,
                "search-releases",
                new ReleaseSelection(profile.Id, selected.Kind, mr, cf)
            );
            return new SearchVersions(
                mr.Select(
                    (x, i) => new SearchRelease(
                        id + ":" + i,
                        x.Name,
                        x.File.Name,
                        "https://modrinth.com/project/" + x.ProjectId,
                        false
                    )
                    {
                        Source = "Modrinth",
                        GameVersions = x.GameVersions.ToArray(),
                        Loaders = x.Loaders.ToArray(),
                        Channel = x.Channel
                    }
                )
                    .Concat(
                        cf.Select(
                            (x, i) => new SearchRelease(id + ":" + (mr.Length + i), x.Name, x.FileName, x.Page, x.DownloadUrl is null)
                            {
                                Source = "CurseForge",
                                GameVersions = x.GameVersions.ToArray(),
                                Loaders = [
                                    profile.Loader
                                ],
                                Channel = x.Channel
                            }
                        )
                    )
                    .ToArray(),
                message
            );
        }

        var releases = Read<ReleaseSelection>(selectionId, "search-releases");
        if (releases.Instance != profile.Id || index < 0 || index >= releases.Modrinth.Length + releases.Curse.Length)
        {
            throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
        }

        var(modrinth, curseFile) = releases.At(index);
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
