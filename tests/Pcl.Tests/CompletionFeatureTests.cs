using Pcl.Core;
using Newtonsoft.Json.Linq;
using System.IO.Compression;

internal static class CompletionFeatureTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        void Check(bool ok, string why)
        {
            if (!ok)
                throw new Exception(why);
        }

        void Reject(Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)when (e is InvalidDataException or NotSupportedException)
            {
                return;
            }

            throw new Exception("Invalid input accepted");
        }

        string Zip(string name, params (string path, string text)[] files)
        {
            var p = Path.Combine(root, name);
            using var z = ZipFile.Open(p, ZipArchiveMode.Create);
            foreach (var f in files)
            {
                using var w = new StreamWriter(
                    z.CreateEntry(f.path)
                        .Open()
                );
                w.Write(f.text);
            }

            return p;
        }

        await test(
            "maven_matches_9721_official_java_comparisons",
            () =>
            {
                int n = 0;
                foreach (var line in File.ReadLines(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures/maven-comparison.tsv")
                ))
                {
                    var p = line.Split('\t');
                    Check(
                        MavenVersionRange.Compare(p[0], p[1]) == int.Parse(p[2]),
                        "Maven mismatch: " + line
                    );
                    n++;
                }

                Check(n == 9721, "Oracle corpus missing");
                return Task.CompletedTask;
            }
        );
        await test(
            "maven_union_recommendation_and_invalid_bounds",
            () =>
            {
                Check(
                    MavenVersionRange.Matches("7.2", "1.0"),
                    "Bare recommendation treated as exact"
                );
                Check(
                    !MavenVersionRange.Matches("7.2", "[1.0]"),
                    "Exact bracket ignored"
                );
                Check(
                    MavenVersionRange.Matches("1.0-1", "[1.0,1.0.1)") && !MavenVersionRange.Matches("1.0.1", "[1.0,1.0.1)"),
                    "Hyphen nesting ignored"
                );
                Check(
                    MavenVersionRange.Matches("3", "(,1],[2,3]"),
                    "Union failed"
                );
                foreach (var r in new[]
                {
                    "[2,1]",
                    "(1,1]",
                    "[]",
                    "[1,3],[2,4]",
                    "[1,2]junk",
                    "[1,2],,[3,4]"
                }

                )
                    Reject(
                        () => MavenVersionRange.Matches("1", r)
                    );
                return Task.CompletedTask;
            }
        );
        await test(
            "unified_search_identity_merge_not_title_and_partial_failure",
            async () =>
            {
                var providers = new Dictionary<string, Func<ResourceSearchQuery, CancellationToken, Task<ResourceSourcePage>>>
                {
                    ["Modrinth"] = (q, ct) => Task.FromResult(
                        new ResourceSourcePage(
                            "Modrinth",
                            [
                                new(
                                    "modrinth:a",
                                    "Same",
                                    "",
                                    [
                                        "Modrinth"
                                    ]
                                )
                            ],
                            true
                        )
                    ),
                    ["本地"] = (q, ct) => Task.FromResult(
                        new ResourceSourcePage(
                            "本地",
                            [
                                new(
                                    "modrinth:a",
                                    "Same",
                                    "",
                                    [
                                        "本地"
                                    ],
                                    LocalDirectory: root
                                ),
                                new(
                                    "local:b",
                                    "Same",
                                    "",
                                    [
                                        "本地"
                                    ]
                                )
                            ],
                            false
                        )
                    ),
                    ["CurseForge"] = (q, ct) => throw new HttpRequestException("simulated key should not leak")
                };
                var r = await UnifiedResourceSearch.QueryAsync(
                    new(ResourceKind.Mod, "Same", "1.21.1", "fabric"),
                    providers
                );
                Check(
                    r.Hits.Count == 2 && r.Hits[0].Sources.Count == 2 && r.HasNext,
                    "Merge lost namespace or continuation"
                );
                Check(
                    r.Sources.Single(
                        x => x.Source == "CurseForge"
                    )
                        .Error == "来源暂不可用，请重试",
                    "Source error exposed details"
                );
            }
        );
        await test(
            "unified_search_user_cancellation_propagates",
            async () =>
            {
                using var c = new CancellationTokenSource();
                c.Cancel();
                try
                {
                    await UnifiedResourceSearch.QueryAsync(
                        new(ResourceKind.Mod, "", "1.21.1", "fabric"),
                        new Dictionary<string, Func<ResourceSearchQuery, CancellationToken, Task<ResourceSourcePage>>>
                        {

                            {
                                "source",
                                async (q, ct) =>
                                {
                                    await Task.Delay(1, ct);
                                    return new(
                                        "source",
                                        [],
                                        false
                                    );
                                }
                            }
                        },
                        c.Token
                    );
                    throw new Exception("Cancellation swallowed");
                }
                catch (OperationCanceledException)
                {
                }
            }
        );
        await test(
            "neoforge_file_properties_language_loader_and_invalid_side",
            () =>
            {
                var toml = "modLoader=\"kotlinforforge\"\nloaderVersion=\"[99,)\"\n[properties]\nrelease=\"2.0-rc1\"\n[[mods]]\nmodId=\"propertymod\"\nversion=\"${file.release}\"\n[[dependencies.propertymod]]\nmodId=\"minecraft\"\ntype=\"required\"\nversionRange=\"[1.21,1.22)\"\nside=\"CLIENT\"\n";
                var file = Zip("properties.jar", ("META-INF/neoforge.mods.toml", toml));
                var info = ForgeModReader.Read(file, "neoforge", "1.21.1")
                    .Single();
                Check(info.Version == "2.0-rc1", "File properties not expanded");
                Check(
                    ForgeModReader.Validate(
                        [
                            file
                        ],
                        "neoforge",
                        "1.21.1",
                        "21.1.200",
                        21,
                        "4"
                    )
                        .Any(
                            x => x.Contains("kotlinforforge")
                        ),
                    "Third-party language range compared against FML"
                );
                Reject(
                    () => ForgeModReader.Read(
                        Zip(
                            "bad-side.jar",
                            ("META-INF/neoforge.mods.toml", toml.Replace("CLIENT", "UNKNOWN"))
                        ),
                        "neoforge",
                        "1.21.1"
                    )
                );
                return Task.CompletedTask;
            }
        );
        var official = new JObject
        {
            ["id"] = "fabric-loader-0.20.1-1.21.1",
            ["inheritsFrom"] = "1.21.1",
            ["mainClass"] = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            ["libraries"] = new JArray(
                new JObject
                {
                    ["name"] = "net.fabricmc:fabric-loader:0.20.1"
                }
            ),
            ["arguments"] = new JObject
            {
                ["game"] = new JArray(),
                ["jvm"] = new JArray()
            }
        };
        await test(
            "hmcl_official_profile_rebuild_and_tamper_rejected_before_install",
            async () =>
            {
                var hmcl = Zip(
                    "hmcl-official.zip",
                    ("modpack.json", "{\"gameVersion\":\"1.21.1\"}"),
                    ("minecraft/versions/custom/custom.json", official.ToString()),
                    ("minecraft/config/test.txt", "keep")
                );
                var packJson = Zip(
                    "hmcl-pack-json.zip",
                    ("modpack.json", "{\"gameVersion\":\"1.21.1\"}"),
                    ("minecraft/pack.json", official.ToString())
                );
                Check(
                    PortableModpackService.Inspect(packJson)
                        .Loader == "fabric",
                    "HMCL pack.json ignored"
                );
                var plan = PortableModpackService.Inspect(hmcl);
                Check(
                    plan.Loader == "fabric" && plan.LoaderVersion == "0.20.1" && plan.EmbeddedMetadata.Count == 1,
                    "HMCL component inference failed"
                );
                using var service = new MinecraftService(
                    new AppPaths(
                        Path.Combine(root, "hmcl-proof")
                    ),
                    metadataHandler: new JsonHandler(
                        _ => official
                    )
                );
                var result = await new PortableModpackService(service).ConvertAsync(
                    hmcl,
                    new()
                );
                using (var zip = ZipFile.OpenRead(result))
                {
                    Check(
                        zip.GetEntry("overrides/config/test.txt")is not null && !zip.Entries.Any(
                            x => x.FullName.Contains("versions/")
                        ),
                        "Embedded version copied as executable metadata"
                    );
                }

                var changed = (JObject)official.DeepClone();
                changed["arguments"]!["jvm"] = new JArray("-javaagent:bad.jar");
                var bad = Zip(
                    "hmcl-mutated.zip",
                    ("modpack.json", "{\"gameVersion\":\"1.21.1\"}"),
                    ("minecraft/versions/custom/custom.json", changed.ToString())
                );
                try
                {
                    await new PortableModpackService(service).ConvertAsync(
                        bad,
                        new()
                    );
                    throw new Exception("Modified launcher args accepted");
                }
                catch (NotSupportedException)
                {
                }
            }
        );
        await test(
            "multimc_official_patch_identity_and_unknown_components",
            () =>
            {
                var components = "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.20.1\"}]}";
                var patch = new JObject
                {
                    ["uid"] = "net.fabricmc.fabric-loader",
                    ["version"] = "0.20.1",
                    ["mainClass"] = "official"
                };
                var pack = Zip(
                    "mmc-canonical.zip",
                    ("instance/mmc-pack.json", components),
                    ("instance/patches/net.fabricmc.fabric-loader.json", patch.ToString()),
                    ("instance/.minecraft/config/test.txt", "data")
                );
                var p = PortableModpackService.Inspect(pack);
                Check(
                    p.EmbeddedMetadata.Count == 1 && p.Overrides.Count == 1,
                    "Known patch not scheduled for official verification"
                );
                var altered = (JObject)patch.DeepClone();
                altered["mainClass"] = "custom";
                Check(
                    !PackMetadataCompatibility.Equivalent(altered, patch, "multimc"),
                    "Patch custom behavior ignored"
                );
                altered = (JObject)patch.DeepClone();
                altered["order"] = 999;
                Check(
                    !PackMetadataCompatibility.Equivalent(altered, patch, "multimc"),
                    "Patch ordering ignored"
                );
                Reject(
                    () => PortableModpackService.Inspect(
                        Zip("multi-instance.zip", ("a/mmc-pack.json", components), ("b/mmc-pack.json", components))
                    )
                );
                return Task.CompletedTask;
            }
        );
    }
}
