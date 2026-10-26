using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using Pcl.Core;
using Pcl.Domain;
using Pcl.Core.Application.Diagnostics;
using Pcl.Core.Contracts.Diagnostics;

/// <summary>Exercises archive graphs, the actual official resolver, all three install entry points and bounded diagnostics.</summary>
internal static class Nested038Tests
{
    internal static byte[] Jar(
        string id,
        string version,
        JObject? depends = null,
        byte[][]? children = null,
        JObject? extra = null
    )
    {
        var metadata = new JObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["version"] = version,
            ["depends"] = depends ?? new()
        };
        if (extra is not null)
        {
            foreach (var property in extra.Properties())
                metadata[property.Name] = property.Value.DeepClone();
        }

        children ??= [];
        metadata["jars"] = new JArray(
            children.Select(
                (_, i) => new JObject
                {
                    ["file"] = $"nested/{i}.jar"
                }
            )
        );
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("fabric.mod.json");
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (var writer = new StreamWriter(
                entry.Open()
            ))
                writer.Write(metadata);
            for (var i = 0; i < children.Length; i++)
            {
                var nested = zip.CreateEntry($"nested/{i}.jar");
                nested.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = nested.Open();
                stream.Write(children[i]);
            }
        }

        return memory.ToArray();
    }

    internal static readonly Dictionary<string, string> Builtins = new()
    {
        ["minecraft"] = "1.21.1",
        ["java"] = "21",
        ["fabricloader"] = "0.19.5"
    };
    public static async Task<int> Run(string root, string java, string loaderJar)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root))
            throw new InvalidOperationException("Use a fresh directory.");
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
                throw new Exception(name);
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }

        var classes = Path.Combine(root, "oracle-classes");
        Directory.CreateDirectory(classes);
        async Task RunJava(string executable, params string[] args)
        {
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new Exception("Official resolver failed: " + error);
        }

        await RunJava(
            Path.Combine(
                Path.GetDirectoryName(java)!,
                "javac"
            ),
            "-cp",
            loaderJar,
            "-d",
            classes,
            Path.GetFullPath("tests/Pcl.Tests/Fabric038Oracle.java")
        );
        async Task<FabricResolution> Case(string name, string expected, params byte[][] jars)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(
                Path.Combine(folder, "mods")
            );
            Directory.CreateDirectory(
                Path.Combine(folder, "config")
            );
            for (var i = 0; i < jars.Length; i++)
                File.WriteAllBytes(
                    Path.Combine(folder, "mods", $"root-{i}.jar"),
                    jars[i]
                );
            var result = FabricDependencySet.Resolve(
                Directory.GetFiles(
                    Path.Combine(folder, "mods")
                ),
                folder,
                Builtins
            );
            Check(result.Status == expected, name + "-port");
            await RunJava(java, "-cp", classes + Path.PathSeparator + loaderJar, "Fabric038Oracle", folder);
            var official = File.ReadAllLines(
                Path.Combine(folder, "official.txt")
            );
            Check(official[0] == expected, name + "-official-loader");
            JsonFiles.Write(
                Path.Combine(folder, "port.json"),
                result
            );
            return result;
        }

        var low = Jar("shared", "1.0.0");
        var high = Jar("shared", "2.0.0");
        var container = Jar(
            "container",
            "1",
            children: [
                low,
                high
            ]
        );
        var consumer = Jar(
            "consumer",
            "1",
            new()
            {
                ["shared"] = "<2"
            }
        );
        var older = await Case("older-valid-version", "satisfied", container, consumer);
        Check(
            older.Selected.Single(
                x => x.Id == "shared"
            )
                .Version == "1.0.0",
            "older-version-selected"
        );
        var poison = Jar(
            "shared",
            "2.0.0",
            new()
            {
                ["missing_mod"] = "*"
            }
        );
        var ignored = await Case(
            "unselected-broken-copy",
            "satisfied",
            Jar(
                "container",
                "1",
                children: [
                    low,
                    poison
                ]
            ),
            consumer
        );
        Check(
            ignored.Selected.All(
                x => x.Id != "shared" || x.Version == "1.0.0"
            ),
            "unselected-dependencies-not-enforced"
        );
        var parentBad = Jar(
            "parent",
            "2",
            new()
            {
                ["missing_mod"] = "*"
            },
            [
                Jar("child", "1")
            ]
        );
        await Case(
            "orphan-child",
            "unsatisfiable",
            Jar(
                "container",
                "1",
                children: [
                    parentBad
                ]
            ),
            Jar(
                "consumer",
                "1",
                new()
                {
                    ["child"] = "*"
                }
            )
        );
        var shared = await Case(
            "shared-byte-identical-child",
            "satisfied",
            Jar(
                "parent_a",
                "1",
                children: [
                    low
                ]
            ),
            Jar(
                "parent_b",
                "1",
                children: [
                    low
                ]
            ),
            consumer
        );
        Check(
            shared.Selected.Single(
                x => x.Id == "shared"
            )
                .Parents.Count == 2,
            "shared-child-retains-both-parents"
        );
        var sameVersionBad = Jar(
            "shared",
            "1.0.0",
            new()
            {
                ["missing_mod"] = "*"
            }
        );
        await Case(
            "same-version-different-bytes",
            "satisfied",
            Jar(
                "container",
                "1",
                children: [
                    sameVersionBad,
                    low
                ]
            ),
            consumer
        );
        await Case(
            "alias-provider",
            "satisfied",
            Jar(
                "container",
                "1",
                children: [
                    Jar(
                        "provider",
                        "1",
                        extra: new()
                        {
                            ["provides"] = new JArray("alias_mod")
                        }
                    )
                ]
            ),
            Jar(
                "consumer",
                "1",
                new()
                {
                    ["alias_mod"] = "*"
                }
            )
        );
        await Case(
            "alias-collision",
            "unsatisfiable",
            Jar(
                "provider_a",
                "1",
                extra: new()
                {
                    ["provides"] = new JArray("alias_mod")
                }
            ),
            Jar(
                "provider_b",
                "1",
                extra: new()
                {
                    ["provides"] = new JArray("alias_mod")
                }
            )
        );
        await Case(
            "optional-builtin-shadow",
            "unsatisfiable",
            Jar(
                "container",
                "1",
                children: [
                    Jar(
                        "provider",
                        "1",
                        extra: new()
                        {
                            ["provides"] = new JArray("minecraft")
                        }
                    )
                ]
            )
        );
        await Case(
            "hard-break",
            "satisfied",
            container,
            Jar(
                "consumer",
                "1",
                new()
                {
                    ["shared"] = "*"
                },
                extra: new()
                {
                    ["breaks"] = new JObject
                    {
                        ["shared"] = ">=2"
                    }
                }
            )
        );
        await Case(
            "dependency-or",
            "satisfied",
            container,
            Jar(
                "consumer",
                "1",
                new()
                {
                    ["shared"] = new JArray("<1", ">=2")
                }
            )
        );
        await Case(
            "dependency-cycle",
            "satisfied",
            Jar(
                "container",
                "1",
                children: [
                    Jar(
                        "cycle_a",
                        "1",
                        new()
                        {
                            ["cycle_b"] = "*"
                        }
                    ),
                    Jar(
                        "cycle_b",
                        "1",
                        new()
                        {
                            ["cycle_a"] = "*"
                        }
                    )
                ]
            ),
            Jar(
                "consumer",
                "1",
                new()
                {
                    ["cycle_a"] = "*"
                }
            )
        );
        await Case(
            "server-disabled-relaxation",
            "satisfied",
            Jar(
                "server_mod",
                "1",
                extra: new()
                {
                    ["environment"] = "server"
                }
            ),
            Jar(
                "consumer",
                "1",
                new()
                {
                    ["server_mod"] = "*"
                }
            )
        );
        await Case(
            "server-parent-children-ignored",
            "unsatisfiable",
            Jar(
                "server_mod",
                "1",
                children: [
                    low
                ],
                extra: new()
                {
                    ["environment"] = "server"
                }
            ),
            consumer
        );
        var conflict = await Case(
            "incompatible-locked-roots",
            "unsatisfiable",
            container,
            consumer,
            Jar(
                "other_root",
                "1",
                new()
                {
                    ["shared"] = ">=2"
                }
            )
        );
        Check(
            conflict.Conflicts.Any(
                x => x.Source.StartsWith("consumer@")
            ) && conflict.Conflicts.Any(
                x => x.Source.StartsWith("other_root@")
            ),
            "conflict-traces-both-root-constraints"
        );
        Exception? failure = null;
        try
        {
            FabricDependencySet.Require(conflict);
        }
        catch (InvalidDataException ex)
        {
            failure = ex;
        }

        var diagnostic = DependencyFailureMapper.From(
            new InvalidDataException("wrapper", failure)
        );
        Check(
            diagnostic is { Code: "DEPENDENCY_CONFLICT" } && diagnostic.Lines.Count > 1 && !diagnostic.DisplayText.Contains(root),
            "bounded-structured-diagnostic-without-host-paths"
        );
        var wire = JsonSerializer.Serialize(diagnostic);
        Check(
            JsonSerializer.Deserialize<DependencyDiagnostic>(wire)!.DisplayText == diagnostic!.DisplayText && !wire.Contains("DisplayText"),
            "diagnostic-contract-round-trip"
        );
        var duplicate = new[]
        {
            new FabricCandidate(
                "a",
                "duplicate",
                "1",
                [],
                [],
                [],
                "a.jar",
                true
            ),
            new FabricCandidate(
                "b",
                "duplicate",
                "2",
                [],
                [],
                [],
                "b.jar",
                true
            )
        };
        Check(
            FabricCandidateSolver.Resolve(duplicate)
                .Status == "unsatisfiable",
            "explicit-root-lock-policy-rejects-duplicate-roots"
        );
        var limited = FabricCandidateSolver.Resolve(
            older.Selected,
            limits: new(Candidates: 1)
        );
        Check(limited.Status == "limit", "budget-is-not-reported-as-conflict");
        Check(
            DependencyFailureMapper.From(
                new FabricResolutionException(limited)
            )!.Code == "DEPENDENCY_SEARCH_LIMIT",
            "budget-diagnostic-distinct"
        );
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel();
            try
            {
                FabricCandidateSolver.Resolve(older.Selected, cts.Token);
                throw new Exception("Cancellation missing");
            }
            catch (OperationCanceledException)
            {
                Check(true, "solver-cancellation");
            }
        }

        // Force a branch rather than only unit propagation: optional version 2 leads to an impossible cycle.
        var backtrack = await Case(
            "search-backtracking",
            "satisfied",
            Jar(
                "container",
                "1",
                children: [
                    Jar(
                        "a_choice",
                        "2",
                        new()
                        {
                            ["b_peer"] = "*"
                        },
                        extra: new()
                        {
                            ["breaks"] = new JObject
                            {
                                ["c_peer"] = "*"
                            }
                        }
                    ),
                    Jar("a_choice", "1"),
                    Jar(
                        "b_peer",
                        "1",
                        new()
                        {
                            ["c_peer"] = "*"
                        }
                    ),
                    Jar("c_peer", "1")
                ]
            ),
            Jar(
                "consumer",
                "1",
                new()
                {
                    ["a_choice"] = "*"
                }
            )
        );
        Check(
            backtrack.SearchNodes > 1 && backtrack.Selected.Single(
                x => x.Id == "a_choice"
            )
                .Version == "1",
            "bounded-search-backtracks-to-valid-choice"
        );
        await EntryPoints(root, checks);
        JsonFiles.Write(
            Path.Combine(root, "result.json"),
            new
            {
                status = "passed",
                checks,
                officialResolver = "Fabric Loader 0.19.5",
                realGame = false
            }
        );
        return 0;
    }

    private static async Task EntryPoints(string root, List<string> checks)
    {
        var bytes = new Dictionary<string, byte[]>();
        ResourceRelease Release(string id, string version, byte[] jar)
        {
            var name = id + "-" + version + ".jar";
            bytes[name] = jar;
            return new(
                id + version,
                id,
                id,
                version,
                "release",
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                [
                    "1.21.1"
                ],
                [
                    "fabric"
                ],
                new(
                    name,
                    new Uri("https://fixture.invalid/" + name),
                    jar.Length,
                    Hash(jar)
                ),
                []
            );
        }

        var a1 = Release(
            "pcl038_bundle",
            "1",
            Jar("pcl038_bundle", "1")
        );
        var b1 = Release(
            "pcl038_consumer",
            "1",
            Jar("pcl038_consumer", "1")
        );
        var a2 = Release(
            "pcl038_bundle",
            "2",
            Jar(
                "pcl038_bundle",
                "2",
                children: [
                    Jar("pcl038_shared", "1"),
                    Jar(
                        "pcl038_shared",
                        "2",
                        new()
                        {
                            ["missing_mod"] = "*"
                        }
                    )
                ]
            )
        );
        var b2 = Release(
            "pcl038_consumer",
            "2",
            Jar(
                "pcl038_consumer",
                "2",
                new()
                {
                    ["pcl038_shared"] = "<2"
                }
            )
        );
        using var downloads = new DownloadService(
            new FilesOnly(bytes)
        );
        using var catalog = new ResourceCatalog(
            new FilesOnly(bytes)
        );
        GameProfile Instance(string name, params ResourceRelease[] installed)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(
                Path.Combine(dir, "mods")
            );
            JsonFiles.Write(
                Path.Combine(dir, "profile.json"),
                new
                {
                    loaderVersion = "0.19.5"
                }
            );
            JsonFiles.Write(
                Path.Combine(dir, "version.json"),
                new
                {
                    javaVersion = new
                    {
                        majorVersion = 21
                    }
                }
            );
            foreach (var release in installed)
                File.WriteAllBytes(
                    Path.Combine(dir, "mods", release.File.Name),
                    bytes[release.File.Name]
                );
            ModpackService.RegisterFiles(dir);
            return new(
                name,
                name,
                "1.21.1",
                "fabric",
                dir,
                Path.Combine(dir, "version.json")
            );
        }

        var single = Instance("single", b2);
        var plan = await ResourceDependencyPlanner.ResolveAsync(catalog, downloads, a2, ResourceKind.Mod, single);
        await new ResourceInstaller(single.GameDirectory, downloads, "fabric").InstallAsync(plan);
        checks.Add("single-install-nested-candidates");
        var batch = Instance("batch", a1, b1);
        var service = new ResourceBatchUpdater(batch.GameDirectory, "fabric", "1.21.1");
        var scan = new LocalResourceService(batch.GameDirectory, "fabric", "1.21.1").Scan();
        using (var prepared = await service.PrepareAsync(
            [
                new(
                    scan.Single(
                        x => x.ModIds.Contains("pcl038_bundle")
                    ),
                    a2
                ),
                new(
                    scan.Single(
                        x => x.ModIds.Contains("pcl038_consumer")
                    ),
                    b2
                )
            ],
            catalog,
            downloads
        ))
            service.Apply(prepared);
        checks.Add("batch-update-nested-candidates");
        var pack = Instance("pack", a2, b2);
        await ResourceDependencyPlanner.ResolvePackAsync(catalog, downloads, pack);
        checks.Add("complete-pack-no-catalog-access");
        string[] Set(GameProfile profile) => Directory.GetFiles(
            Path.Combine(profile.GameDirectory, "mods")
        )
            .Select(
                x => Hash(
                    File.ReadAllBytes(x)
                )
            )
            .Order()
            .ToArray();
        if (!Set(single)
            .SequenceEqual(
                Set(batch)
            ) || !Set(batch)
            .SequenceEqual(
                Set(pack)
            ))
            throw new Exception("Entry point drift");
        checks.Add("three-entry-points-identical-archives");
        var before = Set(single);
        var bad = Release(
            "pcl038_consumer",
            "3",
            Jar(
                "pcl038_consumer",
                "3",
                new()
                {
                    ["pcl038_shared"] = ">=3"
                }
            )
        );
        try
        {
            await ResourceDependencyPlanner.ResolveAsync(catalog, downloads, bad, ResourceKind.Mod, single);
            throw new Exception("Conflict was accepted");
        }
        catch (InvalidDataException ex)
        {
            if (DependencyFailureMapper.From(ex)is null)
                throw new Exception("Conflict evidence was lost", ex);
            checks.Add("planning-conflict-retains-structured-evidence");
        }

        if (!Set(single)
            .SequenceEqual(before))
            throw new Exception("Failed plan modified live files");
        checks.Add("failed-plan-preserves-live-files");
        // Preserve fixtures for real Host and gameplay checks; these contain metadata only, no executable mod code.
        foreach (var release in new[]
        {
            a2,
            b2,
            bad
        }

        )
            File.WriteAllBytes(
                Path.Combine(root, release.File.Name),
                bytes[release.File.Name]
            );
    }

    private static string Hash(byte[] data) => Convert.ToHexString(
        SHA1.HashData(data)
    )
        .ToLowerInvariant();
    private sealed class FilesOnly(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var url = request.RequestUri!;
            if (url.Host != "fixture.invalid")
                throw new Exception("Unexpected catalog access: " + url.AbsolutePath);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        files[Path.GetFileName(url.AbsolutePath)]
                    )
                }
            );
        }
    }
}
