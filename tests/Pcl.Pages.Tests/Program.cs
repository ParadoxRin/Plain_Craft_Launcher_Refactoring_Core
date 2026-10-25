using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Pcl.Client;
using Pcl.Core;
using Pcl.Core.Application.Pages;
using Pcl.Core.Adapters.Operations;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Ports.Operations;
using Pcl.Core.Ports.Pages;

var root = Path.GetFullPath(args[1]);
var report = Path.GetFullPath(args[2]);
var checks = new List<string>();
void Check(bool okay, string name)
{
    if (!okay)
    {
        throw new Exception(name);
    }

    checks.Add(name);
    Console.WriteLine(name);
}

var wire = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};
try
{
    if (args[0] == "--unit")
    {
        Directory.CreateDirectory(root);
        using var store = new FileOperationStore(root, "page-operations-v1");
        using var lane = new SemaphoreSlim(0, 1);
        var fake = new FixtureExecutor();
        var service = new PageOperationService(store, fake, lane);
        var command = new PageCommand("stable", "artifact.list", GameVersion: "1.21.1", Loader: "client");
        var accepted = await service.AcceptAsync(command);
        Check(
            fake.Calls == 0 && store.ReadAll()
                .Count == 1,
            "admission-persists-before-execution"
        );
        Check(
            (await service.AcceptAsync(command)).OperationId == accepted.OperationId,
            "same-command-replays-id"
        );
        bool conflict = false;
        try
        {
            await service.AcceptAsync(
                command with
                {
                    GameVersion = "1.20.1"
                }
            );
        }
        catch (OperationRejectedException ex)
        {
            conflict = ex.Code == "IDEMPOTENCY_CONFLICT";
        }

        Check(conflict, "idempotency-input-conflict");
        using (var waitCancel = new CancellationTokenSource())
        {
            waitCancel.Cancel();
            try
            {
                await service.WaitAsync(accepted.OperationId, waitCancel.Token);
            }
            catch (OperationCanceledException)
            {
            }

            Check(
                (await service.GetAsync(accepted.OperationId)).State == "queued",
                "wait-cancellation-does-not-cancel-backend"
            );
        }

        await service.CancelAsync(accepted.OperationId);
        Check(
            (await service.WaitAsync(accepted.OperationId)).State == "cancelled" && fake.Calls == 0,
            "queued-cancel-never-executes"
        );
        var resumed = await service.AcceptAsync(
            command with
            {
                IdempotencyKey = "resume",
                ResumeOfOperationId = accepted.OperationId
            }
        );
        lane.Release();
        Check(
            (await service.WaitAsync(resumed.OperationId)).State == "succeeded" && fake.LastId == accepted.OperationId,
            "resume-reuses-stable-execution-identity"
        );
        Check(
            (await service.GetAsync(accepted.OperationId)).State == "cancelled",
            "resume-preserves-parent-terminal"
        );
        await service.StopAsync();
        // 模拟已持久化的运行状态；实际进程崩溃路径在 --live 中单独验证。
        var saved = store.ReadAll()
            .Select(
                x => JsonSerializer.Deserialize<StoredPage>(x)!
            )
            .Single(
                x => x.Snapshot.OperationId == resumed.OperationId
            );
        store.Save(
            resumed.OperationId,
            JsonSerializer.SerializeToUtf8Bytes(
                saved with
                {
                    Snapshot = saved.Snapshot with
                    {
                        State = "running",
                        Result = null
                    }
                }
            )
        );
        var restored = new PageOperationService(store, fake, lane);
        Check(
            (await restored.GetAsync(resumed.OperationId)).State == "interrupted" && fake.Calls == 1,
            "restore-marks-interrupted-without-auto-execution"
        );
        Check(
            (await restored.AcceptAsync(
                command with
                {
                    IdempotencyKey = "resume",
                    ResumeOfOperationId = accepted.OperationId
                }
            )).Replayed,
            "replay-survives-store-reopen"
        );
        var continued = await restored.AcceptAsync(
            command with
            {
                IdempotencyKey = "continue",
                ResumeOfOperationId = resumed.OperationId
            }
        );
        Check(
            (await restored.WaitAsync(continued.OperationId)).State == "succeeded" && fake.LastId == accepted.OperationId,
            "resume-chain-keeps-execution-identity"
        );
        await restored.StopAsync();
        using var failingStore = new FaultStore();
        using var failingLane = new SemaphoreSlim(0, 1);
        var observed = new TestObserver();
        var failing = new PageOperationService(
            failingStore,
            new FixtureExecutor(),
            failingLane,
            observed
        );
        var pending = await failing.AcceptAsync(command);
        failingStore.Fail = true;
        failingLane.Release();
        var failure = await failing.WaitAsync(pending.OperationId)
            .WaitAsync(
                TimeSpan.FromSeconds(5)
            );
        Check(
            failure.State == "interrupted" && failure.ErrorCode == "STORE_UNAVAILABLE" && observed.Last?.State == "interrupted",
            "store-failure-notifies-terminal-and-does-not-hang"
        );
        bool rejected = false;
        try
        {
            await failing.AcceptAsync(
                command with
                {
                    IdempotencyKey = "after-failure"
                }
            );
        }
        catch (OperationRejectedException ex)
        {
            rejected = ex.Code == "STORE_UNAVAILABLE";
        }

        Check(rejected, "store-failure-prevents-new-writes");
        await failing.StopAsync();
    }
    else if (args[0] == "--committed")
    {
        // 已有隔离实例的提交后故障注入：仅修改测试文件，finally 恢复，绝不访问日常数据。
        using var rootLock = new FileStream(
            Path.Combine(root, ".launcher.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        using var source = new MinecraftService(
            new AppPaths(root)
        );
        using var store = new FileOperationStore(root, "page-execution-v1");
        var row = store.ReadAll()
            .Select(
                x => JsonSerializer.Deserialize<Pcl.Core.Adapters.Pages.PrivatePageRecord>(x)!
            )
            .First(
                x => x.Kind == "plan-used"
            );
        var used = row.Payload.Deserialize<Pcl.Core.Adapters.Pages.UsedComponentPlan>()!;
        var adapter = new Pcl.Core.Adapters.Pages.PageExecutionAdapter(source, store);
        var request = new PageInput(
            "component.apply",
            used.Plan.Source.Id,
            null,
            null,
            null,
            false,
            null,
            row.Snapshot.OperationId
        );
        var bytes = await adapter.ExecuteAsync(
            used.ExecutionId,
            request,
            new Progress<InstallProgress>(),
            CancellationToken.None
        );
        var result = JsonSerializer.Deserialize<ComponentResult>(bytes, wire)!;
        Check(
            result.ProfileId == "modified-" + used.ExecutionId[3..],
            "committed-receipt-rejoins-same-verified-instance"
        );
        var target = InstanceRegistry.List(source)
            .Single(
                x => x.Id == result.ProfileId
            );
        var marker = used.Plan.Files.First(
            x => x.RelativePath.StartsWith("page-contract-")
        );
        var path = Path.Combine(target.GameDirectory, marker.RelativePath);
        var original = File.ReadAllBytes(path);
        try
        {
            File.WriteAllText(path, "fixture damaged published copy");
            bool rejected = false;
            try
            {
                await adapter.ExecuteAsync(
                    used.ExecutionId,
                    request,
                    new Progress<InstallProgress>(),
                    CancellationToken.None
                );
            }
            catch (OperationInstallException ex)
            {
                rejected = ex.Code == "COMPONENT_COMMIT_REVIEW_REQUIRED";
            }

            Check(
                rejected && Directory.Exists(target.GameDirectory),
                "changed-published-copy-refused-and-preserved"
            );
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }

        var journal = File.ReadAllBytes(result.Receipt);
        try
        {
            File.WriteAllText(result.Receipt, "{\"status\":\"prepared\"}");
            bool rejected = false;
            try
            {
                await adapter.ExecuteAsync(
                    used.ExecutionId,
                    request,
                    new Progress<InstallProgress>(),
                    CancellationToken.None
                );
            }
            catch (OperationInstallException ex)
            {
                rejected = ex.Code == "COMPONENT_COMMIT_REVIEW_REQUIRED";
            }

            Check(
                rejected && Directory.Exists(target.GameDirectory),
                "unconfirmed-commit-refused-without-overwrite"
            );
        }
        finally
        {
            File.WriteAllBytes(result.Receipt, journal);
        }
    }
    else if (args[0] == "--components")
    {
        var executable = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE") ?? Path.GetFullPath(".tooling/dotnet/dotnet");
        var assembly = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE")is null
            ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
            : "";
        await using var client = new HostClient(executable, assembly, root, reconnectable: true);
        await client.HelloAsync();
        var pages = new HostPageOperations(client);
        async Task<PageSnapshot> Run(PageCommand command)
        {
            var accepted = await pages.AcceptAsync(command);
            var row = await pages.WaitAsync(accepted.OperationId)
                .WaitAsync(
                    TimeSpan.FromMinutes(5)
                );
            if (row.State != "succeeded")
            {
                throw new Exception(command.Method + ":" + row.State + ":" + row.ErrorCode);
            }

            return row;
        }

        PageCommand Command(string method) => new(
            Guid.NewGuid()
                .ToString("N"),
            method,
            InstanceId: "1.21.1",
            Loader: "fabric"
        );
        var versions = await Run(
            Command("component.versions")
        );
        var version = versions.Result!.Value.Deserialize<string[]>(wire)!.First();
        var preview = await Run(
            Command("component.preview")with
            {
                Version = version,
                DisableMods = true
            }
        );
        var plan = preview.Result!.Value.Deserialize<ComponentPreview>(wire)!;
        Check(plan.CanApply, "real-fabric-plan-created");
        var apply = Command("component.apply")with
        {
            PlanId = plan.PlanId
        };
        var first = await pages.AcceptAsync(apply);
        Check(
            !(await pages.GetAsync(first.OperationId)).Terminal,
            "component-operation-active-before-host-crash"
        );
        using (var process = Process.GetProcessById(client.HostPid))
        {
            process.Kill();
            await process.WaitForExitAsync();
        }

        await client.DisconnectAsync();
        await client.ReconnectAsync();
        Check(
            (await pages.GetAsync(first.OperationId)).State == "interrupted",
            "component-receipt-interrupted-after-real-crash"
        );
        var resumed = await Run(
            apply with
            {
                IdempotencyKey = Guid.NewGuid()
                    .ToString("N"),
                ResumeOfOperationId = first.OperationId
            }
        );
        var result = resumed.Result!.Value.Deserialize<ComponentResult>(wire)!;
        Check(
            result.ProfileId == "modified-" + first.OperationId[3..] && File.Exists(result.Backup) && File.Exists(result.Receipt),
            "fabric-component-resume-publishes-stable-instance-and-backup"
        );
        var duplicate = await pages.AcceptAsync(apply);
        Check(
            duplicate.OperationId == first.OperationId && duplicate.Replayed,
            "component-original-idempotency-replay"
        );
        var reused = await pages.AcceptAsync(
            apply with
            {
                IdempotencyKey = Guid.NewGuid()
                    .ToString("N")
            }
        );
        Check(
            (await pages.WaitAsync(reused.OperationId)).ErrorCode == "COMPONENT_PLAN_ALREADY_APPLIED",
            "consumed-plan-rejected-after-host-restart"
        );
        var instances = (await Run(
            Command("instance.list")
        )).Result!.Value.Deserialize<ComponentSource[]>(wire)!;
        Check(
            instances.Single(
                x => x.Id == result.ProfileId
            )
                .Loader == "fabric" && instances.Any(
                x => x.Id == "1.21.1" && x.Loader == "vanilla"
            ),
            "source-vanilla-preserved-and-new-fabric-registered"
        );
        // 用 Host 中的原登录/启动链路验证新副本，图形初始化后正常请求结束托管游戏。
        var accounts = await client.Call<AccountSnapshot>(
            "account.snapshot",
            new { }
        );
        var account = accounts.Accounts.First(
            x => x.Kind == 0
        );
        var launch = await client.Launches.AcceptAsync(
            new(
                Guid.NewGuid()
                    .ToString("N"),
                account.Id,
                result.ProfileId
            )
        );
        var started = await client.Launches.WaitAsync(launch.OperationId)
            .WaitAsync(
                TimeSpan.FromMinutes(3)
            );
        Check(started.State == "succeeded", "adjusted-fabric-instance-launches-through-host");
        var game = started.Result!;
        try
        {
            var details = await client.Call<GameDetails>(
                "game.details",
                new
                {
                    gameProcessId = game.GameProcessId
                }
            );
            bool rendered = false;
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                var text = File.Exists(details.LogFile)
                    ? File.ReadAllText(details.LogFile)
                    : "";
                if (text.Contains("Created:") && text.Contains("atlas"))
                {
                    rendered = true;
                    break;
                }

                if ((await client.Launches.GetGameAsync(game.GameProcessId)).State == "exited")
                {
                    break;
                }

                await Task.Delay(250);
            }

            Check(rendered, "adjusted-fabric-game-renderer-initialized");
        }
        finally
        {
            await client.Launches.StopGameAsync(game.GameProcessId);
        }

        while ((await client.Launches.GetGameAsync(game.GameProcessId)).State != "exited")
        {
            await Task.Delay(100);
        }

        // 原版移除加载器同样通过 Host 计划与执行，用新 Fabric 副本作为来源。
        var removePreview = await Run(
            new(
                Guid.NewGuid()
                    .ToString("N"),
                "component.preview",
                InstanceId: result.ProfileId,
                Loader: "vanilla",
                DisableMods: true
            )
        );
        var removePlan = removePreview.Result!.Value.Deserialize<ComponentPreview>(wire)!;
        var removed = await Run(
            new(
                Guid.NewGuid()
                    .ToString("N"),
                "component.apply",
                InstanceId: result.ProfileId,
                PlanId: removePlan.PlanId
            )
        );
        var vanilla = removed.Result!.Value.Deserialize<ComponentResult>(wire)!;
        var after = (await Run(
            Command("instance.list")
        )).Result!.Value.Deserialize<ComponentSource[]>(wire)!;
        Check(
            after.Single(
                x => x.Id == vanilla.ProfileId
            )
                .Loader == "vanilla" && after.Any(
                x => x.Id == result.ProfileId
            ),
            "host-removes-loader-in-new-copy-and-preserves-fabric-source"
        );
        await client.ShutdownAsync();
    }
    else
    {
        var executable = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE") ?? Path.GetFullPath(".tooling/dotnet/dotnet");
        var assembly = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE")is null
            ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
            : "";
        await using var client = new HostClient(executable, assembly, root, reconnectable: true);
        var hello = await client.HelloAsync();
        Check(
            hello.Capabilities.Contains("page.operations.v1"),
            "real-host-page-capability"
        );
        var pages = new HostPageOperations(client);
        var catalogCommand = new PageCommand(
            Guid.NewGuid()
                .ToString("N"),
            "artifact.list",
            GameVersion: "1.21.1",
            Loader: "client"
        );
        var catalog = await pages.AcceptAsync(catalogCommand);
        await client.DisconnectAsync();
        var loaded = await pages.WaitAsync(catalog.OperationId)
            .WaitAsync(
                TimeSpan.FromMinutes(2)
            );
        Check(loaded.State == "succeeded", "catalog-event-recovers-after-disconnect");
        var file = loaded.Result!.Value.Deserialize<ArtifactChoice[]>(wire)!.Single();
        var command = new PageCommand(
            Guid.NewGuid()
                .ToString("N"),
            "artifact.download",
            SelectionId: file.SelectionId
        );
        var download = await pages.AcceptAsync(command);
        await pages.CancelAsync(download.OperationId);
        Check(
            (await pages.WaitAsync(download.OperationId)).State == "cancelled",
            "real-download-explicit-cancellation"
        );
        // 只终止本测试自己创建的 Host，在非终态落盘后检验下一进程安全续做。
        var interrupted = await pages.AcceptAsync(
            command with
            {
                IdempotencyKey = Guid.NewGuid()
                    .ToString("N"),
                ResumeOfOperationId = download.OperationId
            }
        );
        var before = await pages.GetAsync(interrupted.OperationId);
        Check(!before.Terminal, "crash-test-operation-is-active");
        var oldPid = client.HostPid;
        using (var process = Process.GetProcessById(oldPid))
        {
            process.Kill();
            await process.WaitForExitAsync();
        }

        await client.DisconnectAsync();
        await client.ReconnectAsync();
        Check(
            client.HostPid != oldPid && (await pages.GetAsync(interrupted.OperationId)).State == "interrupted",
            "real-host-crash-restores-interrupted-receipt"
        );
        var resumed = await pages.AcceptAsync(
            command with
            {
                IdempotencyKey = Guid.NewGuid()
                    .ToString("N"),
                ResumeOfOperationId = interrupted.OperationId
            }
        );
        var terminal = await pages.WaitAsync(resumed.OperationId)
            .WaitAsync(
                TimeSpan.FromMinutes(4)
            );
        Check(terminal.State == "succeeded", "real-download-resumes-after-host-restart");
        var export = terminal.Result!.Value.Deserialize<ArtifactExport>(wire)!;
        Check(
            export.Size > 16 * 1024 * 1024 && export.Verification == "official_sha1",
            "official-file-exceeds-json-resource-limit"
        );
        var handle = await client.OpenResourceAsync("artifact-export", resumed.OperationId);
        bool range = false;
        try
        {
            await client.ReadResourceAsync(handle.ResourceId, -1, 10);
        }
        catch (OperationRejectedException)
        {
            range = true;
        }

        Check(range, "controlled-file-invalid-range-rejected");
        await client.CloseResourceAsync(handle.ResourceId);
        bool unknown = false;
        try
        {
            await client.OpenResourceAsync("artifact-export", catalog.OperationId);
        }
        catch (OperationRejectedException)
        {
            unknown = true;
        }

        Check(unknown, "catalog-cannot-open-arbitrary-file");
        var target = Path.Combine(
            Path.GetDirectoryName(report)!,
            "live-export.jar"
        );
        // 导出进行中断开管道，重开受控句柄后从已校验偏移继续读取。
        var transfer = LauncherSession.ExportFileAsync(client, resumed.OperationId, export, target);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!transfer.IsCompleted && !Directory.EnumerateFiles(
            Path.GetDirectoryName(target)!,
            Path.GetFileName(target) + ".pcl-*"
        )
            .Any() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1);
        }

        Check(!transfer.IsCompleted, "transfer-active-before-disconnect");
        await client.DisconnectAsync();
        await transfer.WaitAsync(
            TimeSpan.FromMinutes(2)
        );
        using (var input = File.OpenRead(target))
        {
            Check(
                Convert.ToHexString(
                    SHA256.HashData(input)
                )
                    .Equals(export.Sha256, StringComparison.OrdinalIgnoreCase),
                "chunk-export-reconnect-and-full-hash"
            );
        }

        var cancelledTarget = target + ".cancelled";
        using (var cancellation = new CancellationTokenSource())
        {
            var attempt = LauncherSession.ExportFileAsync(client, resumed.OperationId, export, cancelledTarget, cancellation.Token);
            while (!attempt.IsCompleted && !Directory.EnumerateFiles(
                Path.GetDirectoryName(target)!,
                Path.GetFileName(cancelledTarget) + ".pcl-*"
            )
                .Any())
            {
                await Task.Delay(1);
            }

            cancellation.Cancel();
            bool cancelled = false;
            try
            {
                await attempt;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Check(
                cancelled && !File.Exists(cancelledTarget) && !Directory.EnumerateFiles(
                    Path.GetDirectoryName(target)!,
                    Path.GetFileName(cancelledTarget) + ".pcl-*"
                )
                    .Any(),
                "mid-export-cancel-cleans-partial-target"
            );
        }

        Check(
            (await pages.ListAsync()).Any(
                x => x.OperationId == resumed.OperationId && x.State == "succeeded"
            ),
            "paged-task-history-contains-resumed-task"
        );
        await client.ShutdownAsync();
    }

    File.WriteAllText(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "passed",
                checks
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
    File.WriteAllText(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "failed",
                checks,
                error = ex.ToString()
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

// 替身只验证调度和持久化语义，不计作真实安装/网络结果。
sealed class FixtureExecutor : IPageExecutor
{
    public int Calls;
    public string? LastId;
    public Task<byte[]> ExecuteAsync(string id, PageInput input, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        LastId = id;
        return Task.FromResult(
            "{}"u8.ToArray()
        );
    }
}

sealed class FaultStore : IOperationStore
{
    public bool Fail;
    public IReadOnlyList<byte[]> ReadAll() => [];
    public void Save(string id, byte[] value)
    {
        if (Fail)
        {
            throw new IOException("fixture disk unavailable");
        }
    }

    public void Dispose()
    {
    }
}

sealed class TestObserver : IOperationObserver
{
    public OperationChange? Last;
    public void Changed(OperationChange value)
    {
        Last = value;
    }
}
