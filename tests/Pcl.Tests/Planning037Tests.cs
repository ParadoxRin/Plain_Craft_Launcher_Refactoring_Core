using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pcl.Core;
using Pcl.Domain;

/// <summary>File transactions plus a bounded catalog fixture, independently checked against actual Fabric Loader.</summary>
internal static class Planning037Tests
{
    public static async Task<int> Run(string root, string java, string loaderJar)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root))
        {
            throw new InvalidOperationException("Use a fresh test directory.");
        }

        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }

        async Task Reject(Func<Task> action, string name)
        {
            try
            {
                await action();
            }
            catch (Exception ex)when (ex is InvalidDataException or IOException or NotSupportedException or OperationCanceledException)
            {
                Check(true, name);
                return;
            }

            throw new Exception("Expected rejection: " + name);
        }

        var versions = new[]
        {
            "0.2.1",
            "0.3.0",
            "1",
            "1.0.0-alpha",
            "1.0.0",
            "1.2.1",
            "1.3.0-alpha",
            "1.3",
            "2.0.0-alpha",
            "2.0.0",
            "3.0.0",
            "custom",
            "other"
        };
        var cases = new[]
        {
            ("legacy", """{"id":"fixture","version":"1","requires":{"dep":[">=1 <2","custom"]},"conflicts":{"old":"*"},"recommends":{"optional":"*"},"depends":{"ignored":"*"},"side":"universal"}""", """{"version":1}"""),
            ("append", """{"schemaVersion":1,"id":"fixture","version":"1","depends":{"dep":">=1"}}""", """{"version":1,"overrides":{"fixture":{"+depends":{"dep":"<2"}}}}"""),
            ("replace-wins", """{"schemaVersion":1,"id":"fixture","version":"1","depends":{"dep":">=3"}}""", """{"version":1,"overrides":{"fixture":{"+depends":{"ignored":"*"},"-depends":{"dep":"*"},"depends":{"dep":"~1.2"}}}}"""),
            ("remove-before-add", """{"schemaVersion":1,"id":"fixture","version":"1","depends":{"dep":">=3"}}""", """{"version":1,"overrides":{"fixture":{"+depends":{"dep":"^0.2"},"-depends":{"dep":"999"}}}}"""),
            ("all-kinds", """{"schemaVersion":1,"id":"fixture","version":"1","environment":"server","depends":{"dep":"1.x"},"recommends":{"recommended":"*"},"suggests":{"suggested":[]},"conflicts":{"conflict":"custom"},"breaks":{"bad":"<=custom"}}""", """{"version":1,"overrides":{"fixture":{"recommends":{},"+suggests":{"extra":"1.2.x"},"-breaks":{"bad":"*"}}}}"""),
            ("legacy-override", """{"schemaVersion":0,"id":"fixture","version":"1","requires":{"dep":"*"},"conflicts":{"old":"*"},"side":"unknown"}""", """{"version":1,"overrides":{"fixture":{"depends":{},"breaks":{},"+recommends":{"dep":"*"}}}}""")
        };
        foreach (var(name, metadata, overrides)in cases)
        {
            var folder = Path.Combine(root, "oracle", name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "metadata.json"),
                metadata
            );
            File.WriteAllText(
                Path.Combine(folder, "fabric_loader_dependencies.json"),
                overrides
            );
            File.WriteAllLines(
                Path.Combine(folder, "versions.txt"),
                versions
            );
            var parsed = FabricMetadataParser.Parse(metadata);
            var rules = FabricOverrideRules.Parse(overrides)
                .Apply(parsed.Id, parsed.Dependencies);
            var expected = rules.Select(
                rule => rule.Kind + ":" + rule.Id + ":" + string.Concat(
                    versions.Select(
                        v => rule.Matchers.Any(
                            p => FabricVersions.Matches(v, p)
                        )
                            ? "1"
                            : "0"
                    )
                )
            )
                .Concat(
                    [
                        "schema=" + parsed.Schema,
                        "client=" + (parsed.Environment != "server").ToString()
                            .ToLowerInvariant()
                    ]
                )
                .Order(StringComparer.Ordinal)
                .ToArray();
            var start = new ProcessStartInfo(java)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[]
            {
                "-cp",
                loaderJar,
                Path.GetFullPath("tests/Pcl.Tests/Fabric037Oracle.java"),
                folder
            }

            )
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                throw new Exception("Official oracle failed: " + error);
            }

            var official = File.ReadAllLines(
                Path.Combine(folder, "official.txt")
            );
            File.WriteAllLines(
                Path.Combine(folder, "port.txt"),
                expected
            );
            Check(
                expected.SequenceEqual(official),
                "official-loader-oracle-" + name
            );
        }

        foreach (var malformed in new[]
        {
            "{}",
            "{\"overrides\":{},\"version\":1}",
            "{\"version\":2}",
            "{\"version\":1,\"overrides\":{\"a\":{\"+depends\":{\"b\":4}}}}",
            "{\"version\":1,\"overrides\":{\"a\":{\"++depends\":{}}}}"
        }

        )
        {
            await Reject(
                () =>
                {
                    FabricOverrideRules.Parse(malformed);
                    return Task.CompletedTask;
                },
                "malformed-override-" + checks.Count
            );
        }

        await Reject(
            () =>
            {
                FabricMetadataParser.Parse("{\"schemaVersion\":2,\"id\":\"mod\",\"version\":\"1\"}");
                return Task.CompletedTask;
            },
            "unknown-schema-rejected"
        );
        var payload = new Dictionary<string, byte[]>();
        ResourceRelease Release(string id, string number, JObject? depends = null, bool edge = false)
        {
            var name = id + "-" + number + ".jar";
            var bytes = Jar(
                new JObject
                {
                    ["schemaVersion"] = 1,
                    ["id"] = id,
                    ["version"] = number,
                    ["depends"] = depends ?? new()
                }.ToString()
            );
            payload[name] = bytes;
            return new(
                id + number,
                id,
                id,
                number,
                "release",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                    .AddDays(
                        int.Parse(number)
                    ),
                [
                    "1.21.1"
                ],
                [
                    "fabric"
                ],
                new(
                    name,
                    new Uri("https://fixture.invalid/" + name),
                    bytes.Length,
                    Hash(bytes)
                ),
                edge
                    ? [
                        new("shared", null, "required")
                    ]
                    : []
            );
        }

        var a1 = Release("root_a", "1");
        var b1 = Release("root_b", "1");
        var a2 = Release(
            "root_a",
            "2",
            new()
            {
                ["shared"] = ">=1 <2"
            },
            true
        );
        var b2 = Release(
            "root_b",
            "2",
            new()
            {
                ["shared"] = ">=1 <3"
            },
            true
        );
        var x1 = Release("shared", "1");
        var x2 = Release("shared", "2");
        var x3 = Release("shared", "3");
        var releases = new[]
        {
            a1,
            b1,
            a2,
            b2,
            x1,
            x2,
            x3
        };
        using var catalog = new ResourceCatalog(
            new Handler(releases, payload)
        );
        using var downloads = new DownloadService(
            new Handler(releases, payload)
        );
        Check(
            (await catalog.VersionFromHashAsync(a2.File.Sha1))!.File.Sha1 == a2.File.Sha1,
            "hash-lookup-preserves-non-primary-file"
        );
        const string overrideJson = """{"version":1,"overrides":{"root_a":{"depends":{"shared":">=2 <3"}}}}""";
        GameProfile Instance(string name, params ResourceRelease[] existing)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(
                Path.Combine(directory, "mods")
            );
            Directory.CreateDirectory(
                Path.Combine(directory, "config")
            );
            JsonFiles.Write(
                Path.Combine(directory, "profile.json"),
                new
                {
                    loaderVersion = "0.19.5"
                }
            );
            JsonFiles.Write(
                Path.Combine(directory, "version.json"),
                new
                {
                    javaVersion = new
                    {
                        majorVersion = 21
                    }
                }
            );
            File.WriteAllText(
                Path.Combine(directory, FabricDependencyOverrides.RelativePath),
                overrideJson
            );
            foreach (var release in existing)
            {
                File.WriteAllBytes(
                    Path.Combine(directory, "mods", release.File.Name),
                    payload[release.File.Name]
                );
            }

            ModpackService.RegisterFiles(directory);
            return new(
                name,
                name,
                "1.21.1",
                "fabric",
                directory,
                Path.Combine(directory, "version.json")
            );
        }

        var single = Instance("single", b2);
        var plan = await ResourceDependencyPlanner.ResolveAsync(catalog, downloads, a2, ResourceKind.Mod, single);
        Check(
            plan.Releases.Single(
                x => x.ProjectId == "shared"
            )
                .Id == x2.Id,
            "single-joint-backtracks-with-override"
        );
        Check(
            Directory.GetFiles(
                Path.Combine(single.GameDirectory, "mods")
            )
                .Length == 1,
            "planning-does-not-install-live-files"
        );
        await new ResourceInstaller(single.GameDirectory, downloads, "fabric").InstallAsync(plan);
        var batch = Instance("batch", a1, b1);
        var scan = new LocalResourceService(batch.GameDirectory, "fabric", "1.21.1").Scan();
        using (var prepared = await new ResourceBatchUpdater(batch.GameDirectory, "fabric", "1.21.1").PrepareAsync(
            [
                new(
                    scan.Single(
                        x => x.ModIds.Contains("root_a")
                    ),
                    a2
                ),
                new(
                    scan.Single(
                        x => x.ModIds.Contains("root_b")
                    ),
                    b2
                )
            ],
            catalog,
            downloads
        ))
        {
            new ResourceBatchUpdater(batch.GameDirectory, "fabric", "1.21.1").Apply(prepared);
        }

        var pack = Instance("pack", a2, b2);
        await ResourceDependencyPlanner.ResolvePackAsync(catalog, downloads, pack);
        string[] Installed(GameProfile profile) => Directory.GetFiles(
            Path.Combine(profile.GameDirectory, "mods")
        )
            .Select(
                x => Hash(
                    File.ReadAllBytes(x)
                )
            )
            .Order()
            .ToArray();
        Check(
            Installed(single)
                .SequenceEqual(
                    Installed(batch)
                ) && Installed(batch)
                .SequenceEqual(
                    Installed(pack)
                ),
            "single-batch-pack-identical-final-jars"
        );
        var keep = Installed(single);
        await Reject(
            () =>
            {
                new LocalResourceService(single.GameDirectory, "fabric", "1.21.1").SetEnabled(
                    [
                        Path.Combine(single.GameDirectory, "mods", x2.File.Name)
                    ],
                    false
                );
                return Task.CompletedTask;
            },
            "shared-dependency-disable-rejected"
        );
        Check(
            keep.SequenceEqual(
                Installed(single)
            ),
            "disable-rejection-restores-original-files"
        );
        using (var noNetwork = new ResourceCatalog(
            new NoNetwork()
        ))
        {
            await ResourceDependencyPlanner.ResolvePackAsync(noNetwork, downloads, pack);
            Check(true, "complete-locked-pack-needs-no-network");
        }

        var changing = Instance("context-change", b2);
        var changed = false;
        var progress = new SynchronousProgress(
            () =>
            {
                if (!changed)
                {
                    changed = true;
                    File.WriteAllText(
                        Path.Combine(changing.GameDirectory, FabricDependencyOverrides.RelativePath),
                        "{\"version\":1}"
                    );
                }
            }
        );
        await Reject(
            () => ResourceDependencyPlanner.ResolveAsync(catalog, downloads, a2, ResourceKind.Mod, changing, progress),
            "context-change-invalidates-planning"
        );
        Check(
            Directory.GetFiles(
                Path.Combine(changing.GameDirectory, "mods")
            )
                .Length == 1,
            "context-change-leaves-live-mods"
        );
        var cancelled = Instance("cancelled", b2);
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            await Reject(
                () => ResourceDependencyPlanner.ResolveAsync(catalog, downloads, a2, ResourceKind.Mod, cancelled, ct: cts.Token),
                "cancelled-plan-no-write"
            );
        }

        var broken = Instance("broken-download", b2);
        var corrupt = new Dictionary<string, byte[]>(payload)
        {
            [a2.File.Name] = [
                1,
                2,
                3
            ]
        };
        using (var badDownload = new DownloadService(
            new Handler(releases, corrupt)
        ))
        {
            await Reject(
                () => ResourceDependencyPlanner.ResolveAsync(catalog, badDownload, a2, ResourceKind.Mod, broken),
                "integrity-failure-not-a-compatible-fallback"
            );
        }

        Check(
            Directory.GetFiles(
                Path.Combine(broken.GameDirectory, "mods")
            )
                .Length == 1,
            "failed-download-preserves-live-mods"
        );
        JsonFiles.Write(
            Path.Combine(root, "result.json"),
            new
            {
                status = "passed",
                checks,
                oracleLoader = "0.19.5",
                network = "HTTP fixture",
                realGame = false
            }
        );
        return 0;
    }

    internal static byte[] Jar(string metadata)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using var output = new StreamWriter(
                zip.CreateEntry("fabric.mod.json")
                    .Open()
            );
            output.Write(metadata);
        }

        return memory.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(
        SHA1.HashData(bytes)
    )
        .ToLowerInvariant();
    private sealed class SynchronousProgress(Action action) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => action();
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new Exception("Unexpected catalog network access");
    }

    private sealed class Handler(ResourceRelease[] releases, Dictionary<string, byte[]> payload) : HttpMessageHandler
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
            object body = parts[1] switch
            {
                "version_file" => HashWire(
                    releases.Single(
                        x => x.File.Sha1 == parts[2]
                    )
                ),
                "project" when parts.Length == 4 => releases.Where(
                    x => x.ProjectId == parts[2]
                )
                    .OrderByDescending(
                        x => x.Published
                    )
                    .Select(Wire)
                    .ToArray(),
                "project" => new
                {
                    id = parts[2],
                    slug = parts[2],
                    title = parts[2],
                    project_type = "mod"
                },
                _ => Wire(
                    releases.Single(
                        x => x.Id == parts[2]
                    )
                )
            };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonConvert.SerializeObject(body)
                    )
                }
            );
        }

        private static JObject HashWire(ResourceRelease release)
        {
            var json = JObject.FromObject(
                Wire(release)
            );
            var files = (JArray)json["files"]!;
            files[0]["primary"] = false;
            files.Insert(
                0,
                new JObject
                {
                    ["filename"] = "different-primary.jar",
                    ["url"] = "https://fixture.invalid/different-primary.jar",
                    ["size"] = 1,
                    ["primary"] = true,
                    ["hashes"] = new JObject
                    {
                        ["sha1"] = new string ('0', 40)
                    }
                }
            );
            return json;
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
