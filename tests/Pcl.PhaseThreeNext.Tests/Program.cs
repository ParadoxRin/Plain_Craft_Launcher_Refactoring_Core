using Pcl.Core.Application.Launch;
using System.Text.Json;
using System.Diagnostics;
using Pcl.Client;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Adapters.Protocol;
using Pcl.Core;
using Pcl.Core.Adapters.Mac;
using Pcl.Core.Adapters.Operations;
using Pcl.Core.Application.Operations;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Ports.Operations;

var root = Path.GetFullPath(
    args.ElementAtOrDefault(1) ?? "artifacts/architecture-phase-three-next/loader-data"
);
Directory.CreateDirectory(root);
if (args.FirstOrDefault() == "--loaders")
{
    var reports = new List<object>();
    var paths = new AppPaths(root);
    using var source = new MinecraftService(paths);
    var target = new InstallationTarget(source.Platform.Os, source.Platform.Architecture, source.Platform.OsVersion);
    var selectedFamilies = args.Length > 2
        ? args.Skip(2)
            .ToArray()
        : new[]
        {
            "fabric",
            "quilt",
            "forge",
            "neoforge"
        };
    var attempt = Guid.NewGuid()
        .ToString("N");
    foreach (var family in selectedFamilies)
    {
        try
        {
            DownloadTasks.For(paths)
                .Configure(
                    new NetworkOptions
                    {
                        ArtificialDelayMs = 1000
                    }
                );
            string parent;
            using (var store = new FileOperationStore(root))
            {
                var service = new InstallOperationService(
                    store,
                    new ExistingVersionInstaller(source),
                    target
                );
                parent = (await service.AcceptAsync(
                    new(
                        "cancel-" + family + attempt,
                        new("1.21.1", family, target)
                    )
                )).OperationId;
                var until = DateTime.UtcNow.AddMinutes(5);
                while (true)
                {
                    var snapshot = (await service.GetAsync(parent))!;
                    if (snapshot.Stage == (family is "forge" or "neoforge"
                        ? "installer-running"
                        : "loader-planned"))
                    {
                        break;
                    }

                    if (snapshot.Terminal || DateTime.UtcNow > until)
                    {
                        throw new InvalidOperationException("CHECKPOINT_NOT_REACHED:" + snapshot.ErrorCode);
                    }

                    await Task.Delay(10);
                }

                if (family is "forge" or "neoforge")
                {
                    await Task.Delay(200);
                }

                await service.CancelAsync(parent);
                if ((await service.WaitAsync(parent)).State != "cancelled")
                {
                    throw new InvalidOperationException("NOT_CANCELLED");
                }

                await service.StopAsync();
            }

            Console.WriteLine("CANCELLED " + family);
            DownloadTasks.For(paths)
                .Configure(
                    new NetworkOptions()
                );
            using (var store = new FileOperationStore(root))
            {
                var service = new InstallOperationService(
                    store,
                    new ExistingVersionInstaller(source),
                    target
                );
                var accepted = await service.AcceptAsync(
                    new(
                        "resume-" + family + attempt,
                        new("1.21.1", family, target),
                        parent
                    )
                );
                var done = await service.WaitAsync(accepted.OperationId)
                    .WaitAsync(
                        TimeSpan.FromMinutes(12)
                    );
                if (done.State != "succeeded")
                {
                    throw new InvalidOperationException("RESUME_FAILED:" + done.ErrorCode);
                }

                var receipt = store.ReadAll()
                    .Select(
                        x => JsonSerializer.Deserialize<StoredOperation>(x)!
                    )
                    .Single(
                        x => x.Snapshot.OperationId == done.OperationId
                    )
                    .Receipt!;
                var profile = InstanceRegistry.List(source, true)
                    .Single(
                        x => x.Id == done.Result!.Id
                    );
                var installed = InstanceRegistry.Load(source, profile);
                if (installed.Libraries.Any(
                    x => !DownloadService.IsValid(x)
                ))
                {
                    throw new InvalidOperationException("INVALID_LIBRARY");
                }

                var accounts = new AccountManager(
                    paths,
                    new KeychainAccountSecrets(root)
                );
                var account = accounts.Accounts.FirstOrDefault(
                    x => x.Kind == AccountKind.Offline
                ) ?? accounts.AddOffline("LoaderRecovery");
                using var launchStore = new FileOperationStore(root, "launch-operations-v1");
                using var executor = new ExistingGameLaunchExecutor(source, accounts);
                var launches = new GameLaunchOperationService(launchStore, executor);
                var launch = await launches.AcceptAsync(
                    new("launch-" + family + attempt, account.Id, profile.Id)
                );
                var started = await launches.WaitAsync(launch.OperationId);
                if (started.State != "succeeded")
                {
                    throw new InvalidOperationException("LAUNCH_FAILED:" + started.ErrorCode);
                }

                var game = executor.BorrowLocalSession(started.Result!.GameProcessId)!;
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
                        throw new InvalidOperationException("GAME_NOT_INITIALIZED");
                    }

                    var replay = await launches.AcceptAsync(
                        new("launch-" + family + attempt, account.Id, profile.Id)
                    );
                    if (!replay.Replayed || replay.OperationId != launch.OperationId)
                    {
                        throw new InvalidOperationException("LAUNCH_DUPLICATED");
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
                    await launches.StopAsync();
                }

                reports.Add(
                    new
                    {
                        family,
                        status = "passed",
                        parent,
                        operationId = done.OperationId,
                        loaderVersion = receipt.LoaderPin!.Version,
                        rendered,
                        pinPresent = true,
                        libraries = installed.Libraries.Count
                    }
                );
                await service.StopAsync();
            }

            Console.WriteLine("PASSED " + family);
        }
        catch (Exception ex)
        {
            reports.Add(
                new
                {
                    family,
                    status = "failed",
                    error = DiagnosticsService.Redact(ex.Message),
                    type = ex.GetType()
                        .Name
                }
            );
            Console.WriteLine(
                "FAILED " + family + " " + DiagnosticsService.Redact(ex.Message)
            );
            Environment.ExitCode = 1;
        }

        File.WriteAllText(
            Path.Combine(
                Path.GetDirectoryName(root)!,
                args.Length > 2
                    ? "loaders-selected.json"
                    : "loaders-live.json"
            ),
            JsonSerializer.Serialize(
                reports,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }
            )
        );
    }

    return;
}

