using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Client;
using Pcl.Client.Updates;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Updates;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Accounts;

// 受控失败候选：探针成功但业务启动退出，用于实际握手失败回滚验收。
if (args.SequenceEqual(new[] { "--update-probe" }))
{
    Console.WriteLine(JsonSerializer.Serialize(new HostHello(1, "fixture", ["host.update.prepare.v1"], 1048576,
        Build: new("0.43.0", 43000, 1, HostUpdateStore.Runtime))));
    return;
}
if (args.FirstOrDefault() == "--data-root")
{
    Environment.ExitCode = 19;
    return;
}
if (args.FirstOrDefault() == "--survivor")
{
    await Task.Delay(5000);
    File.WriteAllText(args[1], "alive");
    return;
}
if (args.Length is not (4 or 5))
{
    throw new ArgumentException("<old-publish> <new-publish> <fresh-root> <result.json>");
}
var oldHost = Path.GetFullPath(args[0]);
var newHost = Path.GetFullPath(args[1]);
var root = Path.GetFullPath(args[2]);
if (Directory.Exists(root))
{
    throw new InvalidOperationException("FRESH_ROOT_REQUIRED");
}
Directory.CreateDirectory(root);
var checks = new List<string>();
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
using var key = RSA.Create(2048);
var transport = new FixtureTransport();
using var http = new HttpClient(transport);
var policy = new HostUpdatePolicy("https://updates.example/feed.json", key.ExportSubjectPublicKeyInfoPem(),
    RequiredCapabilities: ["host.update.prepare.v1"]);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = timeout.Token;
