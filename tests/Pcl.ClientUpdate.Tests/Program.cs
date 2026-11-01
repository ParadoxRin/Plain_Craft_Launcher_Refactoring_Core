using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Client.SelfUpdate;

if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Actual macOS acceptance required");
if (args.Length != 4) throw new ArgumentException("<base.app> <fixtures> <private-fixture-key> <fresh-root>");
var root = Path.GetFullPath(args[3]);
if (Directory.Exists(root)) throw new Exception("FRESH_ROOT_REQUIRED");
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("PCL_CLIENT_UPDATE_ROOT", Path.Combine(root, "updates"));
var target = Path.Combine(root, "Installed.app");
using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var ct = budget.Token;
await ClientUpdatePackage.RunAsync("/usr/bin/ditto", [Path.GetFullPath(args[0]), target], ct);
var fixtures = Path.GetFullPath(args[1]);
string Feed(string name) => Path.Combine(fixtures, name, "feed.json");
string Archive(string name) => Path.Combine(fixtures, name, "client.zip");
var checks = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{
    try { await action(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or CryptographicException or TimeoutException or OperationCanceledException)
    { Check(true, name); return; }
    throw new Exception("FAILED_TO_REJECT " + name);
}
var updater = new ClientSelfUpdate(target);
var policy = updater.Policy;
var goodSequence = updater.Installed.Sequence + 1;
var recoverySequence = updater.Installed.Sequence + 4;
var signed = File.ReadAllBytes(Feed("good"));
var manifest = ClientUpdatePackage.Verify(signed, policy);
using var key = RSA.Create(); key.ImportFromPem(File.ReadAllText(args[2]));
byte[] Sign(ClientUpdateManifest document)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(document);
    return JsonSerializer.SerializeToUtf8Bytes(new ClientUpdateEnvelope(Convert.ToBase64String(payload),
        Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
}
Task Verify(byte[] value, ClientUpdatePolicy? pin = null) { ClientUpdatePackage.Verify(value, pin ?? policy); return Task.CompletedTask; }
Check(manifest.Release.Sequence == goodSequence, "signed-feed-valid");
using var wrong = RSA.Create(2048);
await Reject(() => Verify(signed, policy with { PublicKeyPem = wrong.ExportSubjectPublicKeyInfoPem() }), "wrong-key-rejected");
var envelope = JsonSerializer.Deserialize<ClientUpdateEnvelope>(signed, ClientUpdatePackage.Json)!;
await Reject(() => Verify(JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String("{}"u8) })), "tampered-feed-rejected");
await Reject(() => Verify(Sign(manifest with { ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1) })), "expired-feed-rejected");
await Reject(() => Verify(Sign(manifest with { Channel = "other" })), "wrong-channel-rejected");
await Reject(() => Verify(Sign(manifest with { Release = manifest.Release with { Runtime = "win-x64" } })), "wrong-platform-rejected");
await Reject(() => Verify(Sign(manifest with { PackageUrl = "http://updates.example/client.zip" })), "non-https-rejected");
await Reject(() => Verify(Sign(manifest with { Size = ClientUpdatePackage.MaxPackageBytes + 1 })), "package-limit-rejected");
var altered = Path.Combine(root, "altered.zip");
File.Copy(Archive("good"), altered);
using (var file = new FileStream(altered, FileMode.Open, FileAccess.ReadWrite)) { file.Position = 500; var original = file.ReadByte(); file.Position = 500; file.WriteByte((byte)(original ^ 1)); }
await Reject(() => { ClientUpdatePackage.VerifyArchive(altered, manifest); return Task.CompletedTask; }, "package-tampering-rejected");
foreach (var (entry, mode) in new[] { ("../escape", 0x8000), (manifest.ArchiveRoot + "/link", 0xa000) })
{
    var badZip = Path.Combine(root, Guid.NewGuid() + ".zip");
    using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create))
    {
        var item = zip.CreateEntry(entry); item.ExternalAttributes = mode << 16;
        using var text = new StreamWriter(item.Open()); text.Write("outside");
    }
    var malicious = manifest with { Size = new FileInfo(badZip).Length, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(badZip))) };
    await Reject(() => { ClientUpdatePackage.VerifyArchive(badZip, malicious); return Task.CompletedTask; }, mode == 0xa000 ? "archive-link-rejected" : "archive-traversal-rejected");
}
var originalHash = ClientUpdatePackage.TreeHash(target);
var hostHash = ClientUpdatePackage.TreeHash(Path.Combine(target, "Contents/MacOS/host"));
using (var transport = new FixtureTransport(policy.FeedUrl, manifest.PackageUrl, signed, File.ReadAllBytes(Archive("good"))))
using (var http = new HttpClient(transport))
{
    var downloaded = await updater.DownloadAsync(http, [], ct);
    Check(File.Exists(downloaded), "bounded-download-and-stage-via-http-client");
}
using (var cancelled = new CancellationTokenSource())
using (var transport = new FixtureTransport(policy.FeedUrl, manifest.PackageUrl, signed, []))
using (var http = new HttpClient(transport))
{
    cancelled.Cancel();
    await Reject(() => updater.DownloadAsync(http, [], cancelled.Token), "cancelled-download-keeps-client");
}
using (updater.Lock())
    await Reject(() => { updater.PrepareLocal(Feed("good"), Archive("good"), []); return Task.CompletedTask; }, "concurrent-installer-rejected");