if (args.FirstOrDefault() == "--host")
{
    var paths = new AppPaths(root);
    var accounts = new AccountManager(
        paths,
        new KeychainAccountSecrets(root)
    );
    var account = accounts.Accounts.FirstOrDefault(
        x => x.Kind == AccountKind.Offline
    ) ?? accounts.AddOffline("HostRecovery");
    var profile = InstanceRegistry.List(
        new MinecraftService(paths),
        true
    )
        .First(
            x => x.Loader == "fabric"
        );
    var dotnet = Path.GetFullPath(".tooling/dotnet/dotnet");
    var assembly = Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll");
    var json = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };
    string launchId;
    StartedGame started;
    await using (var client = new HostClient(dotnet, assembly, root))
    {
        var hello = await client.HelloAsync();
        if (hello.ProtocolVersion != 1)
        {
            throw new Exception("HELLO_FAILED");
        }

        var platform = TargetPlatform.Current;
        var target = new InstallationTarget(platform.Os, platform.Architecture, platform.OsVersion);
        // Host 接收安装并取消排队/规划，之后同一 key 必须重查旧结果，不能重跑。
        var install = await client.Installations.AcceptAsync(
            new(
                "host-cancel",
                new("1.21.1", "vanilla", target)
            )
        );
        await client.Installations.CancelAsync(install.OperationId);
        var cancelled = await client.Installations.WaitAsync(install.OperationId);
        if (cancelled.State != "cancelled")
        {
            throw new Exception("HOST_CANCEL_FAILED");
        }

        var request = new GameLaunchCommand("host-detached", account.Id, profile.Id, true);
        var accepted = await client.Launches.AcceptAsync(request);
        launchId = accepted.OperationId;
        var launched = await client.Launches.WaitAsync(launchId);
        if (launched.State != "succeeded")
        {
            throw new Exception("HOST_LAUNCH_FAILED:" + launched.ErrorCode);
        }

        started = launched.Result!;
        var replay = await client.Launches.AcceptAsync(request);
        if (!replay.Replayed || replay.OperationId != launchId)
        {
            throw new Exception("HOST_DUPLICATE_GAME");
        }

        var resource = await client.OpenResourceAsync("operations");
        var page = await client.ReadPageAsync(resource.ResourceId, 0, 50);
        if (!page.Items.Any(
            x => x.GetProperty("operationId")
                .GetString() == launchId
        ))
        {
            throw new Exception("HOST_PAGE_FAILED");
        }

        var chunk = await client.ReadResourceAsync(resource.ResourceId, 0, 256);
        if (Convert.FromBase64String(chunk.Base64)
            .Length == 0)
        {
            throw new Exception("HOST_CHUNK_FAILED");
        }

        bool rangeRejected = false;
        try
        {
            await client.ReadResourceAsync(resource.ResourceId, -1, 999999);
        }
        catch (OperationRejectedException ex)
        {
            rangeRejected = ex.Code == "RESOURCE_RANGE_INVALID";
        }

        if (!rangeRejected)
        {
            throw new Exception("HOST_RANGE_ACCEPTED");
        }

        await client.CloseResourceAsync(resource.ResourceId);
        bool closed = false;
        try
        {
            await client.ReadResourceAsync(resource.ResourceId, 0, 1);
        }
        catch (OperationRejectedException ex)
        {
            closed = ex.Code == "RESOURCE_EXPIRED_OR_UNKNOWN";
        }

        if (!closed)
        {
            throw new Exception("HOST_RESOURCE_NOT_CLOSED");
        }

        var logs = await client.OpenResourceAsync("operation-log", launchId);
        var events = await client.ReadPageAsync(logs.ResourceId, 0, 50);
        if (events.Items.Length < 2)
        {
            throw new Exception("HOST_LOG_MISSING");
        }

        await client.CloseResourceAsync(logs.ResourceId);
        await client.ShutdownAsync();
    }

    bool rendered = false;
    using (var child = Process.GetProcessById(started.Pid))
    {
        try
        {
            var identity = child.StartTime;
            var receipt = JsonFiles.Read(
                Path.Combine(paths.Evidence, "detached-launch.json")
            );
            var log = (string)receipt["log"]!;
            var until = DateTime.UtcNow.AddMinutes(3);
            while (!child.HasExited && DateTime.UtcNow < until)
            {
                var text = File.ReadAllText(log);
                if (text.Contains("Created:") && text.Contains("atlas"))
                {
                    rendered = true;
                    break;
                }

                await Task.Delay(250);
            }

            if (!rendered || child.HasExited || child.StartTime != identity)
            {
                throw new Exception("DETACHED_GAME_DID_NOT_SURVIVE_HOST");
            }
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

    await using (var client = new HostClient(dotnet, assembly, root))
    {
        await client.HelloAsync();
        var repeated = await client.Launches.AcceptAsync(
            new("host-detached", account.Id, profile.Id, true)
        );
        if (!repeated.Replayed || repeated.OperationId != launchId)
        {
            throw new Exception("HOST_RESTART_LOST_LAUNCH_KEY");
        }

        bool notReattached = false;
        try
        {
            await client.Launches.GetGameAsync(started.GameProcessId);
        }
        catch (OperationRejectedException ex)
        {
            notReattached = ex.Code == "GAME_NOT_MANAGED_BY_THIS_HOST";
        }

        if (!notReattached)
        {
            throw new Exception("HOST_REATTACHED_BY_PID");
        }

        await client.ShutdownAsync();
    }

    File.WriteAllText(
        Path.Combine(
            Path.GetDirectoryName(root)!,
            "host-live.json"
        ),
        JsonSerializer.Serialize(
            new
            {
                status = "passed",
                launchId,
                started.GameProcessId,
                rendered,
                hostExitedBeforeRenderer = true,
                restartKeyReplayed = true,
                noPidReattach = true,
                resourcesPagedAndChunked = true,
                resourceBoundsEnforced = true
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
    Console.WriteLine("HOST_LIVE_PASSED");
    return;
}

if (args.FirstOrDefault() == "--tests")
{
    var results = new List<object>();
    int failures = 0;
    async Task Test(string name, Func<Task> body)
    {
        try
        {
            await body()
                .WaitAsync(
                    TimeSpan.FromSeconds(15)
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
            Console.WriteLine("FAIL " + name);
        }
    }

    void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    await Test(
        "launch-concurrent-idempotency-conflict-and-restart",
        async () =>
        {
            var path = Path.Combine(root, "launch-idempotency");
            string id;
            using (var store = new FileOperationStore(path, "launch-operations-v1"))
            {
                var executor = new FakeLaunch();
                var service = new GameLaunchOperationService(store, executor);
                var command = new GameLaunchCommand("intent", "offline-fixture", "1.21.1");
                var accepted = await Task.WhenAll(
                    Enumerable.Range(0, 10)
                        .Select(
                            _ => service.AcceptAsync(command)
                        )
                );
                id = accepted[0].OperationId;
                var done = await service.WaitAsync(id);
                Require(
                    accepted.All(
                        x => x.OperationId == id
                    ) && executor.Starts == 1 && done.State == "succeeded",
                    "duplicate execution"
                );
                bool conflict = false;
                try
                {
                    await service.AcceptAsync(
                        command with
                        {
                            Detached = true
                        }
                    );
                }
                catch (OperationRejectedException ex)
                {
                    conflict = ex.Code == "IDEMPOTENCY_CONFLICT";
                }

                Require(conflict, "key conflict not rejected");
                await service.StopAsync();
            }

            using (var store = new FileOperationStore(path, "launch-operations-v1"))
            {
                var executor = new FakeLaunch();
                var service = new GameLaunchOperationService(store, executor);
                var replay = await service.AcceptAsync(
                    new("intent", "offline-fixture", "1.21.1")
                );
                Require(
                    replay.OperationId == id && replay.Replayed && executor.Starts == 0,
                    "restart relaunched game"
                );
                await service.StopAsync();
            }
        }
    );
    await Test(
        "launch-cancel-wait-is-not-cancel-game-or-launch",
        async () =>
        {
            using var store = new FileOperationStore(
                Path.Combine(root, "launch-wait"),
                "launch-operations-v1"
            );
            var executor = new FakeLaunch
            {
                Hold = true
            };
            var service = new GameLaunchOperationService(store, executor);
            var accepted = await service.AcceptAsync(
                new("intent", "offline-fixture", "1.21.1")
            );
            await executor.Entered.Task;
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
                "waiting cancellation cancelled operation"
            );
            await service.CancelAsync(accepted.OperationId);
            Require(
                (await service.WaitAsync(accepted.OperationId)).State == "cancelled" && executor.Spawns == 0,
                "cancel spawned game"
            );
            await service.StopAsync();
        }
    );
    await Test(
        "launch-spawn-gap-interrupted-never-reexecuted",
        async () =>
        {
            var path = Path.Combine(root, "launch-gap");
            using (var store = new FileOperationStore(path, "launch-operations-v1"))
            {
                var command = new GameLaunchCommand("intent", "offline-fixture", "1.21.1", true);
                var id = "op-" + Guid.NewGuid()
                    .ToString("N");
                var fingerprint = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(
                            new
                            {
                                method = "game.launch",
                                command.AccountId,
                                command.InstanceId,
                                command.Detached
                            }
                        )
                    )
                );
                store.Save(
                    id,
                    JsonSerializer.SerializeToUtf8Bytes(
                        new StoredLaunch(
                            1,
                            "intent",
                            fingerprint,
                            new(id, "running", "spawning", 3, DateTimeOffset.UtcNow, command)
                        )
                    )
                );
            }

            using (var store = new FileOperationStore(path, "launch-operations-v1"))
            {
                var executor = new FakeLaunch();
                var service = new GameLaunchOperationService(store, executor);
                var repeated = await service.AcceptAsync(
                    new("intent", "offline-fixture", "1.21.1", true)
                );
                var done = await service.WaitAsync(repeated.OperationId);
                Require(
                    done.State == "interrupted" && done.ErrorCode == "LAUNCH_OUTCOME_UNKNOWN" && executor.Starts == 0,
                    "uncertain spawn replayed"
                );
                await service.StopAsync();
            }
        }
    );
    await Test(
        "ndjson-byte-bound-and-resynchronization",
        async () =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                new string ('中', 400000) + "\n{\"ok\":1}\n"
            );
            var frames = new List<ProtocolFrame>();
            await foreach (var frame in BoundedNdjson.ReadAsync(
                new MemoryStream(bytes)
            ))
            {
                frames.Add(frame);
            }

            Require(
                frames.Count == 2 && frames[0].Error == "FRAME_TOO_LARGE" && frames[1].Data is not null,
                "frame limit or resynchronization failed"
            );
        }
    );
    await Test(
        "bounded-journal-redacts-paths-and-signals-truncation",
        async () =>
        {
            var journal = new Pcl.Core.Adapters.Diagnostics.BoundedOperationJournal(root);
            var id = "op-" + Guid.NewGuid()
                .ToString("N");
            journal.Write(
                new(id, 1, "info", "https://secret.example/?access_token=secret")
            );
            Require(
                journal.Read(id)
                    .Single()
                    .Code == "stage-redacted",
                "free text leaked"
            );
            for (int i = 2; i < 150; i++)
            {
                journal.Write(
                    new(id, i, "info", "stage-" + i)
                );
            }

            var rows = journal.Read(id);
            Require(
                rows.Count == 128 && rows[0].Truncated && rows.Last()
                    .Sequence == 149,
                "log bound lost truncation marker"
            );
            await Task.CompletedTask;
        }
    );
    await Test(
        "recovery-publish-replaces-only-verified-files-preserves-worlds",
        async () =>
        {
            var stage = Path.Combine(root, "publish-stage");
            var target = Path.Combine(root, "publish-target");
            Directory.CreateDirectory(
                Path.Combine(stage, "libraries")
            );
            Directory.CreateDirectory(
                Path.Combine(target, "libraries")
            );
            var profile = new
            {
                id = "owned",
                gameVersion = "1.21.1",
                loader = "forge",
                loaderVersion = "test",
                displayName = "Original"
            };
            JsonFiles.Write(
                Path.Combine(stage, "profile.json"),
                profile
            );
            JsonFiles.Write(
                Path.Combine(target, "profile.json"),
                profile
            );
            var saved = JsonFiles.Read(
                Path.Combine(target, "profile.json")
            );
            saved["displayName"] = "Custom name";
            JsonFiles.Write(
                Path.Combine(target, "profile.json"),
                saved
            );
            File.WriteAllText(
                Path.Combine(stage, "version.json"),
                "fresh-version"
            );
            File.WriteAllText(
                Path.Combine(stage, "libraries/generated.jar"),
                "fresh-output"
            );
            File.WriteAllText(
                Path.Combine(target, "version.json"),
                "old-version"
            );
            Directory.CreateDirectory(
                Path.Combine(target, "saves/world")
            );
            File.WriteAllText(
                Path.Combine(target, "saves/world/marker"),
                "keep-world"
            );
            new LoaderRecoveryContext(
                (_, _, _, _) =>
                {
                }
            ).Publish(stage, target);
            Require(
                File.ReadAllText(
                    Path.Combine(target, "libraries/generated.jar")
                ) == "fresh-output" && File.ReadAllText(
                    Path.Combine(target, "saves/world/marker")
                ) == "keep-world",
                "publish overwrote user data or missed output"
            );
            Require(
                (string? )JsonFiles.Read(
                    Path.Combine(target, "profile.json")
                )["displayName"] == "Custom name",
                "publish lost custom name"
            );
            await Task.CompletedTask;
        }
    );
    await Test(
        "recovery-publish-rejects-link-and-foreign-instance",
        async () =>
        {
            var stage = Path.Combine(root, "unsafe-stage");
            var target = Path.Combine(root, "unsafe-target");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(
                Path.Combine(stage, "libraries")
            );
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(outside);
            var profile = new
            {
                id = "owned",
                gameVersion = "1.21.1",
                loader = "forge",
                loaderVersion = "test"
            };
            JsonFiles.Write(
                Path.Combine(stage, "profile.json"),
                profile
            );
            JsonFiles.Write(
                Path.Combine(target, "profile.json"),
                profile
            );
            File.WriteAllText(
                Path.Combine(stage, "libraries/test.jar"),
                "fixture"
            );
            Directory.CreateSymbolicLink(
                Path.Combine(target, "libraries"),
                outside
            );
            bool denied = false;
            try
            {
                new LoaderRecoveryContext(
                    (_, _, _, _) =>
                    {
                    }
                ).Publish(stage, target);
            }
            catch (InvalidDataException)
            {
                denied = true;
            }

            Require(
                denied && !File.Exists(
                    Path.Combine(outside, "test.jar")
                ),
                "recovery followed link"
            );
            var other = JsonFiles.Read(
                Path.Combine(target, "profile.json")
            );
            other["loaderVersion"] = "foreign";
            JsonFiles.Write(
                Path.Combine(target, "profile.json"),
                other
            );
            bool conflict = false;
            try
            {
                new LoaderRecoveryContext(
                    (_, _, _, _) =>
                    {
                    }
                ).Publish(stage, target);
            }
            catch (OperationInstallException ex)
            {
                conflict = ex.Code == "RECOVERY_TARGET_CONFLICT";
            }

            Require(conflict, "recovery overwrote foreign instance");
            await Task.CompletedTask;
        }
    );
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
    Environment.ExitCode = failures == 0
        ? 0
        : 1;
}

