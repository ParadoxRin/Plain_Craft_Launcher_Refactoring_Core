using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;
using Pcl.MinimalClient;

// 测试也不引用 Core/Ports/Application，不读写内部 receipt。每次必须用全新隔离目录。
if (args.Length != 5 || args[4] is not ("contract" or "live"))
{
    throw new ArgumentException("<host executable> <host dll or -> <fresh root> <report.json> <contract|live>");
}
var root = Path.GetFullPath(args[2]);
var report = Path.GetFullPath(args[3]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
{
    throw new InvalidOperationException("FRESH_ROOT_REQUIRED");
}
Directory.CreateDirectory(root);
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
var live = args[4] == "live";
var checks = new List<string>();
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var observed = new List<object>();
var ownedHosts = new List<Process>();
Process? ownedGame = null;
var failure = "";
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(live ? 25 : 3));
var ct = timeout.Token;
var executable = args[0] == "-" ? Environment.ProcessPath! : args[0];
await using var sample = new MinimalLauncher(executable, args[1] == "-" ? "" : args[1], root);
void Check(bool value, string name)
{
    if (!value)
    {
        throw new InvalidOperationException(name);
    }
    if (!checks.Contains(name))
    {
        checks.Add(name);
        Console.WriteLine("PASS " + name);
    }
}
void OwnHost()
{
    // 仅保留本轮隔离 Host 的 OS 句柄；不搜索或终止用户的其他 Host/Java 进程。
    ownedHosts.Add(Process.GetProcessById(sample.Client.HostPid));
}
async Task Reject(Func<Task> action, string code)
{
    try
    {
        await action();
    }
    catch (OperationRejectedException ex) when (ex.Code == code)
    {
        Check(true, "reject-" + code);
        return;
    }
    throw new InvalidOperationException("EXPECTED_" + code);
}
async Task<OperationSnapshot> Downloading(string id)
{
    while (true)
    {
        var row = await sample.Client.Installations.GetAsync(id, ct)
            ?? throw new InvalidOperationException("OPERATION_MISSING");
        if (row.Terminal)
        {
            throw new InvalidOperationException("CANCELLATION_WINDOW_MISSED_" + row.State + "_" + row.ErrorCode);
        }
        // 真实收到文件字节才取消/崩溃；不把排队取消冒充已产生 receipt 的安全续做。
        if (row.Progress is { Bytes: > 0, Total: > 0 })
        {
            observed.Add(new { phase = "download-before-interruption", row.State, row.Stage, row.Sequence,
                row.Progress.Bytes, row.Progress.Total });
            return row;
        }
        await Task.Delay(50, ct);
    }
}
async Task Rendered(GameDetails details)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(TimeSpan.FromMinutes(3));
    while (!ownedGame!.HasExited)
    {
        if (File.Exists(details.LogFile))
        {
            var text = await File.ReadAllTextAsync(details.LogFile, deadline.Token);
            if (text.Contains("Created:", StringComparison.Ordinal) && text.Contains("textures/atlas", StringComparison.Ordinal))
            {
                Check(true, details.Detached ? "detached-real-game-atlases-initialized" : "managed-real-game-atlases-initialized");
                return;
            }
        }
        await Task.Delay(200, deadline.Token);
    }
    throw new InvalidOperationException("GAME_EXITED_BEFORE_RENDERING");
}
try
{
    // 运行时与输出目录双重检查；测试进程和示例均不能通过传递依赖偷带 Core。
    var allowed = new HashSet<string> { "Pcl.Contract", "Pcl.Client", "Pcl.MinimalClient", "Pcl.MinimalClient.Tests" };
    foreach (var assembly in new[] { typeof(MinimalLauncher).Assembly, typeof(HostClient).Assembly,
        typeof(InstallCommand).Assembly, Assembly.GetExecutingAssembly() })
    {
        Check(assembly.GetReferencedAssemblies().All(x => !x.Name!.StartsWith("Pcl.") || allowed.Contains(x.Name)),
            "assembly-boundary-" + assembly.GetName().Name);
    }
    Check(!Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Any(path =>
        new[] { "Pcl.Core", "Pcl.Application", "Pcl.Ports", "Pcl.Domain", "Pcl.Legacy", "MeloongCore", "PCLCS" }
            .Contains(Path.GetFileNameWithoutExtension(path))), "output-has-no-business-assemblies");
    await sample.ConnectAsync(ct);
    OwnHost();
    var hostId = sample.Hello.HostInstanceId;
    var hostPid = sample.Client.HostPid;
    Check(sample.Hello.ProtocolVersion == 1 && sample.Hello.InstallationTarget is not null, "real-host-handshake-and-capabilities");
    Check((await sample.AccountsAsync(ct)).Accounts.Length == 0, "isolated-root-no-user-credentials");
    await Reject(async () => await sample.Client.Call<JsonElement>("arbitrary.execute", new { }, ct), "METHOD_NOT_SUPPORTED");
    await Reject(async () => await sample.Client.Call<HostHello>("host.hello", new { minimumVersion = 2, maximumVersion = 2 }, ct), "PROTOCOL_INCOMPATIBLE");
    await Reject(async () => await sample.Client.Installations.AcceptAsync(
        new("wrong-target", new("1.21.1", "vanilla", new("unsupported", "unsupported", "0"))), ct), "UNSUPPORTED_TARGET");
    var resource = await sample.Client.OpenResourceAsync("operations", ct: ct);
    Check((await sample.Client.ReadPageAsync(resource.ResourceId, 0, 10, ct)).Eof, "bounded-empty-operation-page");
    await sample.Client.CloseResourceAsync(resource.ResourceId, ct);

    var first = await sample.InstallAsync("1.21.1", "vanilla", "first-install", ct);
    // contract 模式尽快取消；live 模式必须先完成真实元数据计划并接收到下载字节。
    if (live)
    {
        await Downloading(first.OperationId);
    }
    else
    {
        await sample.Client.Installations.CancelAsync(first.OperationId, ct);
    }
    var replay = await sample.InstallAsync("1.21.1", "vanilla", "first-install", ct);
    Check(replay.Replayed && replay.OperationId == first.OperationId, "same-key-same-operation");
    await Reject(async () => await sample.InstallAsync("1.20.1", "vanilla", "first-install", ct), "IDEMPOTENCY_CONFLICT");
    await using (var subscription = await sample.Client.SubscribeAsync("operation", first.OperationId, ct))
    {
        await sample.ReconnectAsync(ct);
        Check(sample.Client.HostPid == hostPid && sample.Hello.HostInstanceId == hostId, "reconnect-retains-host-and-operation");
        await sample.Client.Installations.CancelAsync(first.OperationId, ct);
        long previous = -1;
        bool terminal = false;
        await foreach (var message in subscription.ReadAsync(ct))
        {
            Check(message.Sequence > previous && message.HostId == hostId, "subscription-watermark-increases");
            previous = message.Sequence;
            var row = message.Snapshot.Deserialize<OperationSnapshot>(json)!;
            if (row.Terminal)
            {
                Check(row.State == "cancelled", "cancel-reaches-terminal-through-restored-subscription");
                terminal = true;
                break;
            }
        }
        Check(terminal, "subscription-delivers-terminal");
    }
    await sample.RestartHostAsync(ct);
    OwnHost();
    Check(sample.Hello.HostInstanceId != hostId, "restart-creates-new-host");
    Check((await sample.Client.Installations.GetAsync(first.OperationId, ct))?.State == "cancelled", "cancelled-state-survives-host-restart");
    replay = await sample.InstallAsync("1.21.1", "vanilla", "first-install", ct);
    Check(replay.Replayed && replay.OperationId == first.OperationId, "idempotency-survives-host-restart");
    var account = await sample.AddOfflineAsync("MinimalClient", "add-offline", ct);
    Check(account.State == "succeeded", "offline-account-created-through-host-only");
    Check((await sample.AccountsAsync(ct)).Accounts is [{ Kind: 0, HasCredential: false }], "account-snapshot-is-public-and-offline");

    if (live)
    {
        var resumed = await sample.ResumeAsync(first.OperationId, "resume-cancelled", ct);
        Check(resumed.OperationId != first.OperationId, "resume-creates-linked-attempt");
        await Downloading(resumed.OperationId);
        hostId = sample.Hello.HostInstanceId;
        // 在独立测试进程中模拟真正 Host 崩溃，不使用正常 shutdown 伪装崩溃恢复。
        var crashed = ownedHosts.Last();
        crashed.Kill();
        await crashed.WaitForExitAsync(ct);
        await sample.ReconnectAsync(ct);
        OwnHost();
        Check(sample.Hello.HostInstanceId != hostId, "crashed-host-replaced");
        var interrupted = await sample.Client.Installations.GetAsync(resumed.OperationId, ct);
        Check(interrupted?.State == "interrupted" && interrupted.ErrorCode == "HOST_INTERRUPTED"
            && interrupted.ResumeOfOperationId == first.OperationId, "crash-restores-interrupted-receipt-state");
        var replayInterrupted = await sample.ResumeAsync(first.OperationId, "resume-cancelled", ct);
        Check(replayInterrupted.Replayed && replayInterrupted.OperationId == resumed.OperationId, "crash-does-not-silently-rerun-old-key");
        var final = await sample.ResumeAsync(resumed.OperationId, "resume-interrupted", ct);
        var installed = await sample.Client.Installations.WaitAsync(final.OperationId, ct);
        Check(installed.State == "succeeded" && installed.Result is not null
            && installed.ResumeOfOperationId == resumed.OperationId, "real-install-cancel-reconnect-crash-resume-completes");
        Check((await sample.Client.Installations.GetAsync(first.OperationId, ct))?.State == "cancelled"
            && (await sample.Client.Installations.GetAsync(resumed.OperationId, ct))?.State == "interrupted", "resume-preserves-parent-history");
        await Reject(async () => await sample.ResumeAsync(final.OperationId, "illegal-resume-success", ct), "RESUME_NOT_ALLOWED");
        var guidance = await sample.GuidanceAsync("trial", ct);
        Console.WriteLine("原作者赞助：" + guidance.SponsorUrl + "；Minecraft 正版购买：" + guidance.PurchaseUrl);
        Check(guidance.ShowSponsor && guidance.ShowPurchase, "host-guidance-preserved");
        var rejected = await sample.Client.Launches.AcceptAsync(new("without-guidance", "trial", installed.Result!.Id), ct);
        var rejectedResult = await sample.Client.Launches.WaitAsync(rejected.OperationId, ct);
        Check(rejectedResult.State == "failed" && rejectedResult.ErrorCode == "LAUNCH_GUIDANCE_REQUIRED", "launch-without-guidance-rejected");
        var consent = new LaunchGuidanceConsent("zh-CN", true, true, true);
        foreach (var detached in new[] { false, true })
        {
            var key = detached ? "detached-demo" : "managed-demo";
            var accepted = await sample.LaunchAsync(installed.Result.Id, "trial", key, detached, consent, ct);
            var launched = await sample.Client.Launches.WaitAsync(accepted.OperationId, ct);
            Check(launched.State == "succeeded" && launched.Result is not null, key + "-started");
            ownedGame = Process.GetProcessById(launched.Result!.Pid);
            var details = await sample.Client.Call<GameDetails>("game.details", new { gameProcessId = launched.Result.GameProcessId }, ct);
            await Rendered(details);
            var again = await sample.LaunchAsync(installed.Result.Id, "trial", key, detached, consent, ct);
            Check(again.Replayed && again.OperationId == accepted.OperationId, key + "-idempotent-no-second-game");
            if (!detached)
            {
                await sample.ReconnectAsync(ct);
                Check(sample.Client.Attachment!.Games.Any(x => x.Details.Pid == ownedGame.Id), "managed-game-restored-in-session-attachment");
                await sample.Client.Launches.StopGameAsync(details.GameProcessId, ct);
                await ownedGame.WaitForExitAsync(ct);
                Check((await sample.Client.Launches.GetGameAsync(details.GameProcessId, ct)).State == "exited", "managed-game-stop-observed-by-host");
                ownedGame.Dispose();
                ownedGame = null;
            }
            else
            {
                await sample.Client.ShutdownAsync(ct);
                await Task.Delay(2000, ct);
                Check(!ownedGame.HasExited && ownedHosts.Last().HasExited, "detached-game-survives-host-exit");
                // 图形初始化已完成；自动验收结束只终止本轮持有的测试游戏，不计作正常保存退出。
                ownedGame.Kill();
                await ownedGame.WaitForExitAsync(ct);
                ownedGame.Dispose();
                ownedGame = null;
            }
        }
    }
    else
    {
        await sample.Client.ShutdownAsync(ct);
    }
    // 新 Host 重新取得同一根锁；账户仅恢复公开离线索引，未访问真人钥匙串。
    await sample.ReconnectAsync(ct);
    OwnHost();
    Check((await sample.AccountsAsync(ct)).Accounts is [{ Name: "MinimalClient", Kind: 0 }], "offline-account-restored-and-root-lock-reacquired");
    await sample.Client.ShutdownAsync(ct);
    Check(ownedHosts.All(p => p.HasExited), "all-owned-hosts-exited");
    Check(AppDomain.CurrentDomain.GetAssemblies().All(a => !a.GetName().Name!.StartsWith("Pcl.")
        || allowed.Contains(a.GetName().Name!)), "runtime-loaded-assemblies-remain-contract-only");
}
catch (Exception ex)
{
    failure = ex is OperationRejectedException rejected ? rejected.Code
        : ex is InvalidOperationException ? ex.Message : ex.GetType().Name;
    Console.Error.WriteLine("FAIL " + failure);
    Environment.ExitCode = 1;
}
finally
{
    // 成功路径应已收尾。失败时先请求正常关闭，超时才清理本轮已记录的 OS 进程句柄。
    if (ownedGame is not null)
    {
        if (!ownedGame.HasExited)
        {
            ownedGame.Kill();
            await ownedGame.WaitForExitAsync();
        }
        ownedGame.Dispose();
    }
    if (ownedHosts.Any(p => !p.HasExited))
    {
        try
        {
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await sample.Client.ShutdownAsync(close.Token);
        }
        catch (Exception)
        {
            foreach (var host in ownedHosts.Where(p => !p.HasExited))
            {
                host.Kill();
                await host.WaitForExitAsync();
            }
        }
    }
    foreach (var host in ownedHosts)
    {
        host.Dispose();
    }
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
    {
        status = failure.Length == 0 ? "passed" : "failed",
        scope = live ? "Real official vanilla 1.21.1 download, receipt recovery, Java and two demo game processes; renderer only, not world/save acceptance"
            : "Real Host process and public contracts; no complete game install, credentials or GUI",
        platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        checks, observed, error = failure.Length == 0 ? null : failure
    }, json));
}