var requestPath = updater.PrepareLocal(Feed("good"), Archive("good"), []);
var request = ClientUpdatePackage.Read<ClientUpdateRequest>(requestPath);
using (var blocker = Process.Start(new ProcessStartInfo("/bin/sleep") { ArgumentList = { "2" } })!)
{
    var waiting = request with { ParentPid = blocker.Id, ParentStartedTicks = blocker.StartTime.ToUniversalTime().Ticks };
    await Reject(() => updater.ApplyAsync(waiting, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2), ct), "waits-for-old-process-exit");
    Check(!blocker.HasExited, "old-client-not-killed-on-exit-timeout");
    await blocker.WaitForExitAsync(ct);
}
Check(ClientUpdatePackage.TreeHash(target) == originalHash, "all-preflight-failures-preserve-installed-bytes");
using (updater.AcquireRuntimeLease())
    await Reject(() => updater.ApplyAsync(request with { ParentStartedTicks = 0 }, TimeSpan.FromMilliseconds(200),
        TimeSpan.FromSeconds(2), ct), "another-running-client-blocks-replacement");

var data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
File.WriteAllText(Path.Combine(data, ".client-update-test"), "fixture");
File.WriteAllText(Path.Combine(data, "preserved-user-data.txt"), "do not migrate or overwrite");
var report = Path.Combine(data, "client.json");
string[] restart = ["--data-root", data, "--client-update-test-report", report];
Process Launch(string? fixture = null)
{
    var info = new ProcessStartInfo(Path.Combine(ClientUpdatePackage.BaseDirectory(target), "PCL-MacPort")) { UseShellExecute = false };
    foreach (var arg in restart) info.ArgumentList.Add(arg);
    if (fixture is not null)
    {
        info.ArgumentList.Add("--client-update-test-feed"); info.ArgumentList.Add(Feed(fixture));
        info.ArgumentList.Add("--client-update-test-archive"); info.ArgumentList.Add(Archive(fixture));
    }
    return Process.Start(info)!;
}
async Task Wait(Func<bool> condition, string failure)
{
    var watch = Stopwatch.StartNew();
    while (watch.Elapsed < TimeSpan.FromSeconds(100))
    {
        ct.ThrowIfCancellationRequested();
        if (condition()) return;
        await Task.Delay(200, ct);
    }
    throw new Exception(failure);
}
bool Report(long sequence, int notPid = 0)
{
    if (!File.Exists(report)) return false;
    var value = ClientUpdatePackage.Read<JsonElement>(report);
    return value.GetProperty("sequence").GetInt64() == sequence && value.GetProperty("pid").GetInt32() != notPid;
}
async Task Stop()
{
    var current = ClientUpdatePackage.Read<JsonElement>(report);
    using var owned = Process.GetProcessById(current.GetProperty("pid").GetInt32());
    if (owned.StartTime.ToUniversalTime().Ticks != current.GetProperty("startedTicks").GetInt64()) throw new Exception("TEST_PID_REUSED");
    File.WriteAllText(Path.Combine(data, "stop-client"), "stop");
    await owned.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
    File.Delete(Path.Combine(data, "stop-client"));
    File.Delete(report);
}
using (var old = Launch("good"))
{
    await Wait(() => updater.Status.Result == "updated" && Report(goodSequence, old.Id), "ACTUAL_CLIENT_UPDATE_DID_NOT_COMPLETE");
    await old.WaitForExitAsync(ct);
    Check(ClientUpdatePackage.ReadRelease(target).Sequence == goodSequence, "actual-app-exit-helper-replacement-restart");
    Check(updater.Status.Pending is null && Directory.Exists(updater.Status.PreviousDirectory), "startup-ack-commits-and-retains-backup");
    await ClientUpdatePackage.RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", target], ct);
    Check(true, "updated-app-signature-intact");
    await Stop();
}
var goodHash = ClientUpdatePackage.TreeHash(target);
updater = new(target);
using (var old = Launch("broken"))
{
    await Wait(() => updater.Status.Result == "rolled-back" && Report(goodSequence, old.Id), "BROKEN_APP_DID_NOT_ROLL_BACK");
    await old.WaitForExitAsync(ct);
    Check(ClientUpdatePackage.TreeHash(target) == goodHash, "actual-signed-broken-app-restores-identical-previous-client");
    await Stop();
}
await Reject(() => { updater.PrepareLocal(Feed("broken"), Archive("broken"), restart); return Task.CompletedTask; }, "failed-version-not-reinstalled");
requestPath = updater.PrepareLocal(Feed("hang"), Archive("hang"), restart);
request = ClientUpdatePackage.Read<ClientUpdateRequest>(requestPath) with { ParentStartedTicks = 0 };
await Reject(() => updater.ApplyAsync(request, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), ct), "startup-timeout-rolls-back");
await Wait(() => Report(goodSequence), "PREVIOUS_CLIENT_NOT_RESTARTED_AFTER_TIMEOUT");
Check(ClientUpdatePackage.TreeHash(target) == goodHash, "timeout-preserves-previous-app-bytes");
await Stop();

