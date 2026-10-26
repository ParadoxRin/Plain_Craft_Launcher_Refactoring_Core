using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MeloongCore;
using Newtonsoft.Json.Linq;
using Pcl.Core;
using Pcl.Legacy;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
MeloongCore.Main.Init("PCLMacPortTests");
// Focused closeout suite: real isolated files with controlled external metadata.
if (args.FirstOrDefault() == "--closeout-local")
{
    var folder = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(folder);
    var results = new List<object>();
    var failed = 0;
    async Task Run(string name, Func<Task> body)
    {
        if (args.Length > 3 && name != args[3])
        {
            return;
        }

        try
        {
            await body();
            results.Add(
                new
                {
                    name,
                    status = "passed"
                }
            );
        }
        catch (Exception ex)
        {
            failed++;
            results.Add(
                new
                {
                    name,
                    status = "failed",
                    error = ex.Message
                }
            );
        }
    }

    if (args.ElementAtOrDefault(3) == "java_discovery_rejects_escaping_receipts_and_macos_stub")
    {
        await AccountAppearanceTests.RunAll(folder, Run);
    }

    await InstanceManagementTests.RunAll(folder, Run);
    await LocalSettingsTests.RunAsync(folder, Run);
    await PackResourceTests.RunAsync(folder, Run);
    File.WriteAllText(
        args[2],
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                status = failed == 0
                    ? "passed"
                    : "failed",
                results
            },
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
    return failed == 0
        ? 0
        : 1;
}

if (args.FirstOrDefault() == "--live039")
{
    return await Live039Tests.Run(args[1]);
}

if (args.FirstOrDefault() == "--quilt039")
{
    return await Quilt039Tests.Run(args[1], args[2], args[3], args[4]);
}

if (args.FirstOrDefault() == "--live038")
{
    return await Live038Tests.Run(args[1]);
}

if (args.FirstOrDefault() == "--nested038")
{
    return await Nested038Tests.Run(args[1], args[2], args[3]);
}

if (args.FirstOrDefault() == "--live037")
{
    return await Live037Tests.Run(args[1]);
}

if (args.FirstOrDefault() == "--planning037")
{
    return await Planning037Tests.Run(args[1], args[2], args[3]);
}

if (args.FirstOrDefault() == "--license0361")
{
    return await License0361Tests.Run(args[1]);
}

if (args.FirstOrDefault() == "--auth-privacy")
{
    return await AuthPrivacyTests.Run(
        args.ElementAtOrDefault(1) ?? (MicrosoftApplication.BuiltIn.IsConfigured
            ? "configured"
            : "empty")
    );
}

if (args.FirstOrDefault() == "--iris036-child")
{
    return Iris036Tests.Child(args[1], args[2]);
}

if (args.FirstOrDefault() == "--iris036")
{
    return await Iris036Tests.Run(
        Path.GetFullPath(args[1])
    );
}

if (args.FirstOrDefault() == "--txn035-child")
{
    return Transaction035Tests.Child(args[1], args[2], args[3]);
}

if (args.FirstOrDefault() == "--txn035")
{
    return await Transaction035Tests.Run(
        Path.GetFullPath(args[1])
    );
}

if (args.FirstOrDefault() == "--compat034")
{
    return await Compatibility034Tests.Run(args[1], args[2]);
}

if (args.FirstOrDefault() == "--quilt-oracle")
{
    var differences = new List<object>();
    var lines = File.ReadAllLines(args[1]);
    foreach (var line in lines)
    {
        var fields = line.Split('\t');
        string actual;
        try
        {
            actual = QuiltVersionRange.Matches(fields[1], fields[0])
                .ToString()
                .ToLowerInvariant();
        }
        catch (Exception)
        {
            actual = "error";
        }

        if (actual != fields[2])
            differences.Add(
                new
                {
                    range = fields[0],
                    version = fields[1],
                    expected = fields[2],
                    actual
                }
            );
    }

    JsonFiles.Write(
        args[2],
        new
        {
            cases = lines.Length,
            differences
        }
    );
    Console.WriteLine("Quilt oracle cases=" + lines.Length + " differences=" + differences.Count);
    return differences.Count == 0
        ? 0
        : 1;
}

if (args.FirstOrDefault() == "--local-skin-live")
{
    return LocalSkinAcceptance.Run(
        Path.GetFullPath(args[1]),
        Path.GetFullPath(args[2])
    );
}

if (args.FirstOrDefault() == "--dependency-live")
    return await LiveDependencyAcceptance.Run(
        Path.GetFullPath(args[1]),
        Path.GetFullPath(args[2])
    );
var root = Path.GetFullPath(
    args.FirstOrDefault() ?? "artifacts/tests"
);
Directory.CreateDirectory(root);
var scratch = Path.Combine(
    root,
    "scratch-" + Guid.NewGuid()
        .ToString("N")
);
Directory.CreateDirectory(scratch);
var rows = new List<object>();
var failures = 0;
void Check(bool ok, string message = "Assertion failed")
{
    if (!ok)
        throw new Exception(message);
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

async Task Test(string name, Func<Task> action)
{
    try
    {
        await action();
        rows.Add(
            new
            {
                name,
                status = "passed"
            }
        );
        Console.WriteLine("PASS " + name);
    }
    catch (Exception ex)
    {
        failures++;
        rows.Add(
            new
            {
                name,
                status = "failed",
                error = ex.ToString()
            }
        );
        Console.WriteLine("FAIL " + name + " " + ex.Message);
    }
}

Task Sync(Action a)
{
    a();
    return Task.CompletedTask;
}

await Test(
    "settings_gc_java_version_gates",
    () => Sync(
        () =>
        {
            Check(
                LaunchRuntimePolicy.CollectorArguments("generational", 21)
                    .SequenceEqual(
                        new[]
                        {
                            "-XX:+UseZGC",
                            "-XX:+ZGenerational"
                        }
                    )
            );
            Check(
                LaunchRuntimePolicy.CollectorArguments("generational", 25)
                    .SequenceEqual(
                        new[]
                        {
                            "-XX:+UseZGC"
                        }
                    )
            );
            Check(
                LaunchRuntimePolicy.CollectorArguments("zgc", 8)
                    .SequenceEqual(
                        new[]
                        {
                            "-XX:+UseG1GC"
                        }
                    )
            );
            Throws<InvalidDataException>(
                () => new LaunchSettings
                {
                    GarbageCollector = "g1",
                    JvmArguments = "-XX:+UseZGC"
                }.Validate()
            );
        }
    )
);
await Test(
    "settings_memory_and_inheritance",
    () => Sync(
        () =>
        {
            var paths = new AppPaths(
                Path.Combine(scratch, "settings-bindings")
            );
            var store = new LauncherSettingsStore(paths);
            store.SaveGlobal(
                new LaunchSettings
                {
                    Width = 960,
                    MaximumMemoryMb = 1536,
                    AutomaticMemory = true,
                    GarbageCollector = "g1",
                    VersionInformation = "PCL"
                }
            );
            var instance = Path.Combine(paths.Root, "instance");
            store.SavePatch(
                instance,
                new LaunchSettingsPatch
                {
                    AutomaticMemory = false,
                    Width = 1280
                }
            );
            var restored = new LauncherSettingsStore(paths).Effective(instance);
            Check(!restored.AutomaticMemory && restored.Width == 1280 && restored.GarbageCollector == "g1");
            Check(
                LaunchRuntimePolicy.ResolveMemory(store.Global, 8L * 1024 * 1048576)
                    .MaximumMemoryMb == 4096
            );
            Check(
                LaunchRuntimePolicy.ResolveMemory(restored, 8L * 1024 * 1048576)
                    .MaximumMemoryMb == 1536
            );
            Throws<InvalidDataException>(
                () => store.SaveGlobal(
                    store.Global with
                    {
                        JvmArguments = "-Xmx8G"
                    }
                )
            );
            Check(
                new LauncherSettingsStore(paths).Global.JvmArguments == ""
            );
        }
    )
);
await Test(
    "settings_actual_launch_arguments",
    () => Sync(
        () =>
        {
            var paths = new AppPaths(
                Path.Combine(scratch, "settings-plan")
            );
            using var service = new MinecraftService(
                paths,
                new("osx", "aarch64", "27")
            );
            var json = Path.Combine(paths.Root, "version.json");
            JsonFiles.Write(
                json,
                new JObject
                {
                    ["mainClass"] = "test.Main",
                    ["arguments"] = new JObject
                    {
                        ["jvm"] = new JArray("-cp", "${classpath}"),
                        ["game"] = new JArray("--versionType", "${version_type}")
                    }
                }
            );
            new LauncherSettingsStore(paths).SaveGlobal(
                new LaunchSettings
                {
                    Width = 960,
                    Height = 600,
                    MaximumMemoryMb = 1536,
                    VersionInformation = "PCL test",
                    GarbageCollector = "generational",
                    JvmArguments = "-Dpcl.test=true"
                }
            );
            var plan = new LaunchService(service).CreatePlan(
                new(
                    "test",
                    json,
                    Path.Combine(paths.Root, "test.jar"),
                    "17",
                    Array.Empty<DownloadItem>(),
                    21
                ),
                new("/java", "aarch64", 21),
                GameSession.Trial
            );
            foreach (var argument in new[]
            {
                "-Xmx1536M",
                "-XX:+UseZGC",
                "-XX:+ZGenerational",
                "-Dpcl.test=true",
                "960",
                "600",
                "PCL test"
            }

            )
            {
                Check(
                    plan.Arguments.Contains(argument),
                    argument
                );
            }
        }
    )
);
await Test(
    "settings_appearance_persistence_and_recovery",
    () => Sync(
        () =>
        {
            var paths = new AppPaths(
                Path.Combine(scratch, "settings-look")
            );
            var service = new AppearanceService(paths);
            service.Save(
                new AppearanceSettings
                {
                    WindowOpacity = .9,
                    BackgroundBlur = 4,
                    BackgroundFit = "fit",
                    TitleMode = "text",
                    MusicAutoPlay = false,
                    HiddenRoutes = [
                        "mod",
                        "more"
                    ]
                }
            );
            var value = new AppearanceService(paths).Load();
            Check(
                value.WindowOpacity == .9 && !value.MusicAutoPlay && value.HiddenRoutes.Contains("mod")
            );
            Throws<InvalidDataException>(
                () => service.Save(
                    value with
                    {
                        WindowOpacity = double.NaN
                    }
                )
            );
            Throws<InvalidDataException>(
                () => service.Save(
                    value with
                    {
                        HiddenRoutes = [
                            "unknown-route"
                        ]
                    }
                )
            );
            var file = Path.Combine(paths.Root, "portable.json");
            service.Export(file);
            service.Import(file);
            Check(
                service.Load()
                    .BackgroundFit == "fit" && service.Load()
                    .TitleMode == "text"
            );
        }
    )
);
await Test(
    "unix_absolute_path_and_root",
    () => Sync(
        () =>
        {
            Check(
                PathUtils.ForCompare(scratch) == scratch
            );
            Check(
                PathUtils.ForApi("/") == "/"
            );
            Check(
                PathUtils.IsParentOf("/", scratch)
            );
            Check(
                !PathUtils.IsParentOf(scratch, scratch + "-other")
            );
            Check(
                !PathUtils.IsSame(
                    Path.Combine(scratch, "A"),
                    Path.Combine(scratch, "a")
                )
            );
            Check(
                PathUtils.GetLastPart(
                    Path.Combine(scratch, @"literal\name:")
                ) == @"literal\name:"
            );
            Check(
                PathUtils.ForApi(
                    Path.Combine(scratch, "colon:")
                ) == Path.Combine(scratch, "colon:")
            );
        }
    )
);
await Test(
    "unicode_file_copy_move_atomic_replace",
    () => Sync(
        () =>
        {
            var source = Path.Combine(scratch, "中文 空格.json");
            var target = Path.Combine(scratch, "copy.json");
            FileUtils.WriteAtomic(source, "{\"value\":1}");
            FileUtils.WriteAtomic(source, "{\"value\":2}");
            FileUtils.Copy(source, target);
            Check(
                File.ReadAllText(source) == File.ReadAllText(target)
            );
            var moved = Path.Combine(scratch, "moved.json");
            FileUtils.Move(target, moved);
            Check(
                File.Exists(moved) && !File.Exists(target)
            );
            Check(
                new FileChecker
                {
                    IsJson = true,
                    ActualSize = new FileInfo(moved).Length,
                    Hash = Convert.ToHexString(
                        SHA256.HashData(
                            File.ReadAllBytes(moved)
                        )
                    )
                        .ToLowerInvariant()
                }.Check(moved)is null
            );
        }
    )
);
await Test(
    "directory_move_and_symlink_unlink",
    () => Sync(
        () =>
        {
            var source = Path.Combine(scratch, "folder");
            Directory.CreateDirectory(source);
            File.WriteAllText(
                Path.Combine(source, "keep.txt"),
                "safe"
            );
            var moved = Path.Combine(scratch, "new-folder");
            DirectoryUtils.Move(source, moved);
            Check(
                File.Exists(
                    Path.Combine(moved, "keep.txt")
                )
            );
            var link = Path.Combine(scratch, "directory-link");
            Directory.CreateSymbolicLink(link, moved);
            DirectoryUtils.Delete(link);
            Check(
                !Directory.Exists(link) && File.ReadAllText(
                    Path.Combine(moved, "keep.txt")
                ) == "safe"
            );
        }
    )
);
await Test(
    "config_reload_and_progress",
    () => Sync(
        () =>
        {
            var p = new JsonConfigProvider(
                Path.Combine(scratch, "config.json")
            );
            p.Set("name", "Mac", false);
            p.Save();
            p.DiscardCache();
            Check(
                p.Read("name", "", false) == "Mac"
            );
            var progress = new ProgressProvider();
            progress.SplitTo(0.5)
                .Finish();
            Check(
                Math.Abs(
                    progress.GetIncreasing() - 0.5
                ) < .001
            );
            progress.Finish();
            Check(progress.IsFinished);
        }
    )
);
await Test(
    "ecdsa_cng_blob_and_signature_interoperability",
    () => Sync(
        () =>
        {
            using var ecc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var parameters = ecc.ExportParameters(true);
            byte[] Blob(bool priv)
            {
                var blob = new byte[priv
                    ? 104
                    : 72];
                BitConverter.GetBytes(
                    priv
                        ? 0x32534345u
                        : 0x31534345u
                )
                    .CopyTo(blob, 0);
                BitConverter.GetBytes(32)
                    .CopyTo(blob, 4);
                parameters.Q.X!.CopyTo(blob, 8);
                parameters.Q.Y!.CopyTo(blob, 40);
                if (priv)
                    parameters.D!.CopyTo(blob, 72);
                return blob;
            }

            var signature = CryptographyUtils.EcdsaSign(
                "Mac test",
                Convert.ToBase64String(
                    Blob(true)
                )
            );
            Check(
                ecc.VerifyData(
                    Encoding.UTF8.GetBytes("Mac test"),
                    Convert.FromBase64String(signature),
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                )
            );
            var dotnetSignature = ecc.SignData(
                Encoding.UTF8.GetBytes("Mac test"),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            );
            CryptographyUtils.EcdsaVerify(
                "Mac test",
                Convert.ToBase64String(dotnetSignature),
                Convert.ToBase64String(
                    Blob(false)
                )
            );
            Throws<System.Security.SecurityException>(
                () => CryptographyUtils.EcdsaVerify(
                    "wrong",
                    signature,
                    Convert.ToBase64String(
                        Blob(false)
                    )
                )
            );
        }
    )
);
await Test(
    "zip_utf8_and_traversal_rejection",
    () => Sync(
        () =>
        {
            var zip = Path.Combine(scratch, "valid.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry("目录/文件.txt")
                        .Open()
                );
                writer.Write("中文");
            }

            var dest = Path.Combine(scratch, "unzip");
            FileUtils.ExtractToDirectory(zip, dest);
            Check(
                File.ReadAllText(
                    Path.Combine(dest, "目录/文件.txt")
                ) == "中文"
            );
            var bad = Path.Combine(scratch, "bad.zip");
            using (var archive = ZipFile.Open(bad, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry("../escape.txt")
                        .Open()
                );
                writer.Write("bad");
            }

            Throws<InvalidDataException>(
                () => FileUtils.ExtractToDirectory(bad, dest)
            );
            Check(
                !File.Exists(
                    Path.Combine(scratch, "escape.txt")
                )
            );
        }
    )
);
await Test(
    "zip_existing_symlink_rejection",
    () => Sync(
        () =>
        {
            var outside = Path.Combine(scratch, "outside");
            Directory.CreateDirectory(outside);
            var dest = Path.Combine(scratch, "linked-extract");
            Directory.CreateDirectory(dest);
            Directory.CreateSymbolicLink(
                Path.Combine(dest, "link"),
                outside
            );
            var zip = Path.Combine(scratch, "link.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry("link/file.txt")
                        .Open()
                );
                writer.Write("bad");
            }

            Throws<InvalidDataException>(
                () => FileUtils.ExtractToDirectory(zip, dest)
            );
            Check(
                !File.Exists(
                    Path.Combine(outside, "file.txt")
                )
            );
        }
    )
);
await Test(
    "rules_osx_arm_and_features",
    () => Sync(
        () =>
        {
            var rules = JArray.Parse("[{\"action\":\"allow\",\"os\":{\"name\":\"osx\",\"arch\":\"aarch64\"}}]");
            Check(
                MinecraftRules.Check(
                    rules,
                    "osx",
                    "aarch64",
                    "27",
                    new Dictionary<string, bool>()
                )
            );
            Check(
                !MinecraftRules.Check(
                    rules,
                    "windows",
                    "amd64",
                    "10",
                    new Dictionary<string, bool>()
                )
            );
            rules = JArray.Parse("[{\"action\":\"allow\",\"features\":{\"is_demo_user\":true}}]");
            Check(
                !MinecraftRules.Check(
                    rules,
                    "osx",
                    "aarch64",
                    "27",
                    new Dictionary<string, bool>()
                )
            );
        }
    )
);
await Test(
    "real_1_21_1_mac_library_and_argument_plan",
    () => Sync(
        () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures/1.21.1.json");
            var metadata = JsonFiles.Read(path);
            var platform = new TargetPlatform("osx", "aarch64", "27");
            var libraries = MinecraftService.BuildLibraries(metadata, scratch, platform);
            Check(
                libraries.Any(
                    x => x.Path.Contains("natives-macos-arm64")
                )
            );
            Check(
                !libraries.Any(
                    x => x.Path.Contains("natives-windows")
                )
            );
            using var service = new MinecraftService(
                new AppPaths(
                    Path.Combine(scratch, "plan with 空格")
                ),
                platform
            );
            var installed = new InstalledVersion(
                "1.21.1",
                path,
                Path.Combine(scratch, "client.jar"),
                "17",
                libraries,
                21
            );
            var plan = new LaunchService(service).CreatePlan(
                installed,
                new("/java/bin/java", "aarch64", 21),
                GameSession.Trial
            );
            Check(
                plan.Arguments.Contains("-XstartOnFirstThread") && plan.Arguments.Contains("--demo")
            );
            var cp = plan.Arguments[plan.Arguments.ToList()
                .IndexOf("-cp") + 1];
            Check(
                cp.Contains(':') && !cp.Contains(';')
            );
            Check(
                plan.Arguments.All(
                    x => !x.Contains("${") && !x.Contains("WINDOWS-ROOT")
                )
            );
            Check(
                plan.Arguments[plan.Arguments.ToList()
                    .IndexOf("--gameDir") + 1] == service.Paths.Game
            );
        }
    )
);
using (var server = new FixtureServer())
    using (var downloads = new DownloadService())
    {
        DownloadItem Item(string route, string file) => new(
            new Uri(server.Url + route),
            Path.Combine(scratch, file),
            server.Payload.Length,
            Convert.ToHexString(
                SHA1.HashData(server.Payload)
            )
                .ToLowerInvariant()
        );
        await Test(
            "download_resume_206",
            async () =>
            {
                var item = Item("range", "range.bin");
                File.WriteAllBytes(item.Path + ".part", server.Payload[..321]);
                await downloads.DownloadAsync(item);
                Check(
                    File.ReadAllBytes(item.Path)
                        .SequenceEqual(server.Payload)
                );
                Check(server.RangeObserved);
            }
        );
        await Test(
            "download_server_ignores_range",
            async () =>
            {
                var item = Item("ignore", "ignore.bin");
                File.WriteAllBytes(item.Path + ".part", server.Payload[..500]);
                await downloads.DownloadAsync(item);
                Check(
                    File.ReadAllBytes(item.Path)
                        .SequenceEqual(server.Payload)
                );
            }
        );
        await Test(
            "download_bad_hash_never_promoted",
            async () =>
            {
                var item = Item("bad", "bad.bin");
                try
                {
                    await downloads.DownloadAsync(item);
                    throw new Exception("Bad hash accepted");
                }
                catch (InvalidDataException)
                {
                }

                Check(
                    !File.Exists(item.Path)
                );
            }
        );
        await Test(
            "download_cancellation_does_not_promote",
            async () =>
            {
                var item = Item("slow", "slow.bin");
                using var c = new CancellationTokenSource(200);
                try
                {
                    await downloads.DownloadAsync(item, c.Token);
                    throw new Exception("Cancellation ignored");
                }
                catch (OperationCanceledException)
                {
                }

                Check(
                    !File.Exists(item.Path)
                );
            }
        );
        await Test(
            "download_duplicate_targets_and_valid_cache",
            async () =>
            {
                var item = Item("count", "count.bin");
                var before = server.RequestCount;
                await downloads.DownloadManyAsync(
                    new[]
                    {
                        item,
                        item
                    }
                );
                await downloads.DownloadAsync(item);
                Check(server.RequestCount - before == 1);
            }
        );
    }

