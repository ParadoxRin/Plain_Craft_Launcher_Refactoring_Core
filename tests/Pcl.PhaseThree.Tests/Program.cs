using System.Diagnostics;
using System.Text.Json;
using Pcl.Core;
using Pcl.Core.Adapters.Operations;
using Pcl.Core.Application.Operations;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Operations;

var target = new InstallationTarget("osx", "aarch64", "test");
var root = Path.GetFullPath(
    args.ElementAtOrDefault(1) ?? "artifacts/architecture-phase-three/tests"
);
Directory.CreateDirectory(root);
VersionInstallRequest Request(string version = "1.21.1") => new(version, "vanilla", target);
if (args.FirstOrDefault() == "--crash-child")
{
    using var store = new FileOperationStore(root);
    var backend = new FixtureInstaller
    {
        Hold = true
    };
    var service = new InstallOperationService(store, backend, target);
    var accepted = await service.AcceptAsync(
        new(
            "crash-key",
            Request()
        )
    );
    await backend.Entered.Task;
    File.WriteAllText(
        Path.Combine(root, "ready.json"),
        JsonSerializer.Serialize(accepted)
    );
    await Task.Delay(Timeout.Infinite);
    return;
}

if (args.FirstOrDefault() == "--live")
{
    await Live(root);
    return;
}

var results = new List<object>();
int failures = 0;
void Require(bool condition, string error)
{
    if (!condition)
    {
        throw new InvalidOperationException(error);
    }
}

