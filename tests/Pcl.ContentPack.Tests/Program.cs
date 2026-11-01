using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Core;
using Pcl.Core.Adapters.Content;
using Pcl.Core.Adapters.Search;
using Pcl.Client;

if (args is ["host-probe", var host, var hostRoot, var hostPolicy, var hostStore])
{
    if (Directory.Exists(hostRoot)) throw new Exception("FRESH_HOST_ROOT_REQUIRED");
    var profile = Path.Combine(hostRoot, "instances", "fixture");
    Directory.CreateDirectory(profile);
    File.WriteAllText(Path.Combine(profile, "profile.json"), "{\"id\":\"fixture\",\"gameVersion\":\"1.21.1\",\"loader\":\"vanilla\"}");
    Environment.SetEnvironmentVariable("PCL_CONTENT_POLICY", Path.GetFullPath(hostPolicy));
    Environment.SetEnvironmentVariable("PCL_CONTENT_ROOT", Path.GetFullPath(hostStore));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    await using var session = new LauncherSession(new HostClient(Path.GetFullPath(host), "", Path.GetFullPath(hostRoot)));
    await session.InitializeAsync();
    var api = new WorkspaceApi(session.Workspaces);
    var page = await api.CallAsync<Pcl.Core.Contracts.Workspaces.SearchPage>("workspace.search.page", "fixture",
        new { query = "content-fixture", provider = "mcmod", online = false, gameVersion = "", loader = "", kind = 0 }, timeout.Token);
    if (page.Hits.Length != 1 || page.Hits[0].McmodUrl != "https://www.mcmod.cn/class/900001.html")
        throw new Exception("PUBLISHED_HOST_CONTENT_SEARCH_FAILED");
    Console.WriteLine("PUBLISHED_HOST_CONTENT_SEARCH_VERIFIED");
    return 0;
}

if (args is ["probe"])
{
    var hit = McmodCatalog.Search("content-fixture").Projects.Single();
    if (hit.Id != 900001 || BundledProjectIdentity.Index.Find("modrinth", "content-fixture")?.WikiId != hit.Id ||
        McmodCatalog.ActiveIndexSequence != 2) throw new Exception("RUNTIME_INDEX_MISMATCH");
    Console.WriteLine("RUNTIME_SEARCH_AND_IDENTITY_VERIFIED");
    return 0;
}
var root = Path.GetFullPath(args.Single());
if (Directory.Exists(root)) throw new Exception("FRESH_TEST_DIRECTORY_REQUIRED");
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool result, string name) { if (!result) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or CryptographicException or FormatException or JsonException or ArgumentException)
    { Check(true, name); return; }
    throw new Exception("ACCEPTED_INVALID: " + name);
}
using var key = RSA.Create(2048);
using var other = RSA.Create(2048);
var policy = new ContentPolicy("preview", key.ExportSubjectPublicKeyInfoPem());
var version = new Version(0, 42, 1);
ContentDocument Doc(long seq) => new(1, "wiki-project-index", "preview", seq, "0.42.1", "0.42.99", "synthetic local fixture",
    [new(900001, "Content fixture", "content-fixture", "content-fixture-cf", 10)]);