var clients = new List<HostClient>();
void Check(bool ok, string name)
{
    if (!ok)
    {
        throw new Exception(name);
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task Reject(Func<Task> action, string name)
{
    try
    {
        await action();
    }
    catch (Exception ex) when (ex is InvalidDataException or CryptographicException or IOException or FormatException)
    {
        Check(true, name);
        return;
    }
    throw new Exception("NOT_REJECTED_" + name);
}
HostClient Client(string name)
{
    var client = new HostClient(Environment.ProcessPath!, Path.Combine(oldHost, "Pcl.Host.dll"),
        Path.Combine(root, name + "-data"), true);
    client.ConfigureUpdates(policy, Path.Combine(root, name + "-updates"), http);
    clients.Add(client);
    return client;
}
async Task<HostHello> Probe(string dll)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true };
    start.ArgumentList.Add(dll);
    start.ArgumentList.Add("--update-probe");
    using var process = Process.Start(start)!;
    var text = await process.StandardOutput.ReadToEndAsync(ct);
    await process.WaitForExitAsync(ct);
    Check(process.ExitCode == 0, "real-publish-probe-" + Path.GetFileName(Path.GetDirectoryName(dll)));
    return JsonSerializer.Deserialize<HostHello>(text, json)!;
}
(byte[] Zip, HostUpdateManifest Manifest) Package(string directory, HostHello hello, string entry = "Pcl.Host.dll")
{
    var files = new List<HostUpdateFile>();
    using var output = new MemoryStream();
    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(directory, file).Replace('\\', '/');
            var data = File.ReadAllBytes(file);
            using var content = zip.CreateEntry(path).Open();
            content.Write(data);
            files.Add(new(path, data.Length, HostUpdateStore.Hash(data), false));
        }
    }
    var bytes = output.ToArray();
    return (bytes, new(1, "stable", hello.Build!, 1, hello.Capabilities, DateTimeOffset.UtcNow.AddDays(2),
        "https://updates.example/host.zip", bytes.Length, HostUpdateStore.Hash(bytes), entry, true, files.ToArray()));
}
byte[] Envelope(HostUpdateManifest m)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(m);
    return JsonSerializer.SerializeToUtf8Bytes(new HostUpdateEnvelope(Convert.ToBase64String(payload),
        Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
}
void Feed(HostUpdateManifest manifest, byte[] bytes)
{
    transport.Feed = Envelope(manifest);
    transport.Package = bytes;
}
string? error = null;
try
{
    var hello = await Probe(Path.Combine(newHost, "Pcl.Host.dll"));
    Check(hello.Build!.Sequence == 42000 && hello.Build.Version == "0.42.0", "build-version-and-sequence");
    var (zip, manifest) = Package(newHost, hello);
    Feed(manifest, zip);
    var client = Client("main");
    var old = await client.HelloAsync(ct);
    Check(old.Build!.Sequence == 41000, "real-old-host-started");
    // 不经过 Core：真实 Host 创建离线账户，更新前后索引应保持一致。
    var pages = new HostPageOperations(client);
    var accepted = await pages.AcceptAsync(new("update-offline", "workspace.account.addOffline", PayloadJson: "{\"name\":\"UpdateFixture\"}"), ct);
    await pages.WaitAsync(accepted.OperationId, ct);
    var before = await client.Call<JsonElement>("account.snapshot", new { }, ct);
    var preserved = Path.Combine(root, "main-data", "update-skin-preservation-fixture.bin");
    File.WriteAllBytes(preserved, [1, 9, 7, 3]);
    Check((await client.CheckForUpdatesAsync(ct)).State == "staged", "signed-package-staged");
    Check((await client.HelloAsync(ct)).HostInstanceId == old.HostInstanceId, "staging-does-not-restart-host");
    var activeInstall = await client.Installations.AcceptAsync(new("busy", new("1.21.1", "vanilla", old.InstallationTarget!)), ct);
    Check((await client.ApplyUpdateWhenIdleAsync(ct)).Code == "HOST_BUSY", "active-install-defers-update");
    await client.Installations.CancelAsync(activeInstall.OperationId, ct);
    while (!(await client.Installations.GetAsync(activeInstall.OperationId, ct))!.Terminal)
    {
        await Task.Delay(20, ct);
    }
    var survivorStart = new ProcessStartInfo(Environment.ProcessPath!);
    survivorStart.ArgumentList.Add(typeof(Program).Assembly.Location);
    survivorStart.ArgumentList.Add("--survivor");
    survivorStart.ArgumentList.Add(Path.Combine(root, "survivor.txt"));
    using var survivor = Process.Start(survivorStart)!;
    var applied = await client.ApplyUpdateWhenIdleAsync(ct);
    Check(applied.State == "current", "idle-switch-confirmed");
    var current = await client.HelloAsync(ct);
    Check(current.Build!.Sequence == 42000 && current.HostInstanceId != old.HostInstanceId, "real-new-host-handshake");
    Check((await client.Call<JsonElement>("account.snapshot", new { }, ct)).GetRawText() == before.GetRawText(), "account-metadata-unchanged");
    Check(File.ReadAllBytes(preserved).SequenceEqual(new byte[] { 1, 9, 7, 3 }), "unrelated-data-file-unchanged");
    Check((await client.Installations.GetAsync(activeInstall.OperationId, ct))!.Terminal, "receipt-survives-core-update");
    await survivor.WaitForExitAsync(ct);
    Check(File.ReadAllText(Path.Combine(root, "survivor.txt")) == "alive", "unrelated-independent-process-survives-switch");
    Check((await client.CheckForUpdatesAsync(ct)).State == "current", "same-release-idempotent");

    // 已签名仍必须验证平台、协议、数据格式、能力与版本，不以签名代替兼容性。
    foreach (var (bad, name) in new (HostUpdateManifest, string)[]
    {
        (manifest with { Build = manifest.Build with { Runtime = "wrong-architecture" } }, "wrong-runtime"),
        (manifest with { Protocol = 2 }, "wrong-protocol"),
        (manifest with { Build = manifest.Build with { DataEpoch = 2 } }, "data-migration-rejected"),
        (manifest with { Capabilities = [] }, "missing-capability"),
        (manifest with { Channel = "preview" }, "wrong-channel"),
        (manifest with { Expires = DateTimeOffset.UtcNow.AddDays(-1) }, "expired-feed"),
        (manifest with { Build = manifest.Build with { Sequence = 40999 } }, "downgrade-rejected"),
        (manifest with { PackageUrl = "http://updates.example/host.zip" }, "insecure-package-url"),
        (manifest with { Files = [new("../escape", 0, new string('0', 64), false)] }, "path-traversal-rejected"),
        (manifest with { Files = manifest.Files.Concat([manifest.Files[0] with { Path = manifest.Files[0].Path.ToUpperInvariant() }]).ToArray() }, "case-collision-rejected")
    })
    {
        Feed(bad, zip);
        await Reject(async () => await client.CheckForUpdatesAsync(ct), name);
    }
    Feed(manifest, zip);
    var signed = JsonSerializer.Deserialize<HostUpdateEnvelope>(transport.Feed)!;
    transport.Feed = JsonSerializer.SerializeToUtf8Bytes(signed with { Signature = Convert.ToBase64String(new byte[256]) });
    await Reject(async () => await client.CheckForUpdatesAsync(ct), "forged-signature-rejected");
    Check((await client.HelloAsync(ct)).HostInstanceId == current.HostInstanceId, "invalid-updates-preserve-current-host");

    // 原始zip也要校验：清单合法并不意味着归档没有多余文件、链接或路径穿越。
    foreach (var attack in new[] { "extra", "traversal", "symlink" })
    {
        var attackClient = Client("archive-" + attack);
        using var stream = new MemoryStream();
        stream.Write(zip);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, true))
        {
            var injected = archive.CreateEntry(attack == "traversal" ? "../escape" : "unexpected");
            if (attack == "symlink")
            {
                injected.ExternalAttributes = 0xA000 << 16;
            }
        }
        var bytes = stream.ToArray();
        Feed(manifest with { PackageSize = bytes.Length, PackageSha256 = HostUpdateStore.Hash(bytes) }, bytes);
        await Reject(async () => await attackClient.CheckForUpdatesAsync(ct), "archive-" + attack + "-rejected");
    }
    var damaged = Client("damaged-stage");
    Feed(manifest, zip);
    await damaged.HelloAsync(ct);
    await damaged.CheckForUpdatesAsync(ct);
    var damagedFolder = Directory.GetDirectories(Path.Combine(root, "damaged-stage-updates")).Single();
    File.AppendAllText(Path.Combine(damagedFolder, "Pcl.Host.dll"), "tampered");
    Check((await damaged.ApplyUpdateWhenIdleAsync(ct)).Code == "CANDIDATE_PROBE_FAILED", "tampered-staged-files-rejected-before-execution");
    Check((await damaged.HelloAsync(ct)).Build!.Sequence == 41000, "damaged-stage-keeps-old-host");

    // 新作用域测试损坏下载，不能改变旧包；清单和zip可独立注入错误。
    var badClient = Client("bad-download");
    Feed(manifest, zip.ToArray());
    transport.Package[20] ^= 1;
    await Reject(async () => await badClient.CheckForUpdatesAsync(ct), "corrupt-package-hash");
    Feed(manifest, zip[..^1]);
    await Reject(async () => await badClient.CheckForUpdatesAsync(ct), "truncated-download");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try
        {
            await badClient.CheckForUpdatesAsync(cancelled.Token);
            throw new Exception("cancel not observed");
        }
        catch (OperationCanceledException)
        {
            Check(true, "cancelled-update-does-not-activate");
        }
    }
    Check((await badClient.HelloAsync(ct)).Build!.Sequence == 41000, "failed-staging-retains-bundled-host");

    // 待更新持久化：退出客户端以后，下次连接自动尝试，并保留相同账户数据根。
    var restart = Client("restart");
    Feed(manifest, zip);
    await restart.HelloAsync(ct);
    await restart.CheckForUpdatesAsync(ct);
    await restart.ShutdownAsync(ct);
    await restart.DisposeAsync();
    var restarted = Client("restart");
    Check((await restarted.HelloAsync(ct)).Build!.Sequence == 42000, "pending-applied-on-next-start");

    // 参考客户端真实会话自动调度，无需显式update-check/update-apply。
    Feed(manifest, zip);
    var automaticClient = Client("automatic");
    await using (var session = new LauncherSession(automaticClient))
    {
        await session.InitializeAsync();
        var until = DateTimeOffset.UtcNow.AddSeconds(40);
        while (automaticClient.UpdateStatus.State != "current" && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(100, ct);
        }
        Check(automaticClient.UpdateStatus.State == "current", "launcher-session-automatically-checks-and-applies");
        using var ownedHost = Process.GetProcessById(automaticClient.HostPid);
        await session.ShutdownAsync();
        await Task.Delay(300, ct);
        Check(ownedHost.HasExited, "session-close-joins-update-loop-without-restarting-host");
    }

    // 签名有效且probe成功，但实际Host启动失败：必须回到已验证版本。
    var fakeHello = new HostHello(1, "fixture", ["host.update.prepare.v1"], 1048576,
        Build: new("0.43.0", 43000, 1, HostUpdateStore.Runtime));
    var (badZip, badManifest) = Package(AppContext.BaseDirectory, fakeHello, "Pcl.HostUpdates.Tests.dll");
    Feed(badManifest, badZip);
    await client.CheckForUpdatesAsync(ct);
    Check((await client.ApplyUpdateWhenIdleAsync(ct)).State == "rolled-back", "failed-real-start-rolls-back");
    Check((await client.HelloAsync(ct)).Build!.Sequence == 42000, "previous-host-reconnected-after-rollback");
    Check((await client.Call<JsonElement>("account.snapshot", new { }, ct)).GetRawText() == before.GetRawText(), "rollback-preserves-account-index");
    Check((await client.CheckForUpdatesAsync(ct)).Code == "PREVIOUSLY_FAILED", "failed-release-not-retried-forever");

    if (args.Length == 5)
    {
        // 可选Mac图形验收只复用先前 live harness 创建的隔离试玩目录。
        var gameRoot = Path.GetFullPath(args[4]);
        if (!File.Exists(Path.Combine(gameRoot, ".host-update-game-fixture")))
        {
            throw new InvalidDataException("OWNED_GAME_FIXTURE_REQUIRED");
        }
        Feed(manifest, zip);
        var gameClient = new HostClient(Environment.ProcessPath!, Path.Combine(oldHost, "Pcl.Host.dll"), gameRoot, true);
        gameClient.ConfigureUpdates(policy, Path.Combine(root, "game-updates"), http);
        clients.Add(gameClient);
        await gameClient.HelloAsync(ct);
        var installed = (await gameClient.Installations.ListAsync(ct)).First(x => x.State == "succeeded" && x.Result is not null);
        await gameClient.Call<JsonElement>("game.guidance", new LaunchGuidanceRequest("trial", "zh-CN"), ct);
        var launch = await gameClient.Launches.AcceptAsync(new("update-game-" + Guid.NewGuid().ToString("N"), "trial", installed.Result!.Id,
            true, new LaunchGuidanceConsent("zh-CN", true, true, true)), ct);
        var started = await gameClient.Launches.WaitAsync(launch.OperationId, ct);
        Check(started.State == "succeeded", "real-detached-minecraft-started");
        using var game = Process.GetProcessById(started.Result!.Pid);
        try
        {
            var details = await gameClient.Call<GameDetails>("game.details", new { gameProcessId = started.Result.GameProcessId }, ct);
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            bool rendered = false;
            while (!game.HasExited && DateTimeOffset.UtcNow < deadline)
            {
                var log = File.Exists(details.LogFile) ? await File.ReadAllTextAsync(details.LogFile, ct) : "";
                if (log.Contains("Created:") && log.Contains("textures/atlas"))
                {
                    rendered = true;
                    break;
                }
                await Task.Delay(200, ct);
            }
            Check(rendered, "real-game-renderer-initialized");
            await gameClient.CheckForUpdatesAsync(ct);
            Check((await gameClient.ApplyUpdateWhenIdleAsync(ct)).Code == "HOST_BUSY", "known-detached-game-defers-live-update");
            await gameClient.ShutdownAsync(ct);
            await gameClient.DisconnectAsync();
            Check(!game.HasExited, "real-game-survives-old-host-exit");
            Check((await gameClient.HelloAsync(ct)).Build!.Sequence == 42000, "next-start-updates-with-independent-game-alive");
            await Task.Delay(2000, ct);
            Check(!game.HasExited, "real-game-survives-updated-host-start");
        }
        finally
        {
            if (!game.HasExited)
            {
                game.Kill();
                await game.WaitForExitAsync();
            }
        }
    }

    // 持久trial模拟进程在指针切换后崩溃；新存储实例恢复旧指针。
    var crashRoot = Path.Combine(root, "crash-store");
    Feed(manifest, zip);
    using (var store = new HostUpdateStore(crashRoot, Path.Combine(root, "crash-data"), policy, http))
    {
        await store.StageAsync(ct);
        await store.BeginAsync(Environment.ProcessPath!, ct);
        Check(store.InTrial, "trial-written-before-activation");
    }
    using (var recovered = new HostUpdateStore(crashRoot, Path.Combine(root, "crash-data"), policy, http))
    {
        Check(recovered.Active is null && !recovered.InTrial, "unconfirmed-trial-rolls-back-on-reopen");
        await Reject(() => Task.Run(() => new HostUpdateStore(crashRoot, Path.Combine(root, "crash-data"), policy, http).Dispose()), "exclusive-update-store-lock");
    }
    await Reject(() => Task.Run(() => new HostUpdateStore(crashRoot, Path.Combine(root, "other-data"), policy, http).Dispose()), "scope-reuse-rejected");

    // 真正遗留一个未确认的候选Host；重开客户端必须先空闲退出它，再使用回退版本。
    var orphanRoot = Path.Combine(root, "orphan-data");
    var orphanStore = Path.Combine(root, "orphan-updates");
    Directory.CreateDirectory(orphanRoot);
    string candidateDll;
    Feed(manifest, zip);
    using (var store = new HostUpdateStore(orphanStore, orphanRoot, policy, http))
    {
        await store.StageAsync(ct);
        await store.BeginAsync(Environment.ProcessPath!, ct);
        candidateDll = store.Selection(Environment.ProcessPath!)!.Assembly;
    }
    var orphanStart = new ProcessStartInfo(Environment.ProcessPath!);
    foreach (var argument in new[] { candidateDll, "--data-root", orphanRoot, "--pipe" })
    {
        orphanStart.ArgumentList.Add(argument);
    }
    using var orphanHost = Process.Start(orphanStart)!;
    try
    {
        await Task.Delay(800, ct);
        Check(!orphanHost.HasExited, "unconfirmed-candidate-really-left-running");
        var replacement = new HostClient(Environment.ProcessPath!, Path.Combine(oldHost, "Pcl.Host.dll"), orphanRoot, true);
        replacement.ConfigureUpdates(policy, orphanStore, http);
        clients.Add(replacement);
        Check((await replacement.HelloAsync(ct)).Build!.Sequence == 41000, "orphan-trial-retired-and-bundled-host-restored");
        Check(orphanHost.HasExited, "orphan-exited-without-pid-kill");
    }
    finally
    {
        if (!orphanHost.HasExited)
        {
            orphanHost.Kill();
            await orphanHost.WaitForExitAsync();
        }
    }

}
catch (Exception ex)
{
    error = ex.GetType().Name + ": " + ex.Message;
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
finally
{
    foreach (var client in clients)
    {
        try
        {
            if (client.IsConnected)
            {
                await client.ShutdownAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
            await client.DisposeAsync();
        }
        catch (Exception)
        {
        }
    }
    File.WriteAllText(args[3], JsonSerializer.Serialize(new { status = error is null ? "passed" : "failed", checks, error,
        scope = args.Length == 5 ? "Signed transport fixtures, actual Host update/rollback, real Minecraft renderer and independent survival; no world/save or Microsoft login" : "Signed transport fixtures, actual published Host processes, independent synthetic child; no real Microsoft login or Minecraft world" }, json));
}

sealed class FixtureTransport : HttpMessageHandler
{
    public byte[] Feed = [];
    public byte[] Package = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("feed.json") ? Feed : Package)
        });
    }
}