await Test(
    "modrinth_facets_and_query_escaping",
    async () =>
    {
        string requested = "";
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                request =>
                {
                    requested = Uri.UnescapeDataString(request.RequestUri!.Query);
                    return new JObject
                    {
                        ["hits"] = new JArray(),
                        ["offset"] = 20,
                        ["total_hits"] = 33
                    };
                }
            )
        );
        var result = await catalog.SearchAsync(ResourceKind.Mod, "中文 & sodium", "1.21.1", "fabric", 20);
        Check(
            result.Total == 33 && requested.Contains("project_type:mod") && requested.Contains("categories:fabric") && requested.Contains("versions:1.21.1")
        );
        Check(
            requested.Contains("中文 & sodium")
        );
    }
);
// 原版“全部版本”浏览不能生成空版本 facet；带版本的安装检索继续保持约束。
await Test(
    "classic_resource_all_versions_filter",
    async () =>
    {
        string requested = "";
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                request =>
                {
                    requested = Uri.UnescapeDataString(request.RequestUri!.Query);
                    return new JObject
                    {
                        ["hits"] = new JArray(),
                        ["offset"] = 0,
                        ["total_hits"] = 0
                    };
                }
            )
        );
        await catalog.SearchAsync(ResourceKind.Mod, "Sodium", "", "");
        Check(
            requested.Contains("project_type:mod") && !requested.Contains("versions:") && !requested.Contains("categories:")
        );
        await catalog.SearchAsync(ResourceKind.Mod, "Sodium", "1.21.1", "fabric");
        Check(
            requested.Contains("versions:1.21.1") && requested.Contains("categories:fabric")
        );
    }
);
// 详情页可查询全部文件，而依赖求解的精确版本与默认 Fabric 筛选仍必须存在。
await Test(
    "classic_resource_versions_browse_and_install_separation",
    async () =>
    {
        string requested = "";
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                request =>
                {
                    if (!request.RequestUri!.AbsolutePath.EndsWith("/version"))
                    {
                        return ProjectFixture(
                            request.RequestUri.Segments.Last(),
                            request.RequestUri.Segments.Last() == "shader"
                                ? "shader"
                                : "mod"
                        );
                    }

                    requested = Uri.UnescapeDataString(request.RequestUri.Query);
                    return new JArray();
                }
            )
        );
        await catalog.VersionsAsync("sodium", ResourceKind.Mod, "", "");
        Check(
            !requested.Contains("game_versions=") && !requested.Contains("loaders=")
        );
        await catalog.VersionsAsync("sodium", ResourceKind.Mod, "1.21.1");
        Check(
            requested.Contains("game_versions=[\"1.21.1\"]") && requested.Contains("loaders=[\"fabric\"]")
        );
        await catalog.VersionsAsync("shader", ResourceKind.Shader, "1.21.1");
        Check(
            requested.Contains("loaders=[\"iris\"]")
        );
    }
);
// 分开提供项目详情和版本列表，保证标题查询不被列表夹具错误覆盖。
JObject ProjectFixture(string id, string kind = "mod") => new()
{
    ["id"] = id,
    ["slug"] = id,
    ["title"] = id,
    ["project_type"] = kind
};
await Test(
    "resource_project_rejects_wrong_json_shape",
    async () =>
    {
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                _ => new JArray()
            )
        );
        try
        {
            await catalog.ProjectAsync("bad");
            throw new Exception("Malformed project response accepted");
        }
        catch (InvalidDataException)
        {
        }
    }
);
await Test(
    "network_handoff_keeps_borrowed_downloader_usable",
    async () =>
    {
        var paths = new AppPaths(
            Path.Combine(scratch, "borrowed-downloader")
        );
        using var source = new MinecraftService(paths);
        var borrowed = source.Downloads;
        new NetworkOptions
        {
            ConcurrentFiles = 3
        }.Save(paths);
        source.RefreshNetwork();
        Check(
            ReferenceEquals(borrowed, source.Downloads),
            "Legacy pages lost their borrowed downloader"
        );
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(
            async () =>
            {
                using var connection = await listener.AcceptTcpClientAsync();
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync()is { } line && line.Length > 0)
                {
                }

                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nabc")
                );
            }
        );
        try
        {
            var target = Path.Combine(paths.Root, "payload.bin");
            await borrowed.DownloadAsync(
                new(
                    new Uri($"http://127.0.0.1:{port}/file"),
                    target,
                    3,
                    "a9993e364706816aba3e25717850c26c9cd0d89d"
                )
            )
                .WaitAsync(
                    TimeSpan.FromSeconds(5)
                );
            await server;
            Check(
                File.ReadAllText(target) == "abc"
            );
        }
        finally
        {
            listener.Stop();
        }
    }
);
ResourceRelease Release(string id, string project, params ResourceDependency[] dependencies) => new(
    id,
    project,
    project,
    id,
    "release",
    DateTimeOffset.UtcNow,
    new[]
    {
        "1.21.1"
    },
    new[]
    {
        "fabric"
    },
    new(
        project + ".jar",
        new Uri("https://cdn.modrinth.com/" + project),
        1,
        new string ('a', 40)
    ),
    dependencies
);
JObject ReleaseJson(ResourceRelease release) => JObject.FromObject(
    new
    {
        id = release.Id,
        project_id = release.ProjectId,
        name = release.Name,
        version_number = release.Number,
        version_type = release.Channel,
        date_published = release.Published,
        game_versions = release.GameVersions,
        loaders = release.Loaders,
        files = new[]
        {
            new
            {
                filename = release.File.Name,
                url = release.File.Url,
                size = release.File.Size,
                primary = true,
                hashes = new
                {
                    sha1 = release.File.Sha1
                }
            }
        },
        dependencies = release.Dependencies.Select(
            x => new
            {
                project_id = x.ProjectId,
                version_id = x.VersionId,
                dependency_type = x.Type
            }
        )
    }
);
await Test(
    "modrinth_required_dependency_and_optional_exclusion",
    async () =>
    {
        var dependency = Release("dep-version", "dep");
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                _ => ReleaseJson(dependency)
            )
        );
        var parent = Release(
            "top-version",
            "top",
            new("dep", "dep-version", "required"),
            new("optional", null, "optional")
        );
        var plan = await catalog.ResolveAsync(parent, ResourceKind.Mod, "1.21.1");
        Check(
            plan.Releases.Count == 2 && plan.Releases[0].Id == "dep-version" && plan.Releases[1].Id == "top-version"
        );
    }
);
await Test(
    "modrinth_pinned_dependency_conflict_and_loader_rejection",
    async () =>
    {
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                r => ReleaseJson(
                    Release(
                        r.RequestUri!.Segments.Last(),
                        "same"
                    )
                )
            )
        );
        var parent = Release(
            "root",
            "root",
            new("same", "v1", "required"),
            new("same", "v2", "required")
        );
        var rejected = false;
        try
        {
            await catalog.ResolveAsync(parent, ResourceKind.Mod, "1.21.1");
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Check(rejected);
        rejected = false;
        try
        {
            await catalog.ResolveAsync(
                parent with
                {
                    Loaders = new[]
                    {
                        "forge"
                    }
                },
                ResourceKind.Mod,
                "1.21.1"
            );
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Check(rejected);
    }
);
await Test(
    "fabric_merge_overrides_and_instance_isolation",
    () => Sync(
        () =>
        {
            var parent = JsonFiles.Read(
                Path.Combine(AppContext.BaseDirectory, "Fixtures/1.21.1.json")
            );
            var child = JObject.Parse(
                "{\"id\":\"fabric-test\",\"mainClass\":\"net.fabricmc.loader.impl.launch.knot.KnotClient\",\"arguments\":{\"jvm\":[\"-Dtest=fabric\"],\"game\":[]},\"libraries\":[{\"name\":\"org.ow2.asm:asm:9.10\",\"downloads\":{\"artifact\":{\"path\":\"asm.jar\",\"url\":\"https://example.com/asm.jar\",\"size\":1,\"sha1\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}}}]}"
            );
            var merged = FabricService.Merge(parent, child);
            Check((string)parent["id"]! == "1.21.1");
            Check((string)merged["mainClass"]! == "net.fabricmc.loader.impl.launch.knot.KnotClient");
            using var service = new MinecraftService(
                new AppPaths(
                    Path.Combine(scratch, "fabric-plan")
                ),
                new("osx", "aarch64", "27")
            );
            var json = Path.Combine(scratch, "merged.json");
            JsonFiles.Write(json, merged);
            var version = new InstalledVersion(
                "fabric-test",
                json,
                "client.jar",
                "17",
                MinecraftService.BuildLibraries(merged, service.Paths.Game, service.Platform),
                21
            );
            var directory = Path.Combine(scratch, "isolated-fabric");
            var plan = new LaunchService(service).CreatePlan(
                version,
                new("/java", "aarch64", 21),
                GameSession.Trial,
                gameDirectory: directory
            );
            Check(
                plan.WorkingDirectory == directory && plan.Arguments.Contains(directory) && plan.Arguments.Contains("-Dtest=fabric")
            );
            Check(
                FabricService.MavenPath("net.fabricmc:fabric-loader:0.16.10") == "net/fabricmc/fabric-loader/0.16.10/fabric-loader-0.16.10.jar"
            );
            Throws<InvalidDataException>(
                () => FabricService.MavenPath("../escape:a:1")
            );
        }
    )
);
await Test(
    "resource_filename_and_symlink_rejection",
    () => Sync(
        () =>
        {
            foreach (var name in new[]
            {
                "../escape.jar",
                "a\\b.jar",
                "/absolute.jar",
                "x:y.jar"
            }

            )
                Throws<InvalidDataException>(
                    () => ResourceInstaller.ValidateFileName(name)
                );
            var instance = Path.Combine(scratch, "resource-link");
            var outside = Path.Combine(scratch, "resource-outside");
            Directory.CreateDirectory(instance);
            Directory.CreateDirectory(outside);
            Directory.CreateSymbolicLink(
                Path.Combine(instance, "mods"),
                outside
            );
            Throws<InvalidDataException>(
                () => ResourceInstaller.SafeTarget(instance, "mods/evil.jar")
            );
        }
    )
);
await Test(
    "resource_install_update_disable_remove_and_protected_files",
    async () =>
    {
        using var downloads = new DownloadService(
            new ByteHandler()
        );
        var directory = Path.Combine(scratch, "resource-store");
        var store = new ResourceInstaller(directory, downloads);
        var local = Path.Combine(scratch, "test-mod.jar");
        using (var zip = ZipFile.Open(local, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(
                zip.CreateEntry("fabric.mod.json")
                    .Open()
            );
            writer.Write("{\"id\":\"fixture\",\"version\":\"1\"}");
        }

        var bytes = File.ReadAllBytes(local);
        var hash = Convert.ToHexString(
            SHA1.HashData(bytes)
        )
            .ToLowerInvariant();
        ByteHandler.Bytes = bytes;
        var v1 = Release("v1", "fixture")with
        {
            File = new(
                "fixture-v1.jar",
                new Uri("https://fixture.test/mod"),
                bytes.Length,
                hash
            )
        };
        await store.InstallAsync(
            new(
                ResourceKind.Mod,
                "fixture",
                new[]
                {
                    v1
                }
            )
        );
        Check(
            File.Exists(
                Path.Combine(directory, "mods/fixture-v1.jar")
            )
        );
        store.SetEnabled("fixture", false);
        Check(
            File.Exists(
                Path.Combine(directory, "mods/fixture-v1.jar.disabled")
            )
        );
        store.SetEnabled("fixture", true);
        var v2 = v1 with
        {
            Id = "v2",
            Number = "v2",
            File = v1.File with
            {
                Name = "fixture-v2.jar"
            }
        };
        await store.InstallAsync(
            new(
                ResourceKind.Mod,
                "fixture",
                new[]
                {
                    v2
                }
            )
        );
        Check(
            !File.Exists(
                Path.Combine(directory, "mods/fixture-v1.jar")
            ) && store.List()
                .Single()
                .VersionId == "v2"
        );
        File.AppendAllText(
            Path.Combine(directory, "mods/fixture-v2.jar"),
            "external edit"
        );
        Throws<IOException>(
            () => store.Remove("fixture")
        );
        Check(
            store.List()
                .Count == 1
        );
        File.WriteAllBytes(
            Path.Combine(directory, "mods/fixture-v2.jar"),
            bytes
        );
        store.Remove("fixture");
        Check(
            store.List()
                .Count == 0 && !File.Exists(
                Path.Combine(directory, "mods/fixture-v2.jar")
            )
        );
    }
);
await Test(
    "resource_download_failure_never_replaces_installed_version",
    async () =>
    {
        using var downloads = new DownloadService(
            new ByteHandler()
        );
        var directory = Path.Combine(scratch, "resource-failure");
        var store = new ResourceInstaller(directory, downloads);
        var bytes = File.ReadAllBytes(
            Path.Combine(scratch, "test-mod.jar")
        );
        ByteHandler.Bytes = bytes;
        var v1 = Release("v1", "mod")with
        {
            File = new(
                "mod-v1.jar",
                new Uri("https://fixture.test/mod"),
                bytes.Length,
                Convert.ToHexString(
                    SHA1.HashData(bytes)
                )
                    .ToLowerInvariant()
            )
        };
        await store.InstallAsync(
            new(
                ResourceKind.Mod,
                "mod",
                new[]
                {
                    v1
                }
            )
        );
        var bad = v1 with
        {
            Id = "v2",
            File = v1.File with
            {
                Name = "mod-v2.jar",
                Sha1 = new string ('b', 40)
            }
        };
        var rejected = false;
        try
        {
            await store.InstallAsync(
                new(
                    ResourceKind.Mod,
                    "mod",
                    new[]
                    {
                        bad
                    }
                )
            );
        }
        catch (IOException error)when (error.InnerException is InvalidDataException)
        {
            // Integrity failures must not be treated as a candidate compatibility rejection.
            rejected = true;
        }

        Check(
            rejected && store.List()
                .Single()
                .VersionId == "v1" && File.Exists(
                Path.Combine(directory, "mods/mod-v1.jar")
            ) && !File.Exists(
                Path.Combine(directory, "mods/mod-v2.jar")
            )
        );
    }
);
await Test(
    "resource_dependencies_protect_against_incompatible_updates",
    () => Sync(
        () =>
        {
            var old = new ManagedResource(
                "parent",
                "v1",
                "parent",
                "v1",
                ResourceKind.Mod,
                "parent.jar",
                new string ('a', 40),
                1,
                true,
                DateTimeOffset.UtcNow,
                new[]
                {
                    new ResourceDependency("dep", "dep-v1", "required")
                }
            );
            Throws<InvalidDataException>(
                () => ResourceInstaller.ValidateConflicts(
                    new[]
                    {
                        old
                    },
                    new(
                        ResourceKind.Mod,
                        "dep",
                        new[]
                        {
                            Release("dep-v2", "dep")
                        }
                    )
                )
            );
        }
    )
);
await Test(
    "fabric_semver_predicates_and_prerelease_order",
    () => Sync(
        () =>
        {
            Check(
                FabricVersionPredicate.Matches("0.6.13+mc1.21.1", "0.6.x"),
                "predicate assertion 1"
            );
            Check(
                !FabricVersionPredicate.Matches("0.8.13+mc1.21.1", "0.6.x"),
                "predicate assertion 2"
            );
            Check(
                FabricVersionPredicate.Matches(
                    "1.21.1",
                    JArray.Parse("[\"1.21.x\",\"1.20.x\"]")
                ),
                "predicate assertion 3"
            );
            Check(
                FabricVersionPredicate.Matches("0.6.13", ">=0.6.9 <0.7.0"),
                "predicate assertion 4"
            );
            Check(
                !FabricVersionPredicate.Matches("0.7.0", "~0.6.0"),
                "predicate assertion 5"
            );
            Check(
                FabricVersionPredicate.Matches("0.7.0", "^0.6.1"),
                "predicate assertion 6"
            );
            // Fabric caret ranges retain the major component even when it is zero.
            Check(
                !FabricVersionPredicate.Matches("1.0.0", "^0.6.1"),
                "caret upper bound"
            );
            Check(
                FabricVersionPredicate.Compare("1.0.0-beta.10", "1.0.0-beta.2") > 0,
                "predicate assertion 7"
            );
            Check(
                FabricVersionPredicate.Compare("1.0.0-beta.2", "1.0.0") < 0,
                "predicate assertion 8"
            );
        }
    )
);
await Test(
    "fabric_planner_uses_jar_constraints_instead_of_latest_dependency",
    async () =>
    {
        byte[] Jar(string id, string version, JObject? depends = null)
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry("fabric.mod.json")
                        .Open()
                );
                writer.Write(
                    new JObject
                    {
                        ["id"] = id,
                        ["version"] = version,
                        ["depends"] = depends ?? new JObject()
                    }.ToString()
                );
            }

            return memory.ToArray();
        }

        var payloads = new Dictionary<string, byte[]>
        {
            ["root"] = Jar(
                "root",
                "1.0",
                new JObject
                {
                    ["dep"] = "0.6.x"
                }
            ),
            ["dep-new"] = Jar("dep", "0.8.13"),
            ["dep-old"] = Jar("dep", "0.6.13")
        };
        ResourceRelease WithPayload(ResourceRelease r, string key) => r with
        {
            File = new(
                r.File.Name,
                new Uri("https://fixture.test/" + key),
                payloads[key].Length,
                Convert.ToHexString(
                    SHA1.HashData(payloads[key])
                )
                    .ToLowerInvariant()
            )
        };
        var rootMod = WithPayload(
            Release(
                "root-v1",
                "root",
                new ResourceDependency("dep", null, "required")
            ),
            "root"
        );
        var newer = WithPayload(
            Release("dep-new", "dep"),
            "dep-new"
        );
        var older = WithPayload(
            Release("dep-old", "dep"),
            "dep-old"
        );
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                request => request.RequestUri!.AbsolutePath.EndsWith("/version")
                    ? new JArray(
                        ReleaseJson(newer),
                        ReleaseJson(older)
                    )
                    : ProjectFixture(
                        request.RequestUri.Segments.Last()
                    )
            )
        );
        using var downloads = new DownloadService(
            new PayloadHandler(payloads)
        );
        var result = await new FabricModPlanner(
            catalog,
            downloads,
            Path.Combine(scratch, "planner-cache"),
            "0.19.5"
        ).ResolveAsync(rootMod, "1.21.1");
        Check(
            result.Releases.Any(
                x => x.Id == "dep-old"
            ) && !result.Releases.Any(
                x => x.Id == "dep-new"
            )
        );
    }
);
await Test(
    "resource_activation_preserves_existing_settings",
    async () =>
    {
        using var downloads = new DownloadService();
        var directory = Path.Combine(scratch, "activation");
        var store = new ResourceInstaller(directory, downloads);
        var shader = Path.Combine(scratch, "光影包.zip");
        using (var zip = ZipFile.Open(shader, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(
                zip.CreateEntry("shaders/test.vsh")
                    .Open()
            );
            writer.Write("#version 150");
            writer.Dispose();
            using var meta = new StreamWriter(
                zip.CreateEntry("pack.mcmeta")
                    .Open()
            );
            meta.Write("{\"pack\":{\"pack_format\":34,\"description\":\"fixture\"}}");
        }

        var item = await store.ImportAsync(shader, ResourceKind.Shader);
        Directory.CreateDirectory(
            Path.Combine(directory, "config")
        );
        File.WriteAllText(
            Path.Combine(directory, "config/iris.properties"),
            "unknownSetting=true\n"
        );
        store.Activate(item.ProjectId);
        var config = File.ReadAllText(
            Path.Combine(directory, "config/iris.properties")
        );
        Check(
            config.Contains("unknownSetting=true") && config.Contains("enableShaders=true") && config.Contains("\\u5149")
        );
        var pack = await store.ImportAsync(shader, ResourceKind.ResourcePack);
        File.WriteAllText(
            Path.Combine(directory, "options.txt"),
            "music:0.3\nresourcePacks:[\"vanilla\"]\n"
        );
        store.Activate(pack.ProjectId);
        var options = File.ReadAllText(
            Path.Combine(directory, "options.txt")
        );
        Check(
            options.Contains("music:0.3") && options.Contains("file/光影包.zip")
        );
    }
);
await Test(
    "mac_shader_required_optional_and_conditional_features",
    () => Sync(
        () =>
        {
            var blocked = MacShaderCompatibility.Analyze("iris.features.required = COMPUTE_SHADERS SSBO\n");
            Check(!blocked.CanActivate && blocked.UnsupportedRequiredFeatures.Count == 2);
            var fallback = MacShaderCompatibility.Analyze(
                "iris.features.optional=CUSTOM_IMAGES SSBO\n#ifdef MC_OS_MAC\n#define COLORED_LIGHTING 0\n#endif",
                true
            );
            Check(
                fallback.CanActivate && fallback.HasMacGuards && fallback.HasComputePrograms && fallback.Status == "requires_runtime_validation"
            );
            var otherPlatform = MacShaderCompatibility.Analyze("#ifndef MC_OS_MAC\niris.features.required=SSBO\n#endif\n");
            Check(otherPlatform.CanActivate && otherPlatform.RequiredFeatures.Count == 0);
            var unknownBranch = MacShaderCompatibility.Analyze("#if USER_OPTION\niris.features.required=SSBO\n#endif");
            Check(
                unknownBranch.CanActivate && unknownBranch.Notes.Any(
                    x => x.Contains("条件编译")
                )
            );
        }
    )
);
await Test(
    "mac_shader_application_blocks_known_unsupported_requirement",
    async () =>
    {
        using var downloads = new DownloadService();
        var directory = Path.Combine(scratch, "unsupported-shader");
        var store = new ResourceInstaller(directory, downloads);
        var file = Path.Combine(scratch, "unsupported-shader.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(
                zip.CreateEntry("shaders/shaders.properties")
                    .Open()
            );
            writer.Write("iris.features.required=SSBO");
        }

        var item = await store.ImportAsync(file, ResourceKind.Shader);
        if (OperatingSystem.IsMacOS())
            Throws<PlatformNotSupportedException>(
                () => store.Activate(item.ProjectId)
            );
        Check(
            !File.Exists(
                Path.Combine(directory, "config/iris.properties")
            )
        );
    }
);
await Test(
    "mac_native_macho_and_fat_arm64_detection",
    () => Sync(
        () =>
        {
            var thin = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(thin, 0xfeedfacf);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                thin.AsSpan(4),
                0x0100000c
            );
            Check(
                MacNativeAudit.HasArm64(thin)
            );
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                thin.AsSpan(4),
                0x01000007
            );
            Check(
                !MacNativeAudit.HasArm64(thin)
            );
            var fat = new byte[48];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fat, 0xcafebabe);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                fat.AsSpan(4),
                2
            );
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                fat.AsSpan(8),
                0x01000007
            );
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                fat.AsSpan(28),
                0x0100000c
            );
            Check(
                MacNativeAudit.HasArm64(fat) && !MacNativeAudit.HasArm64(
                    new byte[]
                    {
                        0,
                        1
                    }
                )
            );
        }
    )
);
await Test(
    "mac_independent_profiles_keep_original_fabric",
    () => Sync(
        () =>
        {
            using var service = new MinecraftService(
                new AppPaths(
                    Path.Combine(scratch, "mac-profiles")
                )
            );
            foreach (var name in new[]
            {
                "fabric-1.21.1",
                "mac-base-1.21.1",
                "mac-shaders-1.21.1"
            }

            )
                JsonFiles.Write(
                    Path.Combine(service.Paths.Root, "instances", name, "profile.json"),
                    new
                    {
                        id = name,
                        loaderVersion = "0.19.5",
                        gameVersion = "1.21.1",
                        displayName = name
                    }
                );
            using var defaultFabric = new FabricService(service);
            using var mac = new FabricService(service, "mac-shaders-1.21.1");
            Check(
                defaultFabric.Profiles()
                    .Count == 4 && defaultFabric.FabricProfile()
                    .Id == "fabric-1.21.1"
            );
            Check(
                mac.FabricProfile()
                    .Id == "mac-shaders-1.21.1" && FabricService.LoaderFor(
                    mac.FabricProfile()
                ) == "0.19.5"
            );
        }
    )
);
await Test(
    "mac_pin_rejects_changed_file_and_preset_uses_external_settings",
    () => Sync(
        () =>
        {
            var pin = MacSetupService.Iris;
            var release = Release(pin.VersionId, pin.ProjectId)with
            {
                File = new(
                    "iris.jar",
                    new Uri("https://fixture.test/iris"),
                    pin.Size,
                    pin.Sha1
                )
            };
            MacSetupService.CheckPinned(pin, release);
            Throws<InvalidDataException>(
                () => MacSetupService.CheckPinned(
                    pin,
                    release with
                    {
                        File = release.File with
                        {
                            Sha1 = new string ('0', 40)
                        }
                    }
                )
            );
            var zipPath = Path.Combine(scratch, "profiles.zip");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry("shaders/shaders.properties")
                        .Open()
                );
                writer.Write(
                    "profile.LOW=SHADOW_QUALITY=0 COLORED_LIGHTING=0\nprofile.MEDIUM=profile.LOW SHADOW_QUALITY=2 WORLD_SPACE_REFLECTIONS=-1\n"
                );
            }

            var before = File.ReadAllBytes(zipPath);
            var options = MacShaderCompatibility.ProfileOptions(zipPath, "MEDIUM");
            Check(options["SHADOW_QUALITY"] == "2" && options["COLORED_LIGHTING"] == "0");
            var target = Path.Combine(scratch, "shader-options.txt");
            File.WriteAllText(target, "keep=unchanged\nSHADOW_QUALITY=3\n");
            PropertySettings.Merge(target, options);
            Check(
                File.ReadAllText(target)
                    .Contains("keep=unchanged") && File.ReadAllText(target)
                    .Contains("SHADOW_QUALITY=2")
            );
            Check(
                File.ReadAllBytes(zipPath)
                    .SequenceEqual(before)
            );
        }
    )
);
await Test(
    "mac_native_audit_keeps_official_scoped_intel_patch",
    () => Sync(
        () =>
        {
            var directory = Path.Combine(scratch, "native-audit");
            Directory.CreateDirectory(directory);
            byte[] Header(uint cpu)
            {
                var b = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, 0xfeedfacf);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    b.AsSpan(4),
                    cpu
                );
                return b;
            }

            var arm = Path.Combine(directory, "lwjgl-natives-macos-arm64.jar");
            var patch = Path.Combine(directory, "lwjgl-freetype-natives-macos-patch.jar");
            using (var z = ZipFile.Open(arm, ZipArchiveMode.Create))
            {
                using var entry = z.CreateEntry("macos/arm64/liblwjgl.dylib")
                    .Open();
                entry.Write(
                    Header(0x0100000c)
                );
            }

            using (var z = ZipFile.Open(patch, ZipArchiveMode.Create))
            {
                using var entry = z.CreateEntry("macos/x64/org/lwjgl/freetype/libfreetype.dylib")
                    .Open();
                entry.Write(
                    Header(0x01000007)
                );
            }

            var items = new[]
            {
                new DownloadItem(
                    new Uri("https://fixture.test/arm"),
                    arm,
                    -1,
                    null
                ),
                new DownloadItem(
                    new Uri("https://fixture.test/patch"),
                    patch,
                    -1,
                    null
                )
            };
            var report = MacNativeAudit.Check(
                new("1.21.1", "metadata", "jar", "17", items, 21),
                new("java", "aarch64", 21)
            );
            Check(
                report.Count == 2 && report.Count(
                    x => x.Applicable
                ) == 1 && !report[1].Applicable
            );
        }
    )
);
JObject PackIndex(string loader = "fabric-loader", string version = "0.19.5") => new()
{
    ["formatVersion"] = 1,
    ["game"] = "minecraft",
    ["name"] = "Fixture",
    ["versionId"] = "1",
    ["dependencies"] = new JObject
    {
        ["minecraft"] = "1.21.1",
        [loader] = version
    },
    ["files"] = new JArray()
};
JObject PackFile(string path, string client = "required") => new()
{
    ["path"] = path,
    ["fileSize"] = 0,
    ["hashes"] = new JObject
    {
        ["sha1"] = new string ('a', 40),
        ["sha512"] = new string ('b', 128)
    },
    ["downloads"] = new JArray("https://cdn.modrinth.com/file"),
    ["env"] = new JObject
    {
        ["client"] = client
    }
};
await Test(
    "mrpack_client_optional_and_loader_versions",
    () => Sync(
        () =>
        {
            foreach (var(loader, version)in new[]
            {
                ("fabric-loader", "0.19.5"),
                ("forge", "52.1.20"),
                ("neoforge", "21.1.264")
            }

            )
            {
                var index = PackIndex(loader, version);
                index["files"] = new JArray(
                    PackFile("mods/client.jar"),
                    PackFile("mods/optional.jar", "optional"),
                    PackFile("mods/server.jar", "unsupported")
                );
                var plan = ModpackService.Parse(index);
                Check(
                    plan.Files.Count == 1 && plan.SkippedOptionalFiles == 1 && plan.SkippedServerFiles == 1 && plan.LoaderVersion == version
                );
                Check(
                    ModpackService.Parse(index, true)
                        .Files.Count == 2
                );
            }
        }
    )
);
await Test(
    "mrpack_rejects_unsupported_and_ambiguous_dependencies",
    () => Sync(
        () =>
        {
            var index = PackIndex();
            index["dependencies"]!["minecraft"] = "1.20.1";
            Check(
                ModpackService.Parse(index)
                    .Minecraft == "1.20.1"
            );
            index["dependencies"]!["minecraft"] = "../bad";
            Throws<InvalidDataException>(
                () => ModpackService.Parse(index)
            );
            index = PackIndex("truly-unknown-loader");
            Throws<NotSupportedException>(
                () => ModpackService.Parse(index)
            );
            index = PackIndex();
            index["dependencies"]!["forge"] = "52.1.20";
            Throws<InvalidDataException>(
                () => ModpackService.Parse(index)
            );
        }
    )
);
await Test(
    "mrpack_paths_hashes_and_download_sources",
    () => Sync(
        () =>
        {
            foreach (var path in new[]
            {
                "../outside",
                "/absolute",
                "C:/file",
                "mods\\bad.jar",
                "libraries/overwrite",
                "profile.json",
                ".pcl-resources.json",
                "version.json",
                "pcl-modpack.json"
            }

            )
                Throws<InvalidDataException>(
                    () => ModpackService.ValidatePath(path)
                );
            foreach (var url in new[]
            {
                "http://cdn.modrinth.com/a",
                "https://cdn.modrinth.com.evil.test/a",
                "https://user@cdn.modrinth.com/a",
                "https://cdn.modrinth.com:444/a"
            }

            )
            {
                var file = PackFile("mods/a.jar");
                file["downloads"] = new JArray(url);
                var index = PackIndex();
                index["files"] = new JArray(file);
                Throws<InvalidDataException>(
                    () => ModpackService.Parse(index)
                );
            }

            var bad = PackFile("mods/a.jar");
            bad["hashes"]!["sha512"] = null;
            var invalid = PackIndex();
            invalid["files"] = new JArray(bad);
            Throws<InvalidDataException>(
                () => ModpackService.Parse(invalid)
            );
            invalid["files"] = new JArray(
                PackFile("mods/A.jar"),
                PackFile("mods/a.jar")
            );
            Throws<InvalidDataException>(
                () => ModpackService.Parse(invalid)
            );
        }
    )
);
await Test(
    "mrpack_overrides_order_and_server_exclusion",
    async () =>
    {
        var pack = Path.Combine(scratch, "override.mrpack");
        using (var zip = ZipFile.Open(pack, ZipArchiveMode.Create))
            foreach (var(path, value)in new[]
            {
                ("overrides/config/a.txt", "common"),
                ("client-overrides/config/a.txt", "client"),
                ("server-overrides/config/server.txt", "server")
            }

            )
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry(path)
                        .Open()
                );
                writer.Write(value);
            }

        var target = Path.Combine(scratch, "override-target");
        await ModpackService.ExtractOverridesAsync(pack, target);
        Check(
            File.ReadAllText(
                Path.Combine(target, "config/a.txt")
            ) == "client" && !File.Exists(
                Path.Combine(target, "config/server.txt")
            )
        );
    }
);
await Test(
    "mrpack_symlink_and_case_collision_rejected",
    () => Sync(
        () =>
        {
            foreach (var symlink in new[]
            {
                true,
                false
            }

            )
            {
                using var stream = new MemoryStream();
                using var zip = new ZipArchive(stream, ZipArchiveMode.Update);
                var entry = zip.CreateEntry("overrides/config/a");
                if (symlink)
                    entry.ExternalAttributes = unchecked((int)0xa1ff0000);
                else
                    zip.CreateEntry("overrides/config/A");
                Throws<InvalidDataException>(
                    () => ModpackService.ValidateOverrides(zip)
                );
            }
        }
    )
);
await Test(
    "mrpack_files_visible_in_resource_management",
    () => Sync(
        () =>
        {
            var target = Path.Combine(scratch, "managed-pack");
            Directory.CreateDirectory(
                Path.Combine(target, "mods")
            );
            using (var zip = ZipFile.Open(
                Path.Combine(target, "mods/example.jar.disabled"),
                ZipArchiveMode.Create
            ))
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry("fabric.mod.json")
                        .Open()
                );
                writer.Write("{\"schemaVersion\":1,\"id\":\"example\",\"version\":\"1.0.0\"}");
            }

            ModpackService.RegisterFiles(target);
            using var downloads = new DownloadService();
            var store = new ResourceInstaller(target, downloads);
            var item = store.List()
                .Single();
            Check(
                !item.Enabled && item.FileName == "example.jar" && item.ProjectId.StartsWith("local-pack-")
            );
            store.SetEnabled(item.ProjectId, true);
            Check(
                File.Exists(
                    Path.Combine(target, "mods/example.jar")
                )
            );
            store.Remove(item.ProjectId);
            Check(
                store.List()
                    .Count == 0
            );
        }
    )
);
await Test(
    "forge_neoforge_metadata_must_match_target_loader",
    () => Sync(
        () =>
        {
            using var stream = new MemoryStream();
            using var zip = new ZipArchive(stream, ZipArchiveMode.Update);
            zip.CreateEntry("META-INF/neoforge.mods.toml");
            ResourceInstaller.ValidateModArchive(zip, "neoforge");
            Throws<InvalidDataException>(
                () => ResourceInstaller.ValidateModArchive(zip, "forge")
            );
            Throws<InvalidDataException>(
                () => ResourceInstaller.ValidateModArchive(zip, "fabric")
            );
        }
    )
);
await Test(
    "generated_patch_local_artifact_no_network_fallback",
    async () =>
    {
        var json = new JObject
        {
            ["libraries"] = new JArray(
                new JObject
                {
                    ["downloads"] = new JObject
                    {
                        ["artifact"] = new JObject
                        {
                            ["path"] = "generated/client.jar",
                            ["url"] = "",
                            ["pclInstallerGenerated"] = true,
                            ["size"] = 1,
                            ["sha1"] = new string ('a', 40)
                        }
                    }
                }
            )
        };
        var items = MinecraftService.BuildLibraries(
            json,
            scratch,
            new("osx", "aarch64", "27")
        );
        Check(
            items.Single()
                .Url.IsFile
        );
        using var downloads = new DownloadService();
        var rejected = false;
        try
        {
            await downloads.DownloadAsync(
                items.Single()
            );
        }
        catch (FileNotFoundException)
        {
            rejected = true;
        }

        Check(rejected);
    }
);
await Test(
    "neoforge_private_module_path_and_version_jar",
    () => Sync(
        () =>
        {
            var json = new JObject
            {
                ["mainClass"] = "bootstrap.Main",
                ["arguments"] = new JObject
                {
                    ["jvm"] = new JArray(
                        "-p",
                        "${library_directory}/bootstrap.jar${classpath_separator}${library_directory}/util.jar",
                        "-DignoreList=${version_name}.jar",
                        "-cp",
                        "${classpath}"
                    ),
                    ["game"] = new JArray()
                }
            };
            var metadata = Path.Combine(scratch, "neo-version.json");
            JsonFiles.Write(metadata, json);
            using var service = new MinecraftService(
                new AppPaths(
                    Path.Combine(scratch, "neo-plan")
                ),
                new("osx", "aarch64", "27")
            );
            var root = Path.Combine(scratch, "Neo instance 空格");
            var libs = Path.Combine(root, "libraries");
            var jar = Path.Combine(root, "neo.jar");
            var plan = new LaunchService(service).CreatePlan(
                new(
                    "neo",
                    metadata,
                    jar,
                    "17",
                    Array.Empty<DownloadItem>(),
                    21,
                    libs
                ),
                new("/java", "aarch64", 21),
                GameSession.Trial,
                gameDirectory: root
            );
            Check(
                plan.Arguments.Contains(libs + "/bootstrap.jar:" + libs + "/util.jar") && plan.Arguments.Contains("-DignoreList=neo.jar") && plan.Arguments.Contains(jar)
            );
        }
    )
);
await Test(
    "instance_registry_loader_types_and_pending_hidden",
    () => Sync(
        () =>
        {
            using var service = new MinecraftService(
                new AppPaths(
                    Path.Combine(scratch, "registry")
                )
            );
            foreach (var loader in new[]
            {
                "fabric",
                "forge",
                "neoforge",
                "pending-pack-abcd"
            }

            )
            {
                JsonFiles.Write(
                    Path.Combine(service.Paths.Root, "instances", loader, "profile.json"),
                    new
                    {
                        id = loader,
                        gameVersion = "1.21.1",
                        loader,
                        loaderVersion = "1"
                    }
                );
            }

            var profiles = InstanceRegistry.List(service);
            Check(
                profiles.Count == 4 && profiles.Any(
                    x => x.Loader == "neoforge"
                ) && !profiles.Any(
                    x => x.Id.StartsWith("pending-pack-")
                )
            );
        }
    )
);
await Test(
    "java_inspection_accepts_both_streams_and_arch_aliases",
    () => Sync(
        () =>
        {
            foreach (var arch in new[]
            {
                "aarch64",
                "arm64",
                "x86_64",
                "amd64"
            }

            )
            {
                var properties = "    os.arch = " + arch + "\n";
                var runtime = JavaService.ParseInspection("/java", "openjdk version \"21\" 2023-09-19\n", properties);
                Check(
                    runtime.MajorVersion == 21 && runtime.Architecture == (arch is "arm64" or "aarch64"
                        ? "aarch64"
                        : "amd64")
                );
                runtime = JavaService.ParseInspection("/java", properties, "java version \"21.0.7\"");
                Check(runtime.MajorVersion == 21);
            }

            Throws<InvalidDataException>(
                () => JavaService.ParseInspection("/java", "java version \"21\"", "os.arch = x86\n")
            );
            Throws<InvalidDataException>(
                () => JavaService.ParseInspection("/java", "not a JVM", "os.arch = aarch64\n")
            );
            var realSettings = "java.vm.name = OpenJDK 64-Bit Server VM\nos.arch = aarch64\nopenjdk version \"21.0.7\" 2025-04-15 LTS\nOpenJDK 64-Bit Server VM\n";
            Check(
                JavaService.ParseInspection("/java", "", realSettings)
                    .MajorVersion == 21
            );
        }
    )
);
await Test(
    "java_inspection_process_exit_and_cancellation",
    async () =>
    {
        if (OperatingSystem.IsWindows())
            return;
        var exe = Path.Combine(scratch, "fake java with spaces");
        void Script(string body)
        {
            File.WriteAllText(exe, "#!/bin/sh\n" + body);
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Script("printf 'openjdk version \"21\"\\nos.arch = arm64\\n'\n");
        Check(
            (await JavaService.InspectAsync(exe)).Architecture == "aarch64"
        );
        Script("printf 'openjdk version \"21\"\\nos.arch = arm64\\n'\nexit 7\n");
        var rejected = false;
        try
        {
            await JavaService.InspectAsync(exe);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Check(rejected);
        Script("exec /bin/sleep 30\n");
        using var cancel = new CancellationTokenSource(100);
        var cancelled = false;
        try
        {
            await JavaService.InspectAsync(exe, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Check(cancelled);
    }
);
await Test(
    "mac_jna_embedded_native_architecture_audit",
    () => Sync(
        () =>
        {
            var dir = Path.Combine(scratch, "jna-audit");
            Directory.CreateDirectory(dir);
            var jar = Path.Combine(dir, "jna.jar");
            byte[] Header(uint cpu)
            {
                var b = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, 0xfeedfacf);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                    b.AsSpan(4),
                    cpu
                );
                return b;
            }

            using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
            {
                using (var entry = zip.CreateEntry("com/sun/jna/darwin-aarch64/libjnidispatch.jnilib")
                    .Open())
                    entry.Write(
                        Header(0x0100000c)
                    );
                using (var entry = zip.CreateEntry("com/sun/jna/darwin-x86-64/libjnidispatch.jnilib")
                    .Open())
                    entry.Write(
                        Header(0x01000007)
                    );
                using (var entry = zip.CreateEntry("net/jpountz/util/darwin/x86_64/liblz4-java.dylib")
                    .Open())
                    entry.Write(
                        Header(0x01000007)
                    );
                using (var entry = zip.CreateEntry("libnative.dylib.dSYM/Contents/Resources/DWARF/libnative.dylib")
                    .Open())
                    entry.Write(
                        new byte[]
                        {
                            0,
                            0,
                            0
                        }
                    );
            }

            var libraries = new[]
            {
                new DownloadItem(
                    new Uri("https://fixture/jna"),
                    jar,
                    -1,
                    null
                )
            };
            var audit = MacNativeAudit.Check(
                new("test", "json", "jar", "17", libraries, 21),
                new("java", "aarch64", 21)
            );
            Check(
                audit.Count == 3 && audit.Count(
                    x => x.Applicable && x.HasArm64
                ) == 1
            );
        }
    )
);
await Test(
    "launch_preflight_uses_instance_game_version_for_dependencies",
    async () =>
    {
        // 默认 Host 绑定 1.21.1，而所选 Fabric 实例绑定 1.20.1；预检必须按实例判断，且不能跳过真正的冲突。
        var dir = Path.Combine(scratch, "launch-instance-version");
        var instance = Path.Combine(dir, "instances", "fabric-1201");
        Directory.CreateDirectory(
            Path.Combine(instance, "mods")
        );
        using var service = new MinecraftService(
            new AppPaths(dir),
            new("osx", "aarch64", "27")
        );
        var profile = new JObject
        {
            ["loader"] = "fabric",
            ["loaderVersion"] = "0.16.14",
            ["gameVersion"] = "1.20.1"
        };
        JsonFiles.Write(
            Path.Combine(instance, "profile.json"),
            profile
        );
        using (var zip = ZipFile.Open(
            Path.Combine(instance, "mods", "fixture.jar"),
            ZipArchiveMode.Create
        ))
        {
            using var writer = new StreamWriter(
                zip.CreateEntry("fabric.mod.json")
                    .Open()
            );
            writer.Write(
                "{\"schemaVersion\":1,\"id\":\"versionfixture\",\"version\":\"1.0.0\",\"depends\":{\"minecraft\":\"=1.20.1\"}}"
            );
        }

        var metadata = Path.Combine(instance, "version.json");
        JsonFiles.Write(
            metadata,
            new
            {
                javaVersion = new
                {
                    majorVersion = 17
                },
                downloads = new
                {
                    client = new
                    {
                        size = 99,
                        sha1 = new string ('0', 40)
                    }
                }
            }
        );
        var jar = Path.Combine(instance, "client.jar");
        File.WriteAllText(jar, "deliberately corrupt client");
        var version = new InstalledVersion(
            "fabric-1201",
            metadata,
            jar,
            "5",
            [],
            17
        );
        // 用下游客户端完整性检查作边界：兼容依赖必须通过到此处，不运行伪造游戏或触发网络。
        var reachedClientValidation = false;
        try
        {
            await new LaunchService(service).PrepareAsync(
                version,
                new("java", "aarch64", 17),
                GameSession.Trial,
                gameDirectory: instance
            );
        }
        catch (InvalidDataException ex)
        {
            reachedClientValidation = ex.Message.Contains("客户端已损坏");
        }

        Check(reachedClientValidation);
        profile["gameVersion"] = "1.21.1";
        JsonFiles.Write(
            Path.Combine(instance, "profile.json"),
            profile
        );
        var rejectedConflict = false;
        try
        {
            await new LaunchService(service).PrepareAsync(
                version,
                new("java", "aarch64", 17),
                GameSession.Trial,
                gameDirectory: instance
            );
        }
        catch (InvalidDataException ex)
        {
            rejectedConflict = ex.Message.Contains("Fabric 依赖冲突");
        }

        Check(rejectedConflict);
    }
);
await Test(
    "mac_preflight_blocks_corrupt_library_and_instance_client",
    async () =>
    {
        var dir = Path.Combine(scratch, "preflight-corrupt");
        Directory.CreateDirectory(dir);
        using var service = new MinecraftService(
            new AppPaths(dir),
            new("osx", "aarch64", "27")
        );
        var metadata = Path.Combine(dir, "version.json");
        var jar = Path.Combine(dir, "client.jar");
        File.WriteAllText(jar, "client");
        JsonFiles.Write(
            metadata,
            new JObject
            {
                ["downloads"] = new JObject
                {
                    ["client"] = new JObject
                    {
                        ["size"] = 6,
                        ["sha1"] = Convert.ToHexString(
                            SHA1.HashData(
                                File.ReadAllBytes(jar)
                            )
                        )
                            .ToLowerInvariant()
                    }
                }
            }
        );
        var library = new DownloadItem(
            new Uri("https://fixture/lib"),
            Path.Combine(dir, "missing.jar"),
            1,
            new string ('a', 40)
        );
        var version = new InstalledVersion(
            "test",
            metadata,
            jar,
            "17",
            new[]
            {
                library
            },
            21
        );
        var rejected = false;
        try
        {
            await new LaunchService(service).PrepareAsync(
                version,
                new("java", "aarch64", 21),
                GameSession.Trial
            );
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains("库文件");
        }

        Check(rejected);
        File.WriteAllText(jar, "changed");
        rejected = false;
        try
        {
            await new LaunchService(service).PrepareAsync(
                version,
                new("java", "aarch64", 21),
                GameSession.Trial
            );
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains("客户端");
        }

        Check(rejected);
    }
);
await Test(
    "mac_dock_icon_hash_and_first_thread_dedup",
    () => Sync(
        () =>
        {
            using var service = new MinecraftService(
                new AppPaths(
                    Path.Combine(scratch, "dock path 中文")
                ),
                new("osx", "aarch64", "27")
            );
            var bytes = Encoding.UTF8.GetBytes("fixture-icon");
            var hash = Convert.ToHexString(
                SHA1.HashData(bytes)
            )
                .ToLowerInvariant();
            var icon = Path.Combine(service.Paths.Game, "assets/objects", hash[..2], hash);
            Directory.CreateDirectory(
                Path.GetDirectoryName(icon)!
            );
            File.WriteAllBytes(icon, bytes);
            JsonFiles.Write(
                Path.Combine(service.Paths.Game, "assets/indexes/17.json"),
                new JObject
                {
                    ["objects"] = new JObject
                    {
                        ["icons/minecraft.icns"] = new JObject
                        {
                            ["hash"] = hash,
                            ["size"] = bytes.Length
                        }
                    }
                }
            );
            var metadata = Path.Combine(scratch, "dock-version.json");
            JsonFiles.Write(
                metadata,
                new JObject
                {
                    ["mainClass"] = "Main",
                    ["arguments"] = new JObject
                    {
                        ["jvm"] = new JArray("-XstartOnFirstThread"),
                        ["game"] = new JArray()
                    }
                }
            );
            var version = new InstalledVersion(
                "test",
                metadata,
                "jar",
                "17",
                Array.Empty<DownloadItem>(),
                21
            );
            var plan = new LaunchService(service).CreatePlan(
                version,
                new("java", "aarch64", 21),
                GameSession.Trial
            );
            Check(
                plan.Arguments.Count(
                    x => x == "-XstartOnFirstThread"
                ) == 1 && plan.Arguments.Contains("-Xdock:icon=" + icon)
            );
            File.WriteAllText(icon, "corrupt");
            plan = new LaunchService(service).CreatePlan(
                version,
                new("java", "aarch64", 21),
                GameSession.Trial
            );
            Check(
                !plan.Arguments.Any(
                    x => x.StartsWith("-Xdock:icon=")
                )
            );
        }
    )
);
await Test(
    "pcl2_offline_uuid_name_validation_and_non_demo",
    () => Sync(
        () =>
        {
            foreach (var(name, uuid)in new[]
            {
                ("Player", "0000000000003006998f555b7570e899"),
                ("PCL_Mac", "0000000000003007998ffd4d55dea565"),
                ("离线玩家", "0000000000003004998f501aaa14eb71")
            }

            )
            {
                var session = AccountManager.OfflineSession(" " + name + " ");
                Check(
                    session.Uuid == uuid && session.Name == name && !session.Demo && session.UserType == "legacy" && session.AccessToken == uuid
                );
            }

            foreach (var name in new[]
            {
                "",
                "   ",
                new string ('a', 17),
                "bad\"name",
                "a\nb"
            }

            )
                Throws<InvalidDataException>(
                    () => AccountManager.OfflineSession(name)
                );
        }
    )
);
await Test(
    "offline_accounts_persist_switch_remove_without_keychain",
    async () =>
    {
        var paths = new AppPaths(
            Path.Combine(scratch, "offline-accounts")
        );
        var secrets = new MemoryAccountSecrets
        {
            RejectAccess = true
        };
        var manager = new AccountManager(paths, secrets);
        var first = manager.AddOffline("PCL_Mac");
        var second = manager.AddOffline("Player");
        manager.Select(first.Id);
        manager = new(paths, secrets);
        Check(manager.Accounts.Count == 2 && manager.SelectedId == first.Id);
        using var auth = new MicrosoftAuthentication(
            new JsonHandler(
                _ => throw new Exception("Offline must not authenticate")
            )
        );
        Check(
            (await manager.ResolveAsync(first.Id, auth)).Name == "PCL_Mac"
        );
        manager.Remove(second.Id);
        Check(manager.Accounts.Count == 1 && secrets.Calls == 0);
        var fixture = JsonFiles.Read(
            Path.Combine(AppContext.BaseDirectory, "Fixtures/1.21.1.json")
        );
        var json = Path.Combine(scratch, "offline-plan.json");
        JsonFiles.Write(json, fixture);
        using var service = new MinecraftService(
            paths,
            new("osx", "aarch64", "27")
        );
        var plan = new LaunchService(service).CreatePlan(
            new(
                "1.21.1",
                json,
                "jar",
                "17",
                Array.Empty<DownloadItem>(),
                21
            ),
            new("java", "aarch64", 21),
            AccountManager.OfflineSession("PCL_Mac")
        );
        Check(
            plan.Arguments.Contains("PCL_Mac") && plan.Arguments.Contains("legacy") && !plan.Arguments.Contains("--demo")
        );
    }
);
const string LoginClientId = "11111111-2222-3333-4444-555555555555";
await Test(
    "microsoft_embedded_application_config_rejects_secrets_and_invalid_ids",
    () => Sync(
        () =>
        {
            var empty = MicrosoftApplication.Parse("{\"clientId\":\"\"}");
            Check(!empty.IsConfigured);
            Throws<InvalidOperationException>(
                () => empty.RequireClientId()
            );
            Check(
                MicrosoftApplication.Parse("{\"clientId\":\"" + LoginClientId + "\"}")
                    .RequireClientId() == LoginClientId
            );
            foreach (var json in new[]
            {
                "{}",
                "[]",
                "{\"clientId\":null}",
                "{\"clientId\":\"invalid\"}",
                "{\"clientId\":\"00000000-0000-0000-0000-000000000000\"}",
                "{\"clientId\":\"\",\"clientSecret\":\"fixture-secret\"}"
            }

            )
                Throws<InvalidDataException>(
                    () => MicrosoftApplication.Parse(json)
                );
            var embedded = MicrosoftApplication.BuiltIn;
            Check(
                embedded.ClientId == "" || embedded.RequireClientId() == embedded.ClientId
            );
        }
    )
);
await Test(
    "microsoft_public_client_requests_have_no_secret_or_password",
    async () =>
    {
        var requests = new List<string>();
        using var auth = new MicrosoftAuthentication(
            new LoginHttpHandler(
                async request =>
                {
                    if (request.RequestUri!.Host == "login.microsoftonline.com")
                    {
                        var form = await request.Content!.ReadAsStringAsync();
                        requests.Add(form);
                        Check(
                            request.RequestUri.AbsolutePath.StartsWith("/consumers/")
                        );
                        Check(
                            form.Contains("client_id=" + LoginClientId) && !form.Contains("client_secret") && !form.Contains("password")
                        );
                    }

                    if (request.RequestUri.Host.EndsWith(".xboxlive.com"))
                    {
                        var payload = JObject.Parse(
                            await request.Content!.ReadAsStringAsync()
                        );
                        Check(
                            payload["Properties"] is JObject && payload["properties"] is null && (string? )payload["TokenType"] == "JWT"
                        );
                        Check(request.Content.Headers.ContentType?.MediaType == "application/json");
                        if (request.RequestUri.Host == "user.auth.xboxlive.com")
                            Check(
                                (string? )payload["Properties"]?["RpsTicket"] == "d=fixture-oauth" && (string? )payload["RelyingParty"] == "http://auth.xboxlive.com"
                            );
                        else
                            Check(
                                (string? )payload["Properties"]?["SandboxId"] == "RETAIL" && (string? )payload["RelyingParty"] == "rp://api.minecraftservices.com/"
                            );
                    }

                    if (request.RequestUri.AbsolutePath == "/authentication/login_with_xbox")
                    {
                        var payload = JObject.Parse(
                            await request.Content!.ReadAsStringAsync()
                        );
                        Check(payload["identityToken"] is not null && payload["IdentityToken"] is null);
                    }

                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            LoginResponse(request)
                                .ToString(),
                            Encoding.UTF8,
                            "application/json"
                        )
                    };
                }
            ),
            (_, _) => Task.CompletedTask
        );
        var application = MicrosoftApplication.Parse("{\"clientId\":\"" + LoginClientId + "\"}");
        var challenge = await auth.BeginAsync(
            application.RequireClientId()
        );
        var login = await auth.CompleteLoginAsync(
            application.RequireClientId(),
            challenge
        );
        Check(login.ClientId == application.ClientId && requests.Count == 2);
        Check(
            requests[0].Contains("scope=XboxLive.signin+offline_access")
        );
    }
);
const string LoginUuid = "1234567890abcdef1234567890abcdef";
JObject LoginResponse(HttpRequestMessage request, string refresh = "fixture-refresh-rotated", bool ownsGame = true) => (request.RequestUri!.Host + request.RequestUri.AbsolutePath) switch
{
    "login.microsoftonline.com/consumers/oauth2/v2.0/devicecode" => new()
    {
        ["user_code"] = "ABCD-EFGH",
        ["verification_uri"] = "https://www.microsoft.com/link",
        ["device_code"] = "fixture-device",
        ["interval"] = 1,
        ["expires_in"] = 600
    },
    "login.microsoftonline.com/consumers/oauth2/v2.0/token" => new()
    {
        ["access_token"] = "fixture-oauth",
        ["refresh_token"] = refresh
    },
    "user.auth.xboxlive.com/user/authenticate" => new()
    {
        ["Token"] = "fixture-xbl"
    },
    "xsts.auth.xboxlive.com/xsts/authorize" => new()
    {
        ["Token"] = "fixture-xsts",
        ["DisplayClaims"] = new JObject
        {
            ["xui"] = new JArray(
                new JObject
                {
                    ["uhs"] = "fixture-hash",
                    ["xid"] = "42"
                }
            )
        }
    },
    "api.minecraftservices.com/authentication/login_with_xbox" => new()
    {
        ["access_token"] = "fixture-minecraft-access",
        ["expires_in"] = 3600
    },
    "api.minecraftservices.com/entitlements/mcstore" => new()
    {
        ["items"] = ownsGame
            ? new JArray(
                new JObject
                {
                    ["name"] = "game_minecraft"
                }
            )
            : new JArray()
    },
    "api.minecraftservices.com/minecraft/profile" => new()
    {
        ["id"] = LoginUuid,
        ["name"] = "PCL_Test"
    },
    _ => throw new Exception("Unexpected auth endpoint")
};
await Test(
    "microsoft_device_pending_slowdown_exchange_and_profile",
    async () =>
    {
        var waits = new List<double>();
        var polls = 0;
        var requests = new List<string>();
        using var auth = new MicrosoftAuthentication(
            new JsonHandler(
                request =>
                {
                    requests.Add(request.RequestUri!.AbsoluteUri);
                    if (request.RequestUri.AbsolutePath.EndsWith("/token") && ++polls < 3)
                        return new JObject
                        {
                            ["error"] = polls == 1
                                ? "authorization_pending"
                                : "slow_down"
                        };
                    if (request.RequestUri.AbsolutePath is "/entitlements/mcstore" or "/minecraft/profile")
                        Check(request.Headers.Authorization?.Parameter == "fixture-minecraft-access");
                    return LoginResponse(request);
                }
            ),
            (span, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                waits.Add(span.TotalSeconds);
                return Task.CompletedTask;
            }
        );
        var challenge = await auth.BeginAsync(LoginClientId);
        var login = await auth.CompleteLoginAsync(LoginClientId, challenge);
        Check(
            waits.SequenceEqual(
                new double[]
                {
                    1,
                    1,
                    6
                }
            ) && login.Session.Uuid == LoginUuid && login.Session.UserType == "msa" && !login.Session.Demo && login.RefreshToken == "fixture-refresh-rotated"
        );
        Check(
            requests.Any(
                x => x.EndsWith("/entitlements/mcstore")
            ) && requests.Any(
                x => x.EndsWith("/minecraft/profile")
            )
        );
    }
);
await Test(
    "microsoft_expiry_refresh_rotation_and_logout",
    async () =>
    {
        var paths = new AppPaths(
            Path.Combine(scratch, "microsoft-accounts")
        );
        var secrets = new MemoryAccountSecrets();
        var manager = new AccountManager(paths, secrets);
        var old = new MicrosoftLogin(
            new("PCL_Test", LoginUuid, "fixture-old-access", "msa", false),
            "fixture-refresh-old",
            DateTimeOffset.UtcNow.AddSeconds(-1),
            LoginClientId
        );
        var account = manager.SaveMicrosoft(old, true);
        manager = new(paths, secrets);
        var forms = new List<string>();
        using var handler = new LoginHttpHandler(
            async request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                    forms.Add(
                        await request.Content!.ReadAsStringAsync()
                    );
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        LoginResponse(request)
                            .ToString(),
                        Encoding.UTF8,
                        "application/json"
                    )
                };
            }
        );
        using var auth = new MicrosoftAuthentication(handler);
        var session = await manager.ResolveAsync(account.Id, auth);
        Check(session.AccessToken == "fixture-minecraft-access");
        Check(
            forms.Count == 1 && forms[0].Contains("grant_type=refresh_token") && forms[0].Contains("client_id=" + LoginClientId) && forms[0].Contains("fixture-refresh-old") && secrets.Items[account.Id] == "fixture-refresh-rotated"
        );
        await manager.ResolveAsync(account.Id, auth);
        Check(forms.Count == 1);
        var metadata = File.ReadAllText(
            Path.Combine(paths.Root, "accounts.json")
        );
        Check(
            !metadata.Contains("fixture-") && !metadata.Contains("AccessToken") && !metadata.Contains("RefreshToken")
        );
        manager.Logout(account.Id);
        Check(
            !secrets.Items.ContainsKey(account.Id) && !manager.Accounts.Single()
                .Remembered
        );
        var rejected = false;
        try
        {
            await manager.ResolveAsync(account.Id, auth);
        }
        catch (LoginRequiredException)
        {
            rejected = true;
        }

        Check(rejected);
    }
);
// 两个独立协议夹具检查缓存、选中账户和钥匙串隔离；不将此结果当作真人双账户验收。
await Test(
    "microsoft_switch_logout_and_cold_refresh_remain_account_scoped",
    async () =>
    {
        var paths = new AppPaths(
            Path.Combine(scratch, "microsoft-two-identities")
        );
        var secrets = new MemoryAccountSecrets();
        var manager = new AccountManager(paths, secrets);
        const string secondUuid = "abcdef1234567890abcdef1234567890";
        const string secondClient = "d9d2845d-33b9-4e5f-87ee-72745cad2c93";
        var first = manager.SaveMicrosoft(
            new MicrosoftLogin(
                new("First", LoginUuid, "fixture-first", "msa", false),
                "fixture-first-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                LoginClientId
            ),
            true
        );
        var second = manager.SaveMicrosoft(
            new MicrosoftLogin(
                new("Second", secondUuid, "fixture-second", "msa", false),
                "fixture-second-refresh",
                DateTimeOffset.UtcNow.AddHours(1),
                secondClient
            ),
            true
        );
        using var noNetwork = new MicrosoftAuthentication(
            new JsonHandler(
                _ => throw new Exception("Cached account should not use network")
            )
        );
        manager.Select(first.Id);
        Check(
            (await manager.ResolveAsync(first.Id, noNetwork)).AccessToken == "fixture-first"
        );
        manager.Select(second.Id);
        Check(
            (await manager.ResolveAsync(second.Id, noNetwork)).AccessToken == "fixture-second"
        );
        Check(
            manager.HasMicrosoftCredential(first.Id) && manager.HasMicrosoftCredential(second.Id)
        );
        manager.Logout(first.Id);
        Check(
            !manager.HasMicrosoftCredential(first.Id) && manager.HasMicrosoftCredential(second.Id)
        );
        Check(
            !secrets.Items.ContainsKey(first.Id) && secrets.Items[second.Id] == "fixture-second-refresh"
        );
        var rejected = false;
        try
        {
            await manager.ResolveAsync(first.Id, noNetwork);
        }
        catch (LoginRequiredException)
        {
            rejected = true;
        }

        Check(rejected);
        manager = new AccountManager(paths, secrets);
        Check(
            !manager.HasMicrosoftCredential(first.Id) && manager.HasMicrosoftCredential(second.Id)
        );
        var refreshRequests = 0;
        using var auth = new MicrosoftAuthentication(
            new LoginHttpHandler(
                async request =>
                {
                    if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                    {
                        var form = await request.Content!.ReadAsStringAsync();
                        Check(
                            form.Contains("client_id=" + secondClient) && form.Contains("fixture-second-refresh") && !form.Contains("fixture-first")
                        );
                        refreshRequests++;
                    }

                    var json = LoginResponse(request);
                    if (request.RequestUri.AbsolutePath == "/minecraft/profile")
                    {
                        json["id"] = secondUuid;
                        json["name"] = "Second";
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            json.ToString(),
                            Encoding.UTF8,
                            "application/json"
                        )
                    };
                }
            )
        );
        var restored = await manager.ResolveAsync(second.Id, auth);
        GameLaunchAuthorization.Require(restored);
        Check(restored.Uuid == secondUuid && refreshRequests == 1);
        Check(
            !secrets.Items.ContainsKey(first.Id) && secrets.Items[second.Id] == "fixture-refresh-rotated"
        );
        manager.SaveSession(
            new MicrosoftLogin(
                new("First", LoginUuid, "fixture-first", "msa", false),
                null,
                DateTimeOffset.UtcNow.AddHours(1),
                LoginClientId
            )
        );
        Check(
            manager.HasMicrosoftCredential(first.Id)
        );
        manager.Logout(first.Id);
        Check(
            !manager.HasMicrosoftCredential(first.Id)
        );
    }
);
await Test(
    "microsoft_session_only_does_not_survive_restart",
    async () =>
    {
        var paths = new AppPaths(
            Path.Combine(scratch, "session-only")
        );
        var secrets = new MemoryAccountSecrets
        {
            RejectAccess = true
        };
        var manager = new AccountManager(paths, secrets);
        var login = new MicrosoftLogin(
            new("PCL_Test", LoginUuid, "fixture-access", "msa", false),
            "fixture-refresh",
            DateTimeOffset.UtcNow.AddHours(1),
            LoginClientId
        );
        var account = manager.SaveSession(login);
        using var auth = new MicrosoftAuthentication(
            new JsonHandler(
                _ => throw new Exception("Unexpected network")
            )
        );
        Check(
            (await manager.ResolveAsync(account.Id, auth)).Name == "PCL_Test" && secrets.Calls == 0
        );
        manager = new(paths, secrets);
        var rejected = false;
        try
        {
            await manager.ResolveAsync(account.Id, auth);
        }
        catch (LoginRequiredException)
        {
            rejected = true;
        }

        Check(rejected && secrets.Calls == 0);
    }
);
await Test(
    "microsoft_rejected_refresh_no_offline_fallback",
    async () =>
    {
        using var auth = new MicrosoftAuthentication(
            new JsonHandler(
                _ => new JObject
                {
                    ["error"] = "invalid_grant",
                    ["error_description"] = "fixture-sensitive-value"
                }
            )
        );
        var rejected = false;
        try
        {
            await auth.RefreshAsync(LoginClientId, "fixture-refresh");
        }
        catch (LoginRequiredException ex)
        {
            rejected = !ex.Message.Contains("fixture-sensitive-value");
        }

        Check(rejected);
    }
);
// 刷新诊断只允许公开数字错误码；错误描述中的令牌、邮箱以及伪装成错误码的文本不能泄漏。
await Test(
    "microsoft_refresh_diagnostics_keep_codes_without_secrets",
    async () =>
    {
        foreach (var includeArray in new[]
        {
            true,
            false
        }

        )
        {
            using var auth = new MicrosoftAuthentication(
                new JsonHandler(
                    _ => new JObject
                    {
                        ["error"] = "invalid_grant",
                        ["error_description"] = "AADSTS700082: fixture-private-token private@example.invalid",
                        ["error_codes"] = includeArray
                            ? new JArray(700082, "fixture-private-token", -1)
                            : null
                    }
                )
            );
            var rejected = false;
            try
            {
                await auth.RefreshAsync(LoginClientId, "fixture-refresh");
            }
            catch (LoginRequiredException ex)
            {
                rejected = ex.Message.Contains("700082") && !ex.Message.Contains("fixture") && !ex.Message.Contains("example.invalid") && !ex.Message.Contains("-1");
            }

            Check(rejected);
        }
    }
);
await Test(
    "microsoft_entitlement_and_cancel_checks",
    async () =>
    {
        using var auth = new MicrosoftAuthentication(
            new JsonHandler(
                r => LoginResponse(r, ownsGame: false)
            ),
            (_, ct) => Task.CompletedTask
        );
        var rejected = false;
        try
        {
            await auth.CompleteLoginAsync(
                LoginClientId,
                new("code", "https://www.microsoft.com/link", "device", 1, 60)
            );
        }
        catch (InvalidOperationException ex)
        {
            rejected = ex.Message.Contains("权益");
        }

        Check(rejected);
        using var c = new CancellationTokenSource();
        c.Cancel();
        rejected = false;
        try
        {
            await auth.BeginAsync(LoginClientId, c.Token);
        }
        catch (OperationCanceledException)
        {
            rejected = true;
        }

        Check(rejected);
    }
);
await Test(
    "offline_account_resolves_after_restart_without_network_or_keychain",
    async () =>
    {
        var offlinePaths = new AppPaths(
            Path.Combine(scratch, "offline-restart")
        );
        var secrets = new MemoryAccountSecrets
        {
            RejectAccess = true
        };
        var manager = new AccountManager(offlinePaths, secrets);
        var first = manager.AddOffline("Local_First");
        var second = manager.AddOffline("Local_Second");
        manager.Select(first.Id);
        var reopened = new AccountManager(offlinePaths, secrets);
        Check(reopened.SelectedId == first.Id && reopened.Accounts.Count == 2);
        using var auth = new MicrosoftAuthentication(
            new JsonHandler(
                _ => throw new Exception("Offline identity made a network request")
            )
        );
        var session = await reopened.ResolveAsync(first.Id, auth);
        GameLaunchAuthorization.Require(session);
        Check(session.Uuid == first.Uuid && secrets.Calls == 0 && !first.Remembered);
        reopened.Remove(second.Id);
        Check(reopened.Accounts.Count == 1 && secrets.Calls == 0);
    }
);
await Test(
    "offline_identity_launch_gate_preserves_pcl_uuid_without_claiming_ownership",
    () => Sync(
        () =>
        {
            var offline = AccountManager.OfflineSession("PCL_Offline");
            GameLaunchAuthorization.Require(offline);
            Check(
                GameLaunchAuthorization.IsOfflineIdentity(offline)
            );
            Check(offline.UserType == "legacy" && !offline.Demo && offline.AccessToken == offline.Uuid);
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSession>(
                Newtonsoft.Json.JsonConvert.SerializeObject(offline)
            )!;
            GameLaunchAuthorization.Require(restored);
            Check(restored.Uuid == offline.Uuid);
            Throws<LoginRequiredException>(
                () => GameLaunchAuthorization.Require(
                    offline with
                    {
                        AccessToken = "fake-online-token"
                    }
                )
            );
            Throws<LoginRequiredException>(
                () => GameLaunchAuthorization.Require(
                    offline with
                    {
                        Name = "Changed"
                    }
                )
            );
            Throws<LoginRequiredException>(
                () => GameLaunchAuthorization.Require(
                    offline with
                    {
                        UserType = "msa"
                    }
                )
            );
        }
    )
);
await Test(
    "launch_rejects_invalid_or_unverified_microsoft_identity",
    () => Sync(
        () =>
        {
            using var service = new MinecraftService(
                new AppPaths(
                    Path.Combine(scratch, "ownership-gate")
                )
            );
            var launcher = new LaunchService(service);
            foreach (var session in new[]
            {
                AccountManager.OfflineSession("Player")with
                {
                    Uuid = new string ('0', 32)
                },
                AccountManager.OfflineSession("Player")with
                {
                    UserType = "msa"
                },
                new GameSession("PCL_Test", LoginUuid, "fixture-fake-access", "msa", false)
            }

            )
            {
                Throws<LoginRequiredException>(
                    () => GameLaunchAuthorization.Require(session)
                );
                Throws<LoginRequiredException>(
                    () => launcher.Start(
                        new(
                            "/must-never-execute",
                            [],
                            scratch
                        ),
                        session
                    )
                );
            }

            // Official trial is a supported identity; it does not grant full-game ownership.
            GameLaunchAuthorization.Require(GameSession.Trial);
            Check(GameSession.Trial.Demo);
        }
    )
);
await Test(
    "verified_ownership_rejects_mutation_serialization_and_expiry",
    async () =>
    {
        using var auth = new MicrosoftAuthentication(
            new JsonHandler(
                r => LoginResponse(r)
            ),
            (_, _) => Task.CompletedTask
        );
        var login = await auth.CompleteLoginAsync(
            LoginClientId,
            new("code", "https://www.microsoft.com/link", "device", 1, 60)
        );
        GameLaunchAuthorization.Require(login.Session);
        foreach (var altered in new[]
        {
            login.Session with
            {
                Name = "Changed"
            },
            login.Session with
            {
                Uuid = new string ('0', 32)
            },
            login.Session with
            {
                AccessToken = "changed"
            },
            login.Session with
            {
                UserType = "legacy"
            },
            login.Session with
            {
                Demo = true
            }
        }

        )
            Throws<LoginRequiredException>(
                () => GameLaunchAuthorization.Require(altered)
            );
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(login.Session);
        Check(
            !json.Contains("Ownership")
        );
        var copy = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSession>(json)!;
        Throws<LoginRequiredException>(
            () => GameLaunchAuthorization.Require(copy)
        );
        using var expired = new MicrosoftAuthentication(
            new JsonHandler(
                r =>
                {
                    var response = LoginResponse(r);
                    if (r.RequestUri!.AbsolutePath == "/authentication/login_with_xbox")
                        response["expires_in"] = 0;
                    return response;
                }
            ),
            (_, _) => Task.CompletedTask
        );
        var old = await expired.CompleteLoginAsync(
            LoginClientId,
            new("code", "https://www.microsoft.com/link", "device", 1, 60)
        );
        Throws<LoginRequiredException>(
            () => GameLaunchAuthorization.Require(old.Session)
        );
    }
);
await Test(
    "verified_fixture_session_allows_process_start",
    async () =>
    {
        if (OperatingSystem.IsWindows())
            return;
        using var auth = new MicrosoftAuthentication(
            new JsonHandler(
                r => LoginResponse(r)
            ),
            (_, _) => Task.CompletedTask
        );
        var login = await auth.CompleteLoginAsync(
            LoginClientId,
            new("code", "https://www.microsoft.com/link", "device", 1, 60)
        );
        using var service = new MinecraftService(
            new AppPaths(
                Path.Combine(scratch, "verified-process")
            )
        );
        var running = new LaunchService(service).Start(
            new(
                "/bin/sh",
                [
                    "-c",
                    "printf verified-fixture-process"
                ],
                scratch
            ),
            login.Session
        );
        Check(
            await running.Completion == 0 && File.ReadAllText(running.LogFile)
                .Contains("verified-fixture-process")
        );
        await running.DisposeAsync();
    }
);
await Test(
    "offline_script_export_preserves_existing_destination",
    async () =>
    {
        using var service = new MinecraftService(
            new AppPaths(
                Path.Combine(scratch, "no-script-export")
            )
        );
        var destination = Path.Combine(scratch, "existing.command");
        File.WriteAllText(destination, "preserve");
        var rejected = false;
        try
        {
            await new InstanceManager(service).ExportOfflineScriptAsync(null!, destination);
        }
        catch (IOException)
        {
            rejected = true;
        }

        Check(
            rejected && File.ReadAllText(destination) == "preserve"
        );
    }
);
await Test(
    "minecraft_app_rejection_is_not_reported_as_account_ownership_failure",
    async () =>
    {
        using var auth = new MicrosoftAuthentication(
            new LoginHttpHandler(
                r => Task.FromResult(
                    r.RequestUri!.AbsolutePath == "/authentication/login_with_xbox"
                        ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                        {
                            Content = new StringContent("fixture-private-server-details")
                        }
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(
                                LoginResponse(r)
                                    .ToString()
                            )
                        }
                )
            ),
            (_, _) => Task.CompletedTask
        );
        var rejected = false;
        try
        {
            await auth.CompleteLoginAsync(
                LoginClientId,
                new("code", "https://www.microsoft.com/link", "device", 1, 60)
            );
        }
        catch (InvalidOperationException ex)
        {
            rejected = ex.Message.Contains("403") && ex.Message.Contains("审核") && !ex.Message.Contains("fixture-private");
        }

        Check(rejected);
    }
);
await Test(
    "anonymous_catalog_and_resource_download_need_no_account",
    async () =>
    {
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                request =>
                {
                    Check(
                        request.Headers.Authorization is null && !request.RequestUri!.Query.Contains("client_id")
                    );
                    return new JObject
                    {
                        ["hits"] = new JArray(),
                        ["offset"] = 0,
                        ["total_hits"] = 0
                    };
                }
            )
        );
        await catalog.SearchAsync(ResourceKind.Modpack, "test", "1.21.1");
        var bytes = Encoding.UTF8.GetBytes("anonymous file");
        using var downloads = new DownloadService(
            new LoginHttpHandler(
                request =>
                {
                    Check(request.Headers.Authorization is null);
                    return Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(bytes)
                        }
                    );
                }
            )
        );
        var file = Path.Combine(scratch, "anonymous.bin");
        await downloads.DownloadAsync(
            new(
                new Uri("https://fixture/file"),
                file,
                bytes.Length,
                Convert.ToHexString(
                    SHA1.HashData(bytes)
                )
                    .ToLowerInvariant()
            )
        );
        Check(
            File.ReadAllBytes(file)
                .SequenceEqual(bytes)
        );
    }
);
await Test(
    "multi_version_metadata_java_and_arm_native_adapters",
    () => Sync(
        () =>
        {
            foreach (var(version, major)in new[]
            {
                ("1.16.5", 8),
                ("1.18.2", 17),
                ("1.20.1", 17),
                ("1.21.1", 21),
                ("26.3", 25)
            }

            )
            {
                var metadata = JsonFiles.Read(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", version + ".json")
                );
                var before = metadata.ToString();
                var adapted = LegacyMacLibraries.Adapt(
                    metadata,
                    new("osx", "aarch64", "27")
                );
                Check(
                    metadata.ToString() == before && VersionCatalog.JavaMajor(adapted) == major
                );
                var libraries = MinecraftService.BuildLibraries(
                    adapted,
                    scratch,
                    new("osx", "aarch64", "27")
                );
                Check(
                    !libraries.Any(
                        x => x.ExtractNative
                    ) && libraries.Any(
                        x => x.Path.Contains("natives-macos-arm64")
                    )
                );
                Check(
                    !libraries.Any(
                        x => x.Path.Contains("natives-windows") || x.Path.Contains("natives-linux")
                    )
                );
                if (major == 8)
                    Check(
                        libraries.Any(
                            x => x.Path.Contains("jna/5.12.1")
                        ) && !libraries.Any(
                            x => x.Path.Contains("java-objc-bridge/1.0.0")
                        )
                    );
                using var target = new MinecraftService(
                    new AppPaths(
                        Path.Combine(scratch, "multi-plan")
                    ),
                    new("osx", "aarch64", "27"),
                    version
                );
                var path = Path.Combine(scratch, version + ".json");
                JsonFiles.Write(path, adapted);
                var plan = new LaunchService(target).CreatePlan(
                    new(version, path, "/client.jar", "assets", libraries, major),
                    new("/java", "aarch64", major),
                    GameSession.Trial
                );
                Check(
                    plan.WorkingDirectory == target.VanillaGameDirectory && plan.Arguments.Contains("-XstartOnFirstThread")
                );
            }

            Check(
                JavaService.ParseInspection("/java", "java version \"1.8.0_504\"", "os.arch = aarch64")
                    .MajorVersion == 8
            );
            Check(
                VersionCatalog.NeoForgePrefix("1.20.4") == "20.4." && VersionCatalog.NeoForgePrefix("1.21.1") == "21.1."
            );
        }
    )
);
await Test(
    "multi_version_registry_keeps_parent_and_saves_separate",
    () => Sync(
        () =>
        {
            using var service = new MinecraftService(
                new AppPaths(
                    Path.Combine(scratch, "multi-registry")
                )
            );
            foreach (var version in new[]
            {
                "1.20.1",
                "26.3"
            }

            )
            {
                JsonFiles.Write(
                    Path.Combine(
                        service.Paths.VersionFolder(version),
                        "pcl-mac-install.json"
                    ),
                    new
                    {
                        version
                    }
                );
                JsonFiles.Write(
                    Path.Combine(service.Paths.Root, "instances", "fabric-" + version, "profile.json"),
                    new
                    {
                        id = "fabric-" + version,
                        gameVersion = version,
                        loader = "fabric",
                        loaderVersion = "0.19.5"
                    }
                );
            }

            var profiles = InstanceRegistry.List(service);
            Check(profiles.Count == 5);
            Check(
                profiles.Where(
                    x => x.Loader == "vanilla"
                )
                    .Select(
                        x => x.GameDirectory
                    )
                    .Distinct()
                    .Count() == 3
            );
            Check(
                profiles.Single(
                    x => x.Id == "fabric-26.3"
                )
                    .GameVersion == "26.3"
            );
        }
    )
);
await Test(
    "runtime_tar_preserves_executable_and_rejects_escape",
    () => Sync(
        () =>
        {
            if (OperatingSystem.IsWindows())
                return;
            void Archive(string path, string name, string? link = null)
            {
                using var file = File.Create(path);
                using var gzip = new GZipStream(file, CompressionLevel.Fastest);
                using var writer = new System.Formats.Tar.TarWriter(gzip);
                var entry = new System.Formats.Tar.PaxTarEntry(
                    link is null
                        ? System.Formats.Tar.TarEntryType.RegularFile
                        : System.Formats.Tar.TarEntryType.SymbolicLink,
                    name
                )
                {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserExecute
                };
                if (link is null)
                    entry.DataStream = new MemoryStream(
                        Encoding.UTF8.GetBytes("java")
                    );
                else
                    entry.LinkName = link;
                writer.WriteEntry(entry);
            }

            var archive = Path.Combine(scratch, "java.tar.gz");
            var dest = Path.Combine(scratch, "java-extract");
            Directory.CreateDirectory(dest);
            Archive(archive, "jre/bin/java");
            JavaService.ExtractRuntimeArchive(archive, dest);
            Check(
                File.ReadAllText(
                    Path.Combine(dest, "jre/bin/java")
                ) == "java" && File.GetUnixFileMode(
                    Path.Combine(dest, "jre/bin/java")
                )
                    .HasFlag(UnixFileMode.UserExecute)
            );
            Archive(archive, "../escape");
            Throws<InvalidDataException>(
                () => JavaService.ExtractRuntimeArchive(archive, dest)
            );
            Archive(archive, "jre/link", "../../../../outside");
            Throws<InvalidDataException>(
                () => JavaService.ExtractRuntimeArchive(archive, dest)
            );
        }
    )
);
await Test(
    "native_audit_architecture_qualified_netty_and_paired_jtracy",
    () => Sync(
        () =>
        {
            byte[] Header(uint cpu)
            {
                var h = new byte[32];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h, 0xfeedfacf);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                    h.AsSpan(4),
                    cpu
                );
                return h;
            }

            DownloadItem Jar(string name, string entry, uint cpu)
            {
                var path = Path.Combine(scratch, name);
                using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    using var stream = zip.CreateEntry(entry)
                        .Open();
                    stream.Write(
                        Header(cpu)
                    );
                }

                return new(
                    new Uri("https://fixture.test/file"),
                    path,
                    -1,
                    null
                );
            }

            var intel = Jar("jtracy-fixture-natives-macos.jar", "libjtracy-jni-macos.dylib", 0x01000007);
            var arm = Jar("jtracy-fixture-natives-macos-arm64.jar", "libjtracy-jni-macos-arm64.dylib", 0x0100000c);
            var netty = Jar(
                "netty-fixture.jar",
                "META-INF/native/libnetty_transport_native_kqueue_x86_64.jnilib",
                0x01000007
            );
            var version = new InstalledVersion(
                "26.3",
                "json",
                "jar",
                "assets",
                new[]
                {
                    intel,
                    arm,
                    netty
                },
                25
            );
            var audit = MacNativeAudit.Check(
                version,
                new("java", "aarch64", 25)
            );
            Check(
                audit.Count(
                    x => x.Applicable
                ) == 1
            );
            Throws<InvalidDataException>(
                () => MacNativeAudit.Check(
                    version with
                    {
                        Libraries = new[]
                        {
                            intel,
                            netty
                        }
                    },
                    new("java", "aarch64", 25)
                )
            );
            Throws<InvalidDataException>(
                () => MacNativeAudit.Check(
                    version,
                    new("java", "aarch64", 21)
                )
            );
        }
    )
);
await Test(
    "fabric_planner_reads_instance_java_instead_of_fixed_21",
    async () =>
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(
                zip.CreateEntry("fabric.mod.json")
                    .Open()
            );
            // `depends` belongs to schema 1; missing schemaVersion selects legacy schema 0.
            writer.Write(
                "{\"schemaVersion\":1,\"id\":\"javafixture\",\"version\":\"1\",\"depends\":{\"java\":\">=21\"}}"
            );
        }

        var bytes = memory.ToArray();
        var release = Release("java-v1", "javafixture")with
        {
            GameVersions = new[]
            {
                "1.20.1"
            },
            File = new(
                "java.jar",
                new Uri("https://fixture.test/java"),
                bytes.Length,
                Convert.ToHexString(
                    SHA1.HashData(bytes)
                )
                    .ToLowerInvariant()
            )
        };
        using var catalog = new ResourceCatalog(
            new JsonHandler(
                _ => new JArray()
            )
        );
        using var downloads = new DownloadService(
            new PayloadHandler(
                new Dictionary<string, byte[]>()
                {
                    ["java"] = bytes
                }
            )
        );
        var instance = Path.Combine(scratch, "java17-instance");
        JsonFiles.Write(
            Path.Combine(instance, "version.json"),
            new
            {
                javaVersion = new
                {
                    majorVersion = 17
                }
            }
        );
        var planner = new FabricModPlanner(
            catalog,
            downloads,
            Path.Combine(scratch, "java-planner-cache"),
            "0.19.5"
        );
        var rejected = false;
        try
        {
            await planner.ResolveAsync(release, "1.20.1", gameDirectory: instance);
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains("java");
        }

        Check(rejected);
        JsonFiles.Write(
            Path.Combine(instance, "version.json"),
            new
            {
                javaVersion = new
                {
                    majorVersion = 25
                }
            }
        );
        Check(
            (await planner.ResolveAsync(release, "1.20.1", gameDirectory: instance)).Releases.Count == 1
        );
    }
);
await Test(
    "neoforge_1204_uses_official_legacy_mods_toml",
    () => Sync(
        () =>
        {
            using var memory = new MemoryStream();
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry("META-INF/mods.toml")
                        .Open()
                );
                writer.Write("modLoader=\"javafml\"");
            }

            memory.Position = 0;
            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
            ResourceInstaller.ValidateModArchive(archive, "neoforge", "1.20.4");
            Throws<InvalidDataException>(
                () => ResourceInstaller.ValidateModArchive(archive, "neoforge", "1.21.1")
            );
        }
    )
);
await InstanceManagementTests.RunAll(scratch, Test);
await PackResourceTests.RunAsync(scratch, Test);
foreach (var item in LegacyCompatibilityTests.Cases(scratch))
    await Test(
        item.Name,
        () => Sync(item.Run)
    );
