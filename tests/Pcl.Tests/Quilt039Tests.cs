using System.Diagnostics;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Pcl.Core;
using Pcl.Core.Adapters.Compatibility;

/// <summary>Compare the adapter with real Quilt classes, then exercise actual transaction and planning paths.</summary>
internal static class Quilt039Tests
{
    public static async Task<int> Run(string output, string java, string loader, string json5)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output))
            throw new InvalidOperationException("Use a fresh output directory.");
        Directory.CreateDirectory(output);
        java = Path.GetFullPath(java);
        loader = Path.GetFullPath(loader);
        var runtime = new QuiltOverrideRuntime(java, loader);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
                throw new Exception(name);
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }

        static JObject Dep(string id, string range = "*") => new()
        {
            ["id"] = id,
            ["versions"] = range
        };
        static JObject Rule(
            string kind,
            string selector,
            JToken? depends = null,
            JToken? breaks = null,
            string? version = null
        )
        {
            var row = new JObject
            {
                [kind] = selector
            };
            if (depends is not null)
                row["depends"] = depends;
            if (breaks is not null)
                row["breaks"] = breaks;
            if (version is not null)
                row["version"] = version;
            return row;
        }

        static JObject Config(IEnumerable<JObject> rules) => new()
        {
            ["schema_version"] = 1,
            ["overrides"] = new JArray(rules)
        };
        static JObject Set(JArray depends, JArray breaks) => Config(
            [
                Rule(
                    "id",
                    "input",
                    new JArray(
                        depends.Select(
                            x => new JObject
                            {
                                ["add"] = x.DeepClone()
                            }
                        )
                    ),
                    new JArray(
                        breaks.Select(
                            x => new JObject
                            {
                                ["add"] = x.DeepClone()
                            }
                        )
                    )
                )
            ]
        );
        static QuiltCandidate Candidate(string source, JArray? deps = null, bool fabric = false) => new(
            source,
            "application",
            "example",
            "1.0.0",
            true,
            "always",
            "*",
            fabric,
            deps ?? [],
            [],
            []
        );
        var oracle = Path.GetFullPath("tests/Pcl.Tests/Quilt039Oracle.java");
        async Task<QuiltCandidate> Case(
            string name,
            QuiltCandidate input,
            JObject[] rules,
            int expectedDepends,
            string? expectedVersion = null
        )
        {
            var dir = Path.Combine(output, name);
            Directory.CreateDirectory(
                Path.Combine(dir, "config")
            );
            var source = input.Source.StartsWith("nested:")
                ? Path.Combine(dir, "mods/outer.jar") + "!/lib/application.jar"
                : Path.Combine(dir, "mods/Application.jar");
            input = input with
            {
                Source = source
            };
            JsonFiles.Write(
                Path.Combine(dir, QuiltDependencyOverrides.RelativePath),
                Config(rules)
            );
            JsonFiles.Write(
                Path.Combine(dir, "input.json"),
                Set(input.Depends, input.Breaks)
            );
            if (input.Fabric)
            {
                JObject Map(JArray values) => new(
                    values.Select(
                        x => new JProperty(
                            (string)x["id"]!,
                            x["versions"]!.DeepClone()
                        )
                    )
                );
                JsonFiles.Write(
                    Path.Combine(dir, "fabric.mod.json"),
                    new JObject
                    {
                        ["schemaVersion"] = 1,
                        ["id"] = input.Id,
                        ["version"] = input.Version,
                        ["depends"] = Map(input.Depends),
                        ["breaks"] = Map(input.Breaks)
                    }
                );
            }

            var warnings = new List<string>();
            var result = QuiltDependencyOverrides.Read(dir, runtime: runtime)
                .Apply(
                    [
                        input
                    ],
                    warnings
                )
                .Single();
            Check(result.Depends.Count == expectedDepends, name + "-count");
            if (expectedVersion is not null)
                Check(
                    result.Version == expectedVersion && warnings.Any(
                        x => x.Contains("未更改运行时版本")
                    ),
                    name + "-version-field-is-not-applied"
                );
            JsonFiles.Write(
                Path.Combine(dir, "expected.json"),
                Set(result.Depends, result.Breaks)
            );
            var start = new ProcessStartInfo(java)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in new[]
            {
                "-cp",
                loader + Path.PathSeparator + Path.GetFullPath(json5),
                oracle,
                dir,
                input.Id,
                QuiltDependencyOverrides.DescribePath(source, dir),
                input.Fabric.ToString()
                    .ToLowerInvariant()
            }

            )
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new Exception(name + " official mismatch: " + error);
            Check(true, name + "-official-loader");
            JsonFiles.Write(
                Path.Combine(dir, "result.json"),
                new
                {
                    warnings,
                    result.Depends,
                    result.Breaks,
                    result.Version
                }
            );
            return result;
        }

        var remove = new JObject
        {
            ["remove"] = "required"
        };
        await Case(
            "path-fuzzy-remove",
            Candidate(
                "top",
                [
                    Dep("required", ">=2")
                ]
            ),
            [
                Rule("path", "<mods>/Application.jar", remove)
            ],
            0
        );
        await Case(
            "id-strict-remove",
            Candidate(
                "top",
                [
                    Dep("required", ">=2")
                ]
            ),
            [
                Rule("id", "application", remove)
            ],
            1
        );
        await Case(
            "nested-origin",
            Candidate(
                "nested:jar",
                [
                    Dep("required", ">=2")
                ]
            ),
            [
                Rule("path", "<mods>/outer.jar!/lib/application.jar", remove)
            ],
            0
        );
        await Case(
            "nested-missing-boundary-slash-does-not-match",
            Candidate(
                "nested:jar",
                [
                    Dep("required", ">=2")
                ]
            ),
            [
                Rule("path", "<mods>/outer.jar!lib/application.jar", remove)
            ],
            1
        );
        await Case(
            "wrong-game-prefix",
            Candidate(
                "top",
                [
                    Dep("required", ">=2")
                ]
            ),
            [
                Rule("path", "<game>/mods/Application.jar", remove)
            ],
            1
        );
        await Case(
            "path-case-sensitive",
            Candidate(
                "top",
                [
                    Dep("required", ">=2")
                ]
            ),
            [
                Rule("path", "<mods>/application.jar", remove)
            ],
            1
        );
        foreach (var pair in new[]
        {
            ("quoted", @"\Qapplication\E"),
            ("case-flag", "(?i)APPLICATION"),
            ("intersection", "app[a-z&&[^x]]+"),
            ("java-property", @"\p{javaLowerCase}+"),
            ("lookahead", "(?=app)application")
        }

        )
        {
            await Case(
                "pattern-" + pair.Item1,
                Candidate("top"),
                [
                    Rule(
                        "pattern",
                        pair.Item2,
                        new JObject
                        {
                            ["add"] = "required"
                        }
                    )
                ],
                1
            );
        }

        await Case(
            "pattern-full-match",
            Candidate("top"),
            [
                Rule(
                    "pattern",
                    "app",
                    new JObject
                    {
                        ["add"] = "required"
                    }
                )
            ],
            0
        );
        await Case(
            "version-stored-unused",
            Candidate("top"),
            [
                Rule("id", "application", version: "999")
            ],
            0,
            "1.0.0"
        );
        await Case(
            "duplicate-path-last-wins",
            Candidate("top"),
            [
                Rule(
                    "path",
                    "<mods>/Application.jar",
                    new JObject
                    {
                        ["add"] = "old"
                    }
                ),
                Rule(
                    "path",
                    "<mods>/Application.jar",
                    new JObject
                    {
                        ["add"] = "new"
                    }
                )
            ],
            1
        );
        await Case(
            "duplicate-id-adds",
            Candidate("top"),
            [
                Rule(
                    "id",
                    "application",
                    new JObject
                    {
                        ["add"] = "one"
                    }
                ),
                Rule(
                    "id",
                    "application",
                    new JObject
                    {
                        ["add"] = "two"
                    }
                )
            ],
            2
        );
        await Case(
            "overlapping-id-pattern-commutes",
            Candidate("top"),
            [
                Rule(
                    "id",
                    "application",
                    new JObject
                    {
                        ["add"] = "one"
                    }
                ),
                Rule(
                    "pattern",
                    "app.*",
                    new JObject
                    {
                        ["add"] = "two"
                    }
                )
            ],
            2
        );
        await Case(
            "path-before-id",
            Candidate(
                "top",
                [
                    Dep("old", ">=2")
                ]
            ),
            [
                Rule(
                    "id",
                    "application",
                    new JObject
                    {
                        ["remove"] = "new"
                    }
                ),
                Rule(
                    "path",
                    "<mods>/Application.jar",
                    new JObject
                    {
                        ["replace"] = "old",
                        ["with"] = "new"
                    }
                )
            ],
            0
        );
        await Case(
            "fuzzy-two-matches-preserved",
            Candidate(
                "top",
                [
                    Dep("required", ">=2"),
                    Dep("required", "<1")
                ]
            ),
            [
                Rule("path", "<mods>/Application.jar", remove)
            ],
            2
        );
        await Case(
            "fuzzy-group-exact",
            Candidate(
                "top",
                [
                    Dep("example:required", ">=2")
                ]
            ),
            [
                Rule("path", "<mods>/Application.jar", remove)
            ],
            1
        );
        await Case(
            "replace-key-last-wins",
            Candidate(
                "top",
                [
                    Dep("required")
                ]
            ),
            [
                Rule(
                    "id",
                    "application",
                    new JArray(
                        new JObject
                        {
                            ["replace"] = "required",
                            ["with"] = "old"
                        },
                        new JObject
                        {
                            ["replace"] = "required",
                            ["with"] = "new"
                        }
                    )
                )
            ],
            1
        );
        await Case(
            "duplicate-fuzzy-removal-retained",
            Candidate(
                "top",
                [
                    Dep("required"),
                    Dep("required", ">=2")
                ]
            ),
            [
                Rule(
                    "path",
                    "<mods>/Application.jar",
                    new JArray(
                        remove,
                        remove.DeepClone()
                    )
                )
            ],
            0
        );
        await Case(
            "string-dependency-fuzzy-normalization",
            Candidate(
                "top",
                [
                    "required"
                ]
            ),
            [
                Rule("path", "<mods>/Application.jar", remove)
            ],
            0
        );
        await Case(
            "breaks-fuzzy",
            Candidate("top")with
            {
                Breaks = new JArray(
                    Dep("bad", ">=2")
                )
            },
            [
                Rule(
                    "path",
                    "<mods>/Application.jar",
                    breaks: new JObject
                    {
                        ["remove"] = "bad"
                    }
                )
            ],
            0
        );
        await Case(
            "ordered-fuzzy-removals-any-first",
            Candidate(
                "top",
                [
                    Dep("required", ">=2"),
                    Dep("required", "<1")
                ]
            ),
            [
                Rule(
                    "path",
                    "<mods>/Application.jar",
                    new JArray(
                        remove,
                        new JObject
                        {
                            ["remove"] = Dep("required", ">=2")
                        }
                    )
                )
            ],
            1
        );
        await Case(
            "ordered-fuzzy-removals-exact-first",
            Candidate(
                "top",
                [
                    Dep("required", ">=2"),
                    Dep("required", "<1")
                ]
            ),
            [
                Rule(
                    "path",
                    "<mods>/Application.jar",
                    new JArray(
                        new JObject
                        {
                            ["remove"] = Dep("required", ">=2")
                        },
                        remove
                    )
                )
            ],
            0
        );
        var converted = await Case(
            "fabric-path-remove-quilt-add",
            Candidate(
                "top",
                [
                    Dep("required", ">=1 <2")
                ],
                true
            ),
            [
                Rule(
                    "path",
                    "<mods>/Application.jar",
                    new JArray(
                        remove,
                        new JObject
                        {
                            ["add"] = Dep("other", "1.2")
                        }
                    )
                )
            ],
            1
        );
        Check(converted.QuiltDependencySemantics, "fabric-overrides-use-quilt-range-semantics");
        foreach (var range in new[]
        {
            "^0.2",
            "~1.2",
            "1.x",
            ">=1 <2",
            "custom"
        }.Concat(
            Path.GetFileName(loader)
                .Contains("0.27.1")
                ? []
                : new[]
                {
                    ">=2 <1"
                }
        ))
        {
            await Case(
                "fabric-range-" + checks.Count,
                Candidate(
                    "top",
                    [
                        Dep("required", range)
                    ],
                    true
                ),
                [
                    Rule(
                        "id",
                        "application",
                        new JObject
                        {
                            ["add"] = "extra"
                        }
                    )
                ],
                range == ">=2 <1"
                    ? 1
                    : 2
            );
        }

        void Reject(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)when (ex is InvalidDataException or NotSupportedException or OperationCanceledException)
            {
                Check(true, name);
                return;
            }

            throw new Exception("Expected rejection: " + name);
        }

        if (Path.GetFileName(loader)
            .Contains("0.27.1"))
        {
            Reject(
                "old-quilt-rejects-impossible-fabric-range",
                () => runtime.ConvertFabric(
                    Candidate(
                        "top",
                        [
                            Dep("required", ">=2 <1")
                        ],
                        true
                    ),
                    default
                )
            );
        }

        var invalid = Path.Combine(output, "invalid");
        Directory.CreateDirectory(
            Path.Combine(invalid, "config")
        );
        void Write(params JObject[] rules) => JsonFiles.Write(
            Path.Combine(invalid, QuiltDependencyOverrides.RelativePath),
            Config(rules)
        );
        Write(
            Rule("pattern", "[")
        );
        Reject(
            "invalid-regex-even-with-no-matching-mod",
            () => QuiltDependencyOverrides.Read(invalid, runtime: runtime)
                .Apply(
                    [],
                    []
                )
        );
        Write(
            Rule(
                "id",
                "application",
                new JObject
                {
                    ["add"] = "one"
                }
            ),
            Rule(
                "pattern",
                "app.*",
                new JObject
                {
                    ["remove"] = "one"
                }
            )
        );
        Reject(
            "order-dependent-overlap-rejected",
            () => QuiltDependencyOverrides.Read(invalid, runtime: runtime)
                .Apply(
                    [
                        Candidate("top")
                    ],
                    []
                )
        );
        Write(
            new JObject
            {
                ["id"] = "application",
                ["version"] = 12
            }
        );
        Reject(
            "non-string-version-rejected",
            () => QuiltDependencyOverrides.Read(invalid)
        );
        Write(
            new JObject
            {
                ["id"] = "application",
                ["path"] = "anything"
            }
        );
        Reject(
            "multiple-selectors-rejected",
            () => QuiltDependencyOverrides.Read(invalid)
        );
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Reject(
                "java-matcher-cancellation",
                () => runtime.Match(
                    [
                        ".*"
                    ],
                    [
                        "application"
                    ],
                    cancelled.Token
                )
            );
        }

        await Transactions(output, java, loader, Check);
        await QuiltOverrideTests.RunAsync(
            Path.Combine(output, "previous-overrides"),
            async (name, action) =>
            {
                await action();
                Check(true, name);
            }
        );
        JsonFiles.Write(
            Path.Combine(output, "result.json"),
            new
            {
                status = "passed",
                checks,
                officialLoader = Path.GetFileName(loader)
            }
        );
        return 0;
    }

    /// <summary>Exercise the production planner and commit/rollback paths with real Java and a verified loader, using a bounded HTTP catalog fixture.</summary>
    private static async Task Transactions(string output, string java, string loader, Action<bool, string> check)
    {
        var paths = new AppPaths(
            Path.Combine(output, "transactions")
        );
        Directory.CreateDirectory(paths.Runtime);
        new LauncherSettingsStore(paths).SaveGlobal(
            new LaunchSettings
            {
                JavaExecutable = java
            }
        );
        var payload = new Dictionary<string, byte[]>();
        ResourceRelease Release(string number, bool broken)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(
                    zip.CreateEntry("quilt.mod.json")
                        .Open()
                ))
                {
                    writer.Write(
                        new JObject
                        {
                            ["schema_version"] = 1,
                            ["quilt_loader"] = new JObject
                            {
                                ["id"] = "application",
                                ["group"] = "example",
                                ["version"] = number,
                                ["depends"] = broken
                                    ? new JArray(
                                        new JObject
                                        {
                                            ["id"] = "missing",
                                            ["versions"] = ">=2"
                                        }
                                    )
                                    : new JArray()
                            }
                        }
                    );
                }

            var bytes = memory.ToArray();
            var name = "Application-" + number + ".jar";
            payload[name] = bytes;
            return new(
                "application" + number,
                "application",
                "application",
                number,
                "release",
                DateTimeOffset.UtcNow,
                [
                    "1.21.1"
                ],
                [
                    "quilt"
                ],
                new(
                    name,
                    new Uri("https://fixture.invalid/" + name),
                    bytes.Length,
                    Hash(bytes)
                ),
                []
            );
        }

        var old = Release("1", false);
        var current = Release("2", true);
        using var downloads = new DownloadService(
            new Fixture(payload)
        );
        using var catalog = new ResourceCatalog(
            new Fixture(payload)
        );
        GameProfile Instance(string name, ResourceRelease? release)
        {
            var directory = Path.Combine(paths.Root, "instances", name);
            Directory.CreateDirectory(
                Path.Combine(directory, "mods")
            );
            Directory.CreateDirectory(
                Path.Combine(directory, "config")
            );
            Directory.CreateDirectory(
                Path.Combine(directory, "libraries")
            );
            File.Copy(
                loader,
                Path.Combine(directory, "libraries/loader.jar")
            );
            JsonFiles.Write(
                Path.Combine(directory, "profile.json"),
                new
                {
                    loader = "quilt",
                    loaderVersion = "0.30.1",
                    libraryRoot = "instance"
                }
            );
            JsonFiles.Write(
                Path.Combine(directory, "version.json"),
                new
                {
                    javaVersion = new
                    {
                        majorVersion = 21
                    },
                    libraries = new[]
                    {
                        new
                        {
                            name = "org.quiltmc:quilt-loader:0.30.1",
                            downloads = new
                            {
                                artifact = new
                                {
                                    path = "loader.jar",
                                    url = "https://fixture.invalid/loader.jar",
                                    size = new FileInfo(loader).Length,
                                    sha1 = Hash(
                                        File.ReadAllBytes(loader)
                                    )
                                }
                            }
                        }
                    }
                }
            );
            JsonFiles.Write(
                Path.Combine(directory, QuiltDependencyOverrides.RelativePath),
                JObject.Parse(
                    """{"schema_version":1,"overrides":[{"path":"<mods>/Application-2.jar","depends":{"remove":"missing"}},{"pattern":"\\Qapplication\\E","depends":{"add":{"id":"java","versions":">=21"}}}]}"""
                )
            );
            if (release is not null)
                File.WriteAllBytes(
                    Path.Combine(directory, "mods", release.File.Name),
                    payload[release.File.Name]
                );
            ModpackService.RegisterFiles(directory);
            return new(
                name,
                name,
                "1.21.1",
                "quilt",
                directory,
                Path.Combine(directory, "version.json")
            );
        }

        async Task Reject(Func<Task> action, string label)
        {
            try
            {
                await action();
            }
            catch (Exception ex)when (ex is InvalidDataException or NotSupportedException or OperationCanceledException or IOException)
            {
                check(true, label);
                return;
            }

            throw new Exception("Expected failure: " + label);
        }

        string[] Files(GameProfile profile) => Directory.GetFiles(
            Path.Combine(profile.GameDirectory, "mods")
        )
            .Order()
            .Select(
                p => Path.GetFileName(p) + ":" + Hash(
                    File.ReadAllBytes(p)
                )
            )
            .ToArray();
        var single = Instance("single", null);
        var plan = await ResourceDependencyPlanner.ResolveAsync(catalog, downloads, current, ResourceKind.Mod, single);
        check(
            Files(single)
                .Length == 0,
            "single-preview-leaves-live-files"
        );
        await new ResourceInstaller(single.GameDirectory, downloads, "quilt", "1.21.1").InstallAsync(plan);
        check(
            Files(single)
                .Length == 1,
            "single-real-files-installed-with-path-and-java-pattern"
        );
        var batch = Instance("batch", old);
        var updater = new ResourceBatchUpdater(batch.GameDirectory, "quilt", "1.21.1");
        var resource = new LocalResourceService(batch.GameDirectory, "quilt", "1.21.1").Scan()
            .Single(
                x => x.ModIds.Contains("application")
            );
        using (var prepared = await updater.PrepareAsync(
            [
                new(resource, current)
            ],
            catalog,
            downloads
        ))
            updater.Apply(prepared);
        var pack = Instance("pack", current);
        await ResourceDependencyPlanner.ResolvePackAsync(catalog, downloads, pack);
        check(
            Files(single)
                .SequenceEqual(
                    Files(batch)
                ) && Files(batch)
                .SequenceEqual(
                    Files(pack)
                ),
            "single-batch-locked-pack-identical-files"
        );
        var unchanged = Files(single);
        JsonFiles.Write(
            Path.Combine(single.GameDirectory, QuiltDependencyOverrides.RelativePath),
            JObject.Parse("""{"schema_version":1,"overrides":[{"pattern":"["}]}""")
        );
        await Reject(
            () => ResourceDependencyPlanner.ResolveAsync(catalog, downloads, current, ResourceKind.Mod, single),
            "invalid-pattern-blocks-production-plan"
        );
        check(
            unchanged.SequenceEqual(
                Files(single)
            ),
            "invalid-pattern-preserves-installed-files"
        );
        var cancel = Instance("cancel", null);
        using (var token = new CancellationTokenSource())
        {
            token.Cancel();
            await Reject(
                () => ResourceDependencyPlanner.ResolveAsync(catalog, downloads, current, ResourceKind.Mod, cancel, ct: token.Token),
                "cancelled-plan-rejected"
            );
        }

        check(
            Files(cancel)
                .Length == 0,
            "cancelled-plan-no-mod-commit"
        );
        var changed = Instance("changed", old);
        var changedUpdater = new ResourceBatchUpdater(changed.GameDirectory, "quilt", "1.21.1");
        var item = new LocalResourceService(changed.GameDirectory, "quilt", "1.21.1").Scan()
            .Single(
                x => x.ModIds.Contains("application")
            );
        var before = Files(changed);
        using (var prepared = await changedUpdater.PrepareAsync(
            [
                new(item, current)
            ],
            catalog,
            downloads
        ))
        {
            File.WriteAllText(
                Path.Combine(changed.GameDirectory, QuiltDependencyOverrides.RelativePath),
                "{\"schema_version\":1}"
            );
            await Reject(
                () =>
                {
                    changedUpdater.Apply(prepared);
                    return Task.CompletedTask;
                },
                "changed-override-invalidates-prepared-batch"
            );
        }

        check(
            before.SequenceEqual(
                Files(changed)
            ),
            "changed-override-preserves-original-mod"
        );
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(
        System.Security.Cryptography.SHA1.HashData(bytes)
    )
        .ToLowerInvariant();
    private sealed class Fixture(Dictionary<string, byte[]> payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host != "fixture.invalid")
                throw new InvalidOperationException("Unexpected network request in locked fixture");
            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(
                        payload[Path.GetFileName(request.RequestUri.AbsolutePath)]
                    )
                }
            );
        }
    }
}
