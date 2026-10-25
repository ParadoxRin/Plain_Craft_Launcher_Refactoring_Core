using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Pcl.Core;

/// <summary>本次依赖和整合包改动的定向验证；真实文件提交配合隔离的HTTP目录替身，JDK结果来自独立Java工具。</summary>
internal static class Compatibility034Tests
{
    public static async Task<int> Run(string output, string fixtures)
    {
        output = Path.GetFullPath(output);
        fixtures = Path.GetFullPath(fixtures);
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        var failed = 0;
        async Task Test(string name, Func<Task> action)
        {
            try
            {
                await action();
                rows.Add(
                    new
                    {
                        name,
                        status = "passed"
                    }
                );
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                rows.Add(
                    new
                    {
                        name,
                        status = "failed",
                        error = ex.ToString()
                    }
                );
                Console.WriteLine("FAIL " + name + " " + ex.Message);
            }

            JsonFiles.Write(
                Path.Combine(output, "results.json"),
                new
                {
                    failed,
                    rows
                }
            );
        }

        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new Exception(message);
            }
        }

        async Task Reject(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)when (ex is InvalidDataException or NotSupportedException)
            {
                return;
            }

            throw new Exception("Expected compatibility rejection");
        }

        byte[] Jar(params (string Name, byte[] Data)[] entries)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                foreach (var entry in entries)
                {
                    using var stream = zip.CreateEntry(entry.Name)
                        .Open();
                    stream.Write(entry.Data);
                }
            }

            return memory.ToArray();
        }

        byte[] Utf(string value) => System.Text.Encoding.UTF8.GetBytes(value);
        var payload = new Dictionary<string, byte[]>();
        ResourceRelease Release(string project, string version, JObject? depends = null, params string[] remote)
        {
            var name = project + "-" + version + ".jar";
            var data = Jar(
                ("fabric.mod.json", Utf(
                    new JObject
                    {
                        ["schemaVersion"] = 1,
                        ["id"] = project,
                        ["version"] = version,
                        ["depends"] = depends ?? new JObject()
                    }.ToString()
                ))
            );
            payload[name] = data;
            return new(
                project + version,
                project,
                project,
                version,
                "release",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                    .AddDays(version[0]),
                [
                    "1.20.1"
                ],
                [
                    "fabric"
                ],
                new(
                    name,
                    new("https://fixture.invalid/" + name),
                    data.Length,
                    Convert.ToHexString(
                        SHA1.HashData(data)
                    )
                        .ToLowerInvariant()
                ),
                remote.Select(
                    x => new ResourceDependency(x, null, "required")
                )
                    .ToArray()
            );
        }

        var a1 = Release("root_a", "1.0.0");
        var b1 = Release("root_b", "1.0.0");
        var x1 = Release("shared_x", "1.0.0");
        var x2 = Release("shared_x", "2.0.0");
        var y1 = Release("shared_y", "1.0.0");
        var y2 = Release("shared_y", "2.0.0");
        var a2 = Release(
            "root_a",
            "2.0.0",
            JObject.Parse("{\"shared_x\":\"=1.0.0\"}"),
            "shared_x",
            "shared_y",
            "root_b"
        );
        var b2 = Release(
            "root_b",
            "2.0.0",
            JObject.Parse("{\"shared_y\":\"=1.0.0\"}"),
            "shared_y",
            "root_a"
        );
        var releases = new[]
        {
            a1,
            b1,
            a2,
            b2,
            x1,
            x2,
            y1,
            y2
        };
        string Instance(string name)
        {
            var path = Path.Combine(output, name);
            Directory.CreateDirectory(
                Path.Combine(path, "mods")
            );
            JsonFiles.Write(
                Path.Combine(path, "profile.json"),
                new
                {
                    loaderVersion = "0.16.0"
                }
            );
            File.WriteAllBytes(
                Path.Combine(path, "mods", a1.File.Name),
                payload[a1.File.Name]
            );
            File.WriteAllBytes(
                Path.Combine(path, "mods", b1.File.Name),
                payload[b1.File.Name]
            );
            return path;
        }

        LocalResourceUpdate[] Updates(string instance, ResourceRelease a, ResourceRelease b)
        {
            var local = new LocalResourceService(instance, "fabric", "1.20.1").Scan();
            return[
                new(
                    local.Single(
                        x => x.ModIds.Contains("root_a")
                    ),
                    a
                ),
                new(
                    local.Single(
                        x => x.ModIds.Contains("root_b")
                    ),
                    b
                )
            ];
        }

        using var catalog = new ResourceCatalog(
            new FixtureHandler(releases, payload)
        );
        using var downloads = new DownloadService(
            new FixtureHandler(releases, payload)
        );
        await Test(
            "joint_batch_backtracks_two_transitives_preserves_roots_cycle_and_commits",
            async () =>
            {
                var instance = Instance("joint-success");
                var updater = new ResourceBatchUpdater(instance, "fabric", "1.20.1");
                using var prepared = await updater.PrepareAsync(
                    Updates(instance, a2, b2),
                    catalog,
                    downloads
                );
                Check(
                    Directory.GetFiles(
                        Path.Combine(instance, "mods")
                    )
                        .Length == 2,
                    "Preparation modified live mods"
                );
                var backup = updater.Apply(prepared);
                var actual = new LocalResourceService(instance, "fabric", "1.20.1").Scan();
                Check(
                    actual.Count == 4 && actual.Where(
                        x => x.ModIds.Any(
                            id => id.StartsWith("shared_")
                        )
                    )
                        .All(
                            x => x.Version == "1.0.0"
                        ),
                    "Joint alternatives not selected"
                );
                Check(
                    actual.Where(
                        x => x.ModIds.Any(
                            id => id.StartsWith("root_")
                        )
                    )
                        .All(
                            x => x.Version == "2.0.0"
                        ),
                    "Explicit roots were downgraded"
                );
                Check(
                    Directory.GetFiles(
                        Path.Combine(backup, "mods")
                    )
                        .Length == 2,
                    "Original backup incomplete"
                );
                JsonFiles.Write(
                    Path.Combine(output, "joint-selected.json"),
                    actual.Select(
                        x => new
                        {
                            x.ModIds,
                            x.Version
                        }
                    )
                );
            }
        );
        await Test(
            "joint_unsatisfiable_rolls_back_every_candidate_without_live_changes",
            async () =>
            {
                var bad = a2 with
                {
                    Dependencies = [
                        new("shared_x", x2.Id, "required")
                    ]
                };
                var instance = Instance("joint-unsatisfiable");
                var before = Directory.GetFiles(
                    Path.Combine(instance, "mods")
                )
                    .ToDictionary(
                        x => Path.GetFileName(x)!,
                        File.ReadAllBytes
                    );
                await Reject(
                    async () =>
                    {
                        using var prepared = await new ResourceBatchUpdater(instance, "fabric", "1.20.1").PrepareAsync(
                            Updates(instance, bad, b2),
                            catalog,
                            downloads
                        );
                    }
                );
                Check(
                    Directory.GetFiles(
                        Path.Combine(instance, "mods")
                    )
                        .Length == before.Count && before.All(
                        x => File.ReadAllBytes(
                            Path.Combine(instance, "mods", x.Key!)
                        )
                            .SequenceEqual(x.Value)
                    ),
                    "Rejected batch changed live files"
                );
            }
        );
        await Test(
            "joint_pinned_root_conflict_is_not_silently_downgraded",
            async () =>
            {
                await Reject(
                    () => JointResourcePlanner.ResolveAsync(
                        catalog,
                        [
                            a2 with
                            {
                                Dependencies = [
                                    new("root_b", b1.Id, "required")
                                ]
                            },
                            b2
                        ],
                        ResourceKind.Mod,
                        "1.20.1",
                        "fabric",
                        (_, _) => Task.CompletedTask
                    )
                );
            }
        );
        await Test(
            "joint_cancellation_propagates_without_validation",
            async () =>
            {
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                try
                {
                    await JointResourcePlanner.ResolveAsync(
                        catalog,
                        [
                            a2,
                            b2
                        ],
                        ResourceKind.Mod,
                        "1.20.1",
                        "fabric",
                        (_, _) => throw new Exception("Unexpected validation"),
                        ct: cancel.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                throw new Exception("Cancellation was lost");
            }
        );
        await Test(
            "joint_corrupt_download_aborts_without_trying_alternative_versions",
            async () =>
            {
                var instance = Instance("joint-corrupt");
                var corrupted = new Dictionary<string, byte[]>(payload);
                corrupted[a2.File.Name] = new byte[payload[a2.File.Name].Length];
                using var damaged = new DownloadService(
                    new FixtureHandler(releases, corrupted)
                );
                var attempts = 0;
                try
                {
                    await JointResourcePlanner.ResolveAsync(
                        catalog,
                        [
                            a2,
                            b2
                        ],
                        ResourceKind.Mod,
                        "1.20.1",
                        "fabric",
                        async (plan, token) =>
                        {
                            attempts++;
                            await new ResourceInstaller(instance, damaged, "fabric", "1.20.1").InstallAsync(plan, ct: token);
                        }
                    );
                }
                catch (IOException ex)when (ex.InnerException is InvalidDataException)
                {
                    Check(
                        attempts == 1 && Directory.GetFiles(
                            Path.Combine(instance, "mods")
                        )
                            .Length == 2,
                        "Integrity error triggered fallback or changed live files"
                    );
                    return;
                }

                throw new Exception("Integrity failure did not abort resolution");
            }
        );
        foreach (var row in JArray.Parse(
            File.ReadAllText(
                Path.Combine(fixtures, "module-oracle.json")
            )
        ))
        {
            await Test(
                "jdk_module_oracle_" + row["file"] + "_" + row["targetJava"],
                () =>
                {
                    var file = Path.Combine(fixtures, (string)row["file"]!);
                    using var zip = ZipFile.OpenRead(file);
                    Check(
                        JarModuleIdentity.Read(zip, file, (int)row["targetJava"]!) == (string)row["expected"]!,
                        "JDK and C# identities differ"
                    );
                    return Task.CompletedTask;
                }
            );
        }

        await Test(
            "module_descriptor_truncation_and_duplicates_rejected",
            async () =>
            {
                using var truncated = ZipFile.OpenRead(
                    Path.Combine(fixtures, "truncated.jar")
                );
                await Reject(
                    () =>
                    {
                        JarModuleIdentity.Read(truncated, "truncated.jar", 21);
                        return Task.CompletedTask;
                    }
                );
                var data = File.ReadAllBytes(
                    Path.Combine(fixtures, "base/module-info.class")
                );
                using var memory = new MemoryStream(
                    Jar(("module-info.class", data), ("module-info.class", data))
                );
                using var duplicate = new ZipArchive(memory, ZipArchiveMode.Read);
                await Reject(
                    () =>
                    {
                        JarModuleIdentity.Read(duplicate, "duplicate.jar", 21);
                        return Task.CompletedTask;
                    }
                );
            }
        );
        await Test(
            "forge_and_neoforge_jarjar_explicit_module_deduplicates_distinct_coordinates",
            () =>
            {
                var module = File.ReadAllBytes(
                    Path.Combine(fixtures, "base/module-info.class")
                );
                var nestedA = Jar(
                    ("module-info.class", module),
                    ("data/a.txt", Utf("a"))
                );
                var nestedB = Jar(
                    ("module-info.class", module),
                    ("data/b.txt", Utf("b"))
                );
                var entries = new JArray();
                foreach (var id in new[]
                {
                    "alpha",
                    "beta"
                }

                )
                {
                    entries.Add(
                        new JObject
                        {
                            ["identifier"] = new JObject
                            {
                                ["group"] = "example",
                                ["artifact"] = id
                            },
                            ["version"] = new JObject
                            {
                                ["range"] = "[1,2)",
                                ["artifactVersion"] = "1.0"
                            },
                            ["path"] = "META-INF/jarjar/" + id + ".jar"
                        }
                    );
                }

                var toml = Utf(
                    "modLoader=\"javafml\"\nloaderVersion=\"[1,)\"\n[[mods]]\nmodId=\"module_parent\"\nversion=\"1.0\"\n"
                );
                var file = Path.Combine(output, "module-parent.jar");
                File.WriteAllBytes(
                    file,
                    Jar(
                        ("META-INF/mods.toml", toml),
                        ("META-INF/neoforge.mods.toml", toml),
                        ("META-INF/jarjar/metadata.json", Utf(
                            new JObject
                            {
                                ["jars"] = entries
                            }.ToString()
                        )),
                        ("META-INF/jarjar/alpha.jar", nestedA),
                        ("META-INF/jarjar/beta.jar", nestedB)
                    )
                );
                foreach (var loader in new[]
                {
                    "forge",
                    "neoforge"
                }

                )
                {
                    var modern = JarJarResolver.Resolve(
                        [
                            file
                        ],
                        loader,
                        "1.21.1",
                        21
                    );
                    Check(
                        modern.Selected.Count == 1 && modern.Warnings.Count == 1,
                        "Explicit module identity was ignored: " + loader
                    );
                    Check(
                        JarJarResolver.Resolve(
                            [
                                file
                            ],
                            loader,
                            "1.21.1",
                            8
                        )
                            .Selected.Count == 2,
                        "Target Java version was ignored"
                    );
                }

                return Task.CompletedTask;
            }
        );
        var library = JObject.Parse(
            File.ReadAllText(
                Path.Combine(fixtures, "library.json")
            )
        );
        async Task ApplyCase(string name, CustomPackChanges changes, bool pass)
        {
            var root = Path.Combine(output, name);
            using var service = new MinecraftService(
                new AppPaths(root)
            );
            var instance = Path.Combine(root, "instance");
            Directory.CreateDirectory(instance);
            var version = Path.Combine(instance, "version.json");
            JsonFiles.Write(
                version,
                new JObject
                {
                    ["mainClass"] = "net.minecraft.client.main.Main",
                    ["libraries"] = new JArray(),
                    ["arguments"] = new JObject
                    {
                        ["jvm"] = new JArray(),
                        ["game"] = new JArray("--username", "${auth_player_name}")
                    }
                }
            );
            var before = File.ReadAllBytes(version);
            foreach (var lib in changes.Libraries)
            {
                var artifact = lib["downloads"]!["artifact"]!;
                var cache = Path.Combine(
                    service.Paths.Game,
                    "libraries/pcl-custom",
                    (string)artifact["sha1"]!,
                    (string)artifact["path"]!
                );
                Directory.CreateDirectory(
                    Path.GetDirectoryName(cache)!
                );
                File.Copy(
                    Path.Combine(fixtures, "delegate-1.0.jar"),
                    cache,
                    true
                );
            }

            if (pass)
            {
                await CustomPackMetadata.ApplyAsync(service, instance, changes, default);
                var metadata = JsonFiles.Read(version);
                Check((string? )metadata["mainClass"] == changes.MainClass, "Entrypoint not applied");
                Check(
                    metadata["arguments"]!["game"]!.ToString()
                        .Contains("${auth_player_name}"),
                    "Account arguments changed"
                );
                var roundtrip = CustomPackMetadata.Parse(
                    JsonFiles.Read(
                        Path.Combine(instance, CustomPackMetadata.EntryName)
                    )
                );
                Check(roundtrip.MainClass == changes.MainClass, "Exportable receipt lost entrypoint");
            }
            else
            {
                await Reject(
                    () => CustomPackMetadata.ApplyAsync(service, instance, changes, default)
                );
                Check(
                    File.ReadAllBytes(version)
                        .SequenceEqual(before),
                    "Rejected entrypoint modified version"
                );
            }
        }

        await Test(
            "custom_verified_compiled_entrypoint_applied_and_receipt_roundtrips",
            () => ApplyCase(
                "entry-valid",
                new(
                    [
                        library
                    ],
                    [],
                    [],
                    "pcl.compat.Delegate"
                ),
                true
            )
        );
        await Test(
            "custom_missing_class_rejects_before_version_publish",
            () => ApplyCase(
                "entry-missing",
                new(
                    [
                        library
                    ],
                    [],
                    [],
                    "pcl.compat.Missing"
                ),
                false
            )
        );
        await Test(
            "custom_duplicate_class_rejects_before_version_publish",
            () =>
            {
                var second = (JObject)library.DeepClone();
                second["name"] = "pcl.compat:other:1.0";
                second["downloads"]!["artifact"]!["path"] = "pcl/compat/other/1.0/other-1.0.jar";
                return ApplyCase(
                    "entry-duplicate",
                    new(
                        [
                            library,
                            second
                        ],
                        [],
                        [],
                        "pcl.compat.Delegate"
                    ),
                    false
                );
            }
        );
        await Test(
            "custom_windows_only_library_cannot_supply_mac_entrypoint",
            () =>
            {
                var windows = (JObject)library.DeepClone();
                windows["rules"] = JArray.Parse("[{\"action\":\"allow\",\"os\":{\"name\":\"windows\"}}]");
                return ApplyCase(
                    "entry-windows",
                    new(
                        [
                            windows
                        ],
                        [],
                        [],
                        "pcl.compat.Delegate"
                    ),
                    false
                );
            }
        );
        await Test(
            "custom_tweaker_requires_launchwrapper_and_old_schema_rejects_entrypoint",
            async () =>
            {
                await ApplyCase(
                    "entry-tweaker",
                    new(
                        [
                            library
                        ],
                        [],
                        [],
                        "pcl.compat.Delegate",
                        [
                            "pcl.compat.Delegate"
                        ]
                    ),
                    false
                );
                var old = CustomPackMetadata.Serialize(
                    new(
                        [
                            library
                        ],
                        [],
                        [],
                        "pcl.compat.Delegate"
                    )
                );
                old["schemaVersion"] = 1;
                await Reject(
                    () =>
                    {
                        CustomPackMetadata.Parse(old);
                        return Task.CompletedTask;
                    }
                );
            }
        );
        // 覆盖本轮更改实际复用的既有加载器、整合包与批量提交分支。
        await DependencyPackTests.RunAsync(output, Test);
        await PackResourceTests.RunAsync(output, Test);
        await QuiltOverrideTests.RunAsync(output, Test);
        return failed == 0
            ? 0
            : 1;
    }

    /// <summary>只替代远程API和下载输送；求解、JAR解析、暂存、回滚和提交全部执行生产实现。</summary>
    private sealed class FixtureHandler(ResourceRelease[] releases, Dictionary<string, byte[]> payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            if (uri.Host == "fixture.invalid")
            {
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(
                            payload[Path.GetFileName(uri.AbsolutePath)]
                        )
                    }
                );
            }

            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            object body;
            if (parts.Length == 4 && parts[1] == "project")
            {
                body = releases.Where(
                    x => x.ProjectId == parts[2]
                )
                    .Select(Wire)
                    .ToArray();
            }
            else if (parts[1] == "project")
            {
                body = new
                {
                    id = parts[2],
                    slug = parts[2],
                    title = parts[2],
                    project_type = "mod"
                };
            }
            else
            {
                body = Wire(
                    releases.Single(
                        x => x.Id == parts[2]
                    )
                );
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        Newtonsoft.Json.JsonConvert.SerializeObject(body)
                    )
                }
            );
        }

        private static object Wire(ResourceRelease r) => new
        {
            id = r.Id,
            project_id = r.ProjectId,
            name = r.Name,
            version_number = r.Number,
            version_type = r.Channel,
            date_published = r.Published,
            game_versions = r.GameVersions,
            loaders = r.Loaders,
            files = new[]
            {
                new
                {
                    filename = r.File.Name,
                    url = r.File.Url,
                    size = r.File.Size,
                    primary = true,
                    hashes = new
                    {
                        sha1 = r.File.Sha1
                    }
                }
            },
            dependencies = r.Dependencies.Select(
                x => new
                {
                    project_id = x.ProjectId,
                    version_id = x.VersionId,
                    dependency_type = x.Type
                }
            )
        };
    }
}