await WorkbenchTests.RunAsync(scratch, Test);
await SegmentedDownloadTests.RunAsync(scratch, Test);
await AppearanceTests.RunAsync(scratch, Test);
await LocalSettingsTests.RunAsync(scratch, Test);
await FinalIntegrationTests.RunAsync(scratch, Test);
await NoAccountFeatureTests.RunAsync(scratch, Test);
await CompletionFeatureTests.RunAsync(scratch, Test);
await SearchBackupTests.RunAsync(scratch, Test);
await DependencyPackTests.RunAsync(scratch, Test);
await QuiltOverrideTests.RunAsync(scratch, Test);
await McmodTests.RunAsync(scratch, Test);
await AccountAppearanceTests.RunAll(scratch, Test);
await AccountNetworkDiagnosticsTests.RunAll(scratch, Test);
foreach (var item in ComponentMigrationTests.Cases(scratch))
    await Test(item.Name, item.Run);
foreach (var item in ArtifactDownloadTests.Cases(scratch))
    await Test(item.Name, item.Run);
foreach (var item in OptiFabricTests.Cases(scratch))
    await Test(item.Name, item.Run);
await ExternalAuthenticationTests.RunAll(scratch, Test);
JsonFiles.Write(
    Path.Combine(root, "test-results.json"),
    new
    {
        status = failures == 0
            ? "passed"
            : "failed",
        timestamp = DateTimeOffset.UtcNow,
        platform = Pcl.Core.Adapters.Platform.HostPlatform.Detect(),
        tests = rows
    }
);
Console.WriteLine($"{rows.Count - failures}/{rows.Count} passed");
return failures == 0
    ? 0
    : 1;
