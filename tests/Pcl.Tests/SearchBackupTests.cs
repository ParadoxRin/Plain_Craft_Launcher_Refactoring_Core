using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Pcl.Core;

internal static class SearchBackupTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        static void Check(bool value, string message)
        {
            if (!value)
                throw new Exception(message);
        }

        static async Task Reject(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception e)when (e is InvalidDataException or IOException or Newtonsoft.Json.JsonException)
            {
                return;
            }

            throw new Exception("Invalid archive accepted");
        }

        ResourceSearchHit Hit(string id, string source) => new(
            id,
            "Same title",
            "",
            [
                source
            ],
            LocalDirectory: source == "local"
                ? root
                : null
        );
        UnifiedSearchResult Result(ResourceSearchHit[] hits, bool next, string source = "remote", string? error = null) => new(
            hits,
            [
                new(source, hits, next, error)
            ]
        );
        await test(
            "search_session_global_identity_dedup_enrichment_and_cached_back",
            async () =>
            {
                int calls = 0;
                var session = new UnifiedSearchSession(
                    new(ResourceKind.Mod, "snapshot", "1.21.1", "fabric"),
                    (q, ct) =>
                    {
                        calls++;
                        Check(q.Text == "snapshot", "Query snapshot changed");
                        return Task.FromResult(
                            q.Page == 0
                                ? Result(
                                    [
                                        Hit("mr:a", "local"),
                                        Hit("cf:a", "curse")
                                    ],
                                    true
                                )
                                : Result(
                                    [
                                        Hit("mr:a", "Modrinth"),
                                        Hit("mr:b", "Modrinth")
                                    ],
                                    false
                                )
                        );
                    }
                );
                var first = await session.GoToAsync(0);
                Check(first.Hits.Count == 2, "Title incorrectly deduplicated");
                var next = await session.GoToAsync(1);
                Check(
                    next.Hits.Single()
                        .Identity == "mr:b" && session.FilteredDuplicates == 1,
                    "Cross-page identity duplicate retained"
                );
                var back = await session.GoToAsync(0);
                Check(
                    calls == 2 && back.Hits[0].Sources.Count == 2 && back.Hits[0].LocalDirectory == root,
                    "Cached enrichment lost"
                );
                await session.GoToAsync(1);
                Check(calls == 2, "Cached next refetched");
            }
        );
        await test(
            "search_session_cancel_does_not_advance_or_commit",
            async () =>
            {
                var session = new UnifiedSearchSession(
                    new(ResourceKind.Mod, "", "1.21.1", "fabric"),
                    async (q, ct) =>
                    {
                        if (q.Page == 1)
                            await Task.Delay(10000, ct);
                        return Result(
                            [
                                Hit("a", "remote")
                            ],
                            true
                        );
                    }
                );
                await session.GoToAsync(0);
                using var cancel = new CancellationTokenSource(20);
                try
                {
                    await session.GoToAsync(1, ct: cancel.Token);
                    throw new Exception("Cancellation ignored");
                }
                catch (OperationCanceledException)
                {
                }

                Check(session.Page == 0 && session.CachedPages == 1, "Canceled page committed");
            }
        );
        await test(
            "search_session_retry_preserves_success_and_rehomes_duplicate",
            async () =>
            {
                int calls = 0;
                var session = new UnifiedSearchSession(
                    new(ResourceKind.Mod, "", "1.21.1", "fabric"),
                    (q, ct) =>
                    {
                        calls++;
                        if (q.Page == 1)
                            return Task.FromResult(
                                Result(
                                    [
                                        Hit("b", "remote")
                                    ],
                                    false
                                )
                            );
                        return Task.FromResult(
                            new UnifiedSearchResult(
                                calls == 1
                                    ? [
                                        Hit("a", "local")
                                    ]
                                    : [
                                        Hit("changed", "local"),
                                        Hit("b", "remote")
                                    ],
                                [
                                    new(
                                        "local",
                                        [
                                            Hit(
                                                calls == 1
                                                    ? "a"
                                                    : "changed",
                                                "local"
                                            )
                                        ],
                                        false
                                    ),
                                    new(
                                        "remote",
                                        calls == 1
                                            ? []
                                            : [
                                                Hit("b", "remote")
                                            ],
                                        true,
                                        calls == 1
                                            ? "Unavailable"
                                            : null
                                    )
                                ]
                            )
                        );
                    }
                );
                await session.GoToAsync(0);
                await session.GoToAsync(1);
                var retry = await session.GoToAsync(0, true);
                Check(
                    retry.Hits.Select(
                        x => x.Identity
                    )
                        .SequenceEqual(
                            new[]
                            {
                                "a",
                                "b"
                            }
                        ),
                    "Retry replaced good source or lost recovered rows"
                );
                Check(
                    (await session.GoToAsync(1)).Hits.Count == 0,
                    "Recovered earlier duplicate repeated"
                );
            }
        );
        await test(
            "search_session_all_duplicates_keeps_continuation",
            async () =>
            {
                var session = new UnifiedSearchSession(
                    new(ResourceKind.Mod, "", "1.21.1", "fabric"),
                    (q, ct) => Task.FromResult(
                        Result(
                            [
                                Hit(
                                    q.Page == 2
                                        ? "b"
                                        : "a",
                                    "remote"
                                )
                            ],
                            q.Page < 2
                        )
                    )
                );
                await session.GoToAsync(0);
                var middle = await session.GoToAsync(1);
                Check(middle.Hits.Count == 0 && middle.HasNext, "Duplicate-only page ended search");
                Check(
                    (await session.GoToAsync(2)).Hits.Single()
                        .Identity == "b",
                    "Next distinct result lost"
                );
            }
        );
        var data = Path.Combine(root, "backup-v3");
        Directory.CreateDirectory(data);
        using var service = new MinecraftService(
            new AppPaths(data)
        );
        var game = Path.Combine(data, "source");
        Directory.CreateDirectory(
            Path.Combine(game, "saves/世界")
        );
        File.WriteAllText(
            Path.Combine(game, "saves/世界/level.dat"),
            "save-fixture"
        );
        JsonFiles.Write(
            Path.Combine(game, "profile.json"),
            new
            {
                id = "original",
                displayName = "备份实例",
                gameVersion = "1.21.1",
                loader = "vanilla"
            }
        );
        JsonFiles.Write(
            Path.Combine(game, "version.json"),
            new
            {
                id = "original"
            }
        );
        var profile = new GameProfile(
            "original",
            "备份实例",
            "1.21.1",
            "vanilla",
            game,
            Path.Combine(game, "version.json")
        );
        var manager = new InstanceManager(service);
        var good = Path.Combine(data, "good.zip");
        await test(
            "backup_v3_hashes_exact_bytes_and_detects_same_size_damage",
            async () =>
            {
                await manager.BackupAsync(profile, good);
                var receipt = await InstanceBackupArchive.VerifyAsync(good);
                Check(receipt.HasFileHashes && receipt.Files == 3, "Missing complete hash index");
                var damaged = Path.Combine(data, "damaged.zip");
                File.Copy(good, damaged);
                using (var zip = ZipFile.Open(damaged, ZipArchiveMode.Update))
                {
                    zip.GetEntry("game/saves/世界/level.dat")!.Delete();
                    using var w = new StreamWriter(
                        zip.CreateEntry("game/saves/世界/level.dat")
                            .Open()
                    );
                    w.Write("Save-fixture");
                }

                await Reject(
                    () => InstanceBackupArchive.VerifyAsync(damaged)
                );
                await Reject(
                    () => manager.RestoreBackupAsync(damaged)
                );
                Check(
                    !Directory.EnumerateDirectories(
                        Path.Combine(data, "instances")
                    )
                        .Any(),
                    "Damaged restore left instance or stage"
                );
            }
        );
        await test(
            "backup_v3_missing_extra_and_modified_manifest_rejected",
            async () =>
            {
                foreach (var mode in new[]
                {
                    "missing",
                    "extra",
                    "hash",
                    "size"
                }

                )
                {
                    var file = Path.Combine(data, mode + ".zip");
                    File.Copy(good, file);
                    using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
                    {
                        if (mode == "missing")
                            zip.GetEntry("game/version.json")!.Delete();
                        else if (mode == "extra")
                        {
                            using var w = new StreamWriter(
                                zip.CreateEntry("game/extra")
                                    .Open()
                            );
                            w.Write("extra");
                        }
                        else
                        {
                            JObject json;
                            using (var r = new StreamReader(
                                zip.GetEntry("pcl-backup.json")!.Open()
                            ))
                                json = JObject.Parse(
                                    r.ReadToEnd()
                                );
                            json["files"]![0]![mode == "hash"
                                ? "sha256"
                                : "size"] = mode == "hash"
                                ? new JValue(
                                    new string ('0', 64)
                                )
                                : new JValue(-1);
                            zip.GetEntry("pcl-backup.json")!.Delete();
                            using var w = new StreamWriter(
                                zip.CreateEntry("pcl-backup.json")
                                    .Open()
                            );
                            w.Write(
                                json.ToString()
                            );
                        }
                    }

                    await Reject(
                        () => InstanceBackupArchive.VerifyAsync(file)
                    );
                }
            }
        );
        await test(
            "backup_case_unicode_duplicate_and_symlink_rejected",
            async () =>
            {
                foreach (var pair in new[]
                {
                    ("game/Config.txt", "game/config.txt"),
                    ("game/é.txt", "game/e\u0301.txt"),
                    ("game/link", "symlink")
                }

                )
                {
                    var file = Path.Combine(
                        data,
                        Guid.NewGuid() + ".zip"
                    );
                    using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
                    {
                        using (var w = new StreamWriter(
                            zip.CreateEntry("pcl-backup.json")
                                .Open()
                        ))
                            w.Write("{\"format\":\"pcl-mac-instance-backup\",\"schemaVersion\":2}");
                        var entry = zip.CreateEntry(pair.Item1);
                        if (pair.Item2 == "symlink")
                            entry.ExternalAttributes = 0xa000 << 16;
                        else
                            zip.CreateEntry(pair.Item2);
                    }

                    await Reject(
                        () => InstanceBackupArchive.VerifyAsync(file)
                    );
                }
            }
        );
        await test(
            "backup_legacy_explicitly_reports_no_hashes",
            async () =>
            {
                var file = Path.Combine(data, "legacy.zip");
                using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
                {
                    using (var w = new StreamWriter(
                        zip.CreateEntry("pcl-backup.json")
                            .Open()
                    ))
                        w.Write("{\"format\":\"pcl-mac-instance-backup\",\"schemaVersion\":2}");
                    using (var w = new StreamWriter(
                        zip.CreateEntry("game/options.txt")
                            .Open()
                    ))
                        w.Write("test");
                }

                var r = await InstanceBackupArchive.VerifyAsync(file);
                Check(!r.HasFileHashes && r.Files == 1, "Legacy falsely claimed hash verification");
            }
        );
        await test(
            "backup_cancel_preserves_source_and_removes_partial",
            async () =>
            {
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                var destination = Path.Combine(data, "cancel.zip");
                try
                {
                    await manager.BackupAsync(profile, destination, cancel.Token);
                    throw new Exception("Cancellation ignored");
                }
                catch (OperationCanceledException)
                {
                }

                Check(
                    !File.Exists(destination) && !Directory.EnumerateFiles(data, "*.partial-*")
                        .Any() && File.ReadAllText(
                        Path.Combine(game, "saves/世界/level.dat")
                    ) == "save-fixture",
                    "Cancellation changed source or left output"
                );
            }
        );
        await test(
            "backup_restore_cancel_removes_staging_without_registration",
            async () =>
            {
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                try
                {
                    await manager.RestoreBackupAsync(good, ct: cancel.Token);
                    throw new Exception("Cancellation ignored");
                }
                catch (OperationCanceledException)
                {
                }

                Check(
                    !Directory.EnumerateDirectories(
                        Path.Combine(data, "instances")
                    )
                        .Any(),
                    "Canceled restore left data"
                );
            }
        );
        await test(
            "backup_preferences_and_patch_commit_atomically",
            () =>
            {
                manager.Settings.SaveInstance(
                    game,
                    new("Before"),
                    new()
                    {
                        Width = 1280
                    }
                );
                try
                {
                    manager.Settings.SaveInstance(
                        game,
                        new("After"),
                        new()
                        {
                            MinimumMemoryMb = 100000
                        }
                    );
                    throw new Exception("Invalid patch accepted");
                }
                catch (InvalidDataException)
                {
                }

                Check(
                    manager.Settings.Preferences(game)
                        .Name == "Before" && manager.Settings.Effective(game)
                        .Width == 1280,
                    "Partial preferences commit"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "backup_invalid_settings_fail_before_network_or_registration",
            async () =>
            {
                var file = Path.Combine(data, "invalid-settings.zip");
                File.Copy(good, file);
                using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
                {
                    JObject json;
                    using (var r = new StreamReader(
                        zip.GetEntry("pcl-backup.json")!.Open()
                    ))
                        json = JObject.Parse(
                            r.ReadToEnd()
                        );
                    json["preferences"] = new JObject
                    {
                        ["Description"] = new string ('x', 2001)
                    };
                    zip.GetEntry("pcl-backup.json")!.Delete();
                    using var w = new StreamWriter(
                        zip.CreateEntry("pcl-backup.json")
                            .Open()
                    );
                    w.Write(
                        json.ToString()
                    );
                }

                await Reject(
                    () => manager.RestoreBackupAsync(file)
                );
                Check(
                    !Directory.EnumerateDirectories(
                        Path.Combine(data, "instances")
                    )
                        .Any(),
                    "Invalid settings left registered data"
                );
            }
        );
    }
}
