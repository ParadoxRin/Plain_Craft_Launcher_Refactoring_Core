using System.Text.Json;
using System.Text.Json.Nodes;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

internal static class DiscoverySettingsFlow
{
    // 真实独立 Host 上检查公开搜索、原安装器、设置落盘和跨连接恢复；仅使用隔离的数据目录。
    internal static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        report = Path.GetFullPath(report);
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new Exception(label);
            }

            checks.Add(label);
            Console.WriteLine(label);
        }

        // 固定元数据替身只覆盖“作者禁止自动下载”的本地匹配路径，不冒充真实 CF API 验收。
        var manualFile = Path.Combine(root, "manual-resource.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(manualFile, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(
                zip.CreateEntry("pack.mcmeta")
                    .Open()
            );
            writer.Write("{\"pack\":{\"pack_format\":34,\"description\":\"CF manual fixture\"}}");
        }

        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA1.HashData(
                File.ReadAllBytes(manualFile)
            )
        )
            .ToLowerInvariant();
        var privateId = "op-" + Guid.NewGuid()
            .ToString("N");
        var release = new Pcl.Core.CurseResourceRelease(
            29001,
            29002,
            "Manual resource fixture",
            "manual-resource.zip",
            sha,
            new FileInfo(manualFile).Length,
            null,
            DateTimeOffset.UtcNow,
            "release",
            [
                "1.21.1"
            ],
            [],
            "https://example.invalid/manual-file"
        );
        using (var store = new Pcl.Core.Adapters.Operations.FileOperationStore(root, "page-execution-v1"))
        {
            store.Save(
                privateId,
                JsonSerializer.SerializeToUtf8Bytes(
                    new Pcl.Core.Adapters.Pages.PrivatePageRecord(
                        1,
                        new(privateId),
                        "search-releases",
                        JsonSerializer.SerializeToElement(
                            new Pcl.Core.Adapters.Pages.ReleaseSelection(
                                "1.21.1",
                                Pcl.Core.ResourceKind.ResourcePack,
                                [],
                                [
                                    release
                                ]
                            )
                        )
                    )
                )
            );
        }

        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
        await using var session = new LauncherSession(
            new HostClient(
                native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
                native is null
                    ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                    : "",
                root,
                reconnectable: true
            )
        );
        try
        {
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            var manualSelection = privateId + ":0";
            var refused = false;
            try
            {
                await api.CallAsync<SearchInstalled>(
                    "workspace.search.install",
                    "1.21.1",
                    new
                    {
                        selection = manualSelection
                    }
                );
            }
            catch
            {
                refused = true;
            }

            Check(refused, "fixture-cf-blocked-download-requires-verified-local-file");
            var bad = Path.Combine(root, "bad-manual.zip");
            await File.WriteAllTextAsync(bad, "invalid");
            refused = false;
            try
            {
                await api.CallAsync<bool>(
                    "workspace.search.match",
                    "1.21.1",
                    new
                    {
                        selection = manualSelection,
                        path = bad
                    }
                );
            }
            catch
            {
                refused = true;
            }

            Check(refused, "fixture-cf-manual-hash-mismatch-rejected");
            await api.CallAsync<bool>(
                "workspace.search.match",
                "1.21.1",
                new
                {
                    selection = manualSelection,
                    path = manualFile
                }
            );
            var fixtureSecret = await api.SecretAsync("fixture-no-network-key");
            var manualInstalled = await api.CallAsync<SearchInstalled>(
                "workspace.search.install",
                "1.21.1",
                new
                {
                    selection = manualSelection,
                    secretHandle = fixtureSecret
                }
            );
            Check(
                manualInstalled.Files == 1,
                "fixture-cf-verified-local-file-installs-without-network-download"
            );
            var initial = await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
            var next = initial.Appearance with
            {
                Title = "PCL 0.29 Host settings",
                Theme = "dark",
                HiddenRoutes = []
            };
            var updated = await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = next
                    },
                    expected = new
                    {
                        Appearance = initial.Appearance
                    }
                }
            );
            Check(
                updated.Appearance.Title == next.Title && File.Exists(
                    Path.Combine(root, "appearance.json")
                ),
                "appearance-save-through-host"
            );
            var conflict = false;
            try
            {
                await api.CallAsync<SettingsSnapshot>(
                    "workspace.settings.save",
                    null,
                    new
                    {
                        changes = new
                        {
                            Appearance = initial.Appearance
                        },
                        expected = new
                        {
                            Appearance = initial.Appearance
                        }
                    }
                );
            }
            catch (OperationRejectedException e)when (e.Code == "SETTINGS_CHANGED_RELOAD")
            {
                conflict = true;
            }

            Check(conflict, "stale-settings-snapshot-cannot-overwrite-new-value");
            var invalid = false;
            try
            {
                await api.CallAsync<SettingsSnapshot>(
                    "workspace.settings.save",
                    null,
                    new
                    {
                        changes = new
                        {
                            Appearance = next with
                            {
                                Theme = "invalid"
                            },
                            Hook = new LaunchHookSettings(true, "/usr/bin/true")
                        }
                    }
                );
            }
            catch
            {
                invalid = true;
            }

            var unchanged = await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
            Check(
                invalid && unchanged.Hook == initial.Hook && unchanged.Appearance.Title == next.Title,
                "validate-all-settings-before-any-write"
            );
            var changedLaunch = JsonNode.Parse(
                initial.Launch.GetRawText()
            )!.AsObject();
            changedLaunch["Width"] = 1234;
            var changedNetwork = JsonNode.Parse(
                initial.Network.GetRawText()
            )!.AsObject();
            changedNetwork["ConcurrentFiles"] = 3;
            var configured = await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Launch = changedLaunch,
                        Network = changedNetwork,
                        Hook = new LaunchHookSettings(true, "/usr/bin/true", "", true, 10)
                    }
                }
            );
            Check(
                configured.Launch.GetProperty("Width")
                    .GetInt32() == 1234 && configured.Network.GetProperty("ConcurrentFiles")
                    .GetInt32() == 3 && configured.Hook.Enabled,
                "global-launch-network-and-hook-save-and-read"
            );
            var exported = Path.Combine(root, "exported-settings.json");
            await api.ExportAsync(
                "workspace.settings.export",
                null,
                new { },
                exported
            );
            var exportText = await File.ReadAllTextAsync(exported);
            Check(
                !exportText.Contains("/usr/bin/true") && exportText.Contains(next.Title),
                "controlled-settings-export-omits-trusted-hook"
            );
            await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = new AppearanceSettings()
                    }
                }
            );
            var restored = await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.import",
                null,
                new
                {
                    path = exported
                }
            );
            var backupFolder = await api.CallAsync<string>("workspace.settings.backups", null);
            Check(
                restored.Appearance.Title == next.Title && Directory.GetFiles(backupFolder, "*.json", SearchOption.AllDirectories)
                    .Length > 0 && restored.Hook.Enabled,
                "settings-import-restores-portable-values-backs-up-and-preserves-hook"
            );
            var example = await api.CallAsync<string>("workspace.settings.example", null);
            await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = restored.Appearance with
                        {
                            HomeDocument = example,
                            HomeMode = "document"
                        }
                    }
                }
            );
            var preset = await api.CallAsync<SettingsSnapshot>("workspace.settings.savePreset", null);
            File.Move(example, example + ".moved");
            var home = await api.CallAsync<JsonElement>("workspace.settings.home", null);
            Check(
                preset.Appearance.HomePreset.StartsWith("local-") && home.GetProperty("Title")
                    .GetString() == "我的主页",
                "home-preset-is-independent-offline-copy"
            );
            var removed = await api.CallAsync<SettingsSnapshot>("workspace.settings.removePreset", null);
            Check(
                removed.Presets.Length == 0 && removed.Appearance.HomePreset == "welcome",
                "host-removes-custom-preset-and-restores-builtin"
            );
            var cachePath = Path.Combine(root, "cache/discovery-test.part");
            Directory.CreateDirectory(
                Path.GetDirectoryName(cachePath)!
            );
            await File.WriteAllTextAsync(cachePath, "partial-test");
            File.SetLastWriteTimeUtc(
                cachePath,
                DateTime.UtcNow.AddDays(-2)
            );
            var preview = await api.CallAsync<CachePreview>("workspace.settings.cachePreview", null);
            var candidate = preview.Items.Single(
                x => x.RelativePath == "cache/discovery-test.part"
            );
            await File.AppendAllTextAsync(cachePath, "changed");
            var skipped = await api.CallAsync<CacheCleanupResult>(
                "workspace.settings.cacheMove",
                null,
                new
                {
                    handle = preview.Handle,
                    paths = new[]
                    {
                        candidate.RelativePath
                    }
                }
            );
            Check(
                skipped.Skipped.Count > 0 && File.Exists(cachePath),
                "cache-preview-change-is-skipped"
            );
            File.SetLastWriteTimeUtc(
                cachePath,
                DateTime.UtcNow.AddDays(-2)
            );
            preview = await api.CallAsync<CachePreview>("workspace.settings.cachePreview", null);
            var trash = await api.CallAsync<CacheCleanupResult>(
                "workspace.settings.cacheMove",
                null,
                new
                {
                    handle = preview.Handle,
                    paths = new[]
                    {
                        candidate.RelativePath
                    }
                }
            );
            Check(
                !File.Exists(cachePath) && trash.Batch is not null,
                "cache-move-to-recoverable-trash"
            );
            await api.CallAsync<CacheCleanupResult>(
                "workspace.settings.cacheRestore",
                null,
                new
                {
                    id = trash.Batch!.Id
                }
            );
            Check(
                File.Exists(cachePath),
                "cache-restore-original-location"
            );
            var targets = await api.CallAsync<NetworkProbeTarget[]>("workspace.settings.networkTargets", null);
            var probe = await api.CallAsync<NetworkProbeResult>(
                "workspace.settings.probe",
                null,
                new
                {
                    target = new NetworkProbeTarget("Modrinth public", "https://api.modrinth.com/v2/tag/category")
                }
            );
            Check(
                targets.Length > 0 && probe.Success && probe.Bytes <= 65536,
                "public-network-probe-bounded-read"
            );
            var index = await api.CallAsync<SearchPage>(
                "workspace.search.page",
                "1.21.1",
                new
                {
                    kind = 0,
                    provider = "mcmod",
                    online = false,
                    query = "",
                    page = 0
                }
            );
            Check(
                index.Hits.Length > 0 && index.Hits.All(
                    x => x.McmodUrl is not null
                ),
                "offline-mcmod-index-search"
            );
            await session.Client.DisconnectAsync();
            await session.Client.ReconnectAsync();
            var cached = await api.CallAsync<SearchPage>(
                "workspace.search.page",
                "1.21.1",
                new
                {
                    session = index.Session,
                    page = 0
                }
            );
            Check(
                cached.Hits.Select(
                    x => x.Title
                )
                    .SequenceEqual(
                        index.Hits.Select(
                            x => x.Title
                        )
                    ),
                "same-host-session-reconnect-keeps-page-snapshot"
            );
            if (index.HasNext)
            {
                var second = await api.CallAsync<SearchPage>(
                    "workspace.search.page",
                    "1.21.1",
                    new
                    {
                        session = index.Session,
                        page = 1
                    }
                );
                Check(
                    !second.Hits.Select(
                        x => x.Title
                    )
                        .Intersect(
                            index.Hits.Select(
                                x => x.Title
                            )
                        )
                        .Any(),
                    "search-next-page-deduplication"
                );
                var back = await api.CallAsync<SearchPage>(
                    "workspace.search.page",
                    "1.21.1",
                    new
                    {
                        session = index.Session,
                        page = 0
                    }
                );
                Check(back.Hits.Length == index.Hits.Length, "search-back-cached-page");
            }

            var missingKey = false;
            try
            {
                await api.CallAsync<SearchPage>(
                    "workspace.search.page",
                    "1.21.1",
                    new
                    {
                        kind = 0,
                        provider = "curseforge",
                        query = "fabric",
                        page = 0
                    }
                );
            }
            catch
            {
                missingKey = true;
            }

            Check(missingKey, "curseforge-no-key-rejected-without-install");
            var fabric = await api.CallAsync<GameProfile>(
                "workspace.browser.ensureFabric",
                null,
                new
                {
                    gameVersion = "1.21.1",
                    selectedId = "1.21.1"
                }
            );
            var online = await api.CallAsync<SearchPage>(
                "workspace.search.page",
                fabric.Id,
                new
                {
                    kind = 0,
                    provider = "all",
                    online = true,
                    query = "fabric api",
                    page = 0
                }
            );
            var hit = online.Hits.First(
                x => x.Title.Equals("Fabric API", StringComparison.OrdinalIgnoreCase) && x.Sources.Contains("Modrinth")
            );
            var releases = await api.CallAsync<SearchVersions>(
                "workspace.search.versions",
                fabric.Id,
                new
                {
                    selection = hit.Selection
                }
            );
            Check(releases.Releases.Length > 0, "public-search-compatible-versions-through-host");
            var badSelection = false;
            try
            {
                await api.CallAsync<SearchVersions>(
                    "workspace.search.versions",
                    "1.21.1",
                    new
                    {
                        selection = hit.Selection
                    }
                );
            }
            catch
            {
                badSelection = true;
            }

            Check(badSelection, "search-selection-bound-to-original-instance");
            var installed = await api.CallAsync<SearchInstalled>(
                "workspace.search.install",
                fabric.Id,
                new
                {
                    selection = releases.Releases[0].Selection
                }
            );
            Check(
                installed.Files > 0 && Directory.GetFiles(
                    Path.Combine(fabric.GameDirectory, "mods"),
                    "*.jar"
                )
                    .Length > 0,
                "real-fabric-api-download-dependency-resolution-and-install"
            );
            var fakeSecret = "pcl029-not-a-real-key-" + Guid.NewGuid()
                .ToString("N");
            await api.SecretAsync(fakeSecret);
            Check(
                !Directory.GetFiles(
                    Path.Combine(root, "page-operations-v1"),
                    "*.json"
                )
                    .Any(
                        p => File.ReadAllText(p)
                            .Contains(fakeSecret)
                    ),
                "secret-never-written-to-operation-receipts"
            );
            await session.ShutdownAsync();
            await using (var restarted = new LauncherSession(
                new HostClient(
                    native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
                    native is null
                        ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                        : "",
                    root,
                    reconnectable: true
                )
            ))
            {
                await restarted.InitializeAsync();
                var nextApi = new WorkspaceApi(restarted.Workspaces);
                var persisted = await nextApi.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
                Check(
                    persisted.Appearance.Title == next.Title && persisted.Hook.Enabled,
                    "host-restart-restores-settings-and-trusted-hook"
                );
                var expired = false;
                try
                {
                    await nextApi.CallAsync<SearchPage>(
                        "workspace.search.page",
                        "1.21.1",
                        new
                        {
                            session = index.Session,
                            page = 0
                        }
                    );
                }
                catch (OperationRejectedException e)when (e.Code == "SEARCH_SESSION_EXPIRED")
                {
                    expired = true;
                }

                Check(expired, "host-restart-explicitly-expires-memory-search-session");
                await restarted.ShutdownAsync();
            }

            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "passed",
                        Checks = checks,
                        Limitations = new[]
                        {
                            "No real CurseForge API key supplied; keyed API not tested."
                        }
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        Status = "failed",
                        Checks = checks,
                        Error = ex.ToString()
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
            try
            {
                await session.ShutdownAsync();
            }
            catch
            {
            }

            throw;
        }
    }
}
