using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;
using Pcl.Core;

/// <summary>覆盖配置的文件、依赖语义和事务回归；所有操作仅使用测试临时目录。</summary>
internal static class QuiltOverrideTests
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
            catch (Exception e)when (e is InvalidDataException or NotSupportedException or IOException)
            {
                return;
            }

            throw new Exception("Invalid override accepted");
        }

        string Folder()
        {
            var dir = Path.Combine(
                root,
                "overrides-" + Guid.NewGuid()
                    .ToString("N")
            );
            Directory.CreateDirectory(
                Path.Combine(dir, "config")
            );
            return dir;
        }

        void Write(string dir, params JObject[] rules) => JsonFiles.Write(
            Path.Combine(dir, QuiltDependencyOverrides.RelativePath),
            new JObject
            {
                ["schema_version"] = 1,
                ["overrides"] = new JArray(rules)
            }
        );
        static JObject Rule(JToken? depends = null, JToken? breaks = null)
        {
            var rule = new JObject
            {
                ["id"] = "application"
            };
            if (depends is not null)
                rule["depends"] = depends;
            if (breaks is not null)
                rule["breaks"] = breaks;
            return rule;
        }

        static QuiltCandidate Mod(JArray? depends = null, JArray? breaks = null) => new(
            "fixture",
            "application",
            "example",
            "1",
            true,
            "always",
            "*",
            false,
            depends ?? [],
            breaks ?? [],
            []
        );
        static JObject Dep(string id, string version = "*") => new()
        {
            ["id"] = id,
            ["versions"] = version
        };
        static QuiltResolution Resolve(string directory, QuiltCandidate candidate) => QuiltDependencies.Resolve(
            [
                candidate
            ],
            "1.20.1",
            "0.27.1",
            17,
            gameDirectory: directory
        );
        // 相同夹具亦由官方 QuiltOverrideOracle 运行；逐项核对集合变换结果。
        await test(
            "quilt_overrides_official_eight_case_fixtures",
            () =>
            {
                foreach (var file in Directory.EnumerateFiles(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures/quilt-overrides"),
                    "*.json"
                ))
                {
                    var dir = Folder();
                    File.Copy(
                        file,
                        Path.Combine(dir, QuiltDependencyOverrides.RelativePath)
                    );
                    var data = JsonFiles.Read(file);
                    var entries = (JArray)data["overrides"]!;
                    var input = new JArray(
                        entries.Single(
                            x => (string? )x["id"] == "input"
                        )["depends"]!.Select(
                            x => x["add"]!.DeepClone()
                        )
                    );
                    var expected = new JArray(
                        entries.Single(
                            x => (string? )x["id"] == "expected"
                        )["depends"]!.Select(
                            x => x["add"]!.DeepClone()
                        )
                    );
                    var actual = QuiltDependencyOverrides.Read(dir)
                        .Apply(
                            [
                                Mod(input)with
                                {
                                    Id = "rule"
                                }
                            ],
                            []
                        )
                        .Single()
                        .Depends;
                    JToken Canonical(JToken value) => value.Type == JTokenType.String
                        ? new JObject
                        {
                            ["id"] = (string)value!
                        }

                        : value is JArray array
                            ? new JArray(
                                array.Select(Canonical)
                            )
                            : value.DeepClone();
                    Check(
                        JToken.DeepEquals(
                            Canonical(expected),
                            Canonical(actual)
                        ),
                        "Official fixture differs: " + Path.GetFileName(file)
                    );
                }

                return Task.CompletedTask;
            }
        );
        // 新增 Java 限制应参加最终 SAT；移除限制可恢复，但不更改原候选对象。
        await test(
            "quilt_override_add_remove_replace_and_original_immutable",
            () =>
            {
                var dir = Folder();
                var original = Mod(
                    [
                        Dep("java", ">=21")
                    ]
                );
                Write(
                    dir,
                    Rule(
                        new JObject
                        {
                            ["replace"] = Dep("java", ">=21"),
                            ["with"] = Dep("java", ">=17")
                        }
                    )
                );
                var result = Resolve(dir, original);
                Check(
                    result.Warnings.Any(
                        x => x.Contains("已应用")
                    ),
                    "Override not reported"
                );
                Check((string? )original.Depends[0]["versions"] == ">=21", "Shared metadata mutated");
                Write(
                    dir,
                    Rule(
                        new JObject
                        {
                            ["add"] = Dep("java", ">=21")
                        }
                    )
                );
                Reject(
                    () => Resolve(
                        dir,
                        Mod()
                    )
                );
                Write(
                    dir,
                    Rule(
                        new JObject
                        {
                            ["remove"] = Dep("java", ">=21")
                        }
                    )
                );
                Resolve(dir, original);
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_override_semantic_range_equality_reason_optional_and_unless",
            () =>
            {
                var dir = Folder();
                var dependency = Dep("java", ">=21");
                dependency["reason"] = "old reason";
                Write(
                    dir,
                    Rule(
                        new JObject
                        {
                            ["remove"] = Dep("java", ">=21")
                        }
                    )
                );
                Resolve(
                    dir,
                    Mod(
                        [
                            dependency
                        ]
                    )
                );
                dependency["optional"] = true;
                Reject(
                    () => Resolve(
                        dir,
                        Mod(
                            [
                                dependency
                            ]
                        )
                    )
                );
                dependency.Remove("optional");
                dependency["unless"] = "absent";
                Reject(
                    () => Resolve(
                        dir,
                        Mod(
                            [
                                dependency
                            ]
                        )
                    )
                );
                Check(
                    QuiltVersionRange.Equivalent(
                        "1.2",
                        JObject.Parse("{\"all\":[\">=1.2\",\"<2\"]}")
                    ),
                    "Range equivalence differs"
                );
                Check(
                    !QuiltVersionRange.Equivalent(">=21", ">=21.0.0") && !QuiltVersionRange.Equivalent("=1+one", "=1+two"),
                    "Override equality must preserve raw endpoint versions"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_override_arrays_flatten_but_keep_order_and_breaks",
            () =>
            {
                var dir = Folder();
                var original = Mod(
                    [
                        new JArray(
                            "missing",
                            new JArray("absent")
                        )
                    ],
                    [
                        Dep("java", ">=17")
                    ]
                );
                Write(
                    dir,
                    Rule(
                        new JObject
                        {
                            ["replace"] = new JArray("missing", "absent"),
                            ["with"] = "java"
                        },
                        new JObject
                        {
                            ["remove"] = Dep("java", ">=17")
                        }
                    )
                );
                Resolve(dir, original);
                Write(
                    dir,
                    Rule(
                        new JObject
                        {
                            ["replace"] = new JArray("absent", "missing"),
                            ["with"] = "java"
                        }
                    )
                );
                Reject(
                    () => Resolve(dir, original)
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_override_replacement_removal_addition_order_and_deduplication",
            () =>
            {
                var dir = Folder();
                Write(
                    dir,
                    Rule(
                        new JArray(
                            new JObject
                            {
                                ["add"] = "java"
                            },
                            new JObject
                            {
                                ["remove"] = "java"
                            },
                            new JObject
                            {
                                ["replace"] = "absent",
                                ["with"] = "java"
                            }
                        )
                    )
                );
                var result = Resolve(
                    dir,
                    Mod(
                        [
                            "absent",
                            "absent"
                        ]
                    )
                );
                Check(
                    result.Selected.Single()
                        .Depends.Count == 1,
                    "Official set/order semantics lost"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_override_unmatched_selector_and_missing_old_condition_warn",
            () =>
            {
                var dir = Folder();
                var rule = Rule(
                    new JObject
                    {
                        ["remove"] = "absent"
                    }
                );
                Write(dir, rule);
                Check(
                    Resolve(
                        dir,
                        Mod()
                    )
                        .Warnings.Any(
                        x => x.Contains("未找到")
                    ),
                    "Missing removal not surfaced"
                );
                rule["id"] = "other";
                Write(dir, rule);
                Check(
                    Resolve(
                        dir,
                        Mod()
                    )
                        .Warnings.Any(
                        x => x.Contains("未匹配")
                    ),
                    "Unmatched ID not surfaced"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_override_unsupported_selectors_versions_and_fabric_are_explicit",
            () =>
            {
                var dir = Folder();
                foreach (var key in new[]
                {
                    "path",
                    "pattern",
                    "version"
                }

                )
                {
                    var rule = Rule();
                    rule[key] = "some";
                    Write(dir, rule);
                    Reject(
                        () => Resolve(
                            dir,
                            Mod()
                        )
                    );
                }

                Write(
                    dir,
                    Rule(),
                    Rule()
                );
                Reject(
                    () => Resolve(
                        dir,
                        Mod()
                    )
                );
                Write(
                    dir,
                    Rule()
                );
                Reject(
                    () => Resolve(
                        dir,
                        Mod()with
                        {
                            Fabric = true
                        }
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_override_invalid_unmatched_rules_fail_closed",
            () =>
            {
                var dir = Folder();
                foreach (var operation in new[]
                {
                    "{\"add\":null}",
                    "{\"replace\":\"java\"}",
                    "{\"add\":\"java\",\"remove\":\"java\"}",
                    "{\"add\":{\"id\":\"java\",\"versions\":\"^\"}}",
                    "{\"add\":{\"id\":\"java\",\"optional\":\"yes\"}}"
                }

                )
                {
                    var rule = Rule(
                        JObject.Parse(operation)
                    );
                    rule["id"] = "absent";
                    Write(dir, rule);
                    Reject(
                        () => Resolve(
                            dir,
                            Mod()
                        )
                    );
                }

                Write(
                    dir,
                    Rule(
                        new JArray(
                            new JObject
                            {
                                ["replace"] = "first",
                                ["with"] = "second"
                            },
                            new JObject
                            {
                                ["replace"] = "second",
                                ["with"] = "third"
                            }
                        )
                    )
                );
                Reject(
                    () => Resolve(
                        dir,
                        Mod()
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "quilt_override_strict_json_size_and_symlink_boundary",
            () =>
            {
                var dir = Folder();
                var path = Path.Combine(dir, QuiltDependencyOverrides.RelativePath);
                foreach (var text in new[]
                {
                    "{schema_version:1}",
                    "{\"schema_version\":1,}",
                    "{/*comment*/\"schema_version\":1}",
                    "{\"schema_version\":1,\"schema_version\":1}",
                    "{} {}",
                    "{\"schema_version\":2}",
                    "{\"schema_version\":1,\"overrides\":null}"
                }

                )
                {
                    File.WriteAllText(path, text);
                    Reject(
                        () => Resolve(
                            dir,
                            Mod()
                        )
                    );
                }

                File.WriteAllText(
                    path,
                    new string (' ', 2 * 1024 * 1024 + 1)
                );
                Reject(
                    () => Resolve(
                        dir,
                        Mod()
                    )
                );
                File.Delete(path);
                var outside = Path.Combine(root, "override-outside.json");
                File.WriteAllText(outside, "{\"schema_version\":1}");
                File.CreateSymbolicLink(path, outside);
                Reject(
                    () => Resolve(
                        dir,
                        Mod()
                    )
                );
                return Task.CompletedTask;
            }
        );
        // 实际资源启停调用同一套覆盖检查；失败时必须把 JAR 文件名恢复。
        await test(
            "quilt_override_local_disable_rollback_and_json5_production_rejection",
            () =>
            {
                var dir = Folder();
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
                void Jar(string name, string id, string entry = "quilt.mod.json")
                {
                    using var zip = ZipFile.Open(
                        Path.Combine(dir, "mods", name),
                        ZipArchiveMode.Create
                    );
                    using var writer = new StreamWriter(
                        zip.CreateEntry(entry)
                            .Open()
                    );
                    writer.Write(
                        new JObject
                        {
                            ["schema_version"] = 1,
                            ["quilt_loader"] = new JObject
                            {
                                ["id"] = id,
                                ["group"] = "example",
                                ["version"] = "1"
                            }
                        }
                    );
                }

                Jar("app.jar", "application");
                Jar("lib.jar", "library");
                Write(
                    dir,
                    Rule(
                        new JObject
                        {
                            ["add"] = "library"
                        }
                    )
                );
                var resources = new LocalResourceService(dir, "quilt", "1.20.1");
                resources.ValidateEnabledResources();
                var lib = Path.Combine(dir, "mods/lib.jar");
                Reject(
                    () => resources.SetEnabled(
                        [
                            lib
                        ],
                        false
                    )
                );
                Check(
                    File.Exists(lib) && !File.Exists(lib + ".disabled"),
                    "Override rollback failed"
                );
                resources.SetEnabled(
                    [
                        Path.Combine(dir, "mods/app.jar"),
                        lib
                    ],
                    false
                );
                Jar("dev.jar", "devonly", "quilt.mod.json5");
                Check(
                    resources.Scan()
                        .Single(
                            x => x.Name == "dev"
                        )
                        .Error?.Contains("开发环境") == true,
                    "JSON5 misleading scan"
                );
                Reject(
                    () => QuiltDependencies.Read(
                        [
                            Path.Combine(dir, "mods/dev.jar")
                        ]
                    )
                );
                using var zip = ZipFile.Open(
                    Path.Combine(dir, "mods/dev.jar"),
                    ZipArchiveMode.Update
                );
                using (var writer = new StreamWriter(
                    zip.CreateEntry("fabric.mod.json")
                        .Open()
                ))
                    writer.Write("{}");
                zip.Dispose();
                Reject(
                    () => QuiltDependencies.Read(
                        [
                            Path.Combine(dir, "mods/dev.jar")
                        ]
                    )
                );
                return Task.CompletedTask;
            }
        );
    }
}