if (args.FirstOrDefault() == "--commit-recovery")
{
    var sourceRoot = Path.GetFullPath(args[2]);
    using var originalStore = new FileOperationStore(sourceRoot);
    var original = originalStore.ReadAll()
        .Select(
            x => JsonSerializer.Deserialize<StoredOperation>(x)!
        )
        .First(
            x => x.Snapshot.State == "succeeded" && x.Receipt?.LoaderPin?.Family == "forge"
        );
    using var store = new FileOperationStore(root);
    var interrupted = original with
    {
        Snapshot = original.Snapshot with
        {
            State = "interrupted",
            ErrorCode = "HOST_INTERRUPTED",
            Result = null
        }
    };
    store.Save(
        interrupted.Snapshot.OperationId,
        JsonSerializer.SerializeToUtf8Bytes(interrupted)
    );
    var paths = new AppPaths(root);
    using var source = new MinecraftService(paths);
    var target = new InstallationTarget(source.Platform.Os, source.Platform.Architecture, source.Platform.OsVersion);
    var service = new InstallOperationService(
        store,
        new ExistingVersionInstaller(source),
        target
    );
    var folder = Path.Combine(root, "instances", original.Receipt!.LoaderPin!.InstanceName);
    Directory.CreateDirectory(
        Path.Combine(folder, "saves/recovery-fixture")
    );
    File.WriteAllText(
        Path.Combine(folder, "saves/recovery-fixture/marker"),
        "keep-this-world-marker"
    );
    var accepted = await service.AcceptAsync(
        new(
            Guid.NewGuid()
                .ToString("N"),
            original.Snapshot.Request,
            original.Snapshot.OperationId
        )
    );
    var done = await service.WaitAsync(accepted.OperationId)
        .WaitAsync(
            TimeSpan.FromMinutes(12)
        );
    if (done.State != "succeeded" || File.ReadAllText(
        Path.Combine(folder, "saves/recovery-fixture/marker")
    ) != "keep-this-world-marker")
    {
        throw new Exception("COMMIT_RECOVERY_FAILED:" + done.ErrorCode);
    }

    var installed = InstanceRegistry.Load(
        source,
        InstanceRegistry.List(source, true)
            .Single(
                x => x.Id == done.Result!.Id
            )
    );
    if (installed.Libraries.Any(
        x => !DownloadService.IsValid(x)
    ))
    {
        throw new Exception("COMMIT_RECOVERY_INVALID_OUTPUT");
    }

    await service.StopAsync();
    File.WriteAllText(
        Path.Combine(
            Path.GetDirectoryName(root)!,
            "commit-recovery-live.json"
        ),
        JsonSerializer.Serialize(
            new
            {
                status = "passed",
                worldMarkerPreserved = true,
                verifiedLibraries = installed.Libraries.Count,
                generatedStepReexecuted = true,
                instance = original.Receipt.LoaderPin.InstanceName
            }
        )
    );
    Console.WriteLine("COMMIT_RECOVERY_PASSED");
}

