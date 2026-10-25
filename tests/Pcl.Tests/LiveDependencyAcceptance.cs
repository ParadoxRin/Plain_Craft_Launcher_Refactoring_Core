using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Pcl.Core;

// Explicit opt-in network acceptance; operates only on the caller's isolated test root.
static class LiveDependencyAcceptance
{
    public static async Task<int> Run(string root, string inputs)
    {
        using var service = new MinecraftService(
            new AppPaths(root)
        );
        var results = new List<object>();
        int failures = 0;
        var run = Guid.NewGuid()
            .ToString("N")[..8];
        async Task Test(string name, Func<Task<object>> action)
        {
            try
            {
                var evidence = await action();
                results.Add(
                    new
                    {
                        name,
                        status = "passed",
                        evidence
                    }
                );
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failures++;
                results.Add(
                    new
                    {
                        name,
                        status = "failed",
                        error = ex.ToString()
                    }
                );
                Console.WriteLine("FAIL " + name + " " + ex.Message);
            }

            JsonFiles.Write(
                Path.Combine(root, "dependency-acceptance.json"),
                new
                {
                    results,
                    failures,
                    gameStarted = false
                }
            );
        }

        void Check(bool value, string message)
        {
            if (!value)
                throw new Exception(message);
        }

        await Test(
            "official_quilt_loader_qfapi_qsl_install_validate_toggle",
            async () =>
            {
                using var game = service.ForVersion("1.20.1");
                using var loader = new FabricService(game, "accept-quilt", "quilt");
                var profile = await loader.InstallAsync("0.27.1");
                var installer = new ResourceInstaller(profile.GameDirectory, service.Downloads, "quilt", "1.20.1");
                var record = await installer.ImportAsync(
                    Path.Combine(inputs, "quilt-qsl.jar"),
                    ResourceKind.Mod
                );
                var resolution = QuiltDependencies.Validate(profile.GameDirectory, "1.20.1", "0.27.1", 17);
                Check(
                    resolution.Selected.Any(
                        x => x.Id == "quilted_fabric_api"
                    ) && resolution.Selected.Count > 30,
                    "QFAPI dependency graph not selected"
                );
                var path = Path.Combine(profile.GameDirectory, "mods", record.FileName);
                var local = new LocalResourceService(profile.GameDirectory, "quilt", "1.20.1");
                local.SetEnabled(
                    [
                        path
                    ],
                    false
                );
                local.SetEnabled(
                    [
                        path + ".disabled"
                    ],
                    true
                );
                return new
                {
                    profile,
                    resolution.Candidates,
                    selected = resolution.Selected.Select(
                        x => new
                        {
                            x.Id,
                            x.Version,
                            x.Source
                        }
                    )
                        .ToArray(),
                    toggled = true
                };
            }
        );
        await Test(
            "real_neoforge_sodium_jarjar_import_and_cloth_config",
            async () =>
            {
                var profile = InstanceRegistry.ByFolder(service, "accept-neoforge");
                var installer = new ResourceInstaller(profile.GameDirectory, service.Downloads, "neoforge", "1.21.1");
                await installer.ImportAsync(
                    Path.Combine(inputs, "neoforge-sodium.jar"),
                    ResourceKind.Mod
                );
                await installer.ImportAsync(
                    Path.Combine(inputs, "neoforge-cloth-config.jar"),
                    ResourceKind.Mod
                );
                var local = new LocalResourceService(profile.GameDirectory, "neoforge", "1.21.1");
                var warnings = local.ValidateEnabledResources();
                var resolution = JarJarResolver.Resolve(
                    Directory.GetFiles(
                        Path.Combine(profile.GameDirectory, "mods"),
                        "*.jar"
                    ),
                    "neoforge",
                    "1.21.1"
                );
                Check(resolution.Selected.Count == 5, "Sodium nested dependencies missing");
                return new
                {
                    profile,
                    resolution.Selected,
                    mods = resolution.Mods.Select(
                        x => new
                        {
                            x.Id,
                            x.Version
                        }
                    ),
                    warnings
                };
            }
        );
        using var http = new HttpClient();
        var libraryName = "org.apache.commons:commons-collections4:4.4";
        var libraryPath = FabricService.MavenPath(libraryName);
        var url = "https://repo.maven.apache.org/maven2/" + libraryPath;
        var bytes = await http.GetByteArrayAsync(url);
        var hash = Convert.ToHexString(
            SHA1.HashData(bytes)
        )
            .ToLowerInvariant();
        Check(
            hash == (await http.GetStringAsync(url + ".sha1")).Trim(),
            "Maven checksum mismatch"
        );
        var library = new JObject
        {
            ["name"] = libraryName,
            ["downloads"] = new JObject
            {
                ["artifact"] = new JObject
                {
                    ["path"] = libraryPath,
                    ["url"] = url,
                    ["sha1"] = hash,
                    ["size"] = bytes.Length
                }
            }
        };
        string Archive(string name, params (string Name, string Text)[] entries)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
                return path;
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                using var w = new StreamWriter(
                    zip.CreateEntry(entry.Name)
                        .Open()
                );
                w.Write(entry.Text);
            }