async Task Test(string name, Func<Task> test)
{
    try
    {
        await test()
            .WaitAsync(
                TimeSpan.FromSeconds(30)
            );
        results.Add(
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
        results.Add(
            new
            {
                name,
                status = "failed",
                error = ex.GetType()
                    .Name + ": " + ex.Message
            }
        );
        Console.WriteLine(
            "FAIL " + name + " " + ex.GetType()
                .Name
        );
    }

    File.WriteAllText(
        Path.Combine(root, "results.json"),
        JsonSerializer.Serialize(
            new
            {
                failures,
                results
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
}

await Test(
    "accept-before-backend-completes-and-idempotency",
    async () =>
    {
        using var store = new FileOperationStore(
            Path.Combine(root, "accept")
        );
        var backend = new FixtureInstaller
        {
            Hold = true
        };
        var service = new InstallOperationService(store, backend, target);
        var accepted = await service.AcceptAsync(
            new(
                "first",
                Request()
            )
        );
        await backend.Entered.Task;
        var repeated = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(
                    _ => service.AcceptAsync(
                        new(
                            "first",
                            Request()
                        )
                    )
                )
        );
        Require(
            repeated.All(
                x => x.OperationId == accepted.OperationId && x.Replayed
            ) && backend.Calls == 1,
            "duplicate executed"
        );
        bool conflict = false;
        try
        {
            await service.AcceptAsync(
                new(
                    "first",
                    Request("1.20.1")
                )
            );
        }
        catch (OperationRejectedException ex)
        {
            conflict = ex.Code == "IDEMPOTENCY_CONFLICT";
        }

        Require(conflict, "conflicting key accepted");
        await service.CancelAsync(accepted.OperationId);
        Require(
            (await service.WaitAsync(accepted.OperationId)).State == "cancelled",
            "cancel lost"
        );
        await service.StopAsync();
    }
);
await Test(
    "wait-cancel-is-not-operation-cancel-and-session-query",
    async () =>
    {
        using var store = new FileOperationStore(
            Path.Combine(root, "wait")
        );
        var backend = new FixtureInstaller
        {
            Hold = true
        };
        var service = new InstallOperationService(store, backend, target);
        var accepted = await service.AcceptAsync(
            new(
                "wait",
                Request()
            )
        );
        await backend.Entered.Task;
        using var timeout = new CancellationTokenSource(50);
        try
        {
            await service.WaitAsync(accepted.OperationId, timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Require(
            (await service.GetAsync(accepted.OperationId))!.State == "running",
            "cancelled waiting killed operation"
        );
        var water = (await service.GetAsync(accepted.OperationId))!.Sequence;
        backend.Release.TrySetResult();
        var done = await service.WaitAsync(accepted.OperationId);
        Require(
            done.State == "succeeded" && done.Sequence > water && (await service.GetAsync(accepted.OperationId)) == done,
            "session state lost"
        );
        await service.StopAsync();
    }
);
await Test(
    "persistent-key-replay-and-new-attempt-retains-parent",
    async () =>
    {
        var directory = Path.Combine(root, "replay");
        string parent;
        using (var store = new FileOperationStore(directory))
        {
            var backend = new FixtureInstaller
            {
                Hold = true
            };
            var service = new InstallOperationService(store, backend, target);
            parent = (await service.AcceptAsync(
                new(
                    "parent",
                    Request()
                )
            )).OperationId;
            await backend.Entered.Task;
            await service.CancelAsync(parent);
            await service.WaitAsync(parent);
            await service.StopAsync();
        }

        using (var store = new FileOperationStore(directory))
        {
            var backend = new FixtureInstaller();
            var service = new InstallOperationService(store, backend, target);
            Require(
                (await service.AcceptAsync(
                    new(
                        "parent",
                        Request()
                    )
                )).OperationId == parent && backend.Calls == 0,
                "restart replay reran"
            );
            var resumed = await service.AcceptAsync(
                new(
                    "attempt-two",
                    Request(),
                    parent
                )
            );
            Require(
                (await service.WaitAsync(resumed.OperationId)).State == "succeeded",
                "new attempt failed"
            );
            Require(
                (await service.GetAsync(parent))!.State == "cancelled" && resumed.OperationId != parent,
                "rewrote parent"
            );
            await service.StopAsync();
        }
    }
);
await Test(
    "queued-cancel-and-late-cancel-after-commit",
    async () =>
    {
        using var store = new FileOperationStore(
            Path.Combine(root, "queued")
        );
        var backend = new FixtureInstaller
        {
            Hold = true,
            IgnoreCancellation = true
        };
        var service = new InstallOperationService(store, backend, target);
        var first = await service.AcceptAsync(
            new(
                "a",
                Request()
            )
        );
        await backend.Entered.Task;
        var second = await service.AcceptAsync(
            new(
                "b",
                Request("1.20.1")
            )
        );
        await service.CancelAsync(second.OperationId);
        await service.CancelAsync(first.OperationId);
        backend.Release.TrySetResult();
        Require(
            (await service.WaitAsync(first.OperationId)).State == "succeeded",
            "late cancel rewrote commit"
        );
        Require(
            (await service.WaitAsync(second.OperationId)).State == "cancelled" && backend.Calls == 1,
            "queued cancel executed backend"
        );
        await service.StopAsync();
    }
);
await Test(
    "real-os-lock-and-crash-recovery",
    async () =>
    {
        var directory = Path.Combine(root, "crash");
        Directory.CreateDirectory(directory);
        var info = new ProcessStartInfo(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                ".tooling/dotnet/dotnet"
            )
        )
        {
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            typeof(FixtureInstaller).Assembly.Location,
            "--crash-child",
            directory
        }

        )
        {
            info.ArgumentList.Add(argument);
        }

        using var child = Process.Start(info)!;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(
                Path.Combine(directory, "ready.json")
            ) && DateTime.UtcNow < until)
            {
                await Task.Delay(20);
            }

            Require(
                File.Exists(
                    Path.Combine(directory, "ready.json")
                ),
                "child failed to start"
            );
            bool denied = false;
            try
            {
                using var second = new FileOperationStore(directory);
            }
            catch (IOException)
            {
                denied = true;
            }

            Require(denied, "second writer acquired lock");
            child.Kill(true);
            await child.WaitForExitAsync();
            using var store = new FileOperationStore(directory);
            var backend = new FixtureInstaller();
            var service = new InstallOperationService(store, backend, target);
            var snapshot = (await service.ListAsync()).Single();
            Require(snapshot.State == "interrupted", "crash marked as cancel or still running");
            var resumed = await service.AcceptAsync(
                new("after-crash", snapshot.Request, snapshot.OperationId)
            );
            Require(
                (await service.WaitAsync(resumed.OperationId)).State == "succeeded" && backend.Calls == 1,
                "crash resume failed"
            );
            await service.StopAsync();
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(true);
                await child.WaitForExitAsync();
            }
        }
    }
);
await Test(
    "untrusted-record-and-link-rejected",
    async () =>
    {
        var directory = Path.Combine(root, "unsafe");
        Directory.CreateDirectory(directory);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(
            Path.Combine(directory, "operations-v3"),
            outside
        );
        bool rejected = false;
        try
        {
            using var store = new FileOperationStore(directory);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Require(rejected, "symbolic link accepted");
    }
);
await Test(
    "key-scope-host-upgrade-and-unknown-operation",
    async () =>
    {
        string original;
        var directory = Path.Combine(root, "scope-a");
        using (var store = new FileOperationStore(directory))
        {
            var service = new InstallOperationService(
                store,
                new FixtureInstaller(),
                target
            );
            original = (await service.AcceptAsync(
                new(
                    "same-key",
                    Request()
                )
            )).OperationId;
            await service.WaitAsync(original);
            await service.StopAsync();
        }

        using (var store = new FileOperationStore(directory))
        {
            var service = new InstallOperationService(
                store,
                new FixtureInstaller(),
                target with
                {
                    OsVersion = "upgraded"
                }
            );
            Require(
                (await service.AcceptAsync(
                    new(
                        "same-key",
                        Request()
                    )
                )).OperationId == original,
                "host OS upgrade broke old replay"
            );
            bool denied = false;
            try
            {
                await service.CancelAsync("unknown");
            }
            catch (OperationRejectedException ex)
            {
                denied = ex.Code == "OPERATION_NOT_FOUND";
            }

            Require(denied, "unknown operation accepted");
            await service.StopAsync();
        }

        using (var store = new FileOperationStore(
            Path.Combine(root, "scope-b")
        ))
        {
            var service = new InstallOperationService(
                store,
                new FixtureInstaller(),
                target
            );
            var second = await service.AcceptAsync(
                new(
                    "same-key",
                    Request()
                )
            );
            Require(second.OperationId != original, "key scope crossed data directories");
            await service.WaitAsync(second.OperationId);
            await service.StopAsync();
        }
    }
);
await Test(
    "bounded-queue-shutdown-cancels-and-stops-acceptance",
    async () =>
    {
        using var store = new FileOperationStore(
            Path.Combine(root, "queue-bound")
        );
        var backend = new FixtureInstaller
        {
            Hold = true
        };
        var service = new InstallOperationService(store, backend, target);
        var ids = new List<string>();
        for (int i = 0; i < 16; i++)
        {
            ids.Add(
                (await service.AcceptAsync(
                    new(
                        "queue-" + i,
                        Request()
                    )
                )).OperationId
            );
        }

        bool full = false;
        try
        {
            await service.AcceptAsync(
                new(
                    "overflow",
                    Request()
                )
            );
        }
        catch (OperationRejectedException ex)
        {
            full = ex.Code == "QUEUE_FULL";
        }

        Require(full, "queue exceeded limit");
        await service.StopAsync();
        Require(
            (await service.ListAsync()).All(
                x => x.State == "cancelled"
            ),
            "shutdown left running operations"
        );
        bool stopped = false;
        try
        {
            await service.AcceptAsync(
                new(
                    "after-stop",
                    Request()
                )
            );
        }
        catch (OperationRejectedException ex)
        {
            stopped = ex.Code == "APPLICATION_STOPPING";
        }

        Require(stopped, "stopped runner accepted work");
    }
);
await Test(
    "storage-failure-refuses-ack-and-preserves-interruption-evidence",
    async () =>
    {
        using var real = new FileOperationStore(
            Path.Combine(root, "store-failure")
        );
        var store = new FaultStore(real);
        var service = new InstallOperationService(
            store,
            new FixtureInstaller(),
            target
        );
        store.Fail = true;
        bool rejected = false;
        try
        {
            await service.AcceptAsync(
                new(
                    "unacknowledged",
                    Request()
                )
            );
        }
        catch (OperationRejectedException ex)
        {
            rejected = ex.Code == "STORE_UNAVAILABLE";
        }

        Require(
            rejected && real.ReadAll()
                .Count == 0,
            "acknowledged unpersisted intent"
        );
        await service.StopAsync();
        using var secondReal = new FileOperationStore(
            Path.Combine(root, "store-runtime-failure")
        );
        var secondStore = new FaultStore(secondReal);
        var backend = new FixtureInstaller
        {
            Hold = true
        };
        var runner = new InstallOperationService(secondStore, backend, target);
        var accepted = await runner.AcceptAsync(
            new(
                "persisted",
                Request()
            )
        );
        await backend.Entered.Task;
        secondStore.Fail = true;
        backend.Release.TrySetResult();
        bool failed = false;
        try
        {
            await runner.WaitAsync(accepted.OperationId);
        }
        catch (OperationRejectedException ex)
        {
            failed = ex.Code == "STORE_UNAVAILABLE";
        }

        Require(failed, "storage failure presented as success");
        var persisted = JsonSerializer.Deserialize<StoredOperation>(
            secondReal.ReadAll()
                .Single()
        )!;
        Require(!persisted.Snapshot.Terminal, "overwrote recovery evidence without persistence");
        await runner.StopAsync();
    }
);
await Test(
    "temporary-write-and-malformed-record-preserve-history",
    async () =>
    {
        var directory = Path.Combine(root, "malformed");
        string id;
        using (var store = new FileOperationStore(directory))
        {
            var service = new InstallOperationService(
                store,
                new FixtureInstaller(),
                target
            );
            id = (await service.AcceptAsync(
                new(
                    "record",
                    Request()
                )
            )).OperationId;
            await service.WaitAsync(id);
            await service.StopAsync();
        }

        var file = Path.Combine(directory, "operations-v3", id + ".json");
        File.WriteAllText(file + ".tmp", "incomplete-write");
        using (var store = new FileOperationStore(directory))
        {
            var service = new InstallOperationService(
                store,
                new FixtureInstaller(),
                target
            );
            Require(
                (await service.GetAsync(id))!.State == "succeeded",
                "temporary write replaced durable snapshot"
            );
            await service.StopAsync();
        }

        File.WriteAllText(file, "malformed-final-record");
        bool invalid = false;
        try
        {
            using var store = new FileOperationStore(directory);
            store.ReadAll();
        }
        catch (JsonException)
        {
            invalid = true;
        }

        Require(
            invalid && File.ReadAllText(file) == "malformed-final-record",
            "malformed history silently discarded"
        );
    }
);
await Test(
    "cancel-signals-do-not-run-blocking-callback-under-store-lock",
    async () =>
    {
        using var store = new FileOperationStore(
            Path.Combine(root, "callback")
        );
        using var callbackGate = new ManualResetEventSlim(false);
        var backend = new FixtureInstaller
        {
            Hold = true,
            CancellationGate = callbackGate
        };
        var runner = new InstallOperationService(store, backend, target);
        var accepted = await runner.AcceptAsync(
            new(
                "callback",
                Request()
            )
        );
        await backend.Entered.Task;
        try
        {
            var returned = await runner.CancelAsync(accepted.OperationId)
                .WaitAsync(
                    TimeSpan.FromSeconds(1)
                );
            Require(returned.State == "cancelling", "cancel must return intent before callback cleanup");
            await backend.CancellationCallbackEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2)
            );
            Require(
                (await runner.GetAsync(accepted.OperationId))!.State == "cancelling",
                "store lock blocked by callback"
            );
        }
        finally
        {
            callbackGate.Set();
            backend.Release.TrySetResult();
        }

        Require(
            (await runner.WaitAsync(accepted.OperationId)).State == "cancelled",
            "cancel cleanup did not complete"
        );
        await runner.StopAsync();
    }
);
await Test(
    "storage-failure-during-shutdown-still-drains-owned-work",
    async () =>
    {
        using var real = new FileOperationStore(
            Path.Combine(root, "shutdown-store-failure")
        );
        var store = new FaultStore(real);
        var backend = new FixtureInstaller
        {
            Hold = true
        };
        var runner = new InstallOperationService(store, backend, target);
        var accepted = await runner.AcceptAsync(
            new(
                "shutdown-failure",
                Request()
            )
        );
        await backend.Entered.Task;
        store.Fail = true;
        await runner.StopAsync()
            .WaitAsync(
                TimeSpan.FromSeconds(3)
            );
        bool failed = false;
        try
        {
            await runner.WaitAsync(accepted.OperationId);
        }
        catch (OperationRejectedException ex)
        {
            failed = ex.Code == "STORE_UNAVAILABLE";
        }

        Require(failed, "shutdown lost storage failure");
    }
);
Environment.ExitCode = failures == 0
    ? 0
    : 1;