byte[] Sign(ContentDocument document)
{
    var payload = JsonSerializer.SerializeToUtf8Bytes(document);
    return JsonSerializer.SerializeToUtf8Bytes(new ContentEnvelope(Convert.ToBase64String(payload),
        Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
}
var first = Sign(Doc(1));
Check(ContentPack.Verify(first, policy, version).Sequence == 1, "signature-valid");
Reject(() => ContentPack.Verify(first, policy with { PublicKeyPem = other.ExportSubjectPublicKeyInfoPem() }, version), "wrong-key");
var envelope = JsonSerializer.Deserialize<ContentEnvelope>(first)!;
Reject(() => ContentPack.Verify(JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String("{}"u8) }), policy, version), "payload-tampering");
Reject(() => ContentPack.Verify(Sign(Doc(1) with { Channel = "stable" }), policy, version), "channel-mismatch");
Reject(() => ContentPack.Verify(Sign(Doc(1) with { Format = 2 }), policy, version), "format-mismatch");
Reject(() => ContentPack.Verify(Sign(Doc(1) with { Kind = "executable" }), policy, version), "wrong-kind");
Reject(() => ContentPack.Verify(Sign(Doc(1) with { MinHostVersion = "0.43.0" }), policy, version), "host-too-old");
Reject(() => ContentPack.Verify(first, policy, new Version(0, 43, 0)), "host-too-new");
Reject(() => ContentPack.Verify(Sign(Doc(1) with { Entries = [] }), policy, version), "empty-index");
Reject(() => ContentPack.Verify(Sign(Doc(1) with { Entries = [new(1, "bad", "../escape", null, 0)] }), policy, version), "invalid-slug");
Reject(() => ContentPack.Verify(new byte[ContentPack.MaxBytes + 1], policy, version), "size-limit");
var store = Path.Combine(root, "store");
ContentPack.Install(store, first, policy, version);
Check(ContentPack.Load(store, policy, version).Sequence == 1, "install-first");
Reject(() => ContentPack.Install(store, first, policy, version), "replay-rejected");
var second = Sign(Doc(2));
ContentPack.Install(store, second, policy, version);
Check(ContentPack.Load(store, policy, version).Sequence == 2, "upgrade-atomic-selection");
using (var held = new FileStream(Path.Combine(store, "update.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    Reject(() => ContentPack.Install(store, Sign(Doc(3)), policy, version), "concurrent-install-locked");
Check(ContentPack.Load(store, policy, version).Sequence == 2, "failed-install-preserves-selection");
var policyPath = Path.Combine(root, "policy.json");
File.WriteAllBytes(policyPath, JsonSerializer.SerializeToUtf8Bytes(policy));
var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet") start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
start.ArgumentList.Add("probe");
start.Environment["PCL_CONTENT_POLICY"] = policyPath;
start.Environment["PCL_CONTENT_ROOT"] = store;
using (var process = Process.Start(start)!)
{
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("PROBE_TIMEOUT"); }
    Check(process.ExitCode == 0 && output.Contains("RUNTIME_SEARCH_AND_IDENTITY_VERIFIED"), "fresh-process-uses-external-search-and-identity: " + error);
}
var secondPath = Path.Combine(store, Convert.ToHexString(SHA256.HashData(second)).ToLowerInvariant() + ".json");
File.WriteAllText(secondPath, "corrupt");
var fallback = ContentPack.Load(store, policy, version);
Check(fallback.Sequence == 1 && fallback.Rejection is not null, "corrupt-current-falls-back-to-previous");
ContentPack.Rollback(store, policy, version);
Check(ContentPack.Load(store, policy, version).Sequence == 1, "explicit-rollback");
Reject(() => ContentPack.Install(store, first, policy, version), "rollback-preserves-high-water-mark");
ContentPack.Install(store, Sign(Doc(3)), policy, version);
Check(ContentPack.Load(store, policy, new Version(0, 43, 0)).Sequence == 0, "incompatible-host-uses-built-in");
Check(ContentPack.Load(store, policy with { PublicKeyPem = other.ExportSubjectPublicKeyInfoPem() }, version).Sequence == 0, "untrusted-store-uses-built-in");
File.WriteAllText(Path.Combine(store, "state.json"), "{}");
Reject(() => ContentPack.Install(store, Sign(Doc(4)), policy, version), "invalid-state-does-not-reset-sequence");
var builtIn = ContentPack.ExportBuiltIn();
Check(builtIn.Length > 10000, "real-upstream-export");
var actual = Sign(Doc(5) with { Entries = builtIn, Provenance = McmodCatalog.IndexProvenance });
var verified = ContentPack.Verify(actual, policy, version);
Check(ContentPack.Project(verified).Count == builtIn.Length, "real-index-signed-roundtrip");
if (!OperatingSystem.IsWindows())
{
    var linked = Path.Combine(root, "linked");
    Directory.CreateSymbolicLink(linked, store);
    Reject(() => ContentPack.Install(linked, Sign(Doc(4)), policy, version), "linked-store-rejected");
}
File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
return 0;