            return path;
        }

        await Test(
            "custom_multimc_install_export_reimport",
            async () =>
            {
                var patch = new JObject
                {
                    ["formatVersion"] = 1,
                    ["uid"] = "local.acceptance",
                    ["version"] = "1.0",
                    ["order"] = 100,
                    ["libraries"] = new JArray(
                        library.DeepClone()
                    ),
                    ["requires"] = new JArray(
                        new JObject
                        {
                            ["uid"] = "net.minecraft",
                            ["equals"] = "1.21.1"
                        }
                    )
                };
                var archive = Archive(
                    "custom-multimc.zip",
                    ("mmc-pack.json", new JObject
                    {
                        ["formatVersion"] = 1,
                        ["components"] = new JArray(
                            new JObject
                            {
                                ["uid"] = "net.minecraft",
                                ["version"] = "1.21.1"
                            },
                            new JObject
                            {
                                ["uid"] = "local.acceptance",
                                ["version"] = "1.0"
                            }
                        )
                    }.ToString()),
                    ("patches/local.acceptance.json", patch.ToString()),
                    (".minecraft/config/acceptance.txt", "kept")
                );
                var portable = new PortableModpackService(service);
                var profile = await portable.InstallAsync(
                    archive,
                    new(),
                    "accept-custom-mmc-" + run
                );
                var installed = InstanceRegistry.Load(service, profile);
                var custom = installed.Libraries.Single(
                    x => x.Path.Contains("pcl-custom")
                );
                Check(
                    DownloadService.IsValid(custom),
                    "Library not installed with valid hash"
                );
                var export = Path.Combine(root, "custom-export-" + run + ".mrpack");
                if (!File.Exists(export))
                    await PortableModpackService.ExportAsync(
                        profile,
                        export,
                        new("Custom test")
                    );
                Check(
                    CustomPackMetadata.ReadArchive(export)
                        .Libraries.Count == 1,
                    "Export dropped library"
                );
                var imported = await portable.InstallAsync(
                    export,
                    new(),
                    "accept-custom-reimport-" + run
                );
                Check(
                    InstanceRegistry.Load(service, imported)
                        .Libraries.Any(
                        x => x.Path.Contains("pcl-custom") && DownloadService.IsValid(x)
                    ),
                    "Reimport dropped library"
                );
                return new
                {
                    profile.Id,
                    custom,
                    export,
                    reimported = imported.Id,
                    config = File.ReadAllText(
                        Path.Combine(imported.GameDirectory, "config/acceptance.txt")
                    )
                };
            }
        );
        await Test(
            "custom_hmcl_official_profile_addition_install_plan",
            async () =>
            {
                var official = JsonFiles.Read(
                    Path.Combine(service.Paths.Game, "versions/1.21.1/1.21.1.official.json")
                );
                official["id"] = "custom-hmcl";
                ((JArray)official["libraries"]!).Add(
                    library.DeepClone()
                );
                ((JArray)official["arguments"]!["jvm"]!).Add("-Dpack.acceptance=中文验证");
                var archive = Archive(
                    "custom-hmcl.zip",
                    ("modpack.json", new JObject
                    {
                        ["name"] = "HMCL custom",
                        ["gameVersion"] = "1.21.1"
                    }.ToString()),
                    ("minecraft/pack.json", official.ToString()),
                    ("minecraft/config/acceptance.txt", "hmcl-kept")
                );
                var profile = await new PortableModpackService(service).InstallAsync(
                    archive,
                    new(),
                    "accept-custom-hmcl-" + run
                );
                var installed = InstanceRegistry.Load(service, profile);
                var java = new JavaRuntime("not-executed", service.Platform.Architecture, 21);
                var plan = new LaunchService(service).CreatePlan(installed, java, GameSession.Trial, gameDirectory: profile.GameDirectory);
                Check(
                    plan.Arguments.Contains("-Dpack.acceptance=中文验证") && plan.Arguments.Any(
                        x => x.Contains("pcl-custom")
                    ),
                    "Custom additions lost in launch plan"
                );
                return new
                {
                    profile.Id,
                    customLibraries = installed.Libraries.Where(
                        x => x.Path.Contains("pcl-custom")
                    ),
                    propertyPreserved = true,
                    gameStarted = false
                };
            }
        );
        await Test(
            "custom_neoforge_private_library_root",
            async () =>
            {
                var profile = InstanceRegistry.ByFolder(service, "accept-neoforge");
                if (!File.Exists(
                    Path.Combine(profile.GameDirectory, CustomPackMetadata.EntryName)
                ))
                    await CustomPackMetadata.ApplyAsync(
                        service,
                        profile.GameDirectory,
                        new(
                            [
                                library
                            ],
                            [],
                            [
                                "acceptance"
                            ]
                        ),
                        default
                    );
                var item = InstanceRegistry.Load(service, profile)
                    .Libraries.Single(
                    x => x.Path.Contains("pcl-custom")
                );
                Check(
                    item.Path.StartsWith(profile.GameDirectory) && DownloadService.IsValid(item),
                    "Private library root mismatch"
                );
                return new
                {
                    item.Path,
                    hashVerified = true
                };
            }
        );
        return failures == 0
            ? 0
            : 1;
    }
}