// 使用隔离缓存副本，真实受理/下载/取消/重启/校验/续做/Java 游戏启动；不读取用户账户或世界。
static async Task Live(string directory)
{
    var report = Path.Combine(
        Path.GetDirectoryName(directory)!,
        Path.GetFileName(directory) + "-chain.json"
    );
    var paths = new AppPaths(directory);
    using var source = new MinecraftService(paths);
    var target = new InstallationTarget(source.Platform.Os, source.Platform.Architecture, source.Platform.OsVersion);
    var request = new VersionInstallRequest("1.21.1", "vanilla", target);
    var asset = Directory.EnumerateFiles(
        Path.Combine(paths.Game, "assets/objects"),
        "*",
        SearchOption.AllDirectories
    )
        .First(
            x => new FileInfo(x).Length is> 1024 and < 100000
        );
    File.Delete(asset);
    DownloadTasks.For(paths)
        .Configure(
            new NetworkOptions
            {
                ArtificialDelayMs = 2000,
                DisableLocalCopy = true
            }
        );
    string parent;
    InstallReceipt receipt;
    using (var store = new FileOperationStore(directory))
    {
        var service = new InstallOperationService(
            store,
            new ExistingVersionInstaller(source),
            target
        );
        parent = (await service.AcceptAsync(
            new("live-first", request)
        )).OperationId;
        var until = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            var snapshot = (await service.GetAsync(parent))!;
            if (snapshot.Stage == "planned")
            {
                break;
            }

            if (snapshot.Terminal || DateTime.UtcNow > until)
            {
                throw new InvalidOperationException("LIVE_PLAN_NOT_REACHED:" + snapshot.ErrorCode);
            }

            await Task.Delay(10);
        }

        await service.CancelAsync(parent);
        var cancelled = await service.WaitAsync(parent)
            .WaitAsync(
                TimeSpan.FromSeconds(30)
            );
        if (cancelled.State != "cancelled")
        {
            throw new InvalidOperationException("LIVE_NOT_CANCELLED");
        }

        receipt = store.ReadAll()
            .Select(
                x => JsonSerializer.Deserialize<StoredOperation>(x)!
            )
            .Single(
                x => x.Snapshot.OperationId == parent
            )
            .Receipt!;
        await service.StopAsync();
        // 测试恶意恢复文件：构造独立父记录，不修改原取消操作的历史。
        var original = store.ReadAll()
            .Select(
                x => JsonSerializer.Deserialize<StoredOperation>(x)!
            )
            .Single(
                x => x.Snapshot.OperationId == parent
            );
        foreach (var kind in new[]
        {
            "path",
            "metadata"
        }

        )
        {
            var id = "op-" + Guid.NewGuid()
                .ToString("N");
            var badReceipt = receipt with
            {
                OperationId = id
            };
            if (kind == "path")
            {
                badReceipt = badReceipt with
                {
                    Files = new[]
                    {
                        new ReceiptFile(
                            "../outside",
                            new string ('a', 40),
                            1
                        )
                    }
                };
            }
            else
            {
                badReceipt = badReceipt with
                {
                    MetadataSha1 = new string ('0', 40)
                };
            }

            var malicious = original with
            {
                Key = "tamper-" + kind,
                Snapshot = original.Snapshot with
                {
                    OperationId = id
                },
                Receipt = badReceipt
            };
            store.Save(
                id,
                JsonSerializer.SerializeToUtf8Bytes(malicious)
            );
        }
    }

    // 保留损坏的完整文件与 .part，证明不是看到路径存在便复用。
    File.WriteAllText(asset, "corrupt-not-a-valid-asset");
    File.WriteAllText(asset + ".part", "corrupt-partial");
    DownloadTasks.For(paths)
        .Configure(
            new NetworkOptions
            {
                DisableLocalCopy = true
            }
        );
    bool pathRejected = false, metadataRejected = false;
    using (var store = new FileOperationStore(directory))
    {
        var service = new InstallOperationService(
            store,
            new ExistingVersionInstaller(source),
            target
        );
        if ((await service.GetAsync(parent))!.State != "cancelled" || !(await service.AcceptAsync(
            new("live-first", request)
        )).Replayed)
        {
            throw new InvalidOperationException("LIVE_PERSISTENT_SESSION_RECOVERY_FAILED");
        }

        foreach (var kind in new[]
        {
            "path",
            "metadata"
        }

        )
        {
            var bad = store.ReadAll()
                .Select(
                    x => JsonSerializer.Deserialize<StoredOperation>(x)!
                )
                .Single(
                    x => x.Key == "tamper-" + kind
                );
            var accepted = await service.AcceptAsync(
                new("reject-" + kind, request, bad.Snapshot.OperationId)
            );
            var result = await service.WaitAsync(accepted.OperationId)
                .WaitAsync(
                    TimeSpan.FromMinutes(2)
                );
            if (kind == "path")
            {
                pathRejected = result.State == "failed" && result.ErrorCode == "RECEIPT_OR_METADATA_INVALID";
            }
            else
            {
                metadataRejected = result.State == "failed" && result.ErrorCode == "RESUME_PLAN_CHANGED";
            }
        }

        if (!pathRejected || !metadataRejected)
        {
            throw new InvalidOperationException("LIVE_UNTRUSTED_RECEIPT_ACCEPTED");
        }

        var acceptedResume = await service.AcceptAsync(
            new("live-resume", request, parent)
        );
        // 新调用方恢复展示只查询同一操作，不重新受理。
        var sessionSnapshot = await service.GetAsync(acceptedResume.OperationId);
        var finished = await service.WaitAsync(acceptedResume.OperationId)
            .WaitAsync(
                TimeSpan.FromMinutes(5)
            );
        if (finished.State != "succeeded" || (await service.GetAsync(parent))!.State != "cancelled")
        {
            throw new InvalidOperationException("LIVE_RESUME_FAILED:" + finished.ErrorCode);
        }

        var originalAsset = receipt.Files.Single(
            x => Path.GetFullPath(
                Path.Combine(directory, x.Path)
            ) == asset
        );
        bool repaired = DownloadService.IsValid(
            new(
                new Uri("https://localhost/unused"),
                asset,
                originalAsset.Size,
                originalAsset.Sha1
            )
        );
        if (!repaired)
        {
            throw new InvalidOperationException("LIVE_CORRUPT_FILE_REUSED");
        }

        var installed = source.LoadInstalled();
        var java = await new JavaService(source).EnsureForAsync(installed);
        var session = AccountManager.OfflineSession("OperationTest");
        var launcher = new LaunchService(source);
        var plan = await launcher.PrepareAsync(installed, java, session, "", paths.Game);
        var game = await launcher.StartAsync(plan, session);
        bool rendered = false;
        try
        {
            var until = DateTime.UtcNow.AddMinutes(3);
            while (!game.HasExited && DateTime.UtcNow < until)
            {
                var text = File.ReadAllText(game.LogFile);
                if (text.Contains("Created:") && text.Contains("atlas"))
                {
                    rendered = true;
                    break;
                }

                await Task.Delay(250);
            }

            if (!rendered)
            {
                throw new InvalidOperationException("LIVE_GAME_NOT_INITIALIZED");
            }
        }
        finally
        {
            if (!game.HasExited)
            {
                await game.StopAsync();
            }

            await game.Completion;
            await game.DisposeAsync();
            await service.StopAsync();
        }

        File.WriteAllText(
            report,
            JsonSerializer.Serialize(
                new
                {
                    status = "passed",
                    parent,
                    resumed = acceptedResume.OperationId,
                    cancelled = true,
                    persistentSessionRecovered = true,
                    sameHostSnapshotRecovered = sessionSnapshot is not null,
                    repaired,
                    pathRejected,
                    metadataRejected,
                    rendered,
                    receiptFiles = receipt.Files.Count,
                    metadataHashPinned = true,
                    originalTerminalPreserved = true
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }
            )
        );
        Console.WriteLine("LIVE_CHAIN_PASSED");
    }
}

