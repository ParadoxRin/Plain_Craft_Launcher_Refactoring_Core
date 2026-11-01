using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Pcl.Core;
using Pcl.Core.Adapters.Search;
using Pcl.Domain.Search;

internal static class UnifiedBrowserTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        static void Check(bool value, string message)
        {
            if (!value)
            {
                throw new Exception(message);
            }
        }

        await test(
            "unified_identity_unique_links_and_ambiguous_wiki_are_separate",
            () =>
            {
                var index = new ProjectIdentityIndex(
                    [
                        new(1, "one", "other-name"),
                        new(2, "fork-a", "fork-a"),
                        new(2, "fork-b", "fork-b"),
                        new(3, "duplicate", "x"),
                        new(4, "duplicate", "y")
                    ]
                );
                Check(
                    index.Identity("modrinth", "A", "one") == index.Identity("curseforge", "B", "other-name"),
                    "explicit pair lost"
                );
                Check(
                    index.FindWiki(2)is null && index.Find("modrinth", "fork-a")is null,
                    "forks merged by wiki ID"
                );
                Check(
                    index.Find("modrinth", "duplicate")is null,
                    "ambiguous slug accepted"
                );
                Check(
                    index.Identity("modrinth", "same", "same") != index.Identity("curseforge", "same", "same"),
                    "names merged"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "unified_bundled_merge_preserves_both_source_projects",
            () =>
            {
                var row = McmodCatalog.Entries.First(
                    x => BundledProjectIdentity.Index.FindWiki(x.Id)is { Modrinth: not null, CurseForge: not null }
                );
                var pair = BundledProjectIdentity.Index.FindWiki(row.Id)!;
                var mr = new ResourceProject("mr-id", pair.Modrinth!, "Same", "", "", 0, ResourceKind.Mod);
                var cf = new CurseResourceProject(
                    123,
                    "Different display",
                    "",
                    "https://www.curseforge.com/projects/123",
                    0,
                    ResourceKind.Mod
                )
                {
                    Slug = pair.CurseForge!
                };
                var merged = UnifiedResourceSearch.Merge(
                    [
                        new(
                            "modrinth:mr-id",
                            mr.Title,
                            "",
                            [
                                "Modrinth"
                            ],
                            mr
                        ),
                        new(
                            "curseforge:123",
                            cf.Name,
                            "",
                            [
                                "CurseForge"
                            ],
                            CurseForge: cf
                        ),
                        new(
                            "mcmod:" + row.Id,
                            row.Title,
                            "",
                            [
                                McmodCatalog.Source
                            ],
                            Mcmod: row
                        )
                    ]
                );
                Check(
                    merged.Count == 1 && merged[0].Modrinth?.Id == "mr-id" && merged[0].CurseForge?.Id == 123 && merged[0].Sources.Count == 3,
                    "merge lost source identity"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "unified_alias_expansion_preserves_original_and_bounds_queries",
            () =>
            {
                var terms = ResourceAliases.SearchTerms("工业时代2", "modrinth");
                Check(
                    terms[0] == "工业时代2" && terms.Count <= 3 && terms.Any(
                        x => x.Contains("industrial")
                    ),
                    "alias replaced original"
                );
                Check(
                    ResourceAliases.SearchTerms("工业", "modrinth")
                        .SequenceEqual(
                            new[]
                            {
                                "工业"
                            }
                        ),
                    "fuzzy popular alias selected"
                );
                return Task.CompletedTask;
            }
        );
        ResourceSearchHit Hit() => new(
            "fixture",
            "Fixture",
            "",
            [
                "Modrinth",
                "CurseForge"
            ],
            new("mr-id", "fixture", "Fixture", "", "", 0, ResourceKind.Mod),
            new(123, "Fixture", "", "https://www.curseforge.com/projects/123", 0, ResourceKind.Mod)
        );
        await test(
            "unified_versions_both_sources_same_file_compatibility_and_source_filter",
            async () =>
            {
                using var mr = new ResourceCatalog(
                    new Fixture(false)
                );
                using var cf = new CurseForgeCatalog(
                    "synthetic-key",
                    new Fixture(true)
                );
                var resolver = new UnifiedReleaseResolver(mr, cf);
                var all = await resolver.ResolveAsync(
                    Hit(),
                    ResourceKind.Mod,
                    "1.21.1",
                    "fabric",
                    "all",
                    default
                );
                Check(all.Modrinth.Length == 1 && all.Curse.Length == 1, "failed conjunction or lost source");
                Check(
                    all.Modrinth[0].Id == "good" && all.Curse[0].FileId == 100,
                    "different files satisfied different filters"
                );
                var snapshot = new Pcl.Core.Adapters.Pages.ReleaseSelection("fixture", ResourceKind.Mod, all.Modrinth, all.Curse);
                Check(
                    snapshot.At(0)
                        .Modrinth?.Id == "good" && snapshot.At(0)
                        .Curse is null,
                    "MR index collided"
                );
                Check(
                    snapshot.At(1)
                        .Curse?.FileId == 100 && snapshot.At(1)
                        .Modrinth is null,
                    "CF index selected MR file"
                );
                try
                {
                    snapshot.At(2);
                    throw new Exception("out of range accepted");
                }
                catch (Pcl.Core.Ports.Operations.OperationInstallException)
                {
                }

                var only = await resolver.ResolveAsync(
                    Hit(),
                    ResourceKind.Mod,
                    "1.21.1",
                    "fabric",
                    "curseforge",
                    default
                );
                Check(only.Modrinth.Length == 0 && only.Curse.Length == 1, "source choice ignored");
                var quilt = await resolver.ResolveAsync(
                    Hit(),
                    ResourceKind.Mod,
                    "1.21.1",
                    "quilt",
                    "modrinth",
                    default
                );
                Check(quilt.Modrinth.Length == 1, "existing Fabric-on-Quilt compatibility removed");
            }
        );
        await test(
            "unified_versions_partial_failure_and_missing_key_visible",
            async () =>
            {
                using var mr = new ResourceCatalog(
                    new Fixture(false)
                );
                using var cf = new CurseForgeCatalog(
                    "synthetic-key",
                    new Fixture(true, fail: true)
                );
                var partial = await new UnifiedReleaseResolver(mr, cf).ResolveAsync(
                    Hit(),
                    ResourceKind.Mod,
                    "1.21.1",
                    "fabric",
                    "all",
                    default
                );
                Check(
                    partial.Modrinth.Length == 1 && partial.Messages.Length == 1 && !partial.Messages[0].Contains("synthetic"),
                    "partial failure hidden or leaked"
                );
                var unavailable = await new UnifiedReleaseResolver(mr, null).ResolveAsync(
                    Hit(),
                    ResourceKind.Mod,
                    "1.21.1",
                    "fabric",
                    "all",
                    default
                );
                Check(
                    unavailable.Messages.Any(
                        x => x.Contains("未配置")
                    ),
                    "missing source claimed empty success"
                );
            }
        );
        await test(
            "unified_versions_cancellation_propagates",
            async () =>
            {
                using var mr = new ResourceCatalog(
                    new Fixture(false)
                );
                using var ct = new CancellationTokenSource();
                ct.Cancel();
                try
                {
                    await new UnifiedReleaseResolver(mr, null).ResolveAsync(
                        Hit(),
                        ResourceKind.Mod,
                        "1.21.1",
                        "fabric",
                        "all",
                        ct.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                throw new Exception("cancellation became empty versions");
            }
        );
    }

    // 用真实提供方解析器读取受控 HTTP 元数据，故意返回交叉不匹配文件以验证本地再校验。
    private sealed class Fixture(bool curse, bool fail = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (fail)
            {
                throw new HttpRequestException("synthetic-key-must-not-leak");
            }

            JToken data;
            var path = request.RequestUri!.AbsolutePath;
            if (!curse)
            {
                data = path.EndsWith("/version")
                    ? new JArray(
                        Mr("good", "1.21.1", "fabric"),
                        Mr("wrong-loader", "1.21.1", "forge"),
                        Mr("wrong-game", "1.20.1", "fabric")
                    )
                    : new JObject
                    {
                        ["id"] = "mr-id",
                        ["slug"] = "fixture",
                        ["title"] = "Fixture",
                        ["project_type"] = "mod"
                    };
            }
            else
            {
                data = path.EndsWith("/files")
                    ? new JArray(
                        Cf(100, "1.21.1", "Fabric"),
                        Cf(101, "1.21.1", "Forge"),
                        Cf(102, "1.20.1", "Fabric")
                    )
                    : new JObject
                    {
                        ["id"] = 123,
                        ["slug"] = "fixture",
                        ["name"] = "Fixture",
                        ["links"] = new JObject
                        {
                            ["websiteUrl"] = "https://www.curseforge.com/projects/123"
                        }
                    };
                data = new JObject
                {
                    ["data"] = data
                };
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        data.ToString(),
                        Encoding.UTF8,
                        "application/json"
                    )
                }
            );
        }

        private static JObject Mr(string id, string game, string loader) => new()
        {
            ["id"] = id,
            ["project_id"] = "mr-id",
            ["name"] = id,
            ["version_number"] = "1",
            ["version_type"] = "release",
            ["date_published"] = "2026-01-01T00:00:00Z",
            ["game_versions"] = new JArray(game),
            ["loaders"] = new JArray(loader),
            ["dependencies"] = new JArray(),
            ["files"] = new JArray(
                new JObject
                {
                    ["filename"] = "fixture.jar",
                    ["url"] = "https://example.invalid/fixture.jar",
                    ["size"] = 1,
                    ["hashes"] = new JObject
                    {
                        ["sha1"] = new string ('a', 40)
                    }
                }
            )
        };
        private static JObject Cf(int id, string game, string loader) => new()
        {
            ["modId"] = 123,
            ["id"] = id,
            ["displayName"] = "CF fixture",
            ["fileName"] = "fixture.jar",
            ["fileLength"] = 1,
            ["fileDate"] = "2026-01-01T00:00:00Z",
            ["releaseType"] = 1,
            ["downloadUrl"] = "https://example.invalid/cf.jar",
            ["gameVersions"] = new JArray(game, loader),
            ["dependencies"] = new JArray(),
            ["hashes"] = new JArray(
                new JObject
                {
                    ["algo"] = 1,
                    ["value"] = new string ('b', 40)
                }
            )
        };
    }
}