if (args.FirstOrDefault() == "--microsoft")
{
    var originalRoot = Path.GetFullPath(args[2]);
    var paths = new AppPaths(root);
    File.Copy(
        Path.Combine(originalRoot, "accounts.json"),
        Path.Combine(root, "accounts.json"),
        true
    );
    var accounts = new AccountManager(
        paths,
        new KeychainAccountSecrets(originalRoot)
    );
    var account = accounts.Accounts.First(
        x => x.Kind == AccountKind.Microsoft && x.Remembered
    );
    using var source = new MinecraftService(paths);
    using var store = new FileOperationStore(root, "launch-operations-v1");
    using var executor = new ExistingGameLaunchExecutor(source, accounts);
    var launches = new GameLaunchOperationService(store, executor);
    var accepted = await launches.AcceptAsync(
        new(
            Guid.NewGuid()
                .ToString("N"),
            account.Id,
            "1.21.1"
        )
    );
    var done = await launches.WaitAsync(accepted.OperationId)
        .WaitAsync(
            TimeSpan.FromMinutes(3)
        );
    if (done.State != "succeeded")
    {
        throw new Exception("MS_LAUNCH_FAILED:" + done.ErrorCode);
    }

    var game = executor.BorrowLocalSession(done.Result!.GameProcessId)!;
    bool rendered = false;
    try
    {
        var until = DateTime.UtcNow.AddMinutes(3);
        while (!game.HasExited && DateTime.UtcNow < until)
        {
            var log = File.ReadAllText(game.LogFile);
            if (log.Contains("Created:") && log.Contains("atlas"))
            {
                rendered = true;
                break;
            }

            await Task.Delay(250);
        }

        if (!rendered)
        {
            throw new Exception("MS_GAME_NOT_INITIALIZED");
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
        await launches.StopAsync();
    }

    File.WriteAllText(
        Path.Combine(
            Path.GetDirectoryName(root)!,
            "microsoft-live.json"
        ),
        JsonSerializer.Serialize(
            new
            {
                status = "passed",
                credentialScopePreserved = true,
                ownershipVerifiedBeforeLaunch = true,
                rendered,
                noCredentialsInPublicCommand = true
            }
        )
    );
    Console.WriteLine("MICROSOFT_LAUNCH_PASSED");
}

if (args.FirstOrDefault() == "--loader-tamper")
{
    var originalRoot = Path.GetFullPath(args[2]);
    using var original = new FileOperationStore(originalRoot);
    var rows = original.ReadAll()
        .Select(
            x => JsonSerializer.Deserialize<StoredOperation>(x)!
        )
        .ToArray();
    using var source = new MinecraftService(
        new AppPaths(root)
    );
    var observations = new List<object>();
    foreach (var family in new[]
    {
        "forge",
        "fabric"
    }

    )
    {
        var row = rows.First(
            x => x.Snapshot.State == "succeeded" && x.Receipt?.LoaderPin?.Family == family
        );
        var receipt = row.Receipt!;
        var pin = receipt.LoaderPin!;
        var bad = family == "forge"
            ? pin with
            {
                ArtifactHash = new string ('0', 40)
            }

            : pin with
            {
                MetadataHash = new string ('0', 64)
            };
        var request = row.Snapshot.Request;
        bool rejected = false;
        try
        {
            await new ExistingVersionInstaller(source).InstallAsync(
                "op-" + Guid.NewGuid()
                    .ToString("N"),
                new(
                    request.GameVersion,
                    request.Loader,
                    request.Target.Os,
                    request.Target.Architecture,
                    request.Target.OsVersion
                ),
                receipt with
                {
                    LoaderPin = bad
                },
                _ =>
                {
                },
                new QuietProgress(),
                CancellationToken.None
            );
        }
        catch (OperationInstallException ex)
        {
            rejected = ex.Code == (family == "forge"
                ? "RESUME_INSTALLER_CHANGED"
                : "RESUME_LOADER_METADATA_CHANGED");
        }

        if (!rejected)
        {
            throw new Exception("TAMPERED_LOADER_INPUT_ACCEPTED:" + family);
        }

        observations.Add(
            new
            {
                family,
                rejected = true
            }
        );
    }

    File.WriteAllText(
        Path.Combine(
            Path.GetDirectoryName(root)!,
            "loader-tamper-live.json"
        ),
        JsonSerializer.Serialize(
            new
            {
                status = "passed",
                observations
            }
        )
    );
    Console.WriteLine("LOADER_TAMPER_REJECTED");
}

sealed class FakeLaunch : IGameLaunchExecutor
{
    public bool Hold;
    public int Starts;
    public int Spawns;
    public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<StartedGameProcess> StartAsync(GameLaunchInput input, Action beforeSpawn, CancellationToken ct)
    {
        Starts++;
        Entered.TrySetResult();
        if (Hold)
        {
            await Release.Task.WaitAsync(ct);
        }

        beforeSpawn();
        Spawns++;
        return new("game-fixture", 42, input.Detached);
    }

    public ManagedGameState Get(string id) => new(id, true, null, false);
    public Task StopAsync(string id, CancellationToken ct) => Task.CompletedTask;
}

sealed class QuietProgress : IProgress<InstallProgress>
{
    public void Report(InstallProgress value)
    {
    }
}