sealed class FixtureServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public byte[] Payload { get; } = Enumerable.Range(0, 16384)
        .Select(
            x => (byte)(x % 251)
        )
        .ToArray();
    public string Url { get; }

    public bool RangeObserved;
    public int RequestCount;
    public FixtureServer()
    {
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        _ = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        var client = await listener.AcceptTcpClientAsync(stop.Token);
                        _ = Serve(client);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }
                }
            }
        );
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
            try
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var first = await reader.ReadLineAsync() ?? "";
                var route = first.Split(' ')[1];
                int offset = 0;
                for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        offset = int.Parse(
                            line[13..].Split('-')[0]
                        );
                        RangeObserved = true;
                    }

                Interlocked.Increment(ref RequestCount);
                var partial = route == "/range" && offset > 0;
                if (!partial)
                    offset = 0;
                var data = route == "/bad"
                    ? new byte[Payload.Length]
                    : Payload;
                var header = $"HTTP/1.1 {(partial
                        ? "206 Partial Content"
                        : "200 OK")}\r\nContent-Length: {data.Length - offset}\r\nConnection: close\r\n" + (partial
                    ? $"Content-Range: bytes {offset}-{data.Length - 1}/{data.Length}\r\n"
                    : "") + "\r\n";
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(header)
                );
                if (route == "/slow")
                {
                    await stream.WriteAsync(
                        data.AsMemory(0, 100)
                    );
                    await Task.Delay(1000);
                    await stream.WriteAsync(
                        data.AsMemory(100)
                    );
                }
                else
                    await stream.WriteAsync(
                        data.AsMemory(offset)
                    );
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}

sealed class JsonHandler(Func<HttpRequestMessage, JToken> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                response(request)
                    .ToString(),
                Encoding.UTF8,
                "application/json"
            )
        }
    );
}

sealed class ByteHandler : HttpMessageHandler
{
    public static byte[] Bytes = Array.Empty<byte>();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Bytes)
        }
    );
}

sealed class PayloadHandler(IReadOnlyDictionary<string, byte[]> payloads) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(
                payloads[request.RequestUri!.AbsolutePath.TrimStart('/')]
            )
        }
    );
}

sealed class MemoryAccountSecrets : IAccountSecrets
{
    public readonly Dictionary<string, string> Items = new();
    public bool RejectAccess;
    public int Calls;
    private void Access()
    {
        Calls++;
        if (RejectAccess)
            throw new Exception("Unexpected keychain access");
    }

    public string? Get(string id)
    {
        Access();
        return Items.GetValueOrDefault(id);
    }

    public void Set(string id, string secret)
    {
        Access();
        Items[id] = secret;
    }

    public void Delete(string id)
    {
        Access();
        Items.Remove(id);
    }
}

sealed class LoginHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return response(request);
    }
}
