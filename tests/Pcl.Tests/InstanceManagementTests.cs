using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Pcl.Core;

public static class InstanceManagementTests
{
    public static async Task RunAll(string root, Func<string, Func<Task>, Task> test)
    {
        static void Check(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }

        static void Throws<T>(Action action)
        where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new Exception("Expected " + typeof(T).Name);
        }

        static async Task ThrowsAsync<T>(Func<Task> action)
        where T : Exception
        {
            try
            {
                await action();
            }
            catch (T)
            {
                return;
            }

            throw new Exception("Expected " + typeof(T).Name);
        }

        using var service = new MinecraftService(
            new AppPaths(
                Path.Combine(
                    root,
                    "实例管理 空格-" + Guid.NewGuid()
                        .ToString("N")
                )
            ),
            new("osx", "aarch64", "14")
        );
        var paths = service.Paths;
        var manager = new InstanceManager(service);
        var settings = manager.Settings;
        var folder = paths.VersionFolder("1.21.1");
        Directory.CreateDirectory(folder);
        var jar = Path.Combine(folder, "1.21.1.jar");
        File.WriteAllText(jar, "test-client");
        var metadata = new JObject
        {
            ["id"] = "1.21.1",
            ["type"] = "release",
            ["mainClass"] = "net.minecraft.client.main.Main",
            ["javaVersion"] = new JObject
            {
                ["majorVersion"] = 21
            },
            ["assetIndex"] = new JObject
            {
                ["id"] = "test"
            },
            ["libraries"] = new JArray(),
            ["downloads"] = new JObject
            {
                ["client"] = new JObject
                {
                    ["url"] = "https://example.com/client.jar",
                    ["size"] = new FileInfo(jar).Length,
                    ["sha1"] = Convert.ToHexString(
                        SHA1.HashData(
                            File.ReadAllBytes(jar)
                        )
                    )
                        .ToLowerInvariant()
                }
            },
            ["arguments"] = new JObject
            {
                ["jvm"] = new JArray("-cp", "${classpath}"),
                ["game"] = new JArray(
                    "--username",
                    "${auth_player_name}",
                    "--gameDir",
                    "${game_directory}",
                    "--accessToken",
                    "${auth_access_token}"
                )
            }
        };
        JsonFiles.Write(
            Path.Combine(folder, "1.21.1.json"),
            metadata
        );
        JsonFiles.Write(
            Path.Combine(folder, "pcl-mac-install.json"),
            new
            {
                complete = true
            }
        );
        Directory.CreateDirectory(
            Path.Combine(paths.Game, "saves", "世界")
        );
        File.WriteAllText(
            Path.Combine(paths.Game, "saves", "世界", "level.dat"),
            "save-data"
        );
        File.WriteAllText(
            Path.Combine(paths.Game, "launcher_accounts.json"),
            "not-exported"
        );
        var original = manager.List()
            .Single(
                x => x.Id == "1.21.1"
            );
        GameProfile? copied = null;
        await test(
            "copied_and_restored_instance_bootstrap_excludes_actual_client_filename",
            () =>
            {
                var jsonPath = Path.Combine(folder, "1.21.1.json");
                var originalJson = File.ReadAllBytes(jsonPath);
                try
                {
                    var bootstrap = (JObject)metadata.DeepClone();
                    bootstrap["arguments"]!["jvm"] = new JArray(
                        "-cp",
                        "${classpath}",
                        "-DignoreList=client-extra,${version_name}.jar,keep.jar",
                        "-Dother=${version_name}.jar"
                    );
                    bootstrap["arguments"]!["game"] = new JArray("--version", "${version_name}");
                    JsonFiles.Write(jsonPath, bootstrap);
                    foreach (var id in new[]
                    {
                        "1.21.1",
                        "copy-fixture",
                        "restore-fixture"
                    }

                    )
                    {
                        // 模拟原实例、复制及恢复后的 ID；真实客户端路径仍指向原文件名。
                        var installed = service.LoadInstalled()with
                        {
                            Id = id
                        };
                        var plan = new LaunchService(service).CreatePlan(
                            installed,
                            new("/usr/bin/java", "aarch64", 21),
                            AccountManager.OfflineSession("Player"),
                            gameDirectory: paths.Game
                        );
                        Check(
                            plan.Arguments.Contains("-DignoreList=client-extra,1.21.1.jar,keep.jar"),
                            "actual JAR excluded"
                        );
                        Check(
                            plan.Arguments.Contains("-Dother=" + id + ".jar"),
                            "unrelated JVM property preserved"
                        );
                        Check(
                            plan.Arguments.Contains(id),
                            "game version remains instance ID"
                        );
                    }
                }
                finally
                {
                    File.WriteAllBytes(jsonPath, originalJson);
                }

                return Task.CompletedTask;
            }
        );
        await test(
            "quick_play_world_uses_metadata_and_rejects_missing_linked_or_conflicting_targets",
            () =>
            {
                var jsonPath = Path.Combine(folder, "1.21.1.json");
                var originalJson = File.ReadAllBytes(jsonPath);
                try
                {
                    var quick = (JObject)metadata.DeepClone();
                    ((JArray)quick["arguments"]!["game"]!).Add(
                        new JObject
                        {
                            ["rules"] = new JArray(
                                new JObject
                                {
                                    ["action"] = "allow",
                                    ["features"] = new JObject
                                    {
                                        ["is_quick_play_singleplayer"] = true
                                    }
                                }
                            ),
                            ["value"] = new JArray("--quickPlaySingleplayer", "${quickPlaySingleplayer}")
                        }
                    );
                    JsonFiles.Write(jsonPath, quick);
                    void Configure(string world) => settings.SavePatch(
                        paths.Game,
                        new()
                        {
                            QuickPlayWorld = world
                        }
                    );
                    LaunchPlan Plan() => new LaunchService(service).CreatePlan(
                        service.LoadInstalled(),
                        new("/usr/bin/java", "aarch64", 21),
                        AccountManager.OfflineSession("Player"),
                        gameDirectory: paths.Game
                    );
                    Configure("世界");
                    var plan = Plan();
                    Check(
                        plan.Arguments.Count(
                            arg => arg == "--quickPlaySingleplayer"
                        ) == 1 && plan.Arguments.Contains("世界"),
                        "official metadata argument resolved once"
                    );
                    JsonFiles.Write(
                        Path.Combine(paths.Game, "profile.json"),
                        new
                        {
                            loader = "forge"
                        }
                    );
                    Throws<InvalidDataException>(
                        () => Plan()
                    );
                    settings.SavePatch(
                        paths.Game,
                        new()
                        {
                            QuickPlayWorld = "",
                            GameArguments = "--quickPlaySingleplayer=世界"
                        }
                    );
                    Throws<InvalidDataException>(
                        () => Plan()
                    );
                    File.Delete(
                        Path.Combine(paths.Game, "profile.json")
                    );
                    Configure("missing");
                    Throws<InvalidDataException>(
                        () => Plan()
                    );
                    Throws<InvalidDataException>(
                        () => new LaunchSettings
                        {
                            QuickPlayWorld = "../outside"
                        }.Validate()
                    );
                    Throws<InvalidDataException>(
                        () => new LaunchSettings
                        {
                            QuickPlayWorld = "世界",
                            ServerAddress = "localhost"
                        }.Validate()
                    );
                    Throws<InvalidDataException>(
                        () => new LaunchSettings
                        {
                            QuickPlayWorld = "世界",
                            GameArguments = "--quickPlayRealms 123"
                        }.Validate()
                    );
                    var link = Path.Combine(paths.Game, "saves", "linked");
                    Directory.CreateSymbolicLink(
                        link,
                        Path.Combine(paths.Game, "saves", "世界")
                    );
                    Configure("linked");
                    Throws<InvalidDataException>(
                        () => Plan()
                    );
                    Configure("世界");
                    File.WriteAllBytes(jsonPath, originalJson);
                    Throws<InvalidDataException>(
                        () => Plan()
                    );
                    Check(
                        new LaunchSettingsPatch
                        {
                            QuickPlayWorld = "世界"
                        }.WithoutExecutableOverrides()
                            .QuickPlayWorld == "",
                        "restored/imported settings do not auto-enter a world"
                    );
                }
                finally
                {
                    File.WriteAllBytes(jsonPath, originalJson);
                    settings.SavePatch(paths.Game, null);
                    var link = Path.Combine(paths.Game, "saves", "linked");
                    if (Directory.Exists(link))
                    {
                        Directory.Delete(link);
                    }
                }

                return Task.CompletedTask;
            }
        );
        await test(
            "instance_groups_persist_compose_and_follow_copy_backup",
            async () =>
            {
                Check(
                    settings.Preferences(original.GameDirectory)
                        .Group == "",
                    "old settings default to ungrouped"
                );
                var grouped = await manager.CopyAsync(original, "分组测试实例");
                settings.SavePreferences(
                    grouped.GameDirectory,
                    new(Name: "分组测试实例", Favorite: true, Hidden: true, Group: "  Cafe\u0301  ")
                );
                Check(
                    new LauncherSettingsStore(paths).Preferences(grouped.GameDirectory)
                        .Group == "Café",
                    "group persisted and normalized"
                );
                Check(
                    manager.Find(query: "Café", loader: "vanilla", favoritesOnly: true, hiddenOnly: true, group: "CAFÉ")
                        .Single()
                        .Id == grouped.Id,
                    "filters compose with group search"
                );
                Check(
                    !manager.Find(group: "Café", includeHidden: false)
                        .Any(),
                    "hidden instances remain filtered"
                );
                Check(
                    manager.Groups()
                        .Contains("Café"),
                    "hidden group remains manageable"
                );
                var clone = await manager.CopyAsync(grouped, "分组副本");
                Check(
                    settings.Preferences(clone.GameDirectory)
                        .Group == "Café",
                    "copy carries group"
                );
                var backup = Path.Combine(paths.Root, "group-backup.zip");
                await manager.BackupAsync(grouped, backup);
                using (var archive = ZipFile.OpenRead(backup))
                    using (var reader = new StreamReader(
                        archive.GetEntry("pcl-backup.json")!.Open()
                    ))
                    {
                        var manifest = JObject.Parse(
                            await reader.ReadToEndAsync()
                        );
                        Check((string? )manifest["preferences"]?["Group"] == "Café", "backup carries group");
                    }

                var bytes = File.ReadAllBytes(
                    Path.Combine(paths.Root, "launch-settings.json")
                );
                Throws<InvalidDataException>(
                    () => settings.SavePreferences(
                        grouped.GameDirectory,
                        new(Group: "bad\nname")
                    )
                );
                Throws<InvalidDataException>(
                    () => settings.SaveInstance(
                        grouped.GameDirectory,
                        new(
                            Group: new string ('a', 61)
                        ),
                        null
                    )
                );
                Check(
                    bytes.SequenceEqual(
                        File.ReadAllBytes(
                            Path.Combine(paths.Root, "launch-settings.json")
                        )
                    ),
                    "invalid group does not mutate settings"
                );
                settings.SavePreferences(
                    clone.GameDirectory,
                    new(Group: "全部分组")
                );
                Check(
                    manager.Find(group: "全部分组")
                        .Single()
                        .Id == clone.Id,
                    "literal group cannot be confused with all sentinel"
                );
                settings.SavePreferences(
                    grouped.GameDirectory,
                    new(Group: " ")
                );
                Check(
                    manager.Find(group: "")
                        .Any(
                            item => item.Id == grouped.Id
                        ),
                    "clear moves instance to ungrouped"
                );
                manager.DeleteToTrash(grouped);
                manager.DeleteToTrash(clone);
            }
        );
        await test(
            "instance_settings_inheritance_override_and_argument_safety",
            () =>
            {
                settings.SaveGlobal(
                    new()
                    {
                        MaximumMemoryMb = 4096,
                        Width = 1280
                    }
                );
                Check(
                    new LauncherSettingsStore(paths).Effective(paths.Game)
                        .Width == 1280,
                    "global persisted"
                );
                settings.SaveOverride(
                    paths.Game,
                    new()
                    {
                        MaximumMemoryMb = 3072,
                        Width = 1600,
                        JvmArguments = "'-Dlabel=中文 空格'",
                        GameArguments = "--fullscreen"
                    }
                );
                var plan = new LaunchService(service).CreatePlan(
                    service.LoadInstalled(),
                    new("/usr/bin/java", "aarch64", 21),
                    AccountManager.OfflineSession("Player"),
                    gameDirectory: paths.Game
                );
                Check(
                    plan.Arguments.Contains("-Xmx3072M") && plan.Arguments.Contains("-Dlabel=中文 空格") && plan.Arguments.Contains("1600"),
                    "effective options in plan"
                );
                Check(
                    ArgumentText.Parse("'a b' \"c d\" e\\ f")
                        .SequenceEqual(
                            new[]
                            {
                                "a b",
                                "c d",
                                "e f"
                            }
                        ),
                    "quoted argument parsing"
                );
                Throws<InvalidDataException>(
                    () => ArgumentText.Parse("'unclosed")
                );
                Throws<InvalidDataException>(
                    () => new LaunchSettings
                    {
                        GameArguments = "--uuid=spoof"
                    }.Validate()
                );
                Throws<InvalidDataException>(
                    () => new LaunchSettings
                    {
                        JvmArguments = "-Xmx1G"
                    }.Validate()
                );
                settings.SaveOverride(paths.Game, null);
                Check(
                    settings.Effective(paths.Game)
                        .MaximumMemoryMb == 4096,
                    "inherit restored"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "instance_copy_preserves_save_excludes_credentials_and_shared_cache",
            async () =>
            {
                copied = await manager.CopyAsync(original, "中文 副本");
                Check(
                    File.ReadAllText(
                        Path.Combine(copied.GameDirectory, "saves", "世界", "level.dat")
                    ) == "save-data",
                    "save copied"
                );
                Check(
                    !Directory.Exists(
                        Path.Combine(copied.GameDirectory, "versions")
                    ) && !File.Exists(
                        Path.Combine(copied.GameDirectory, "launcher_accounts.json")
                    ),
                    "no shared files or credentials copied"
                );
                Check(
                    InstanceRegistry.Load(service, copied)
                        .Id == copied.Id && InstanceRegistry.ByFolder(
                        service,
                        Path.GetFileName(copied.GameDirectory)
                    )
                        .Id == copied.Id,
                    "custom vanilla load and lookup"
                );
            }
        );
        await test(
            "instance_favorite_hidden_persisted_and_management_still_finds_hidden",
            () =>
            {
                settings.SavePreferences(
                    copied!.GameDirectory,
                    new("重命名", "描述", true, true)
                );
                Check(
                    !InstanceRegistry.List(service)
                        .Any(
                            x => x.Id == copied.Id
                        ),
                    "hidden absent home"
                );
                Check(
                    InstanceRegistry.List(service, true)
                        .First()
                        .Name == "重命名",
                    "favorite first and rename persisted"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "instance_trash_restore_preserves_save_and_parent_dependencies",
            () =>
            {
                var trash = manager.DeleteToTrash(copied!);
                Check(
                    !Directory.Exists(copied!.GameDirectory),
                    "private instance moved"
                );
                Check(
                    File.Exists(jar),
                    "parent shared client preserved"
                );
                manager.RestoreTrash(trash.Id);
                Check(
                    File.ReadAllText(
                        Path.Combine(copied.GameDirectory, "saves", "世界", "level.dat")
                    ) == "save-data",
                    "private instance restored"
                );
                var sharedTrash = manager.DeleteToTrash(original);
                Check(
                    File.Exists(jar) && File.Exists(
                        Path.Combine(paths.Game, "saves", "世界", "level.dat")
                    ),
                    "shared data retained"
                );
                Check(
                    !manager.List()
                        .Any(
                            x => x.Id == original.Id
                        ),
                    "shared profile removed from registry"
                );
                manager.RestoreTrash(sharedTrash.Id);
                Check(
                    manager.List()
                        .Any(
                            x => x.Id == original.Id
                        ),
                    "shared profile restored"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "instance_backup_content_and_unsafe_restore_rejected",
            async () =>
            {
                var backup = Path.Combine(paths.Root, "备份.zip");
                await manager.BackupAsync(original, backup);
                using (var archive = ZipFile.OpenRead(backup))
                {
                    Check(
                        archive.GetEntry("game/saves/世界/level.dat")is not null && archive.GetEntry("game/profile.json")is not null,
                        "backup contains restorable profile and world"
                    );
                    Check(
                        !archive.Entries.Any(
                            x => x.FullName.Contains("launcher_accounts")
                        ),
                        "backup excludes account file"
                    );
                }

                var invalid = Path.Combine(paths.Root, "bad-backup.zip");
                using (var archive = ZipFile.Open(invalid, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(
                        archive.CreateEntry("pcl-backup.json")
                            .Open()
                    ))
                        writer.Write("{\"format\":\"pcl-mac-instance-backup\",\"schemaVersion\":1}");
                    using var bad = new StreamWriter(
                        archive.CreateEntry("game/../../escape")
                            .Open()
                    );
                    bad.Write("bad");
                }

                await ThrowsAsync<InvalidDataException>(
                    () => manager.RestoreBackupAsync(invalid)
                );
                Check(
                    !File.Exists(
                        Path.Combine(paths.Root, "escape")
                    ),
                    "traversal prevented"
                );
            }
        );
        await test(
            "instance_import_scan_rejects_unresolved_inherited_metadata_and_symlinks",
            async () =>
            {
                var external = Path.Combine(paths.Root, "external");
                var inherited = Path.Combine(external, "versions", "custom");
                Directory.CreateDirectory(inherited);
                JsonFiles.Write(
                    Path.Combine(inherited, "custom.json"),
                    new
                    {
                        id = "custom",
                        inheritsFrom = "1.21.1",
                        mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient"
                    }
                );
                Check(
                    manager.Scan(external)
                        .Single()
                        .Supported == false,
                    "inherited third-party rejected explicitly"
                );
                var link = Path.Combine(copied!.GameDirectory, "outside");
                Directory.CreateSymbolicLink(link, external);
                await ThrowsAsync<InvalidDataException>(
                    () => manager.CopyAsync(copied, "bad-copy")
                );
                Directory.Delete(link);
                Check(
                    !Directory.EnumerateDirectories(
                        Path.Combine(paths.Root, "instances")
                    )
                        .Any(
                            x => Path.GetFileName(x)
                                .StartsWith(".pending-")
                        ),
                    "failed staging removed"
                );
            }
        );
        await test(
            "instance_trash_restore_rejects_replaced_data_symlink",
            async () =>
            {
                var copy = await manager.CopyAsync(original, "回收路径检查");
                var trash = manager.DeleteToTrash(copy);
                var held = trash.TrashDirectory + ".held";
                var outside = Path.Combine(paths.Root, "outside-trash");
                Directory.CreateDirectory(outside);
                File.WriteAllText(
                    Path.Combine(outside, "keep.txt"),
                    "unchanged"
                );
                Directory.Move(trash.TrashDirectory, held);
                Directory.CreateSymbolicLink(trash.TrashDirectory, outside);
                Throws<InvalidDataException>(
                    () => manager.RestoreTrash(trash.Id)
                );
                Check(
                    File.ReadAllText(
                        Path.Combine(outside, "keep.txt")
                    ) == "unchanged" && !Directory.Exists(copy.GameDirectory),
                    "no escaped restore"
                );
                Directory.Delete(trash.TrashDirectory);
                Directory.Move(held, trash.TrashDirectory);
                manager.RestoreTrash(trash.Id);
                Check(
                    Directory.Exists(copy.GameDirectory),
                    "restorable after rejecting malformed trash"
                );
            }
        );
        await test(
            "instance_filters_combine_loader_favorite_hidden_and_search",
            () =>
            {
                var selected = manager.Find("重命名", "vanilla", favoritesOnly: true, hiddenOnly: true);
                Check(
                    selected.Count == 1 && selected[0].Id == copied!.Id,
                    "combined filters select intended instance"
                );
                Check(
                    manager.Find(loader: "forge", favoritesOnly: true, hiddenOnly: true)
                        .Count == 0,
                    "loader filter rejects unmatched"
                );
                Check(
                    !manager.Find(includeHidden: false)
                        .Any(
                            x => x.Id == copied!.Id
                        ),
                    "unhidden classification excludes hidden"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "instance_icon_is_passive_bounded_and_follows_copy_backup",
            async () =>
            {
                var icons = new InstanceIconService(paths);
                var png = AccountAppearanceTests.Png(64, 64);
                icons.SavePng(original, png);
                Check(
                    InstanceIconService.Read(original.GameDirectory)!.SequenceEqual(png),
                    "icon persisted within instance"
                );
                Throws<InvalidDataException>(
                    () => InstanceIconService.InspectInput(
                        AccountAppearanceTests.Png(2049, 1)
                    )
                );
                Throws<InvalidDataException>(
                    () => InstanceIconService.InspectInput(
                        System.Text.Encoding.UTF8.GetBytes("<svg><script>doNotExecute()</script></svg>")
                    )
                );
                var copy = await manager.CopyAsync(original, "图标副本");
                Check(
                    InstanceIconService.Read(copy.GameDirectory)!.SequenceEqual(png),
                    "copy carries icon bytes"
                );
                var backup = Path.Combine(paths.Root, "with-icon.zip");
                await manager.BackupAsync(copy, backup);
                using (var archive = ZipFile.OpenRead(backup))
                    Check(
                        archive.GetEntry("game/" + InstanceIconService.FileName)is not null,
                        "backup carries passive icon"
                    );
                icons.Remove(original);
                Check(
                    InstanceIconService.Read(original.GameDirectory)is null && InstanceIconService.Read(copy.GameDirectory)is not null,
                    "reset isolated to selected instance"
                );
                Throws<InvalidDataException>(
                    () => icons.SavePng(
                        original with
                        {
                            GameDirectory = Path.GetDirectoryName(paths.Root)!
                        },
                        png
                    )
                );
            }
        );
    }
}