// Simulate updater termination on either side of the atomic exchange. Production startup must recover.
foreach (var swapped in new[] { false, true })
{
    var sibling = Path.Combine(root, ".Installed.app.pcl-update-" + Guid.NewGuid().ToString("N"), "Recovery.app");
    Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
    await ClientUpdatePackage.RunAsync("/usr/bin/ditto", [target, sibling], ct);
    var metadata = Path.Combine(sibling, "Contents/MacOS/client-release.json");
    ClientUpdatePackage.Save(metadata, ClientUpdatePackage.ReadRelease(sibling) with { Sequence = recoverySequence });
    await ClientUpdatePackage.RunAsync("/usr/bin/plutil", ["-replace", "CFBundleVersion", "-string", recoverySequence.ToString(), Path.Combine(sibling, "Contents/Info.plist")], ct);
    await ClientUpdatePackage.RunAsync("/usr/bin/codesign", ["--force", "--sign", "-", metadata], ct);
    await ClientUpdatePackage.RunAsync("/usr/bin/codesign", ["--force", "--sign", "-", sibling], ct);
    var transaction = new ClientUpdateTransaction(sibling, goodHash, ClientUpdatePackage.TreeHash(sibling), goodSequence, recoverySequence);
    ClientUpdatePackage.Save(Path.Combine(updater.Store, "state.json"), new ClientUpdateState(recoverySequence, "starting", transaction));
    if (swapped && Native.Swap(-2, target, -2, sibling, 2) != 0) throw new IOException("FIXTURE_SWAP_FAILED");
    Check(Directory.Exists(target), "target-always-present-" + swapped);
    using var interrupted = Launch();
    await Wait(() => updater.Status.Pending is null && Report(goodSequence, interrupted.Id), "INTERRUPTED_TRANSACTION_RECOVERY_FAILED");
    await interrupted.WaitForExitAsync(ct);
    Check(ClientUpdatePackage.TreeHash(target) == goodHash, "automatic-recovery-" + (swapped ? "after-swap" : "before-swap"));
    await Stop();
}
Check(ClientUpdatePackage.TreeHash(Path.Combine(target, "Contents/MacOS/host")) == hostHash, "host-payload-preserved-in-client-fixtures");
Check(File.ReadAllText(Path.Combine(data, "preserved-user-data.txt")) == "do not migrate or overwrite", "user-data-preserved");
target = Path.Combine(root, "StandaloneClient");
await ClientUpdatePackage.RunAsync("/usr/bin/ditto", [Path.Combine(fixtures, "raw-base"), target], ct);
Environment.SetEnvironmentVariable("PCL_HOST_EXECUTABLE", Path.Combine(Path.GetFullPath(args[0]), "Contents/MacOS/host/Pcl.Host"));
data = Path.Combine(root, "raw-data"); Directory.CreateDirectory(data);
File.WriteAllText(Path.Combine(data, ".client-update-test"), "fixture");
report = Path.Combine(data, "client.json");
restart = ["--data-root", data, "--client-update-test-report", report];
updater = new(target);
using (var old = Launch("raw"))
{
    await Wait(() => updater.Status.Result == "updated" && Report(goodSequence, old.Id), "STANDALONE_CLIENT_UPDATE_FAILED");
    await old.WaitForExitAsync(ct);
    Check(!Directory.Exists(Path.Combine(target, "host")), "standalone-client-updates-without-bundling-core");
    Check(ClientUpdatePackage.ReadRelease(target).Sequence == goodSequence, "standalone-client-restarts-with-external-host");
    await Stop();
}
File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { status = "passed", checks,
    scope = "Real macOS application, copied self-contained helper, RSA signing, atomic directory exchange, startup acknowledgement and rollback; HTTP transport fixture only" }, ClientUpdatePackage.Json));
return 0;

static class Native
{
    [DllImport("libSystem.B.dylib", EntryPoint = "renameatx_np", SetLastError = true)]
    internal static extern int Swap(int firstFd, string first, int secondFd, string second, uint flags);
}
sealed class FixtureTransport(string feedUrl, string packageUrl, byte[] feed, byte[] package) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri!.AbsoluteUri;
        if (url != feedUrl && url != packageUrl) throw new InvalidOperationException("UNEXPECTED_URL");
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(url == feedUrl ? feed : package) });
    }
}
