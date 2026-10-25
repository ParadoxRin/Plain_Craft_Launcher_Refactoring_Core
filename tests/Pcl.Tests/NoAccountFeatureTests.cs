using Pcl.Core;
using Newtonsoft.Json.Linq;
using System.IO.Compression;

internal static class NoAccountFeatureTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        static void Check(bool value, string message)
        {
            if (!value)
                throw new Exception(message);
        }

        static void Reject(Action action)
        {
            try
            {
                action();
                throw new Exception("Invalid operation accepted");
            }
            catch (InvalidDataException)
            {
            }
        }

        await test(
            "fabric_metadata_retries_transient_timeout_and_preserves_user_cancellation",
            async () =>
            {
                using var service = new MinecraftService(
                    new AppPaths(
                        Path.Combine(root, "fabric-metadata-retry")
                    )
                );
                int calls = 0;
                using var fabric = new FabricService(
                    service,
                    handler: new MetadataHandler(
                        (request, ct) =>
                        {
                            calls++;
                            if (calls == 1)
                                throw new TaskCanceledException("simulated network timeout");
                            if (calls == 2)
                                return Task.FromResult(
                                    new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                                );
                            return Task.FromResult(
                                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                                {
                                    Content = new StringContent("[{\"loader\":{\"version\":\"0.20.1\",\"stable\":true}}]")
                                }
                            );
                        }
                    )
                );
                Check(
                    (await fabric.VersionsAsync()).Single()
                        .Version == "0.20.1" && calls == 3,
                    "Fabric transient recovery failed"
                );
                int canceledCalls = 0;
                using var cancel = new CancellationTokenSource();
                using var canceledFabric = new FabricService(
                    service,
                    handler: new MetadataHandler(
                        (request, ct) =>
                        {
                            canceledCalls++;
                            cancel.Cancel();
                            throw new OperationCanceledException(ct);
                        }
                    )
                );
                try
                {
                    await canceledFabric.VersionsAsync(cancel.Token);
                    throw new Exception("Cancellation ignored");
                }
                catch (OperationCanceledException)
                {
                }

                Check(canceledCalls == 1, "User cancellation retried");
            }
        );
        await test(
            "field_inheritance_migrates_v1_with_backup_and_preserves_explicit_empty_false",
            () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "field-migration")
                );
                var file = Path.Combine(paths.Root, "launch-settings.json");
                var legacy = new LaunchSettings
                {
                    Width = 1280,
                    MaximumMemoryMb = 3072
                };
                var old = new JObject
                {
                    ["schemaVersion"] = 1,
                    ["global"] = JObject.FromObject(
                        new LaunchSettings
                        {
                            Width = 1400
                        }
                    ),
                    ["instances"] = new JObject
                    {
                        [LauncherSettingsStore.InstanceKey(paths.Game)] = new JObject
                        {
                            ["directory"] = paths.Game,
                            ["launch"] = JObject.FromObject(legacy)
                        }
                    }
                };
                JsonFiles.Write(file, old);
                var bytes = File.ReadAllBytes(file);
                var settings = new LauncherSettingsStore(paths);
                Check(
                    settings.Effective(paths.Game) == legacy,
                    "v1 behavior changed on read"
                );
                Check(
                    File.ReadAllBytes(file)
                        .SequenceEqual(bytes),
                    "Read mutated old settings"
                );
                settings.SaveGlobal(
                    new()
                    {
                        Width = 1500,
                        Fullscreen = true,
                        ServerAddress = "example.test"
                    }
                );
                Check(
                    settings.Effective(paths.Game) == legacy,
                    "Migration changed explicit legacy values"
                );
                var backup = Directory.GetFiles(
                    Path.Combine(paths.Root, "settings-backups"),
                    "launch-schema1-*.json"
                );
                Check(
                    backup.Length == 1 && File.ReadAllBytes(backup[0])
                        .SequenceEqual(bytes),
                    "Migration backup missing or altered"
                );
                settings.SavePatch(
                    paths.Game,
                    new()
                    {
                        MaximumMemoryMb = 4096,
                        Fullscreen = false,
                        ServerAddress = ""
                    }
                );
                settings.SaveGlobal(
                    new()
                    {
                        Width = 1600,
                        Height = 900,
                        Fullscreen = true,
                        ServerAddress = "other.test"
                    }
                );
                var value = new LauncherSettingsStore(paths).Effective(paths.Game);
                Check(
                    value.Width == 1600 && value.Height == 900 && value.MaximumMemoryMb == 4096 && !value.Fullscreen && value.ServerAddress == "",
                    "Per-field inheritance/false/empty failed"
                );
                Check(
                    Directory.GetFiles(
                        Path.Combine(paths.Root, "settings-backups"),
                        "launch-schema1-*.json"
                    )
                        .Length == 1,
                    "Repeated schema backups"
                );
                settings.SavePatch(paths.Game, null);
                Check(
                    settings.Effective(paths.Game) == settings.Global,
                    "Reset inheritance failed"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "field_inheritance_conflict_is_atomic_and_patch_copy_stays_inherited",
            () =>
            {
                var paths = new AppPaths(
                    Path.Combine(root, "field-atomic")
                );
                var store = new LauncherSettingsStore(paths);
                store.SaveGlobal(
                    new()
                    {
                        MaximumMemoryMb = 4096
                    }
                );
                store.SavePatch(
                    paths.Game,
                    new()
                    {
                        MinimumMemoryMb = 3072
                    }
                );
                var before = File.ReadAllBytes(
                    Path.Combine(paths.Root, "launch-settings.json")
                );
                Reject(
                    () => store.SaveGlobal(
                        new()
                        {
                            MaximumMemoryMb = 2048
                        }
                    )
                );
                Check(
                    File.ReadAllBytes(
                        Path.Combine(paths.Root, "launch-settings.json")
                    )
                        .SequenceEqual(before),
                    "Conflicting global edit was committed"
                );
                Reject(
                    () => store.SavePatch(
                        paths.Game,
                        new()
                        {
                            MaximumMemoryMb = 64
                        }
                    )
                );
                Check(
                    File.ReadAllBytes(
                        Path.Combine(paths.Root, "launch-settings.json")
                    )
                        .SequenceEqual(before),
                    "Invalid patch was committed"
                );
                var copy = Path.Combine(paths.Root, "copy");
                store.SavePatch(
                    copy,
                    store.Patch(paths.Game)
                );
                store.SaveGlobal(
                    new()
                    {
                        MaximumMemoryMb = 6144,
                        Width = 1500
                    }
                );
                Check(
                    store.Effective(copy)
                        .MaximumMemoryMb == 6144 && store.Patch(copy)!.Fields.SequenceEqual(
                        [
                            "MinimumMemoryMb"
                        ]
                    ),
                    "Copy froze inherited fields"
                );
                var safe = new LaunchSettingsPatch
                {
                    Width = 1280,
                    JavaExecutable = "/untrusted/java",
                    JvmArguments = "-javaagent:/bad.jar"
                }.WithoutExecutableOverrides();
                Check(
                    safe.Width == 1280 && safe.JavaExecutable == "" && safe.JvmArguments == "" && safe.MaximumMemoryMb is null,
                    "Import sanitization changed inheritance"
                );
                return Task.CompletedTask;
            }
        );
        JObject FileRow(string name, string env) => new()
        {
            ["path"] = name,
            ["fileSize"] = 1,
            ["hashes"] = new JObject
            {
                ["sha1"] = new string ('a', 40),
                ["sha512"] = new string ('b', 128)
            },
            ["downloads"] = new JArray("https://cdn.modrinth.com/data/test/" + name),
            ["env"] = new JObject
            {
                ["client"] = env
            }
        };
        await test(
            "mrpack_per_file_selection_keeps_required_and_rejects_stale_choices",
            () =>
            {
                var index = new JObject
                {
                    ["formatVersion"] = 1,
                    ["game"] = "minecraft",
                    ["dependencies"] = new JObject
                    {
                        ["minecraft"] = "1.21.1"
                    },
                    ["files"] = new JArray(
                        FileRow("config/required.txt", "required"),
                        FileRow("config/a.txt", "optional"),
                        FileRow("config/b.txt", "optional"),
                        FileRow("config/server.txt", "unsupported")
                    )
                };
                var plan = ModpackService.Parse(
                    index,
                    true,
                    [
                        "config/b.txt"
                    ]
                );
                Check(
                    plan.Files.Select(
                        x => x.Path
                    )
                        .SequenceEqual(
                            [
                                "config/required.txt",
                                "config/b.txt"
                            ]
                        ) && plan.SkippedOptionalFiles == 1 && plan.SkippedServerFiles == 1,
                    "Selection altered mandatory/server semantics"
                );
                Check(
                    ModpackService.Parse(
                        index,
                        true,
                        []
                    )
                        .Files.Count == 1 && ModpackService.Parse(index, true)
                        .Files.Count == 3,
                    "Empty selection/all compatibility failed"
                );
                Reject(
                    () => ModpackService.Parse(
                        index,
                        false,
                        [
                            "config/required.txt"
                        ]
                    )
                );
                Reject(
                    () => ModpackService.Parse(
                        index,
                        false,
                        [
                            "../escape"
                        ]
                    )
                );
                ((JArray)index["files"]!).Add(
                    FileRow("config/A.txt", "optional")
                );
                Reject(
                    () => ModpackService.Parse(index)
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "curse_optional_selection_converts_only_selected_local_files_without_key",
            async () =>
            {
                var folder = Path.Combine(root, "optional-cf");
                Directory.CreateDirectory(folder);
                var source = Path.Combine(folder, "pack.zip");
                var mappings = new Dictionary<string, string>();
                for (var i = 1; i <= 3; i++)
                {
                    var jar = Path.Combine(folder, "mod" + i + ".jar");
                    using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
                    {
                        using var writer = new StreamWriter(
                            zip.CreateEntry("fabric.mod.json")
                                .Open()
                        );
                        writer.Write(
                            new JObject
                            {
                                ["schemaVersion"] = 1,
                                ["id"] = "testmod" + i,
                                ["version"] = "1.0.0"
                            }.ToString()
                        );
                    }

                    mappings[i + ":" + i * 10] = jar;
                }

                var manifest = new JObject
                {
                    ["manifestType"] = "minecraftModpack",
                    ["manifestVersion"] = 1,
                    ["minecraft"] = new JObject
                    {
                        ["version"] = "1.21.1",
                        ["modLoaders"] = new JArray(
                            new JObject
                            {
                                ["id"] = "fabric-0.16.14"
                            }
                        )
                    },
                    ["files"] = new JArray(
                        Enumerable.Range(1, 3)
                            .Select(
                                i => new JObject
                                {
                                    ["projectID"] = i,
                                    ["fileID"] = i * 10,
                                    ["required"] = i == 1
                                }
                            )
                    )
                };
                using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
                {
                    using var writer = new StreamWriter(
                        zip.CreateEntry("manifest.json")
                            .Open()
                    );
                    writer.Write(
                        manifest.ToString()
                    );
                }

                var options = new PackImportOptions(
                    ManualFiles: mappings,
                    SelectedOptionalFiles: [
                        "curse:2:20"
                    ]
                );
                var plan = PortableModpackService.Inspect(source, options);
                Check(
                    plan.OptionalFiles.Count == 2 && plan.CurseFiles.Select(
                        x => x.Key
                    )
                        .SequenceEqual(
                            [
                                "1:10",
                                "2:20"
                            ]
                        ),
                    "Wrong CF choices"
                );
                using var service = new MinecraftService(
                    new AppPaths(
                        Path.Combine(folder, "data")
                    )
                );
                var converted = await new PortableModpackService(service).ConvertAsync(source, options);
                using var result = ZipFile.OpenRead(converted);
                Check(
                    result.GetEntry("overrides/mods/mod1.jar")is not null && result.GetEntry("overrides/mods/mod2.jar")is not null && result.GetEntry("overrides/mods/mod3.jar")is null,
                    "Excluded CF artifact was included"
                );
                Reject(
                    () => PortableModpackService.Inspect(
                        source,
                        options with
                        {
                            SelectedOptionalFiles = [
                                "curse:9:90"
                            ]
                        }
                    )
                );
                ((JArray)manifest["files"]!).Add(
                    new JObject
                    {
                        ["projectID"] = 1,
                        ["fileID"] = 10,
                        ["required"] = false
                    }
                );
                var duplicate = Path.Combine(folder, "duplicate.zip");
                using (var zip = ZipFile.Open(duplicate, ZipArchiveMode.Create))
                {
                    using var writer = new StreamWriter(
                        zip.CreateEntry("manifest.json")
                            .Open()
                    );
                    writer.Write(
                        manifest.ToString()
                    );
                }

                Reject(
                    () => PortableModpackService.Inspect(
                        duplicate,
                        new(
                            SelectedOptionalFiles: []
                        )
                    )
                );
            }
        );
    }
}
