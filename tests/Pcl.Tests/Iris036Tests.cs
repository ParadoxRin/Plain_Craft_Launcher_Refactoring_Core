using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pcl.Core;

/// <summary>真实 ZIP 元数据、文件/用途清单与进程中断验证；下载端使用内存 HTTP 替身，不冒充网络验收。</summary>
internal static class Iris036Tests
{
    private sealed class FilesHandler(Dictionary<string, byte[]> data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    data[Path.GetFileName(request.RequestUri!.AbsolutePath)]
                )
            }
        );
    }

    private static byte[] Jar(
        string id,
        string[] dependencies,
        string[]? provides = null,
        byte[]? nested = null
    )
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(
                zip.CreateEntry("fabric.mod.json")
                    .Open()
            ))
            {
                writer.Write(
                    JsonSerializer.Serialize(
                        new
                        {
                            schemaVersion = 1,
                            id,
                            version = "1.0.0",
                            environment = "client",
                            depends = dependencies.ToDictionary(
                                x => x,
                                _ => "*"
                            ),
                            provides = provides ?? [],
                            jars = nested is null
                                ? Array.Empty<object>()
                                : new object[]
                                {
                                    new
                                    {
                                        file = "nested.jar"
                                    }
                                }
                        }
                    )
                );
            }

            if (nested is not null)
            {
                using var output = zip.CreateEntry("nested.jar")
                    .Open();
                output.Write(nested);
            }
        }

        return memory.ToArray();
    }

    internal static int Child(string root, string point)
    {
        var environment = new IrisEnvironmentService(root, "fabric", "1.21.1");
        var plan = environment.Preview();
        environment.Uninstall(
            plan.Fingerprint,
            "process-retry",
            default,
            stage =>
            {
                if (stage == point)
                {
                    Environment.Exit(93);
                }
            }
        );
        return 0;
    }

    internal static async Task<int> Run(string output)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        var failed = 0;
        void Check(bool ok, string message)
        {
            if (!ok)
            {
                throw new Exception(message);
            }
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
                failed++;
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

            JsonFiles.Write(
                Path.Combine(output, "results.json"),
                new
                {
                    failed,
                    rows
                }
            );
        }

        void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)when (ex is IOException or InvalidDataException or OperationCanceledException)
            {
                return;
            }

            throw new Exception("Expected safe refusal");
        }

        string Fresh()
        {
            var path = Path.Combine(
                output,
                Guid.NewGuid()
                    .ToString("N")
            );
            Directory.CreateDirectory(
                Path.Combine(path, "mods")
            );
            File.WriteAllText(
                Path.Combine(path, "profile.json"),
                "{\"loaderVersion\":\"0.19.5\"}"
            );
            return path;
        }

        var data = new Dictionary<string, byte[]>
        {
            ["iris.jar"] = Jar(
                "iris",
                [
                    "sodium"
                ]
            ),
            ["sodium.jar"] = Jar(
                "sodium",
                [
                    "base"
                ],
                [
                    "sodium_alias"
                ]
            ),
            ["base.jar"] = Jar(
                "base",
                []
            ),
            ["consumer.jar"] = Jar(
                "consumer",
                [
                    "sodium"
                ]
            )
        };
        ResourceRelease Release(string file, string project) => new(
            "v-" + project,
            project,
            file,
            "1.0.0",
            "release",
            DateTimeOffset.UtcNow,
            [
                "1.21.1"
            ],
            [
                "fabric"
            ],
            new(
                file,
                new Uri("https://fixture.invalid/" + file),
                data[file].Length,
                Convert.ToHexString(
                    SHA1.HashData(data[file])
                )
                    .ToLowerInvariant()
            ),
            []
        );
        var iris = Release("iris.jar", IrisEnvironmentService.IrisProject);
        var sodium = Release("sodium.jar", "sodium-project");
        var basis = Release("base.jar", "base-project");
        var plan = new ResourceInstallPlan(
            ResourceKind.Mod,
            IrisEnvironmentService.IrisProject,
            [
                iris,
                sodium,
                basis
            ]
        );
        using var downloads = new DownloadService(
            new FilesHandler(data)
        );
        async Task<IrisEnvironmentService> Install(string root)
        {
            var env = new IrisEnvironmentService(root, "fabric", "1.21.1");
            await env.InstallAsync(plan, downloads, null, default);
            return env;
        }

        await Test(
            "exclusive-environment-removes-only-owned-components",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                Directory.CreateDirectory(
                    Path.Combine(root, "shaderpacks")
                );
                File.WriteAllText(
                    Path.Combine(root, "shaderpacks/user.zip"),
                    "user"
                );
                Directory.CreateDirectory(
                    Path.Combine(root, "config")
                );
                File.WriteAllText(
                    Path.Combine(root, "config/iris.properties"),
                    "user config"
                );
                var preview = env.Preview();
                Check(preview.CanApply && preview.Remove.Length == 3, "Exclusive plan");
                var result = env.Uninstall(preview.Fingerprint, "remove-exclusive");
                Check(
                    Directory.GetFiles(
                        Path.Combine(root, "mods")
                    )
                        .Length == 0,
                    "Environment remains"
                );
                Check(
                    File.ReadAllText(
                        Path.Combine(root, "shaderpacks/user.zip")
                    ) == "user" && File.ReadAllText(
                        Path.Combine(root, "config/iris.properties")
                    ) == "user config",
                    "User files changed"
                );
                Check(
                    env.Uninstall(preview.Fingerprint, "remove-exclusive")
                        .BackupDirectory == result.BackupDirectory,
                    "Retry did not reuse result"
                );
            }
        );
        await Test(
            "actual-jar-dependency-transitive-closure-preserves-shared",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                File.WriteAllBytes(
                    Path.Combine(root, "mods/user-consumer.jar"),
                    data["consumer.jar"]
                );
                var preview = env.Preview();
                Check(
                    preview.CanApply && preview.Remove.SequenceEqual(
                        new[]
                        {
                            "mods/iris.jar"
                        }
                    ),
                    "Shared closure incomplete"
                );
                env.Uninstall(preview.Fingerprint, "shared");
                new LocalResourceService(root, "fabric", "1.21.1").ValidateEnabledResources();
                Check(
                    File.Exists(
                        Path.Combine(root, "mods/sodium.jar")
                    ) && File.Exists(
                        Path.Combine(root, "mods/base.jar")
                    ),
                    "Shared dependencies removed"
                );
            }
        );
        await Test(
            "explicit-install-purpose-preserves-dependency-without-dependent-mod",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                await new ResourceInstaller(root, downloads, "fabric", "1.21.1").InstallAsync(
                    new(
                        ResourceKind.Mod,
                        sodium.ProjectId,
                        [
                            sodium,
                            basis
                        ]
                    )
                );
                var preview = env.Preview();
                Check(preview.CanApply && preview.Remove.Length == 1, "Explicit use lost");
            }
        );
        await Test(
            "preexisting-components-are-never-adopted",
            async () =>
            {
                var root = Fresh();
                await new ResourceInstaller(root, downloads, "fabric", "1.21.1").InstallAsync(
                    new(
                        ResourceKind.Mod,
                        sodium.ProjectId,
                        [
                            sodium,
                            basis
                        ]
                    )
                );
                var env = await Install(root);
                var preview = env.Preview();
                Check(
                    preview.CanApply && preview.Remove.SequenceEqual(
                        new[]
                        {
                            "mods/iris.jar"
                        }
                    ),
                    "Preexisting dependency adopted"
                );
            }
        );
        await Test(
            "manual-iris-and-legacy-environment-have-no-implicit-ownership",
            async () =>
            {
                var root = Fresh();
                File.WriteAllBytes(
                    Path.Combine(root, "mods/iris.jar"),
                    data["iris.jar"]
                );
                var env = await Install(root);
                Check(
                    !env.Preview()
                        .CanApply && File.Exists(
                        Path.Combine(root, "mods/iris.jar")
                    ),
                    "Manual Iris adopted"
                );
            }
        );
        await Test(
            "mod-requiring-iris-refuses-entire-uninstall",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                File.WriteAllBytes(
                    Path.Combine(root, "mods/user-addon.jar"),
                    Jar(
                        "addon",
                        [
                            "iris"
                        ]
                    )
                );
                Check(
                    !env.Preview()
                        .CanApply,
                    "Iris dependent ignored"
                );
                Reject(
                    () => env.Uninstall(
                        env.Preview()
                            .Fingerprint,
                        "blocked"
                    )
                );
                Check(
                    Directory.GetFiles(
                        Path.Combine(root, "mods")
                    )
                        .Length == 4,
                    "Partial uninstall occurred"
                );
            }
        );
        await Test(
            "nested-alias-and-disabled-dependents-protect-shared-component",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                File.WriteAllBytes(
                    Path.Combine(root, "mods/outer.jar.disabled"),
                    Jar(
                        "outer",
                        [],
                        nested: Jar(
                            "nested",
                            [
                                "sodium_alias"
                            ]
                        )
                    )
                );
                Check(
                    env.Preview()
                        .Remove.SequenceEqual(
                        new[]
                        {
                            "mods/iris.jar"
                        }
                    ),
                    "Nested/disabled/provides not protected"
                );
            }
        );
        await Test(
            "edited-owned-file-refuses-before-any-removal",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                File.AppendAllText(
                    Path.Combine(root, "mods/sodium.jar"),
                    "user edit"
                );
                var preview = env.Preview();
                Check(
                    !preview.CanApply && preview.Conflicts.Any(
                        x => x.Contains("sodium")
                    ),
                    "Modified component not reported"
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "mods/iris.jar")
                    ),
                    "Iris removed"
                );
            }
        );
        await Test(
            "new-user-mod-after-preview-refuses-stale-plan",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                var preview = env.Preview();
                File.WriteAllBytes(
                    Path.Combine(root, "mods/new-user.jar"),
                    Jar(
                        "new_user",
                        []
                    )
                );
                Reject(
                    () => env.Uninstall(preview.Fingerprint, "stale")
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "mods/iris.jar")
                    ),
                    "Stale plan changed files"
                );
            }
        );
        await Test(
            "precommit-cancel-leaves-files-and-no-journal",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                var preview = env.Preview();
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                Reject(
                    () => env.Uninstall(preview.Fingerprint, "cancel", cancel.Token)
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "mods/iris.jar")
                    ) && !File.Exists(
                        Path.Combine(root, ".pcl-iris-uninstall.json")
                    ),
                    "Cancelled commit started"
                );
            }
        );
        async Task Crash(string root, string point)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false
            };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            {
                start.ArgumentList.Add(typeof(Iris036Tests).Assembly.Location);
            }

            foreach (var arg in new[]
            {
                "--iris036-child",
                root,
                point
            }

            )
            {
                start.ArgumentList.Add(arg);
            }

            using var child = Process.Start(start)!;
            await child.WaitForExitAsync();
            Check(child.ExitCode == 93, "Crash point not reached");
        }

        foreach (var point in new[]
        {
            "prepared",
            "component-removed",
            "manifest-written",
            "environment-written"
        }

        )
        {
            await Test(
                "process-crash-recovery-" + point,
                async () =>
                {
                    var root = Fresh();
                    var env = await Install(root);
                    await Crash(root, point);
                    IrisEnvironmentService.RecoverBeforeLaunch(root);
                    Check(
                        Directory.GetFiles(
                            Path.Combine(root, "mods")
                        )
                            .Length == 0,
                        "Recovery incomplete"
                    );
                    Check(
                        env.Uninstall("obsolete", "process-retry")
                            .Removed.Length == 3,
                        "Recovered result not idempotent"
                    );
                }
            );
        }

        await Test(
            "new-dependent-during-interruption-blocks-recovery-and-launch",
            async () =>
            {
                var root = Fresh();
                await Install(root);
                await Crash(root, "prepared");
                File.WriteAllBytes(
                    Path.Combine(root, "mods/new-dependent.jar"),
                    Jar(
                        "new_dependent",
                        [
                            "iris"
                        ]
                    )
                );
                Reject(
                    () => IrisEnvironmentService.RecoverBeforeLaunch(root)
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "mods/iris.jar")
                    ),
                    "Recovery removed needed Iris"
                );
            }
        );
        await Test(
            "backup-corruption-blocks-recovery",
            async () =>
            {
                var root = Fresh();
                await Install(root);
                await Crash(root, "prepared");
                var backup = Directory.GetFiles(
                    Path.Combine(root, ".pcl-backups"),
                    "0.jar",
                    SearchOption.AllDirectories
                )
                    .Single();
                File.AppendAllText(backup, "corrupt");
                Reject(
                    () => IrisEnvironmentService.RecoverBeforeLaunch(root)
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "mods/iris.jar")
                    ),
                    "Corrupt backup ignored"
                );
            }
        );
        await Test(
            "dependency-override-refuses-initial-and-interrupted-removal",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                var preview = env.Preview();
                Directory.CreateDirectory(
                    Path.Combine(root, "config")
                );
                var overrides = Path.Combine(root, "config/fabric_loader_dependencies.json");
                File.WriteAllText(overrides, "{}");
                Check(
                    !env.Preview()
                        .CanApply,
                    "Override ignored"
                );
                Reject(
                    () => env.Uninstall(preview.Fingerprint, "override")
                );
                File.Delete(overrides);
                await Crash(root, "prepared");
                File.WriteAllText(overrides, "{}");
                Reject(
                    () => IrisEnvironmentService.RecoverBeforeLaunch(root)
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "mods/iris.jar")
                    ),
                    "Recovery ignored override"
                );
            }
        );
        await Test(
            "mod-directory-symlink-refused-without-recursive-loop",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                Directory.CreateSymbolicLink(
                    Path.Combine(root, "mods/loop"),
                    Path.Combine(root, "mods")
                );
                Reject(
                    () => env.Preview()
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "mods/iris.jar")
                    ),
                    "Symlink changed components"
                );
            }
        );
        await Test(
            "uninstalled-vanilla-directory-needs-no-resource-lock",
            () =>
            {
                var root = Path.Combine(
                    output,
                    Guid.NewGuid()
                        .ToString("N")
                );
                IrisEnvironmentService.RecoverBeforeLaunch(root);
                Check(
                    !Directory.Exists(root),
                    "Guard created vanilla directory"
                );
                return Task.CompletedTask;
            }
        );
        await Test(
            "download-failure-before-mutations-does-not-block-existing-game",
            async () =>
            {
                var root = Fresh();
                var env = new IrisEnvironmentService(root, "fabric", "1.21.1");
                using var failure = new DownloadService(
                    new FilesHandler(
                        new()
                    )
                );
                bool failedInstall = false;
                try
                {
                    await env.InstallAsync(plan, failure, null, default);
                }
                catch
                {
                    failedInstall = true;
                }

                Check(failedInstall, "Download did not fail");
                IrisEnvironmentService.RecoverBeforeLaunch(root);
                Check(
                    Directory.GetFiles(
                        Path.Combine(root, "mods")
                    )
                        .Length == 0 && !env.Preview()
                        .CanApply,
                    "Failed download changed environment"
                );
            }
        );
        await Test(
            "reinstall-does-not-turn-old-environment-purpose-into-shared-use",
            async () =>
            {
                var root = Fresh();
                var env = await Install(root);
                env.Uninstall(
                    env.Preview()
                        .Fingerprint,
                    "first-removal"
                );
                env = await Install(root);
                Check(
                    env.Preview()
                        .CanApply && env.Preview()
                        .Remove.Length == 3,
                    "Old environment claim blocks uninstall"
                );
                env.Uninstall(
                    env.Preview()
                        .Fingerprint,
                    "second-removal"
                );
                Check(
                    Directory.GetFiles(
                        Path.Combine(root, "mods")
                    )
                        .Length == 0,
                    "Second uninstall incomplete"
                );
            }
        );
        await Test(
            "independent-api-provider-allows-iris-nested-provider-removal",
            async () =>
            {
                var root = Fresh();
                data["iris-nested.jar"] = Jar(
                    "iris",
                    [
                        "sodium"
                    ],
                    nested: Jar(
                        "base",
                        []
                    )
                );
                var release = Release("iris-nested.jar", IrisEnvironmentService.IrisProject);
                var env = new IrisEnvironmentService(root, "fabric", "1.21.1");
                await env.InstallAsync(
                    new(
                        ResourceKind.Mod,
                        IrisEnvironmentService.IrisProject,
                        [
                            release,
                            sodium,
                            basis
                        ]
                    ),
                    downloads,
                    null,
                    default
                );
                File.WriteAllBytes(
                    Path.Combine(root, "mods/consumer.jar"),
                    data["consumer.jar"]
                );
                Check(
                    env.Preview()
                        .CanApply && env.Preview()
                        .Remove.SequenceEqual(
                        new[]
                        {
                            "mods/iris-nested.jar"
                        }
                    ),
                    "Alternative provider not selected"
                );
                env.Uninstall(
                    env.Preview()
                        .Fingerprint,
                    "nested-alternative"
                );
                new LocalResourceService(root, "fabric", "1.21.1").ValidateEnabledResources();
            }
        );
        return failed == 0
            ? 0
            : 1;
    }
}
