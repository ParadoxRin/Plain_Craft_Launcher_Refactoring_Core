using System.IO.Compression;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using Pcl.Core;

internal static class PackResourceTests
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> test)
    {
        var folder = Path.Combine(
            root,
            "pack-resource-tests-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(folder);
        void Check(bool ok, string reason)
        {
            if (!ok)
                throw new Exception(reason);
        }

        void Throws<T>(Action action)
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

        string Zip(string name, params (string Path, string Text)[] files)
        {
            var path = Path.Combine(folder, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var item in files)
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry(item.Path)
                        .Open()
                );
                writer.Write(item.Text);
            }

            return path;
        }

        await test(
            "pack_import_all_declared_formats",
            async () =>
            {
                var files = new[]
                {
                    Zip(
                        "cf.zip",
                        ("manifest.json", "{\"manifestType\":\"minecraftModpack\",\"manifestVersion\":1,\"minecraft\":{\"version\":\"1.20.1\",\"modLoaders\":[{\"id\":\"fabric-0.16.0\"}]},\"overrides\":\"overrides\",\"files\":[]}"),
                        ("overrides/config/test.txt", "cf")
                    ),
                    Zip(
                        "mmc.zip",
                        ("instance/mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.20.1\"},{\"uid\":\"net.fabricmc.fabric-loader\",\"version\":\"0.16.0\"}]}"),
                        ("instance/.minecraft/config/test.txt", "mmc")
                    ),
                    Zip(
                        "hmcl.zip",
                        ("modpack.json", "{\"name\":\"HMCL\",\"gameVersion\":\"1.20.1\"}"),
                        ("minecraft/config/test.txt", "hmcl")
                    ),
                    Zip(
                        "mcbbs.zip",
                        ("mcbbs.packmeta", "{\"addons\":[{\"id\":\"game\",\"version\":\"1.20.1\"},{\"id\":\"fabric\",\"version\":\"0.16.0\"}]}"),
                        ("overrides/config/test.txt", "mcbbs")
                    ),
                    Zip(
                        "plain.zip",
                        ("config/test.txt", "plain"),
                        ("launcher.exe", "not copied"),
                        ("start.sh", "must not execute")
                    )
                };
                using var service = new MinecraftService(
                    new AppPaths(
                        Path.Combine(folder, "launcher")
                    )
                );
                var converter = new PortableModpackService(service);
                foreach (var file in files)
                {
                    var options = new PackImportOptions("1.20.1");
                    var plan = PortableModpackService.Inspect(file, options);
                    Check(plan.Overrides.Count == 1, "Expected one config file");
                    var converted = await converter.ConvertAsync(file, options);
                    var mr = ModpackService.Inspect(converted);
                    Check(mr.Minecraft == "1.20.1", "Version mismatch");
                    var extracted = Path.Combine(
                        folder,
                        Path.GetFileNameWithoutExtension(file)
                    );
                    await ModpackService.ExtractOverridesAsync(converted, extracted);
                    Check(
                        File.Exists(
                            Path.Combine(extracted, "config/test.txt")
                        ),
                        "Missing converted config"
                    );
                    Check(
                        !File.Exists(
                            Path.Combine(extracted, "start.sh")
                        ),
                        "Executed data copied"
                    );
                }
            }
        );
        await test(
            "pack_rejects_traversal_symlink_case_collision_and_custom_patch",
            () =>
            {
                Throws<InvalidDataException>(
                    () => PortableModpackService.Inspect(
                        Zip("evil.zip", ("../escape", "bad")),
                        new("1.20.1")
                    )
                );
                Throws<InvalidDataException>(
                    () => PortableModpackService.Inspect(
                        Zip("case.zip", ("config/A", "a"), ("config/a", "b")),
                        new("1.20.1")
                    )
                );
                var link = Path.Combine(folder, "symlink.zip");
                using (var z = ZipFile.Open(link, ZipArchiveMode.Create))
                {
                    var e = z.CreateEntry("config/link");
                    e.ExternalAttributes = unchecked((int)0xa1ff0000);
                }

                Throws<InvalidDataException>(
                    () => PortableModpackService.Inspect(
                        link,
                        new("1.20.1")
                    )
                );
                Throws<NotSupportedException>(
                    () => PortableModpackService.Inspect(
                        Zip(
                            "custom.zip",
                            ("mmc-pack.json", "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.20.1\"}]}"),
                            ("patches/custom.json", "{}")
                        )
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "pack_export_privacy_hashes_and_roundtrip",
            async () =>
            {
                var game = Path.Combine(folder, "export-source");
                Directory.CreateDirectory(
                    Path.Combine(game, "config")
                );
                Directory.CreateDirectory(
                    Path.Combine(game, "saves/World")
                );
                File.WriteAllText(
                    Path.Combine(game, "config/keep.json"),
                    "{\"test\":true}"
                );
                File.WriteAllText(
                    Path.Combine(game, "config/tokens.json"),
                    "secret"
                );
                File.WriteAllText(
                    Path.Combine(game, "launcher_accounts.json"),
                    "secret"
                );
                File.WriteAllText(
                    Path.Combine(game, "servers.dat"),
                    "private"
                );
                File.WriteAllText(
                    Path.Combine(game, "saves/World/level.dat"),
                    "world"
                );
                var profile = new GameProfile(
                    "export",
                    "测试 包",
                    "1.20.1",
                    "vanilla",
                    game,
                    Path.Combine(game, "version.json")
                );
                var output = Path.Combine(folder, "export.mrpack");
                await PortableModpackService.ExportAsync(
                    profile,
                    output,
                    new("测试 包")
                );
                var plan = ModpackService.Inspect(output);
                Check(plan.Loader == "vanilla", "Vanilla export failed");
                using var zip = ZipFile.OpenRead(output);
                Check(
                    zip.GetEntry("overrides/config/keep.json")is not null,
                    "Config missing"
                );
                Check(
                    !zip.Entries.Any(
                        x => x.FullName.Contains("secret") || x.FullName.Contains("tokens.json") || x.FullName.Contains("saves/") || x.FullName.Contains("servers.dat") || x.FullName.Contains("accounts.json")
                    ),
                    "Private data leaked"
                );
                using var reader = new StreamReader(
                    zip.GetEntry("pcl-export.json")!.Open()
                );
                var receipt = JObject.Parse(
                    reader.ReadToEnd()
                );
                Check(
                    receipt["files"]!.Count() == 1 && receipt["files"]![0]!["sha512"]!.ToString()
                        .Length == 128,
                    "Hash receipt missing"
                );
                var target = Path.Combine(folder, "roundtrip");
                await ModpackService.ExtractOverridesAsync(output, target);
                Check(
                    File.ReadAllText(
                        Path.Combine(target, "config/keep.json")
                    ) == "{\"test\":true}",
                    "Roundtrip changed data"
                );
            }
        );
        await test(
            "curse_api_metadata_and_publisher_disabled_download",
            async () =>
            {
                using var handler = new CurseHandler();
                using var client = new CurseForgeClient("test-only-key", handler);
                var file = await client.FileAsync(123, 456);
                Check(
                    file.DownloadUrl is null && file.Folder == "mods" && file.Sha1.Length == 40,
                    "Publisher restriction lost"
                );
                Check(handler.Requests == 2 && handler.KeySeen, "API contract wrong");
            }
        );
        await test(
            "pack_update_baseline_distinguishes_modified_added_deleted_and_private",
            () =>
            {
                var game = Path.Combine(folder, "update-baseline");
                Directory.CreateDirectory(
                    Path.Combine(game, "config")
                );
                File.WriteAllText(
                    Path.Combine(game, "config/modified.json"),
                    "old"
                );
                File.WriteAllText(
                    Path.Combine(game, "config/deleted.json"),
                    "old"
                );
                File.WriteAllText(
                    Path.Combine(game, "config/same.json"),
                    "same"
                );
                JsonFiles.Write(
                    Path.Combine(game, "pcl-modpack.json"),
                    new
                    {
                        packDataHashes = PortableModpackService.CaptureDataHashes(game)
                    }
                );
                File.WriteAllText(
                    Path.Combine(game, "config/modified.json"),
                    "new"
                );
                File.Delete(
                    Path.Combine(game, "config/deleted.json")
                );
                File.WriteAllText(
                    Path.Combine(game, "config/added.json"),
                    "added"
                );
                File.WriteAllText(
                    Path.Combine(game, "config/tokens.json"),
                    "secret"
                );
                var profile = new GameProfile("update", "Update", "1.20.1", "vanilla", game, "unused");
                Check(
                    PortableModpackService.ChangedUserFiles(profile)
                        .Order()
                        .SequenceEqual(
                            new[]
                            {
                                "config/added.json",
                                "config/modified.json"
                            }
                        ),
                    "Wrong changed file set"
                );
                Check(
                    PortableModpackService.DeletedUserFiles(profile)
                        .SequenceEqual(
                            new[]
                            {
                                "config/deleted.json"
                            }
                        ),
                    "Deleted file not preserved"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "curse_missing_credentials_blocks_before_instance_install",
            async () =>
            {
                var zip = Zip(
                    "cf-missing.zip",
                    ("manifest.json", "{\"manifestType\":\"minecraftModpack\",\"manifestVersion\":1,\"minecraft\":{\"version\":\"1.20.1\",\"modLoaders\":[{\"id\":\"fabric-0.16.0\"}]},\"files\":[{\"projectID\":123,\"fileID\":456,\"required\":true}]}")
                );
                using var service = new MinecraftService(
                    new AppPaths(
                        Path.Combine(folder, "missing-key")
                    )
                );
                try
                {
                    await new PortableModpackService(service).ConvertAsync(
                        zip,
                        new()
                    );
                    throw new Exception("Expected missing API failure");
                }
                catch (InvalidOperationException ex)
                {
                    Check(
                        ex.Message.Contains("123:456") && ex.Message.Contains("curseforge.com"),
                        "No useful manual recovery"
                    );
                }

                Check(
                    !Directory.Exists(
                        Path.Combine(service.Paths.Root, "instances")
                    ),
                    "Created instance before prerequisites satisfied"
                );
            }
        );
        await test(
            "curse_catalog_filters_dependencies_and_file_restrictions",
            async () =>
            {
                using var handler = new CurseCatalogHandler();
                using var catalog = new CurseForgeCatalog("test-only-key", handler);
                var projects = await catalog.SearchAsync(ResourceKind.Mod, "a b", "1.20.1", "fabric");
                Check(
                    projects.Single()
                        .Id == 100 && handler.SearchFiltered,
                    "CurseForge filters missing"
                );
                var versions = await catalog.VersionsAsync(100, ResourceKind.Mod, "1.20.1", "fabric");
                var plan = await catalog.ResolveAsync(
                    versions.Single(),
                    ResourceKind.Mod,
                    "1.20.1",
                    "fabric"
                );
                Check(
                    plan.Releases.Count == 2 && plan.Releases[0].ProjectId == "curse-project-200" && plan.Releases[1].ProjectId == "curse-project-100",
                    "Dependency order wrong"
                );
                handler.RestrictDependency = true;
                try
                {
                    await catalog.ResolveAsync(
                        versions.Single(),
                        ResourceKind.Mod,
                        "1.20.1",
                        "fabric"
                    );
                    throw new Exception("Restricted download accepted");
                }
                catch (InvalidOperationException ex)
                {
                    Check(
                        ex.Message.Contains("/200/files/2000"),
                        "Missing manual recovery URL"
                    );
                }

                var manual = await catalog.ResolveAsync(
                    versions.Single(),
                    ResourceKind.Mod,
                    "1.20.1",
                    "fabric",
                    new HashSet<long>
                    {
                        2000
                    }
                );
                Check(manual.Releases.Count == 2, "Verified manual recovery rejected");
                Check(
                    CurseForgeCatalog.ClassId(ResourceKind.Shader) == 6552 && CurseForgeCatalog.ClassId(ResourceKind.Modpack) == 4471,
                    "Resource class mapping wrong"
                );
            }
        );
        await test(
            "modrinth_datapack_filters_need_no_fabric_loader",
            async () =>
            {
                using var handler = new DatapackHandler();
                using var catalog = new ResourceCatalog(handler);
                var search = await catalog.SearchAsync(ResourceKind.DataPack, "test", "1.21.1", "forge");
                var versions = await catalog.VersionsAsync("test", ResourceKind.DataPack, "1.21.1", "forge");
                Check(handler.ValidSearch && handler.ValidVersions, "Datapack loader filters incorrect");
            }
        );
        await test(
            "local_scan_toggle_and_dependency_rollback",
            () =>
            {
                var game = Path.Combine(folder, "local");
                Directory.CreateDirectory(
                    Path.Combine(game, "mods")
                );
                JsonFiles.Write(
                    Path.Combine(game, "profile.json"),
                    new
                    {
                        loaderVersion = "0.16.0"
                    }
                );
                var a = Zip(
                    "a.jar",
                    ("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"a\",\"name\":\"A\",\"version\":\"1.0.0\"}")
                );
                var b = Zip(
                    "b.jar",
                    ("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"b\",\"name\":\"B\",\"version\":\"1.0.0\",\"depends\":{\"a\":\">=1.0.0\"}}")
                );
                File.Copy(
                    a,
                    Path.Combine(game, "mods/a.jar")
                );
                File.Copy(
                    b,
                    Path.Combine(game, "mods/b.jar")
                );
                var service = new LocalResourceService(game, "fabric", "1.20.1");
                Check(
                    service.Scan()
                        .Count == 2,
                    "External files absent"
                );
                Throws<InvalidDataException>(
                    () => service.SetEnabled(
                        [
                            Path.Combine(game, "mods/a.jar")
                        ],
                        false
                    )
                );
                Check(
                    File.Exists(
                        Path.Combine(game, "mods/a.jar")
                    ) && !File.Exists(
                        Path.Combine(game, "mods/a.jar.disabled")
                    ),
                    "Dependency rollback failed"
                );
                service.SetEnabled(
                    service.Scan()
                        .Select(
                            x => x.Path
                        ),
                    false
                );
                Check(
                    service.Scan()
                        .All(
                            x => !x.Enabled
                        ),
                    "Batch disable failed"
                );
                service.SetEnabled(
                    service.Scan()
                        .Select(
                            x => x.Path
                        ),
                    true
                );
                Check(
                    service.Scan()
                        .All(
                            x => x.Enabled
                        ),
                    "Batch enable failed"
                );
                var backup = service.Remove(
                    service.Scan()
                        .Select(
                            x => x.Path
                        )
                );
                Check(
                    service.Scan()
                        .Count == 0 && File.Exists(
                        Path.Combine(backup, "rollback.json")
                    ),
                    "Recoverable remove failed"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "resource_metadata_legacy_forge_neoforge_quilt_liteloader",
            () =>
            {
                foreach (var item in new[]
                {
                    (Loader: "forge", Game: "1.12.2", Meta: "mcmod.info"),
                    (Loader: "neoforge", Game: "1.20.1", Meta: "META-INF/mods.toml"),
                    (Loader: "quilt", Game: "1.20.1", Meta: "quilt.mod.json"),
                    (Loader: "quilt", Game: "1.20.1", Meta: "fabric.mod.json"),
                    (Loader: "liteloader", Game: "1.12.2", Meta: "litemod.json")
                }

                )
                {
                    using var bytes = new MemoryStream();
                    using var zip = new ZipArchive(bytes, ZipArchiveMode.Update);
                    zip.CreateEntry(item.Meta);
                    ResourceInstaller.ValidateModArchive(zip, item.Loader, item.Game);
                }

                Check(
                    ResourceInstaller.ModExtension("liteloader") == ".litemod",
                    "LiteLoader extension lost"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "resource_alias_embedded_database_and_explicit_cross_platform_identity",
            () =>
            {
                var alias = ResourceAliases.Find("industrial-craft") ?? throw new Exception("Missing embedded wiki data");
                Check(
                    alias.ChineseName!.Contains("工业") && alias.WikiId > 0,
                    "Chinese alias missing"
                );
                Check(
                    ResourceAliases.SearchQuery("工业时代2")
                        .Contains("industrial"),
                    "Chinese search did not map"
                );
                Check(
                    ResourceAliases.CanonicalIdentity("modrinth", "industrial-craft") == ResourceAliases.CanonicalIdentity("curseforge", "industrial-craft"),
                    "Known mapping not reused"
                );
                Check(
                    ResourceAliases.CanonicalIdentity("modrinth", "unknown-fixture") != ResourceAliases.CanonicalIdentity("curseforge", "unknown-fixture"),
                    "Unverified identity merged"
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "forge_toml_dependencies_versions_side_and_neoforge_legacy_alias",
            () =>
            {
                Check(
                    MavenVersionRange.Matches("1.5", "[1.0,2.0)") && !MavenVersionRange.Matches("2.0", "[1.0,2.0)"),
                    "Range bound wrong"
                );
                Check(
                    MavenVersionRange.Matches("3.0", "(,1.0],[3.0,)") && MavenVersionRange.Compare("1.0-rc1", "1.0") < 0,
                    "Union or prerelease wrong"
                );
                var a = Zip(
                    "forge-a.jar",
                    ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 1.2.0\n"),
                    ("META-INF/mods.toml", """
                modLoader = 'javafml'
                loaderVersion = '[47,)'
                [[mods]]
                modId = 'example_a'
                displayName = 'Example A'
                version = '${file.jarVersion}'
                [[dependencies.example_a]]
                modId = 'forge'
                mandatory = true
                versionRange = '[47,)'
                side = 'CLIENT'
                [[dependencies.example_a]]
                modId = 'server_only'
                mandatory = true
                versionRange = '[1,)'
                side = 'SERVER'
                [features.example_a]
                javaVersion = '[17,)'
                openGLVersion = '[3.2,)'
                """)
                );
                var b = Zip(
                    "forge-b.jar",
                    ("META-INF/mods.toml", """
                modLoader = 'javafml'
                loaderVersion = '[47,)'
                [[mods]]
                modId = 'example_b'
                version = '1.0.0'
                [[dependencies.example_b]]
                modId = 'example_a'
                mandatory = true
                versionRange = '[1.0,2.0)'
                ordering = 'AFTER'
                side = 'BOTH'
                """)
                );
                Check(
                    ForgeModReader.Read(a, "forge", "1.20.1")
                        .Single()
                        .Version == "1.2.0",
                    "Manifest version not resolved"
                );
                Check(
                    ForgeModReader.Validate(
                        [
                            a,
                            b
                        ],
                        "neoforge",
                        "1.20.1",
                        "47.1.106",
                        17,
                        "47.1.106"
                    )
                        .Count == 1,
                    "NeoForge legacy alias or OpenGL warning wrong"
                );
                Throws<InvalidDataException>(
                    () => ForgeModReader.Validate(
                        [
                            b
                        ],
                        "forge",
                        "1.20.1",
                        "47.1.0",
                        17,
                        "47.1.0"
                    )
                );
                Throws<InvalidDataException>(
                    () => ForgeModReader.Validate(
                        [
                            a
                        ],
                        "forge",
                        "1.20.1",
                        "47.1.0",
                        8,
                        "47.1.0"
                    )
                );
                return Task.CompletedTask;
            }
        );
        await test(
            "pack_export_recipe_selection_and_exact_payload_hash",
            async () =>
            {
                var game = Path.Combine(folder, "recipe");
                Directory.CreateDirectory(
                    Path.Combine(game, "config")
                );
                Directory.CreateDirectory(
                    Path.Combine(game, "resourcepacks")
                );
                File.WriteAllText(
                    Path.Combine(game, "config/local-change.txt"),
                    "用户修改后的文件\n"
                );
                File.WriteAllText(
                    Path.Combine(game, "resourcepacks/excluded.zip"),
                    "unselected"
                );
                var profile = new GameProfile("recipe", "Recipe", "1.20.1", "vanilla", game, "unused");
                PortableModpackService.SaveExportOptions(
                    profile,
                    new(
                        "选中配置",
                        "2",
                        IncludedFolders: [
                            "config"
                        ]
                    )
                );
                var recipe = PortableModpackService.LoadExportOptions(profile);
                Check(
                    recipe.Name == "选中配置" && recipe.IncludedFolders!.SequenceEqual(
                        [
                            "config"
                        ]
                    ),
                    "Export recipe not retained"
                );
                Throws<InvalidDataException>(
                    () => PortableModpackService.SaveExportOptions(
                        profile,
                        new(
                            "unsafe",
                            IncludedFolders: [
                                "../outside"
                            ]
                        )
                    )
                );
                var output = Path.Combine(folder, "recipe.mrpack");
                await PortableModpackService.ExportAsync(profile, output, recipe);
                using var zip = ZipFile.OpenRead(output);
                Check(
                    zip.Entries.All(
                        x => !x.FullName.Contains("resourcepacks/")
                    ),
                    "Unselected folder included"
                );
                using var reader = new StreamReader(
                    zip.GetEntry("pcl-export.json")!.Open()
                );
                var receipt = JObject.Parse(
                    reader.ReadToEnd()
                );
                using var data = zip.GetEntry("overrides/config/local-change.txt")!.Open();
                var sha = Convert.ToHexString(
                    System.Security.Cryptography.SHA512.HashData(data)
                )
                    .ToLowerInvariant();
                Check(
                    (string? )receipt["files"]![0]!["sha512"] == sha,
                    "Export receipt differs from exact ZIP content"
                );
            }
        );
        await test(
            "resource_activation_undo_history_and_external_change_protection",
            async () =>
            {
                var game = Path.Combine(folder, "activation");
                Directory.CreateDirectory(game);
                var config = Path.Combine(game, "options.txt");
                const string previous = "music:0.5\nresourcePacks:[\"vanilla\"]\n";
                File.WriteAllText(config, previous);
                using var downloads = new DownloadService();
                var resources = new ResourceInstaller(game, downloads, "vanilla", "1.20.1");
                var pack = Zip(
                    "visual.zip",
                    ("pack.mcmeta", "{\"pack\":{\"pack_format\":15,\"description\":\"test\"}}"),
                    ("assets/example/file.txt", "fixture")
                );
                var installed = await resources.ImportAsync(pack, ResourceKind.ResourcePack);
                resources.Activate(installed.ProjectId);
                Check(
                    File.ReadAllText(config)
                        .Contains("file/visual.zip"),
                    "Resource pack not activated"
                );
                resources.UndoActivation();
                Check(
                    File.ReadAllText(config) == previous,
                    "Prior settings not restored"
                );
                resources.Activate(installed.ProjectId);
                File.AppendAllText(config, "new_game_setting:true\n");
                Throws<IOException>(
                    () => resources.UndoActivation()
                );
                Check(
                    File.ReadAllText(config)
                        .Contains("new_game_setting:true"),
                    "External changes overwritten"
                );
                Check(
                    ResourceHistory.Read(game)
                        .Any(
                            x => x.Action == "撤销应用"
                        ) && ResourceHistory.Read(game)
                        .Count >= 4,
                    "Operation history missing"
                );
            }
        );
        await test(
            "resource_batch_update_projects_whole_dependency_set_and_preserves_external_edits",
            async () =>
            {
                string Mod(string file, string id, string version, string? dependency = null) => Zip(
                    file,
                    ("fabric.mod.json", new JObject
                    {
                        ["schemaVersion"] = 1,
                        ["id"] = id,
                        ["version"] = version,
                        ["depends"] = dependency is null
                            ? new JObject()
                            : JObject.Parse(dependency)
                    }.ToString())
                );
                var a1 = Mod("batch-a1.jar", "batch_a", "1.0.0", "{\"batch_b\":\"=1.0.0\"}");
                var b1 = Mod("batch-b1.jar", "batch_b", "1.0.0");
                var a2 = Mod("batch-a2.jar", "batch_a", "2.0.0", "{\"batch_b\":\">=2.0.0\"}");
                var b2 = Mod("batch-b2.jar", "batch_b", "2.0.0", "{\"batch_a\":\">=2.0.0\"}");
                ResourceRelease Release(string file, string project, string dependency)
                {
                    var bytes = File.ReadAllBytes(file);
                    return new(
                        project + "-v2",
                        project,
                        project,
                        "2.0.0",
                        "release",
                        DateTimeOffset.UtcNow,
                        [
                            "1.20.1"
                        ],
                        [
                            "fabric"
                        ],
                        new(
                            Path.GetFileName(file),
                            new Uri(
                                "https://fixture.invalid/" + Path.GetFileName(file)
                            ),
                            bytes.Length,
                            Convert.ToHexString(
                                System.Security.Cryptography.SHA1.HashData(bytes)
                            )
                                .ToLowerInvariant()
                        ),
                        [
                            new(dependency, null, "required")
                        ]
                    );
                }

                var releases = new[]
                {
                    Release(a2, "project-a", "project-b"),
                    Release(b2, "project-b", "project-a")
                };
                var payloads = new Dictionary<string, byte[]>
                {
                    [Path.GetFileName(a2)] = File.ReadAllBytes(a2),
                    [Path.GetFileName(b2)] = File.ReadAllBytes(b2)
                };
                using var downloads = new DownloadService(
                    new ResourcePayloadHandler(payloads)
                );
                using var catalog = new ResourceCatalog(
                    new ResourcePayloadHandler(
                        new()
                    )
                );
                string Instance(string name)
                {
                    var directory = Path.Combine(folder, name);
                    Directory.CreateDirectory(
                        Path.Combine(directory, "mods")
                    );
                    JsonFiles.Write(
                        Path.Combine(directory, "profile.json"),
                        new
                        {
                            loaderVersion = "0.16.0"
                        }
                    );
                    File.Copy(
                        a1,
                        Path.Combine(directory, "mods/a.jar")
                    );
                    File.Copy(
                        b1,
                        Path.Combine(directory, "mods/b.jar")
                    );
                    File.WriteAllText(
                        Path.Combine(directory, "mods/user-note.txt"),
                        "retain"
                    );
                    return directory;
                }

                LocalResourceUpdate[] Updates(string directory)
                {
                    var current = new LocalResourceService(directory, "fabric", "1.20.1").Scan();
                    return[
                        new(
                            current.Single(
                                x => x.ModIds.Contains("batch_a")
                            ),
                            releases[0]
                        ),
                        new(
                            current.Single(
                                x => x.ModIds.Contains("batch_b")
                            ),
                            releases[1]
                        )
                    ];
                }

                var success = Instance("batch-success");
                var updater = new ResourceBatchUpdater(success, "fabric", "1.20.1");
                using (var prepared = await updater.PrepareAsync(
                    Updates(success),
                    catalog,
                    downloads
                ))
                {
                    Check(
                        File.Exists(
                            Path.Combine(success, "mods/a.jar")
                        ) && !File.Exists(
                            Path.Combine(success, "mods/batch-a2.jar")
                        ),
                        "Preparation changed live files"
                    );
                    var backup = updater.Apply(prepared);
                    Check(
                        File.Exists(
                            Path.Combine(backup, "mods/a.jar")
                        ),
                        "Original file backup missing"
                    );
                }

                Check(
                    new LocalResourceService(success, "fabric", "1.20.1").Scan()
                        .All(
                            x => x.Version == "2.0.0"
                        ),
                    "Whole dependency set not committed"
                );
                Check(
                    File.ReadAllText(
                        Path.Combine(success, "mods/user-note.txt")
                    ) == "retain",
                    "Unmanaged auxiliary content lost"
                );
                var changed = Instance("batch-external-change");
                updater = new(changed, "fabric", "1.20.1");
                using (var prepared = await updater.PrepareAsync(
                    Updates(changed),
                    catalog,
                    downloads
                ))
                {
                    File.AppendAllText(
                        Path.Combine(changed, "mods/user-note.txt"),
                        " externally changed"
                    );
                    Throws<IOException>(
                        () => updater.Apply(prepared)
                    );
                    Check(
                        File.Exists(
                            Path.Combine(changed, "mods/a.jar")
                        ) && File.ReadAllText(
                            Path.Combine(changed, "mods/user-note.txt")
                        )
                            .Contains("externally changed"),
                        "External edits not retained"
                    );
                }
            }
        );
        await test(
            "world_datapack_install_toggle_backup_and_target_validation",
            async () =>
            {
                var game = Path.Combine(folder, "datapacks");
                Directory.CreateDirectory(
                    Path.Combine(game, "saves/世界 A")
                );
                File.WriteAllText(
                    Path.Combine(game, "saves/世界 A/level.dat"),
                    "fixture"
                );
                var pack = Zip(
                    "data.zip",
                    ("pack.mcmeta", "{\"pack\":{\"pack_format\":48,\"description\":\"test\"}}"),
                    ("data/example/function/test.mcfunction", "say hello")
                );
                var service = new DataPackService(game);
                var world = service.Worlds()
                    .Single();
                await service.InstallAsync(world, pack);
                Check(
                    service.List(world)
                        .Count == 1,
                    "Data pack missing"
                );
                service.SetEnabled(
                    world,
                    service.List(world)
                        .Single(),
                    false
                );
                Check(
                    !service.List(world)
                        .Single()
                        .Enabled,
                    "Disabled datapack active"
                );
                var backup = service.Remove(
                    world,
                    service.List(world)
                        .Single()
                );
                Check(
                    File.Exists(backup) && service.List(world)
                        .Count == 0,
                    "Data pack not backed up"
                );
                Throws<InvalidDataException>(
                    () => service.List(
                        new("foreign", folder)
                    )
                );
                Throws<InvalidDataException>(
                    () => DataPackService.Validate(
                        Zip("resource-only.zip", ("pack.mcmeta", "{\"pack\":{}}"), ("assets/example/a.txt", "a"))
                    )
                );
            }
        );
    }

    private sealed class CurseHandler : HttpMessageHandler
    {
        public int Requests;
        public bool KeySeen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            KeySeen |= request.Headers.TryGetValues("x-api-key", out var values) && values.Single() == "test-only-key";
            var data = request.RequestUri!.AbsolutePath.Contains("files/")
                ? "{\"data\":{\"id\":456,\"modId\":123,\"fileName\":\"example.jar\",\"fileLength\":10,\"downloadUrl\":null,\"hashes\":[{\"algo\":1,\"value\":\"0123456789012345678901234567890123456789\"}]}}"
                : "{\"data\":{\"classId\":6}}";
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(data, Encoding.UTF8, "application/json")
                }
            );
        }
    }

    private sealed class ResourcePayloadHandler(Dictionary<string, byte[]> payloads) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
            payloads.TryGetValue(
                Path.GetFileName(request.RequestUri!.AbsolutePath),
                out var bytes
            )
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound)
        );
    }

    private sealed class DatapackHandler : HttpMessageHandler
    {
        public bool ValidSearch;
        public bool ValidVersions;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = Uri.UnescapeDataString(
                request.RequestUri!.ToString()
            );
            var search = request.RequestUri.AbsolutePath.EndsWith("search");
            if (search)
                ValidSearch = uri.Contains("project_type:datapack") && !uri.Contains("categories:forge");
            else if (request.RequestUri.AbsolutePath.EndsWith("/version"))
                ValidVersions = uri.Contains("loaders=[\"datapack\"]") && !uri.Contains("forge");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        search
                            ? "{\"hits\":[],\"offset\":0,\"total_hits\":0}"
                            : request.RequestUri.AbsolutePath.EndsWith("/version")
                                ? "[]"
                                : "{\"id\":\"test\",\"slug\":\"test\",\"title\":\"Test\",\"project_type\":\"datapack\"}"
                    )
                }
            );
        }
    }

    private sealed class CurseCatalogHandler : HttpMessageHandler
    {
        public bool SearchFiltered;
        public bool RestrictDependency;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = Uri.UnescapeDataString(
                request.RequestUri!.ToString()
            );
            var path = request.RequestUri.AbsolutePath;
            JObject json;
            if (path.EndsWith("/search"))
            {
                SearchFiltered = uri.Contains("classId=6") && uri.Contains("modLoaderType=4") && uri.Contains("gameVersion=1.20.1") && uri.Contains("searchFilter=a b");
                json = new JObject
                {
                    ["data"] = new JArray(
                        new JObject
                        {
                            ["id"] = 100,
                            ["name"] = "Root",
                            ["summary"] = "Fixture",
                            ["downloadCount"] = 1
                        }
                    )
                };
            }
            else if (!path.Contains("/files"))
            {
                var project = path.EndsWith("/200")
                    ? 200
                    : 100;
                json = new JObject
                {
                    ["data"] = new JObject
                    {
                        ["id"] = project,
                        ["name"] = "Project " + project,
                        ["summary"] = "Fixture"
                    }
                };
            }
            else
            {
                var project = path.Contains("/200/")
                    ? 200
                    : 100;
                var item = new JObject
                {
                    ["id"] = project * 10,
                    ["modId"] = project,
                    ["fileName"] = project + ".jar",
                    ["displayName"] = project + " v1",
                    ["fileLength"] = 1,
                    ["downloadUrl"] = project == 200 && RestrictDependency
                        ? null
                        : "https://edge.forgecdn.net/files/example/" + project + ".jar",
                    ["releaseType"] = 1,
                    ["fileDate"] = "2026-01-01T00:00:00Z",
                    ["gameVersions"] = new JArray("1.20.1", "Fabric"),
                    ["hashes"] = new JArray(
                        new JObject
                        {
                            ["algo"] = 1,
                            ["value"] = "0123456789012345678901234567890123456789"
                        }
                    ),
                    ["dependencies"] = project == 100
                        ? new JArray(
                            new JObject
                            {
                                ["modId"] = 200,
                                ["relationType"] = 3
                            }
                        )
                        : new JArray()
                };
                json = new JObject
                {
                    ["data"] = new JArray(item)
                };
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        json.ToString(),
                        Encoding.UTF8,
                        "application/json"
                    )
                }
            );
        }
    }
}