sealed class FixtureInstaller : IOperationInstaller
{
    public bool Hold { get; init; }
    public bool IgnoreCancellation { get; init; }

    public int Calls;
    public ManualResetEventSlim? CancellationGate { get; init; }
    public TaskCompletionSource CancellationCallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<OperationInstalledInstance> InstallAsync(
        string id,
        OperationInstallInput request,
        InstallReceipt? previous,
        Action<InstallReceipt> checkpoint,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    )
    {
        Interlocked.Increment(ref Calls);
        checkpoint(
            new(
                1,
                id,
                "vanilla-" + request.GameVersion,
                request.GameVersion,
                request.Loader,
                new(request.Os, request.Architecture, request.OsVersion),
                new string ('a', 40),
                new string ('b', 40),
                "fixture",
                [],
                "planned"
            )
        );
        using var registration = CancellationGate is null
            ? default
            : ct.Register(
                () =>
                {
                    CancellationCallbackEntered.TrySetResult();
                    CancellationGate?.Wait();
                }
            );
        Entered.TrySetResult();
        if (Hold)
        {
            if (CancellationGate is not null)
            {
                await Release.Task;
                ct.ThrowIfCancellationRequested();
            }
            else
            {
                await Release.Task.WaitAsync(
                    IgnoreCancellation
                        ? CancellationToken.None
                        : ct
                );
            }
        }

        return new(
            "vanilla-" + request.GameVersion,
            "fixture",
            request.GameVersion,
            request.Loader,
            "/fixture/game"
        );
    }
}

// 故障注入只影响测试的存储 Port，不改变真实持久化实现。
sealed class FaultStore(IOperationStore inner) : IOperationStore
{
    public bool Fail;
    public IReadOnlyList<byte[]> ReadAll() => inner.ReadAll();
    public void Save(string id, byte[] document)
    {
        if (Fail)
        {
            throw new IOException("fixture disk unavailable");
        }

        inner.Save(id, document);
    }

    public void Dispose()
    {
    }
}
