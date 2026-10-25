using Pcl.Core;
using Newtonsoft.Json.Linq;

internal static class McmodTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        void Check(bool ok, string message)
        {
            if (!ok)
                throw new Exception(message);
        }

        var lithium = McmodCatalog.Entries.Single(
            x => x.ModrinthSlugs.Contains("lithium")
        );
        await test(
            "mcmod_index_chinese_slug_id_url_and_disjoint_pages",
            () =>
            {
                Check(McmodCatalog.Entries.Count > 10000, "Index not loaded");
                Check(
                    McmodCatalog.Search("锂")
                        .Projects.Any(
                        x => x.Id == lithium.Id
                    ),
                    "Chinese lookup failed"
                );
                foreach (var q in new[]
                {
                    "lithium",
                    lithium.Id.ToString(),
                    lithium.WebUrl,
                    lithium.DownloadPage
                }

                )
                    Check(
                        McmodCatalog.Search(q)
                            .Projects.Any(
                            x => x.Id == lithium.Id
                        ),
                        "Identity lookup failed: " + q
                    );
                Check(
                    !McmodCatalog.Search("https://www.mcmod.cn.evil/class/" + lithium.Id + ".html")
                        .Projects.Any(),
                    "Foreign URL treated as ID"
                );
                var a = McmodCatalog.Search("");
                var b = McmodCatalog.Search("", 1);
                Check(
                    a.HasNext && a.Total == McmodCatalog.Entries.Count && !a.Projects.Select(
                        x => x.Id
                    )
                        .Intersect(
                            b.Projects.Select(
                                x => x.Id
                            )
                        )
                        .Any(),
                    "Page overlap"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "mcmod_offline_search_never_requires_network_or_claims_compatibility",
            async () =>
            {
                using var service = new MinecraftService(
                    new AppPaths(
                        Path.Combine(root, "mcmod-offline")
                    )
                );
                var profile = new GameProfile(
                    "test",
                    "test",
                    "1.21.1",
                    "fabric",
                    Path.Combine(root, "mcmod-game"),
                    "unused"
                );
                var r = await UnifiedResourceSearch.SearchAsync(
                    service,
                    profile,
                    new(ResourceKind.Mod, "锂", "1.21.1", "fabric"),
                    online: false,
                    mcmodOnly: true
                );
                Check(
                    r.Hits.Any(
                        x => x.Mcmod?.Id == lithium.Id
                    ) && r.Sources.Single()
                        .Source == McmodCatalog.Source,
                    "Offline index missing"
                );
                Check(
                    r.Hits.All(
                        x => x.Modrinth is null && x.Identity.StartsWith("mcmod:")
                    ),
                    "Index claims remote project resolution"
                );
                var shader = await UnifiedResourceSearch.SearchAsync(
                    service,
                    profile,
                    new(ResourceKind.Shader, "", "1.21.1", "fabric"),
                    online: false,
                    mcmodOnly: true
                );
                Check(shader.Hits.Count == 0, "Mod index leaked into shader type");
            }
        );
        JObject Project(string slug, string type = "mod") => new()
        {
            ["id"] = "verified",
            ["slug"] = slug,
            ["title"] = "Name",
            ["project_type"] = type
        };
        JObject Version(string id, string game = "1.21.1", string loader = "fabric", string project = "verified") => new()
        {
            ["id"] = id,
            ["project_id"] = project,
            ["name"] = id,
            ["version_number"] = "1.0",
            ["version_type"] = "release",
            ["date_published"] = "2026-01-01T00:00:00Z",
            ["game_versions"] = new JArray(game),
            ["loaders"] = new JArray(loader),
            ["files"] = new JArray(
                new JObject
                {
                    ["primary"] = true,
                    ["filename"] = "sample.jar",
                    ["url"] = "https://cdn.modrinth.com/data/file.jar",
                    ["size"] = 5,
                    ["hashes"] = new JObject
                    {
                        ["sha1"] = new string ('a', 40)
                    }
                }
            ),
            ["dependencies"] = new JArray()
        };
        await test(
            "mcmod_resolves_exact_slug_and_rechecks_release_kind_loader_version_identity",
            async () =>
            {
                var paths = new List<string>();
                using var catalog = new ResourceCatalog(
                    new JsonHandler(
                        uri =>
                        {
                            paths.Add(
                                uri.RequestUri!.ToString()
                            );
                            return uri.RequestUri!.AbsolutePath.EndsWith("/version")
                                ? new JArray(
                                    Version("good"),
                                    Version("wrong-game", "1.20.1"),
                                    Version("wrong-loader", loader: "forge"),
                                    Version("wrong-project", project: "other")
                                )
                                : Project("lithium");
                        }
                    )
                );
                var r = await McmodCatalog.VersionsAsync(lithium, catalog, "1.21.1", "fabric");
                Check(
                    r.Releases.Count == 1 && r.Releases[0].Id == "good",
                    "Incompatible release admitted"
                );
                Check(
                    paths.All(
                        x => !x.Contains("search?")
                    ),
                    "Fuzzy search used for identity"
                );
                using var wrong = new ResourceCatalog(
                    new JsonHandler(
                        _ => Project("other")
                    )
                );
                Check(
                    (await McmodCatalog.VersionsAsync(lithium, wrong, "1.21.1", "fabric")).Releases.Count == 0,
                    "Stale slug accepted"
                );
                using var type = new ResourceCatalog(
                    new JsonHandler(
                        _ => Project("lithium", "modpack")
                    )
                );
                Check(
                    (await McmodCatalog.VersionsAsync(lithium, type, "1.21.1", "fabric")).Releases.Count == 0,
                    "Wrong kind accepted"
                );
            }
        );
        await test(
            "mcmod_mapping_partial_failure_and_cancellation",
            async () =>
            {
                var entry = new McmodProject(
                    1,
                    "Multiple",
                    [
                        "missing",
                        "working"
                    ],
                    [],
                    0
                );
                using var c = new ResourceCatalog(
                    new JsonHandler(
                        uri => uri.RequestUri!.AbsolutePath.EndsWith("/missing")
                            ? throw new HttpRequestException("private diagnostic")
                            : uri.RequestUri!.AbsolutePath.EndsWith("/version")
                                ? new JArray(
                                    Version("ok")
                                )
                                : Project("working")
                    )
                );
                var r = await McmodCatalog.VersionsAsync(entry, c, "1.21.1", "fabric");
                Check(
                    r.Releases.Count == 1 && r.UnavailableProjects.SequenceEqual(
                        new[]
                        {
                            "missing"
                        }
                    ),
                    "Partial source failure lost valid mapping"
                );
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    await McmodCatalog.VersionsAsync(entry, c, "1.21.1", "fabric", cancelled.Token);
                    throw new Exception("Cancellation ignored");
                }
                catch (OperationCanceledException)
                {
                }

                try
                {
                    McmodCatalog.Search("", ct: cancelled.Token);
                    throw new Exception("Search cancellation ignored");
                }
                catch (OperationCanceledException)
                {
                }
            }
        );
        await test(
            "mcmod_cf_only_entries_do_not_guess_modrinth_identity",
            async () =>
            {
                var cf = McmodCatalog.Entries.First(
                    x => x.ModrinthSlugs.Count == 0 && x.CurseForgeSlugs.Count > 0
                );
                using var c = new ResourceCatalog(
                    new JsonHandler(
                        _ => throw new Exception("Unexpected network request")
                    )
                );
                var r = await McmodCatalog.VersionsAsync(cf, c, "1.21.1", "fabric");
                Check(
                    r.Releases.Count == 0 && r.UnavailableProjects.Count == 0,
                    "Unverified cross-source identity"
                );
            }
        );
    }
}
