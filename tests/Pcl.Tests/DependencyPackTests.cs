using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using Pcl.Core;

internal static class DependencyPackTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        static void Check(bool ok, string message)
        {
            if (!ok)
                throw new Exception(message);
        }

        static void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)when (e is InvalidDataException or NotSupportedException)
            {
                return;
            }

            throw new Exception("Invalid compatibility accepted");
        }

        static JObject Dep(string id, JToken? versions = null, bool optional = false, JToken? unless = null) => new()
        {
            ["id"] = id,
            ["versions"] = versions ?? "*",
            ["optional"] = optional,
            ["unless"] = unless
        };
        static QuiltCandidate Q(
            string id,
            string version = "1.0",
            bool top = true,
            JArray? deps = null,
            JArray? breaks = null,
            string load = "if_required",
            string env = "*",
            IReadOnlyList<QuiltProvided>? provides = null
        ) => new(
            id + "-" + version,
            id,
            "example",
            version,
            top,
            load,
            env,
            false,
            deps ?? [],
            breaks ?? [],
            provides ?? []
        );
        static QuiltResolution Solve(params QuiltCandidate[] mods) => QuiltDependencies.Resolve(mods, "1.20.1", "0.27.1", 17);
        // JSON null is absence; construct real dependency fixtures without optional empty fields.
        JObject D(string id, JToken? version = null, bool optional = false, JToken? unless = null)
        {
            var d = Dep(id, version, optional, unless);
            if (unless is null)
                d.Remove("unless");
            return d;
        }

        byte[] Zip(params (string Name, byte[] Data)[] files)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                foreach (var f in files)
                {
                    using var stream = zip.CreateEntry(f.Name)
                        .Open();
                    stream.Write(f.Data);
                }

            return buffer.ToArray();
        }

        byte[] Utf(string s) => Encoding.UTF8.GetBytes(s);
        string Save(string name, byte[] data)
        {
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        string QuiltJson(string id, JArray? dependencies = null, string? jar = null) => new JObject
        {
            ["schema_version"] = 1,
            ["quilt_loader"] = new JObject
            {
                ["group"] = "test",
                ["id"] = id,
                ["version"] = "1.0",
                ["depends"] = dependencies ?? [],
                ["jars"] = jar is null
                    ? new JArray()
                    : new JArray(jar)
            }
        }.ToString();
        byte[] ForgeJar(
            string id,
            string version = "1.0",
            string? depends = null,
            params (string Coordinate, string Range, string Version, byte[] Data)[] nested
        )
        {
            var toml = "modLoader=\"javafml\"\nloaderVersion=\"[1,)\"\n[[mods]]\nmodId=\"" + id + "\"\nversion=\"" + version + "\"\n";
            if (depends is not null)
                toml += "[[dependencies." + id + "]]\nmodId=\"" + depends + "\"\ntype=\"required\"\nversionRange=\"[1,)\"\nside=\"BOTH\"\n";
            var files = new List<(string, byte[])>
            {
                ("META-INF/neoforge.mods.toml", Utf(toml))
            };
            var entries = new JArray();
            int i = 0;
            foreach (var n in nested)
            {
                var p = "META-INF/jarjar/" + i++ + ".jar";
                var coord = n.Coordinate.Split(':');
                entries.Add(
                    new JObject
                    {
                        ["identifier"] = new JObject
                        {
                            ["group"] = coord[0],
                            ["artifact"] = coord[1]
                        },
                        ["version"] = new JObject
                        {
                            ["range"] = n.Range,
                            ["artifactVersion"] = n.Version
                        },
                        ["path"] = p
                    }
                );
                files.Add((p, n.Data));
            }

            if (nested.Length > 0)
                files.Add(
                    ("META-INF/jarjar/metadata.json", Utf(
                        new JObject
                        {
                            ["jars"] = entries
                        }.ToString()
                    ))
                );
            return Zip(
                files.ToArray()
            );
        }

        await test(
            "quilt_1140_official_loader_version_oracle",
            () =>
            {
                foreach (var line in File.ReadLines(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures/quilt-0.27.1-ranges.tsv")
                ))
                {
                    var fields = line.Split('\t');
                    string actual;
                    try
                    {
                        actual = QuiltVersionRange.Matches(fields[1], fields[0])
                            .ToString()
                            .ToLowerInvariant();
                    }
                    catch (Exception)
                    {
                        actual = "error";
                    }

                    Check(actual == fields[2], "Official Quilt mismatch: " + line + " actual=" + actual);
                }

                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_version_bare_caret_zero_major_tilde_and_any_all",
            () =>
            {
                Check(
                    QuiltVersionRange.Matches("0.9.0", "0.2.3") && QuiltVersionRange.Matches("0.9.0", "^0.2.3") && !QuiltVersionRange.Matches("1.0", "0.2.3"),
                    "Quilt caret incorrectly used Fabric semantics"
                );
                Check(
                    QuiltVersionRange.Matches("1.0.9", "~1") && !QuiltVersionRange.Matches("1.1", "~1"),
                    "Tilde major boundary wrong"
                );
                var condition = JObject.Parse("{\"all\":[\">=1.2\",{\"any\":[\"<1.4\",\">=2\"]}]}");
                Check(
                    QuiltVersionRange.Matches("1.3", condition) && !QuiltVersionRange.Matches("1.5", condition) && QuiltVersionRange.Matches("2.1", condition),
                    "Nested version logic wrong"
                );
                Check(
                    QuiltVersionRange.Matches("1.2.9", "1.2.x") && !QuiltVersionRange.Matches("1.3", "1.2.x") && QuiltVersionRange.Matches("1.0+build", "=1.0"),
                    "Wildcard/build comparison wrong"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_invalid_empty_universal_and_contradictory_ranges_rejected",
            () =>
            {
                foreach (var range in new[]
                {
                    "{\"all\":[\">2\",\"<1\"]}",
                    "{\"any\":[\">=1\",\"<1\"]}",
                    "{\"any\":[]}",
                    "{\"any\":[\"1\"],\"all\":[\"2\"]}"
                }

                )
                    Reject(
                        () => QuiltVersionRange.Matches(
                            "1",
                            JToken.Parse(range)
                        )
                    );
                Reject(
                    () => QuiltVersionRange.Matches("1", "^")
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_solver_backtracks_to_older_nested_candidate_and_cycles",
            () =>
            {
                var app = Q(
                    "app",
                    deps: [
                        D("library", "<2")
                    ]
                );
                var old = Q(
                    "library",
                    "1.0",
                    false,
                    deps: [
                        D("helper")
                    ]
                );
                var newer = Q("library", "2.0", false);
                var helper = Q(
                    "helper",
                    top: false,
                    deps: [
                        D("library", "<2")
                    ]
                );
                var result = Solve(app, newer, old, helper);
                Check(
                    result.Selected.Any(
                        x => x.Id == "library" && x.Version == "1.0"
                    ) && result.Selected.Any(
                        x => x.Id == "helper"
                    ),
                    "Global dependency selection failed"
                );
                Reject(
                    () => Solve(app, newer)
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_any_optional_unless_alias_group_and_conflict",
            () =>
            {
                var alias = Q(
                    "implementation",
                    provides: [
                        new("api.group", "provided", "2.0")
                    ]
                );
                var app = Q(
                    "app",
                    deps: [
                        new JArray(
                            D("missing"),
                            D("api.group:provided", "=2.0")
                        ),
                        D("absent", optional: true),
                        D(
                            "missing",
                            unless: D("implementation")
                        )
                    ]
                );
                Solve(app, alias);
                Reject(
                    () => Solve(
                        app,
                        alias with
                        {
                            Provides = [
                                new("wrong", "provided", "2.0")
                            ]
                        }
                    )
                );
                Reject(
                    () => Solve(
                        Q(
                            "app",
                            deps: [
                                D("other", "=2", true)
                            ]
                        ),
                        Q("other")
                    )
                );
                var conflicts = Q(
                    "app",
                    breaks: [
                        new JArray(
                            D("one"),
                            D("two")
                        )
                    ]
                );
                Solve(
                    conflicts,
                    Q("one")
                );
                Reject(
                    () => Solve(
                        conflicts,
                        Q("one"),
                        Q("two")
                    )
                );
                Solve(
                    Q(
                        "app",
                        breaks: [
                            D(
                                "one",
                                unless: D("escape")
                            )
                        ]
                    ),
                    Q("one"),
                    Q("escape")
                );
                Reject(
                    () => Solve(
                        Q(
                            "app",
                            breaks: [
                                D(
                                    "one",
                                    unless: D("escape")
                                )
                            ]
                        ),
                        Q("one")
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_load_types_environment_and_alias_exclusivity",
            () =>
            {
                var r = Solve(
                    Q("app"),
                    Q("unused", top: false),
                    Q("preferred", top: false, load: "if_possible"),
                    Q(
                        "servermod",
                        deps: [
                            D("missing")
                        ],
                        env: "dedicated_server"
                    )
                );
                Check(
                    !r.Selected.Any(
                        x => x.Id is "unused" or "servermod"
                    ) && r.Selected.Any(
                        x => x.Id == "preferred"
                    ),
                    "Load type/environment selection wrong"
                );
                Reject(
                    () => Solve(
                        Q(
                            "one",
                            provides: [
                                new("", "same", "1")
                            ]
                        ),
                        Q(
                            "two",
                            provides: [
                                new("", "same", "1")
                            ]
                        )
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_solver_scales_to_large_independent_nested_set",
            () =>
            {
                var mods = Enumerable.Range(0, 180)
                    .Select(
                        i => Q("nested_" + i, top: false, load: "if_possible")
                    )
                    .Append(
                        Q("app")
                    )
                    .ToArray();
                Check(
                    Solve(mods)
                        .Selected.Count == 181,
                    "Large satisfiable set not solved"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_reads_nested_archives_and_rejects_missing_or_unsafe_path",
            () =>
            {
                var child = Zip(
                    ("quilt.mod.json", Utf(
                        QuiltJson("child")
                    ))
                );
                var file = Save(
                    "quilt-nested.jar",
                    Zip(
                        ("quilt.mod.json", Utf(
                            QuiltJson(
                                "parent",
                                [
                                    D("child")
                                ],
                                "nested/child.jar"
                            )
                        )),
                        ("nested/child.jar", child)
                    )
                );
                var mods = QuiltDependencies.Read(
                    [
                        file
                    ]
                );
                Check(
                    mods.Count == 2 && Solve(
                        mods.ToArray()
                    )
                        .Selected.Count == 2,
                    "Nested jar lost"
                );
                Reject(
                    () => QuiltDependencies.Read(
                        [
                            Save(
                                "quilt-missing.jar",
                                Zip(
                                    ("quilt.mod.json", Utf(
                                        QuiltJson("parent", jar: "missing.jar")
                                    ))
                                )
                            )
                        ]
                    )
                );
                Reject(
                    () => QuiltDependencies.Read(
                        [
                            Save(
                                "quilt-unsafe.jar",
                                Zip(
                                    ("quilt.mod.json", Utf(
                                        QuiltJson("parent", jar: "../child.jar")
                                    ))
                                )
                            )
                        ]
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_dependency_disable_and_remove_roll_back",
            () =>
            {
                var dir = Path.Combine(root, "quilt-transaction");
                Directory.CreateDirectory(
                    Path.Combine(dir, "mods")
                );
                JsonFiles.Write(
                    Path.Combine(dir, "profile.json"),
                    new
                    {
                        loader = "quilt",
                        loaderVersion = "0.27.1"
                    }
                );
                JsonFiles.Write(
                    Path.Combine(dir, "version.json"),
                    new
                    {
                        javaVersion = new
                        {
                            majorVersion = 17
                        },
                        libraries = new JArray()
                    }
                );
                var a = Path.Combine(dir, "mods/a.jar");
                var b = Path.Combine(dir, "mods/b.jar");
                File.WriteAllBytes(
                    a,
                    Zip(
                        ("quilt.mod.json", Utf(
                            QuiltJson(
                                "parent",
                                [
                                    D("child")
                                ]
                            )
                        ))
                    )
                );
                File.WriteAllBytes(
                    b,
                    Zip(
                        ("quilt.mod.json", Utf(
                            QuiltJson("child")
                        ))
                    )
                );
                var manager = new LocalResourceService(dir, "quilt", "1.20.1");
                manager.ValidateEnabledResources();
                Reject(
                    () => manager.SetEnabled(
                        [
                            b
                        ],
                        false
                    )
                );
                Check(
                    File.Exists(b) && !File.Exists(b + ".disabled"),
                    "Disabled dependency not rolled back"
                );
                Reject(
                    () => manager.Remove(
                        [
                            b
                        ]
                    )
                );
                Check(
                    File.Exists(b),
                    "Removed dependency not restored"
                );
                manager.SetEnabled(
                    [
                        a,
                        b
                    ],
                    false
                );
                Check(
                    File.Exists(b + ".disabled"),
                    "Valid batch disable rejected"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "jarjar_intersects_all_ranges_and_excludes_unselected_mod_constraints",
            () =>
            {
                var old = ForgeJar("nested", "1.4");
                var newer = ForgeJar("nested", "2.0", "nonexistent");
                var a = Save(
                    "jij-a.jar",
                    ForgeJar(
                        "alpha",
                        nested: [
                            ("example:lib", "[1,3)", "2.0", newer)
                        ]
                    )
                );
                var b = Save(
                    "jij-b.jar",
                    ForgeJar(
                        "beta",
                        nested: [
                            ("example:lib", "[1,2)", "1.4", old)
                        ]
                    )
                );
                var selected = JarJarResolver.Resolve(
                    [
                        a,
                        b
                    ],
                    "neoforge",
                    "1.21.1"
                );
                Check(
                    selected.Selected.Single()
                        .Version == "1.4" && selected.Mods.Single(
                        x => x.Id == "nested"
                    )
                        .Version == "1.4",
                    "Wrong candidate chosen"
                );
                ForgeModReader.Validate(
                    [
                        a,
                        b
                    ],
                    "neoforge",
                    "1.21.1",
                    "21.1.200",
                    21,
                    "4"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "jarjar_library_wrapper_toml_is_not_a_duplicate_mod",
            () =>
            {
                var child = ForgeJar("wrapped");
                var parent = ForgeJar(
                    "wrapped",
                    nested: [
                        ("example:wrapped", "[1,)", "1.0", child)
                    ]
                );
                using var buffer = new MemoryStream();
                buffer.Write(parent);
                using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, true))
                {
                    using var writer = new StreamWriter(
                        zip.CreateEntry("META-INF/MANIFEST.MF")
                            .Open()
                    );
                    writer.Write("Manifest-Version: 1.0\nFMLModType: LIBRARY\nAutomatic-Module-Name: wrapped.service\n");
                }

                var file = Save(
                    "jij-wrapper.jar",
                    buffer.ToArray()
                );
                var result = JarJarResolver.Resolve(
                    [
                        file
                    ],
                    "neoforge",
                    "1.21.1"
                );
                Check(
                    result.Selected.Count == 1 && result.Mods.Single()
                        .Id == "wrapped" && result.Mods.Single()
                        .Nested,
                    "Library wrapper shadowed actual nested mod"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "jarjar_unselected_invalid_toml_does_not_poison_selected_mod",
            () =>
            {
                var invalid = Zip(
                    ("META-INF/neoforge.mods.toml", Utf("[[broken"))
                );
                var valid = ForgeJar("nested", "1.4");
                var a = Save(
                    "jij-invalid-unused.jar",
                    ForgeJar(
                        "alpha",
                        nested: [
                            ("example:lib", "[1,3)", "2.0", invalid)
                        ]
                    )
                );
                var b = Save(
                    "jij-valid-old.jar",
                    ForgeJar(
                        "beta",
                        nested: [
                            ("example:lib", "[1,2)", "1.4", valid)
                        ]
                    )
                );
                ForgeModReader.Validate(
                    [
                        a,
                        b
                    ],
                    "neoforge",
                    "1.21.1",
                    "21.1.200",
                    21,
                    "4"
                );
                Reject(
                    () => JarJarResolver.Resolve(
                        [
                            a
                        ],
                        "neoforge",
                        "1.21.1"
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "jarjar_conflicting_ranges_and_identical_version_different_bytes_rejected",
            () =>
            {
                var a = Save(
                    "jij-conflict-a.jar",
                    ForgeJar(
                        "alpha",
                        nested: [
                            ("example:lib", "[1,2)", "1.0", ForgeJar("nested"))
                        ]
                    )
                );
                var b = Save(
                    "jij-conflict-b.jar",
                    ForgeJar(
                        "beta",
                        nested: [
                            ("example:lib", "[2,3)", "2.0", ForgeJar("nested", "2.0"))
                        ]
                    )
                );
                Reject(
                    () => JarJarResolver.Resolve(
                        [
                            a,
                            b
                        ],
                        "neoforge",
                        "1.21.1"
                    )
                );
                var c = Save(
                    "jij-different.jar",
                    ForgeJar(
                        "gamma",
                        nested: [
                            ("example:lib", "[1,2)", "1.0", ForgeJar("different"))
                        ]
                    )
                );
                Reject(
                    () => JarJarResolver.Resolve(
                        [
                            a,
                            c
                        ],
                        "neoforge",
                        "1.21.1"
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "jarjar_nested_library_transitives_and_top_level_override",
            () =>
            {
                var lib = Zip(
                    ("META-INF/MANIFEST.MF", Utf("Manifest-Version: 1.0\nAutomatic-Module-Name: example.lib\n"))
                );
                var child = ForgeJar(
                    "child",
                    nested: [
                        ("example:pure", "[1,)", "1.0", lib)
                    ]
                );
                var parent = Save(
                    "jij-transitive.jar",
                    ForgeJar(
                        "parent",
                        nested: [
                            ("example:child", "[1,)", "1.0", child)
                        ]
                    )
                );
                var top = Save(
                    "jij-top.jar",
                    ForgeJar("child", "2.0")
                );
                var result = JarJarResolver.Resolve(
                    [
                        parent,
                        top
                    ],
                    "neoforge",
                    "1.21.1"
                );
                Check(
                    result.Selected.Single()
                        .Coordinate == "example:pure" && result.Mods.Count(
                        x => x.Id == "child"
                    ) == 1 && result.Mods.Single(
                        x => x.Id == "child"
                    )
                        .Version == "2.0",
                    "Top-level override or nested library lost"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "custom_pack_preserves_additive_libraries_properties_and_verified_entrypoint",
            () =>
            {
                var lib = new JObject
                {
                    ["name"] = "example:library:1.0",
                    ["downloads"] = new JObject
                    {
                        ["artifact"] = new JObject
                        {
                            ["path"] = "example/library/1.0/library-1.0.jar",
                            ["url"] = "https://example.org/library.jar",
                            ["sha1"] = new string ('a', 40),
                            ["size"] = 100
                        }
                    }
                };
                var official = new JObject
                {
                    ["id"] = "base",
                    ["mainClass"] = "official.Main",
                    ["libraries"] = new JArray(),
                    ["arguments"] = new JObject
                    {
                        ["jvm"] = new JArray("-cp", "${classpath}"),
                        ["game"] = new JArray()
                    }
                };
                var imported = (JObject)official.DeepClone();
                ((JArray)imported["libraries"]!).Add(lib);
                ((JArray)imported["arguments"]!["jvm"]!).Add("-Dpack.title=中文测试");
                var additions = CustomPackMetadata.Additions(imported, official, "hmcl", "test");
                Check(
                    additions.Libraries.Count == 1 && additions.JvmArguments.Count == 1,
                    "Additions dropped"
                );
                imported["mainClass"] = "custom.Main";
                Check(
                    CustomPackMetadata.Additions(imported, official, "hmcl", "test")
                        .MainClass == "custom.Main",
                    "Custom entrypoint lost"
                );
                imported["mainClass"] = "custom.Main --token";
                Reject(
                    () => CustomPackMetadata.Additions(imported, official, "hmcl", "test")
                );
                Reject(
                    () => CustomPackMetadata.ValidateArgument("-javaagent:evil.jar")
                );
                Reject(
                    () => CustomPackMetadata.ValidateArgument("-Dpack.token=${auth_access_token}")
                );
                var broken = (JObject)lib.DeepClone();
                broken["downloads"]!["artifact"]!["sha1"] = "";
                Reject(
                    () => CustomPackMetadata.ValidateLibrary(broken)
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "multimc_custom_component_requires_order_and_conversion_retains_metadata",
            async () =>
            {
                var components = new JArray(
                    new JObject
                    {
                        ["uid"] = "net.minecraft",
                        ["version"] = "1.21.1"
                    },
                    new JObject
                    {
                        ["uid"] = "local.example",
                        ["version"] = "1.0"
                    }
                );
                var patch = new JObject
                {
                    ["formatVersion"] = 1,
                    ["uid"] = "local.example",
                    ["version"] = "1.0",
                    ["order"] = 100,
                    ["libraries"] = new JArray(
                        new JObject
                        {
                            ["name"] = "example:library:1.0",
                            ["url"] = "https://example.org/",
                            ["sha1"] = new string ('a', 40),
                            ["size"] = 100
                        }
                    ),
                    ["requires"] = new JArray(
                        new JObject
                        {
                            ["uid"] = "net.minecraft",
                            ["equals"] = "1.21.1"
                        }
                    )
                };
                var archive = Save(
                    "custom-multimc.zip",
                    Zip(
                        ("instance/mmc-pack.json", Utf(
                            new JObject
                            {
                                ["formatVersion"] = 1,
                                ["components"] = components
                            }.ToString()
                        )),
                        ("instance/patches/local.example.json", Utf(
                            patch.ToString()
                        )),
                        ("instance/.minecraft/config/example.txt", Utf("preserved"))
                    )
                );
                using var service = new MinecraftService(
                    new AppPaths(
                        Path.Combine(root, "custom-pack")
                    )
                );
                var plan = PortableModpackService.Inspect(archive);
                Check(
                    plan.EmbeddedMetadata.Single()
                        .Provider == "custom-multimc",
                    "Custom component not recognized"
                );
                var converted = await new PortableModpackService(service).ConvertAsync(
                    archive,
                    new()
                );
                Check(
                    (string? )CustomPackMetadata.ReadArchive(converted)
                        .Libraries.Single()["name"] == "example:library:1.0",
                    "Converted pack lost customization"
                );
                patch["order"] = 2;
                Reject(
                    () => CustomPackMetadata.ReadCustomPatch(patch, components, "test")
                );
                patch["order"] = 100;
                patch["requires"]![0]!["equals"] = "1.20.1";
                Reject(
                    () => CustomPackMetadata.ReadCustomPatch(patch, components, "test")
                );
            }
        );
        await test(
            "quilt_remote_backtracks_project_versions_and_preserves_exact_pins",
            async () =>
            {
                var cache = Path.Combine(root, "quilt-remote");
                Directory.CreateDirectory(cache);
                ResourceRelease Release(string id, string project, string version, JArray deps, ResourceDependency[] edges)
                {
                    var json = JObject.Parse(
                        QuiltJson(project, deps)
                    );
                    json["quilt_loader"]!["version"] = version;
                    var bytes = Zip(
                        ("quilt.mod.json", Utf(
                            json.ToString()
                        ))
                    );
                    var hash = Convert.ToHexString(
                        System.Security.Cryptography.SHA1.HashData(bytes)
                    )
                        .ToLowerInvariant();
                    File.WriteAllBytes(
                        Path.Combine(cache, hash),
                        bytes
                    );
                    return new(
                        id,
                        project,
                        id,
                        version,
                        "release",
                        DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                            .AddDays(
                                version.StartsWith("2")
                                    ? 1
                                    : 0
                            ),
                        [
                            "1.20.1"
                        ],
                        [
                            "quilt"
                        ],
                        new(
                            id + ".jar",
                            new Uri("https://example.org/" + id),
                            bytes.Length,
                            hash
                        ),
                        edges
                    );
                }

                var older = Release(
                    "old",
                    "library",
                    "1.0",
                    [],
                    []
                );
                var newer = Release(
                    "new",
                    "library",
                    "2.0",
                    [],
                    []
                );
                var app = Release(
                    "app",
                    "application",
                    "1.0",
                    [
                        D("library", "<2")
                    ],
                    [
                        new("library", null, "required")
                    ]
                );
                JObject Json(ResourceRelease r) => new()
                {
                    ["id"] = r.Id,
                    ["project_id"] = r.ProjectId,
                    ["name"] = r.Name,
                    ["version_number"] = r.Number,
                    ["version_type"] = r.Channel,
                    ["date_published"] = r.Published,
                    ["game_versions"] = new JArray(r.GameVersions),
                    ["loaders"] = new JArray(r.Loaders),
                    ["files"] = new JArray(
                        new JObject
                        {
                            ["filename"] = r.File.Name,
                            ["url"] = r.File.Url.ToString(),
                            ["size"] = r.File.Size,
                            ["hashes"] = new JObject
                            {
                                ["sha1"] = r.File.Sha1
                            }
                        }
                    ),
                    ["dependencies"] = new JArray(
                        r.Dependencies.Select(
                            d => new JObject
                            {
                                ["project_id"] = d.ProjectId,
                                ["version_id"] = d.VersionId,
                                ["dependency_type"] = d.Type
                            }
                        )
                    )
                };
                using var catalog = new ResourceCatalog(
                    new JsonHandler(
                        request => request.RequestUri!.AbsolutePath.Contains("/project/")
                            ? request.RequestUri.AbsolutePath.EndsWith("/version")
                                ? new JArray(
                                    Json(newer),
                                    Json(older)
                                )
                                : new JObject
                                {
                                    ["id"] = "library",
                                    ["slug"] = "library",
                                    ["title"] = "Library",
                                    ["project_type"] = "mod"
                                }
                            : Json(newer)
                    )
                );
                using var downloads = new DownloadService();
                var plan = await new QuiltModPlanner(catalog, downloads, cache, "0.27.1").ResolveAsync(app, "1.20.1");
                Check(
                    plan.Releases.Any(
                        x => x.Id == "old"
                    ) && !plan.Releases.Any(
                        x => x.Id == "new"
                    ),
                    "Remote candidates did not backtrack"
                );
                bool rejected = false;
                try
                {
                    await new QuiltModPlanner(catalog, downloads, cache, "0.27.1").ResolveAsync(
                        app with
                        {
                            Dependencies = [
                                new("library", "new", "required")
                            ]
                        },
                        "1.20.1"
                    );
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }

                Check(rejected, "Exact remote pin was silently changed");
            }
        );
        await test(
            "custom_metadata_normalization_roundtrip_and_export_retains_extension",
            async () =>
            {
                var lib = new JObject
                {
                    ["name"] = "example:library:1.0",
                    ["url"] = "https://example.org/",
                    ["sha1"] = new string ('a', 40),
                    ["size"] = 100
                };
                var normalized = CustomPackMetadata.ValidateLibrary(lib);
                Check(normalized["rules"] is null, "Absent rules emitted as JSON null");
                CustomPackMetadata.ValidateLibrary(normalized);
                var dir = Path.Combine(root, "custom-export");
                Directory.CreateDirectory(dir);
                JsonFiles.Write(
                    Path.Combine(dir, CustomPackMetadata.EntryName),
                    CustomPackMetadata.Serialize(
                        new(
                            [
                                normalized
                            ],
                            [
                                "-Dpack.title=测试"
                            ],
                            []
                        )
                    )
                );
                var output = Path.Combine(root, "custom-export.mrpack");
                await PortableModpackService.ExportAsync(
                    new(
                        "test",
                        "test",
                        "1.21.1",
                        "vanilla",
                        dir,
                        Path.Combine(dir, "version.json")
                    ),
                    output,
                    new("test")
                );
                var restored = CustomPackMetadata.ReadArchive(output);
                Check(
                    restored.Libraries.Count == 1 && restored.JvmArguments.Single() == "-Dpack.title=测试",
                    "Custom metadata missing after export"
                );
            }
        );
    }
}
