using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Pcl.Core;

/// <summary>真实文件和强制退出子进程覆盖提交边界；不把故障注入当成游戏画面验收。</summary>
internal static class Transaction035Tests
{
    internal static int Child(string action, string point, string root)
    {
        void Fault(string current)
        {
            if (current == point)
            {
                Environment.Exit(91);
            }
        }

        if (action == "apply")
        {
            new ActivationTransaction(root, Fault).Apply("config/iris.properties", "shaderPack=test.zip\nenableShaders=true\n");
        }
        else if (action == "undo")
        {
            new ActivationTransaction(root, Fault).Undo(operationId: "retry-undo");
        }
        else
        {
            var managed = JsonConvert.DeserializeObject<ManagedResource[]>(
                File.ReadAllText(
                    Path.Combine(root, ".pcl-resources.json")
                )
            )!;
            new ShaderRemovalTransaction(root, Fault).RemoveManaged(managed, operationId: "retry-remove");
        }

        return 0;
    }

    internal static async Task<int> Run(string output)
    {
        Directory.CreateDirectory(output);
        var rows = new List<object>();
        int failed = 0;
        string Fresh(string name)
        {
            var root = Path.Combine(
                output,
                name + "-" + Guid.NewGuid()
                    .ToString("N")
            );
            Directory.CreateDirectory(
                Path.Combine(root, "config")
            );
            File.WriteAllText(
                Path.Combine(root, "config/iris.properties"),
                "enableShaders=false\nshadowDistance=16\n"
            );
            Directory.CreateDirectory(
                Path.Combine(root, "user-folder")
            );
            File.WriteAllText(
                Path.Combine(root, "user-folder/keep.txt"),
                "user data"
            );
            return root;
        }

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception(message);
            }
        }

        void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            {
                return;
            }

            throw new Exception("Expected refusal");
        }

        async Task Test(string name, Func<Task> body)
        {
            try
            {
                await body();
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

        async Task Crash(string action, string point, string root)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false
            };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            {
                start.ArgumentList.Add(typeof(Transaction035Tests).Assembly.Location);
            }

            foreach (var arg in new[]
            {
                "--txn035-child",
                action,
                point,
                root
            }

            )
            {
                start.ArgumentList.Add(arg);
            }

            using var child = Process.Start(start)!;
            await child.WaitForExitAsync();
            Check(child.ExitCode == 91, "Child did not terminate at checkpoint");
        }

        ManagedResource[] Packages(string root)
        {
            Directory.CreateDirectory(
                Path.Combine(root, "shaderpacks")
            );
            var rows = new List<ManagedResource>();
            foreach (var name in new[]
            {
                "first.zip",
                "second.zip"
            }

            )
            {
                var data = Encoding.UTF8.GetBytes("owned-package-" + name);
                File.WriteAllBytes(
                    Path.Combine(root, "shaderpacks", name),
                    data
                );
                rows.Add(
                    new(
                        name,
                        "v1",
                        name,
                        "1",
                        ResourceKind.Shader,
                        name,
                        Convert.ToHexString(
                            SHA1.HashData(data)
                        )
                            .ToLowerInvariant(),
                        data.Length,
                        true,
                        DateTimeOffset.UtcNow,
                        []
                    )
                );
            }

            File.WriteAllText(
                Path.Combine(root, ".pcl-resources.json"),
                JsonConvert.SerializeObject(rows)
            );
            return rows.ToArray();
        }

        foreach (var point in new[]
        {
            "prepared",
            "target-written",
            "receipt-written"
        }

        )
        {
            await Test(
                "apply-process-exit-" + point,
                async () =>
                {
                    var root = Fresh("apply");
                    await Crash("apply", point, root);
                    Check(
                        File.Exists(
                            Path.Combine(root, ".pcl-activation-transaction.json")
                        ),
                        "Intent absent"
                    );
                    ActivationTransaction.RecoverBeforeLaunch(root);
                    new ActivationTransaction(root).Undo();
                    Check(
                        File.ReadAllText(
                            Path.Combine(root, "config/iris.properties")
                        ) == "enableShaders=false\nshadowDistance=16\n",
                        "Baseline mismatch"
                    );
                    Check(
                        File.ReadAllText(
                            Path.Combine(root, "user-folder/keep.txt")
                        ) == "user data",
                        "User data lost"
                    );
                }
            );
            await Test(
                "undo-process-exit-" + point,
                async () =>
                {
                    var root = Fresh("undo");
                    new ActivationTransaction(root).Apply("config/iris.properties", "new value\n");
                    await Crash("undo", point, root);
                    ActivationTransaction.RecoverBeforeLaunch(root);
                    new ActivationTransaction(root).Undo(operationId: "retry-undo");
                    Check(
                        File.ReadAllText(
                            Path.Combine(root, "config/iris.properties")
                        ) == "enableShaders=false\nshadowDistance=16\n",
                        "Undo not recovered"
                    );
                }
            );
        }

        await Test(
            "cancel-before-commit-and-no-original-config",
            () =>
            {
                var root = Fresh("cancel");
                var tx = new ActivationTransaction(root);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Reject(
                    () => tx.Apply("config/iris.properties", "no", cancelled.Token)
                );
                Check(
                    !File.Exists(
                        Path.Combine(root, ".pcl-activation-transaction.json")
                    ),
                    "Cancelled intent created"
                );
                tx.Apply("options.txt", "resourcePacks:[]\n");
                Reject(
                    () => tx.Undo(cancelled.Token)
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "options.txt")
                    ),
                    "Cancelled undo changed target"
                );
                tx.Undo();
                Check(
                    !File.Exists(
                        Path.Combine(root, "options.txt")
                    ),
                    "Absent baseline not restored"
                );
                return Task.CompletedTask;
            }
        );
        await Test(
            "external-config-and-backup-corruption-refused",
            () =>
            {
                var root = Fresh("conflict");
                var tx = new ActivationTransaction(root);
                tx.Apply("config/iris.properties", "new value");
                File.AppendAllText(
                    Path.Combine(root, "config/iris.properties"),
                    "user edit"
                );
                Reject(
                    () => tx.Undo()
                );
                Check(
                    File.ReadAllText(
                        Path.Combine(root, "config/iris.properties")
                    )
                        .Contains("user edit"),
                    "User edit lost"
                );
                File.WriteAllText(
                    Path.Combine(root, "config/iris.properties"),
                    "new value"
                );
                File.WriteAllText(
                    Directory.GetFiles(
                        Path.Combine(root, ".pcl-backups"),
                        "previous.config",
                        SearchOption.AllDirectories
                    )
                        .Single(),
                    "corrupt"
                );
                Reject(
                    () => tx.Undo()
                );
                Check(
                    File.ReadAllText(
                        Path.Combine(root, "config/iris.properties")
                    ) == "new value",
                    "Corrupt backup applied"
                );
                return Task.CompletedTask;
            }
        );
        await Test(
            "unresolved-config-recovery-blocks-launch",
            async () =>
            {
                var root = Fresh("blocked");
                await Crash("apply", "prepared", root);
                File.WriteAllText(
                    Path.Combine(root, "config/iris.properties"),
                    "foreign config"
                );
                Reject(
                    () => ActivationTransaction.RecoverBeforeLaunch(root)
                );
                Check(
                    File.ReadAllText(
                        Path.Combine(root, "config/iris.properties")
                    ) == "foreign config",
                    "Conflict overwritten"
                );
            }
        );
        foreach (var point in new[]
        {
            "prepared",
            "package-removed",
            "manifest-written"
        }

        )
        {
            await Test(
                "shader-batch-remove-process-exit-" + point,
                async () =>
                {
                    var root = Fresh("remove");
                    Packages(root);
                    File.WriteAllText(
                        Path.Combine(root, "shaderpacks/user.zip"),
                        "unmanaged"
                    );
                    await Crash("remove", point, root);
                    ShaderRemovalTransaction.RecoverBeforeLaunch(root);
                    var tx = new ShaderRemovalTransaction(root);
                    Check(
                        tx.Completed("retry-remove"),
                        "Removal completion missing"
                    );
                    Check(
                        Directory.GetFiles(
                            Path.Combine(root, "shaderpacks")
                        )
                            .Length == 1,
                        "Owned package remains"
                    );
                    Check(
                        File.ReadAllText(
                            Path.Combine(root, "shaderpacks/user.zip")
                        ) == "unmanaged",
                        "Unmanaged package removed"
                    );
                    Check(
                        JsonConvert.DeserializeObject<ManagedResource[]>(
                            File.ReadAllText(
                                Path.Combine(root, ".pcl-resources.json")
                            )
                        )!.Length == 0,
                        "Manifest not committed"
                    );
                }
            );
        }

        await Test(
            "batch-conflict-refuses-all-files-before-mutation",
            () =>
            {
                var root = Fresh("batch-conflict");
                var packages = Packages(root);
                var manifest = File.ReadAllBytes(
                    Path.Combine(root, ".pcl-resources.json")
                );
                File.AppendAllText(
                    Path.Combine(root, "shaderpacks/second.zip"),
                    "user edit"
                );
                Reject(
                    () => new ShaderRemovalTransaction(root).RemoveManaged(packages)
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "shaderpacks/first.zip")
                    ),
                    "First package removed before conflict"
                );
                Check(
                    manifest.SequenceEqual(
                        File.ReadAllBytes(
                            Path.Combine(root, ".pcl-resources.json")
                        )
                    ),
                    "Manifest changed"
                );
                Check(
                    !Directory.Exists(
                        Path.Combine(root, ".pcl-backups")
                    ),
                    "Conflict created backups"
                );
                return Task.CompletedTask;
            }
        );
        await Test(
            "batch-cancel-and-unmanaged-refusal",
            () =>
            {
                var root = Fresh("batch-cancel");
                var packages = Packages(root);
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                Reject(
                    () => new ShaderRemovalTransaction(root).RemoveManaged(packages, cancelled.Token)
                );
                Reject(
                    () => new ShaderRemovalTransaction(root).RemoveManaged(
                        [
                            packages[0] with
                            {
                                ProjectId = "not-owned"
                            }
                        ]
                    )
                );
                Check(
                    Directory.GetFiles(
                        Path.Combine(root, "shaderpacks")
                    )
                        .Length == 2,
                    "Refused operation changed packages"
                );
                return Task.CompletedTask;
            }
        );
        await Test(
            "removal-recovery-validates-all-conflicts-first",
            async () =>
            {
                var root = Fresh("batch-recover-conflict");
                Packages(root);
                await Crash("remove", "prepared", root);
                File.AppendAllText(
                    Path.Combine(root, "shaderpacks/second.zip"),
                    "external edit"
                );
                Reject(
                    () => ShaderRemovalTransaction.RecoverBeforeLaunch(root)
                );
                Check(
                    File.Exists(
                        Path.Combine(root, "shaderpacks/first.zip")
                    ),
                    "Recovery removed first package before conflict"
                );
            }
        );
        await Test(
            "backend-absent-no-guessed-warning",
            () =>
            {
                var root = Fresh("backend");
                Check(
                    ShaderBackendInspection.Describe(root, "neoforge", "1.21.1")
                        .Length == 0,
                    "Guessed Oculus warning"
                );
                return Task.CompletedTask;
            }
        );
        // 复用受影响资源包与组件迁移用例，避免配置事务改动破坏既有非光影路径。
        await PackResourceTests.RunAsync(output, Test);
        foreach (var test in ComponentMigrationTests.Cases(output))
        {
            await Test(test.Name, test.Run);
        }

        return failed == 0
            ? 0
            : 1;
    }
}
