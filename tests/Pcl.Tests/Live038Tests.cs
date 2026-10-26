using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Pcl.Core;

/// <summary>Real HTTPS installs into an isolated data root; never reads the developer's accounts or saves.</summary>
internal static class Live038Tests
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
        using var fabric = new FabricService(service, "PCL038-Single");
        var single = await fabric.InstallAsync("0.19.5", progress);
        var releases = await catalog.VersionsAsync("continuity", ResourceKind.Mod, "1.21.1", "fabric");
        var latest = releases.First(
            x => x.Channel == "release"
        );
        var older = releases.First(
            x => x.Id != latest.Id
        );
        AddContext(single.GameDirectory);
        var plan = await ResourceDependencyPlanner.ResolveAsync(catalog, service.Downloads, older, ResourceKind.Mod, single, progress);
        await new ResourceInstaller(single.GameDirectory, service.Downloads, "fabric", "1.21.1").InstallAsync(plan, progress);
        Check(plan.Releases.Count > 1, "public-single-download-installs-required-fabric-api");
        var before = new LocalResourceService(single.GameDirectory, "fabric", "1.21.1").Scan();
        var current = before.Single(
            x => x.ModIds.Contains("continuity")
        );
        var updater = new ResourceBatchUpdater(single.GameDirectory, "fabric", "1.21.1");
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
                new LocalResourceService(single.GameDirectory, "fabric", "1.21.1").Scan()
                    .Single(
                        x => x.ModIds.Contains("continuity")
                    )
                    .Sha1 == current.Sha1,
                "batch-prepare-keeps-installed-release"
            );
            updater.Apply(prepared);
        }

        Check(
            new LocalResourceService(single.GameDirectory, "fabric", "1.21.1").Scan()
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
        // Legacy schema-0 metadata and a supported override exercise the same path in the actual Loader later.
        var source = Path.Combine(single.GameDirectory, "mods", latest.File.Name);
        var bytes = File.ReadAllBytes(source);
        var legacy = LegacyJar();
        var nested = NestedFixtures();
        var pack = Path.Combine(paths.Root, "PCL038-Controlled.mrpack");
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
                        ["versionId"] = "0.38-test",
                        ["name"] = "PCL038 Controlled Public Resources",
                        ["dependencies"] = new JObject
                        {
                            ["minecraft"] = "1.21.1",
                            ["fabric-loader"] = "0.19.5"
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
            Write(zip, "overrides/mods/pcl038_legacy.jar", legacy);
            foreach (var fixture in nested)
            {
                Write(zip, "overrides/mods/" + fixture.Key, fixture.Value);
            }

            Write(
                zip,
                "overrides/config/fabric_loader_dependencies.json",
                System.Text.Encoding.UTF8.GetBytes(Overrides)
            );
        }

        var packed = await new ModpackService(service).InstallAsync(pack, "PCL038-Pack", progress: progress);
        var installed = new LocalResourceService(packed.GameDirectory, "fabric", "1.21.1").Scan();
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
        new LocalResourceService(packed.GameDirectory, "fabric", "1.21.1").ValidateEnabledResources();
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
                fixturePack = "controlled pack, public Continuity and Fabric API plus schema-0 and nested multi-version metadata fixtures"
            }
        );
        return 0;
    }

    private const string Overrides = """{"version":1,"overrides":{"continuity":{"+depends":{"java":">=21"}},"pcl038_legacy":{"+depends":{"minecraft":"1.21.x"}}}}""";
    private static void AddContext(string directory)
    {
        Directory.CreateDirectory(
            Path.Combine(directory, "config")
        );
        File.WriteAllText(
            Path.Combine(directory, "config/fabric_loader_dependencies.json"),
            Overrides
        );
        Directory.CreateDirectory(
            Path.Combine(directory, "mods")
        );
        File.WriteAllBytes(
            Path.Combine(directory, "mods/pcl038_legacy.jar"),
            LegacyJar()
        );
        foreach (var fixture in NestedFixtures())
        {
            File.WriteAllBytes(
                Path.Combine(directory, "mods", fixture.Key),
                fixture.Value
            );
        }

        ModpackService.RegisterFiles(directory);
    }

    // Both archives contain metadata only. The real Loader must reject version 2's missing dependency and choose version 1.
    private static Dictionary<string, byte[]> NestedFixtures() => new()
    {
        ["pcl038_bundle.jar"] = Nested038Tests.Jar(
            "pcl038_bundle",
            "1.0.0",
            children: [
                Nested038Tests.Jar("pcl038_shared", "1.0.0"),
                Nested038Tests.Jar(
                    "pcl038_shared",
                    "2.0.0",
                    new()
                    {
                        ["pcl038_missing"] = "*"
                    }
                )
            ]
        ),
        ["pcl038_consumer.jar"] = Nested038Tests.Jar(
            "pcl038_consumer",
            "1.0.0",
            new()
            {
                ["pcl038_shared"] = "<2"
            }
        )
    };
    private static byte[] LegacyJar()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Write(
                zip,
                "fabric.mod.json",
                System.Text.Encoding.UTF8.GetBytes(
                    """{"id":"pcl038_legacy","version":"1.0.0","name":"PCL038 Legacy Metadata Fixture","side":"universal","requires":{"minecraft":"1.21.x","java":">=21"},"recommends":{"nonessential_fixture":"*"}}"""
                )
            );
        }

        return memory.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static string Hash(string file) => Convert.ToHexString(
        SHA256.HashData(
            File.ReadAllBytes(file)
        )
    );
    private sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        private string last = "";
        public void Report(InstallProgress progress)
        {
            if (progress.Stage != last)
            {
                last = progress.Stage;
                report(progress);
            }
        }
    }
}
