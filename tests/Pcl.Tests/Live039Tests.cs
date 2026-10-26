using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Pcl.Core;

/// <summary>Real HTTPS installs into an isolated data root; never reads the developer's accounts or saves.</summary>
internal static class Live039Tests
{
    public static async Task<int> Run(string dataRoot)
    {
        var paths = new AppPaths(dataRoot);
        using var service = new MinecraftService(paths);
        using var catalog = new ResourceCatalog(paths: paths);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidDataException(name);
            }

            checks.Add(name);
            Console.WriteLine("PASS " + name);
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "real-installs.json"),
                new
                {
                    checks
                }
            );
        }

        var progress = new InlineProgress(
            p => Console.WriteLine(p.Stage)
        );
        await service.InstallAsync(progress);
        using var fabric = new FabricService(service, "PCL039-Single", "quilt");
        var single = await fabric.InstallAsync("0.30.1", progress);
        var releases = await catalog.VersionsAsync("continuity", ResourceKind.Mod, "1.21.1", "fabric");
        var latest = releases.First(
            x => x.Channel == "release"
        );
        var older = releases.First(
            x => x.Id != latest.Id
        );
        AddContext(single.GameDirectory);
        var plan = await ResourceDependencyPlanner.ResolveAsync(catalog, service.Downloads, older, ResourceKind.Mod, single, progress);
        await new ResourceInstaller(single.GameDirectory, service.Downloads, "quilt", "1.21.1").InstallAsync(plan, progress);
        Check(plan.Releases.Count > 1, "public-single-download-installs-required-fabric-api");
        var before = new LocalResourceService(single.GameDirectory, "quilt", "1.21.1").Scan();
        var current = before.Single(
            x => x.ModIds.Contains("continuity")
        );
        var updater = new ResourceBatchUpdater(single.GameDirectory, "quilt", "1.21.1");
        using (var prepared = await updater.PrepareAsync(
            [
                new(current, latest)
            ],
            catalog,
            service.Downloads,
            progress
        ))
        {
            Check(
                new LocalResourceService(single.GameDirectory, "quilt", "1.21.1").Scan()
                    .Single(
                        x => x.ModIds.Contains("continuity")
                    )
                    .Sha1 == current.Sha1,
                "batch-prepare-keeps-installed-release"
            );
            updater.Apply(prepared);
        }

        Check(
            new LocalResourceService(single.GameDirectory, "quilt", "1.21.1").Scan()
                .Single(
                    x => x.ModIds.Contains("continuity")
                )
                .Sha1 == latest.File.Sha1,
            "public-batch-update-installs-selected-release"
        );
        Check(
            !Directory.GetFiles(
                Path.Combine(single.GameDirectory, "mods")
            )
                .Any(
                    x => Path.GetFileName(x) == older.File.Name
                ),
            "batch-removes-replaced-release"
        );
        // The test pack pins the real downloaded Continuity file and intentionally omits its required Fabric API.
        // Overrides and nested metadata exercise the same path in actual Quilt Loader later.
        var source = Path.Combine(single.GameDirectory, "mods", latest.File.Name);
        var bytes = File.ReadAllBytes(source);
        var nested = Fixtures();
        var pack = Path.Combine(paths.Root, "PCL039-Controlled.mrpack");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
        {
            Write(
                zip,
                "modrinth.index.json",
                System.Text.Encoding.UTF8.GetBytes(
                    new JObject
                    {
                        ["formatVersion"] = 1,
                        ["game"] = "minecraft",
                        ["versionId"] = "0.39-test",
                        ["name"] = "PCL039 Controlled Public Resources",
                        ["dependencies"] = new JObject
                        {
                            ["minecraft"] = "1.21.1",
                            ["quilt-loader"] = "0.30.1"
                        },
                        ["files"] = new JArray(
                            new JObject
                            {
                                ["path"] = "mods/" + latest.File.Name,
                                ["hashes"] = new JObject
                                {
                                    ["sha1"] = latest.File.Sha1,
                                    ["sha512"] = Convert.ToHexString(
                                        SHA512.HashData(bytes)
                                    )
                                        .ToLowerInvariant()
                                },
                                ["env"] = new JObject
                                {
                                    ["client"] = "required",
                                    ["server"] = "unsupported"
                                },
                                ["downloads"] = new JArray(
                                    latest.File.Url.ToString()
                                ),
                                ["fileSize"] = latest.File.Size
                            }
                        )
                    }.ToString()
                )
            );
            foreach (var fixture in nested)
            {
                Write(zip, "overrides/mods/" + fixture.Key, fixture.Value);
            }

            Write(
                zip,
                "overrides/" + QuiltDependencyOverrides.RelativePath,
                System.Text.Encoding.UTF8.GetBytes(Overrides)
            );
        }

        var packed = await new ModpackService(service).InstallAsync(pack, "PCL039-Pack", progress: progress);
        var installed = new LocalResourceService(packed.GameDirectory, "quilt", "1.21.1").Scan();
        Check(
            installed.Any(
                x => x.ModIds.Contains("fabric-api")
            ),
            "real-modpack-download-provisions-omitted-required-api"
        );
        Check(
            installed.Single(
                x => x.ModIds.Contains("continuity")
            )
                .Sha1 == latest.File.Sha1,
            "pack-preserves-exact-pinned-root"
        );
        var singleHashes = Directory.GetFiles(
            Path.Combine(single.GameDirectory, "mods")
        )
            .Select(Hash)
            .Order()
            .ToArray();
        var packHashes = Directory.GetFiles(
            Path.Combine(packed.GameDirectory, "mods")
        )
            .Select(Hash)
            .Order()
            .ToArray();
        Check(
            singleHashes.SequenceEqual(packHashes),
            "real-single-batch-pack-final-hashes-identical"
        );
        new LocalResourceService(packed.GameDirectory, "quilt", "1.21.1").ValidateEnabledResources();
        JsonFiles.Write(
            Path.Combine(paths.Evidence, "public-resources.json"),
            new
            {
                latest.Id,
                latest.Number,
                latest.File.Sha1,
                previous = older.Id,
                profiles = new[]
                {
                    single.Id,
                    packed.Id
                },
                files = installed.Select(
                    x => new
                    {
                        name = Path.GetFileName(x.Path),
                        x.Sha1,
                        x.Size
                    }
                )
                    .ToArray(),
                fixturePack = "controlled pack, public Continuity and Fabric API plus path/pattern/Fabric/nested Quilt override metadata fixtures"
            }
        );
        return 0;
    }

    // Native and Fabric fixtures have no entrypoints or game code; their deliberately missing requirements can only pass through the tested override rules.
    private const string Overrides = """{"schema_version":1,"overrides":[{"path":"<mods>/pcl039_native.jar","depends":{"remove":"missing_native"},"version":"999"},{"pattern":"\\Qpcl039_native\\E","depends":{"add":{"id":"loader.fabric:pcl039_fabric","versions":"=1.0.0"}}},{"path":"<mods>/pcl039_outer.jar!/lib/child.jar","depends":{"remove":"missing_child"}},{"path":"<mods>/pcl039_fabric.jar","depends":[{"remove":"missing_fabric"},{"add":{"id":"java","versions":"21"}}]},{"id":"pcl039_outer","depends":[{"add":{"id":"pcl039_native","versions":"=1.0.0"}},{"add":{"id":"pcl039_child","versions":"=1.0.0"}}]}]}""";
    private static void AddContext(string directory)
    {
        Directory.CreateDirectory(
            Path.Combine(directory, "config")
        );
        File.WriteAllText(
            Path.Combine(directory, QuiltDependencyOverrides.RelativePath),
            Overrides
        );
        Directory.CreateDirectory(
            Path.Combine(directory, "mods")
        );
        foreach (var fixture in Fixtures())
            File.WriteAllBytes(
                Path.Combine(directory, "mods", fixture.Key),
                fixture.Value
            );
    }

    private static Dictionary<string, byte[]> Fixtures()
    {
        byte[] Native(string id, string missing, byte[]? nested = null)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                var metadata = new JObject
                {
                    ["schema_version"] = 1,
                    ["quilt_loader"] = new JObject
                    {
                        ["id"] = id,
                        ["group"] = "example",
                        ["intermediate_mappings"] = "net.fabricmc:intermediary",
                        ["version"] = "1.0.0",
                        ["metadata"] = new JObject
                        {
                            ["name"] = id
                        },
                        ["depends"] = missing.Length == 0
                            ? new JArray()
                            : new JArray(
                                new JObject
                                {
                                    ["id"] = missing,
                                    ["versions"] = ">=2"
                                }
                            )
                    }
                };
                if (nested is not null)
                {
                    metadata["quilt_loader"]!["jars"] = new JArray("lib/child.jar");
                    Write(zip, "lib/child.jar", nested);
                }

                Write(
                    zip,
                    "quilt.mod.json",
                    System.Text.Encoding.UTF8.GetBytes(
                        metadata.ToString()
                    )
                );
            }

            return memory.ToArray();
        }

        byte[] Fabric(string metadata)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                Write(
                    zip,
                    "fabric.mod.json",
                    System.Text.Encoding.UTF8.GetBytes(metadata)
                );
            return memory.ToArray();
        }

        return new()
        {
            ["pcl039_native.jar"] = Native("pcl039_native", "missing_native"),
            ["pcl039_outer.jar"] = Native(
                "pcl039_outer",
                "",
                Native("pcl039_child", "missing_child")
            ),
            ["pcl039_fabric.jar"] = Fabric(
                """{"schemaVersion":1,"id":"pcl039_fabric","version":"1.0.0","depends":{"missing_fabric":">=2"}}"""
            )
        };
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string Hash(string file) => Convert.ToHexString(
        SHA256.HashData(
            File.ReadAllBytes(file)
        )
    )
        .ToLowerInvariant();
    private sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }
}
