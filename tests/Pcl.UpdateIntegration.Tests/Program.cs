using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Client;
using Pcl.Desktop.Updates;
using Pcl.Core.Adapters.Content;
using Pcl.Host;

var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new Exception("FRESH_ROOT_REQUIRED");
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
var oldPolicy = "{\"MinimumSequence\":42101,\"PublicKeyPem\":\"pin\"}"u8.ToArray();
Check(ClientSelfUpdate.CompatiblePolicy(oldPolicy, "{\"MinimumSequence\":42102,\"PublicKeyPem\":\"pin\"}"u8.ToArray(), true), "host-floor-can-increase-without-changing-trust");
Check(!ClientSelfUpdate.CompatiblePolicy(oldPolicy, "{\"MinimumSequence\":42100,\"PublicKeyPem\":\"pin\"}"u8.ToArray(), true) &&
    !ClientSelfUpdate.CompatiblePolicy(oldPolicy, "{\"MinimumSequence\":42102,\"PublicKeyPem\":\"other\"}"u8.ToArray(), true), "host-floor-downgrade-and-trust-change-rejected");
using var key = RSA.Create(2048);
var policy = new ContentPolicy("preview", key.ExportSubjectPublicKeyInfoPem(), "https://updates.example/content.json");
var policyPath = Path.Combine(root, "policy.json");
File.WriteAllText(policyPath, JsonSerializer.Serialize(policy));
var contentRoot = Path.Combine(root, "content");
Environment.SetEnvironmentVariable("PCL_CONTENT_ROOT", contentRoot);
Environment.SetEnvironmentVariable("PCL_CONTENT_POLICY", policyPath);
byte[] Package(long sequence, RSA? signer = null)
{
    var document = new ContentDocument(1, "wiki-project-index", "preview", sequence, "0.42.1", "0.42.99", "isolated integration fixture",
        [new(10001, "自动更新验收", "fixture-mod", null, 1)]);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
    return JsonSerializer.SerializeToUtf8Bytes(new ContentEnvelope(Convert.ToBase64String(bytes),
        Convert.ToBase64String((signer ?? key).SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
}
JsonElement Status(ContentUpdates updates) => JsonSerializer.SerializeToElement(updates.Status());
async Task<string> Finish(ContentUpdates updates)
{
    for (var i = 0; i < 200; i++)
    {
        var state = Status(updates);
        if (!state.GetProperty("busy").GetBoolean()) return state.GetProperty("result").GetString()!;
        await Task.Delay(50);
    }
    throw new TimeoutException();
}
var transport = new Fixture { Bytes = Package(1), Block = true };
using (var updates = new ContentUpdates(() => transport))
{
    updates.Begin(); updates.Begin();
    await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(Status(updates).GetProperty("busy").GetBoolean() && transport.Requests == 1, "background-check-single-flight-status-responsive");
    transport.Release.TrySetResult();
    Check(await Finish(updates) == "installed", "automatic-content-fetch-install");
    var state = Status(updates);
    Check(state.GetProperty("activeSequence").GetInt64() == 0 && state.GetProperty("installedSequence").GetInt64() == 1, "active-index-stays-immutable-until-host-restart");
}
using (var updates = new ContentUpdates(() => new Fixture { Bytes = Package(1) }))
{ updates.Begin(); Check(await Finish(updates) == "up-to-date", "replay-is-not-reinstalled"); }
using var wrong = RSA.Create(2048);
using (var updates = new ContentUpdates(() => new Fixture { Bytes = Package(2, wrong) }))
{ updates.Begin(); Check(await Finish(updates) == "failed", "wrong-signature-preserves-installed-data"); }
Check(ContentPack.Load(contentRoot, policy, new Version(0,42,1)).Sequence == 1, "failed-fetch-keeps-previous-selection");
var cancelled = new Fixture { Bytes = Package(2), Block = true };
var cancelling = new ContentUpdates(() => cancelled); cancelling.Begin();
await cancelled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
cancelling.Dispose(); Check(true, "host-shutdown-cancels-background-download");
ContentPack.Install(contentRoot, Package(2), policy, new Version(0,42,1));
ContentPack.Install(contentRoot, Package(3), policy, new Version(0,42,1));
File.WriteAllText(Path.Combine(contentRoot, "unrelated.json"), "keep");
Check(ContentPack.Cleanup(contentRoot) == 1, "content-cleanup-removes-only-obsolete-package");
ContentPack.Rollback(contentRoot, policy, new Version(0,42,1));
Check(ContentPack.Load(contentRoot, policy, new Version(0,42,1)).Sequence == 2 && File.Exists(Path.Combine(contentRoot, "unrelated.json")), "content-cleanup-preserves-rollback-and-unrelated-files");
try { ContentPack.Install(contentRoot, Package(3), policy, new Version(0,42,1)); throw new Exception("replay accepted"); }
catch (InvalidDataException) { Check(true, "cleanup-preserves-anti-replay-state"); }
if (args.Length > 1)
{
    var profile = Path.Combine(root, "host-data", "instances", "fixture");
    Directory.CreateDirectory(profile);
    File.WriteAllText(Path.Combine(profile, "profile.json"), "{\"id\":\"fixture\",\"gameVersion\":\"1.21.1\",\"loader\":\"vanilla\"}");
    await using var host = new HostClient(Path.GetFullPath(args[1]), "", Path.Combine(root,"host-data"), true);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    Check((await host.HelloAsync(timeout.Token)).Capabilities.Contains("content.update.v1"), "actual-host-advertises-content-capability");
    var state = await host.Call<JsonElement>("content.update.status", new { }, timeout.Token);
    Check(state.GetProperty("activeSequence").GetInt64() == 2 && state.GetProperty("installedSequence").GetInt64() == 2,
        "actual-host-restart-activates-installed-data-via-rpc");
    await using var session = new LauncherSession(host);
    await session.InitializeAsync();
    var page = await new WorkspaceApi(session.Workspaces).CallAsync<Pcl.Core.Contracts.Workspaces.SearchPage>(
        "workspace.search.page", "fixture", new { query = "自动更新验收", provider = "mcmod", online = false,
            gameVersion = "", loader = "", kind = 0 }, timeout.Token);
    Check(page.Hits.Length == 1 && page.Hits[0].McmodUrl == "https://www.mcmod.cn/class/10001.html",
        "installed-content-reaches-client-search-through-actual-host");
    await host.ShutdownAsync(timeout.Token);
}
if (OperatingSystem.IsMacOS())
{
    var target = Path.Combine(root, "client"); Directory.CreateDirectory(target);
    ClientUpdatePackage.Save(Path.Combine(target, "client-release.json"), new ClientRelease("0.42.1",424,"osx-arm64"));
    ClientUpdatePackage.Save(Path.Combine(target, "client-update-policy.json"), new ClientUpdatePolicy("https://updates.example/client", policy.PublicKeyPem,"preview"));
    Environment.SetEnvironmentVariable("PCL_CLIENT_UPDATE_ROOT", Path.Combine(root,"client-cache"));
    var updater = new ClientSelfUpdate(target);
    string OldDir(string parent, string prefix)
    {
        var path = Path.Combine(parent,prefix + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"owned"),"fixture"); Directory.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddDays(-10)); return path;
    }
    var obsolete = OldDir(updater.Store,"download-");
    var protectedDownload = OldDir(updater.Store,"download-");
    var recent1 = OldDir(updater.Store,"helper-"); Directory.SetLastWriteTimeUtc(recent1,DateTime.UtcNow);
    var recent2 = OldDir(updater.Store,"helper-"); Directory.SetLastWriteTimeUtc(recent2,DateTime.UtcNow);
    var oldStage = OldDir(root,".client.pcl-update-");
    var rollback = OldDir(root,".client.pcl-update-");
    ClientUpdatePackage.Save(Path.Combine(updater.Store,"state.json"), new ClientUpdateState(424,"updated",null,rollback));
    using var self = Process.GetCurrentProcess();
    var request = Path.Combine(updater.Store,"request-"+Guid.NewGuid().ToString("N")+".json");
    ClientUpdatePackage.Save(request,new ClientUpdateRequest(target,"unused",Path.Combine(protectedDownload,"client.zip"),self.Id,self.StartTime.ToUniversalTime().Ticks,[]));
    File.SetLastWriteTimeUtc(request,DateTime.UtcNow.AddDays(-10));
    Check(updater.CleanupCache()==2 && !Directory.Exists(obsolete) && !Directory.Exists(oldStage),"client-cleanup-removes-aged-download-and-stage");
    Check(Directory.Exists(rollback)&&Directory.Exists(protectedDownload)&&File.Exists(request)&&Directory.Exists(recent1),"client-cleanup-preserves-backup-live-request-and-recent-helper");
    var outside = Path.Combine(root,"outside"); Directory.CreateDirectory(outside);
    File.WriteAllText(Path.Combine(outside,"sentinel"),"preserve"); Directory.SetLastWriteTimeUtc(outside,DateTime.UtcNow.AddDays(-10));
    var linkedStage = OldDir(root,".client.pcl-update-");
    var link = Path.Combine(linkedStage,"linked"); Directory.CreateSymbolicLink(link,outside);
    Directory.SetLastWriteTimeUtc(linkedStage,DateTime.UtcNow.AddDays(-10));
    try { updater.CleanupCache(); throw new Exception("linked cleanup accepted"); }
    catch (InvalidDataException) { Check(File.Exists(Path.Combine(outside,"sentinel")),"client-cleanup-rejects-links-without-touching-target"); }
    Directory.Delete(link);
    ClientUpdatePackage.Save(Path.Combine(updater.Store,"state.json"),new ClientUpdateState(425,"starting",new(rollback,"old","new",424,425)));
    try { updater.CleanupCache(); throw new Exception("pending cleanup accepted"); }
    catch(IOException) { Check(true,"pending-update-blocks-cache-cleanup"); }
}
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new { status="passed",checks },new JsonSerializerOptions {WriteIndented=true}));
sealed class Fixture : HttpMessageHandler
{
    public byte[] Bytes = []; public bool Block; public int Requests;
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        Interlocked.Increment(ref Requests); Entered.TrySetResult();
        if(Block) await Release.Task.WaitAsync(ct);
        if(request.RequestUri!.AbsoluteUri!="https://updates.example/content.json") throw new Exception("unexpected URI");
        return new(HttpStatusCode.OK) {Content=new ByteArrayContent(Bytes)};
    }
}
