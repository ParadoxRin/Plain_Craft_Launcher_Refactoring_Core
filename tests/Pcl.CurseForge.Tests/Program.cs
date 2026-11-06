using System.Net;
using ResourceKind = Pcl.Core.ResourceKind;
using NetworkOptions = Pcl.Core.NetworkOptions;
using System.Text.Json;
using Pcl.Core;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Ports.Operations;

if (args is ["host-probe", var host, var hostRoot])
{
    Environment.SetEnvironmentVariable(CurseForgeAccess.KeyFileVariable, null);
    var profile = Path.Combine(hostRoot, "instances", "fixture");
    Directory.CreateDirectory(profile);
    File.WriteAllText(Path.Combine(profile, "profile.json"), "{\"id\":\"fixture\",\"gameVersion\":\"1.21.1\",\"loader\":\"vanilla\"}");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    await using var session = new Pcl.Client.LauncherSession(new Pcl.Client.HostClient(Environment.ProcessPath!, Path.GetFullPath(host), Path.GetFullPath(hostRoot)));
    await session.InitializeAsync();
    var api = new Pcl.Client.WorkspaceApi(session.Workspaces);
    var missing = await api.CallAsync<SearchPage>("workspace.search.page", "fixture",
        new { query = "test", provider = "curseforge", online = true, gameVersion = "", loader = "", kind = 0 }, timeout.Token);
    if (missing.Availability.Single().Code != "CURSEFORGE_KEY_REQUIRED" || !missing.HasErrors) throw new Exception("HOST_STATUS_MISSING");
    var handle = await api.SecretAsync("synthetic-protocol-secret\r\n", timeout.Token);
    var invalid = await api.CallAsync<SearchPage>("workspace.search.page", "fixture",
        new { query = "test", provider = "curseforge", online = true, gameVersion = "", loader = "", kind = 0, secretHandle = handle }, timeout.Token);
    if (invalid.Availability.Single().Code != "CURSEFORGE_KEY_INVALID" || JsonSerializer.Serialize(invalid).Contains("synthetic-protocol-secret")) throw new Exception("HOST_SECRET_STATUS");
    var local = await api.CallAsync<SearchPage>("workspace.search.page", "fixture",
        new { query = "sodium", provider = "mcmod", online = false, gameVersion = "", loader = "", kind = 0, secretHandle = "expired-handle" }, timeout.Token);
    if (local.HasErrors || local.Hits.Length == 0) throw new Exception("OTHER_SOURCE_BROKEN");
    Console.WriteLine("PASS real Host missing/invalid status, secret redaction and independent source");
    return;
}

var checks = 0;
const string secret = "synthetic-credential-never-export";
void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks++; }
var root = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/curseforge");
Directory.CreateDirectory(root);
var original = Environment.GetEnvironmentVariable(CurseForgeAccess.KeyFileVariable);
Task<IReadOnlyList<CurseResourceProject>> Search(CurseForgeCatalog c, CancellationToken ct = default) => c.SearchAsync(ResourceKind.Mod, "test", "", "", ct: ct);
async Task<CurseForgeRequestException> Fails(CurseForgeCatalog c, string code)
{
    try { await Search(c); throw new Exception("expected " + code); }
    catch (CurseForgeRequestException e)
    {
        Check(e.Code == code, "code " + code + " got " + e.Code);
        Check(!e.ToString().Contains(secret) && e.InnerException is null, "exception redaction");
        return e;
    }
}
try
{
    Environment.SetEnvironmentVariable(CurseForgeAccess.KeyFileVariable, null);
    var missingHandler = new Handler(_ => Ok());
    using (var c = new CurseForgeCatalog("", missingHandler)) await Fails(c, "CURSEFORGE_KEY_REQUIRED");
    Check(missingHandler.Calls == 0, "missing key makes no request");
    using (var c = new CurseForgeCatalog("bad\r\n" + secret, new Handler(_ => Ok()))) await Fails(c, "CURSEFORGE_KEY_INVALID");
    foreach (var (status, code) in new[] { (401,"CURSEFORGE_KEY_REJECTED"), (403,"CURSEFORGE_KEY_REJECTED"), (429,"CURSEFORGE_RATE_LIMITED"), (503,"CURSEFORGE_UNAVAILABLE"), (302,"CURSEFORGE_UNAVAILABLE") })
    {
        using var c = new CurseForgeCatalog(secret, new Handler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(secret), ReasonPhrase = secret }));
        await Fails(c, code);
    }
    using (var c = new CurseForgeCatalog(secret, new Handler(_ => throw new HttpRequestException(secret)))) await Fails(c, "CURSEFORGE_NETWORK_ERROR");
    using (var c = new CurseForgeCatalog(secret, new Handler(_ => throw new TaskCanceledException(secret)))) await Fails(c, "CURSEFORGE_TIMEOUT");
    foreach (var body in new[] { secret, "{\"data\":null}", "{\"data\":\"" + secret + "\"}" })
    {
        using var c = new CurseForgeCatalog(secret, new Handler(_ => Ok(body)));
        await Fails(c, "CURSEFORGE_RESPONSE_INVALID");
    }
    using (var c = new CurseForgeCatalog(secret, new Handler(_ => Ok("{\"data\":[{\"id\":\"" + secret + "\"}]}")))) await Fails(c, "CURSEFORGE_RESPONSE_INVALID");
    using (var c = new CurseForgeCatalog("", new Handler(_ => Ok()), keyProvider: () => throw new OperationInstallException("SECRET_REENTRY_REQUIRED"))) await Fails(c, "SECRET_REENTRY_REQUIRED");
    using (var c = new CurseForgeCatalog(secret, new Handler(_ => throw new TaskCanceledException(secret))))
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await Search(c, cancel.Token); throw new Exception("cancellation swallowed"); }
        catch (OperationCanceledException e) { Check(!e.ToString().Contains(secret), "cancellation redacted"); }
    }
    var file = Path.Combine(root, "synthetic-key.txt");
    await File.WriteAllTextAsync(file, secret + "\n");
    Environment.SetEnvironmentVariable(CurseForgeAccess.KeyFileVariable, file);
    using (var c = new CurseForgeCatalog("", new Handler(r => { Check(r.Headers.GetValues("x-api-key").Single() == secret, "host file header"); return Ok(); }))) await Search(c);
    using (var c = new CurseForgeCatalog("override", new Handler(r => { Check(r.Headers.GetValues("x-api-key").Single() == "override", "session precedence"); return Ok(); }))) await Search(c);
    File.Delete(file);
    using (var c = new CurseForgeCatalog("", new Handler(_ => Ok()))) await Fails(c, "CURSEFORGE_CONFIG_INVALID");
    await File.WriteAllTextAsync(file, new string('x', 8193));
    using (var c = new CurseForgeCatalog("", new Handler(_ => Ok()))) await Fails(c, "CURSEFORGE_CONFIG_INVALID");
    File.Delete(file);
    Environment.SetEnvironmentVariable(CurseForgeAccess.KeyFileVariable, null);
    var paths = new AppPaths(Path.Combine(root, "mirror"));
    DownloadTasks.For(paths).Configure(new NetworkOptions { CurseForgeApiMirror = "https://mirror.invalid/v1", ResourceSource = "mirror" });
    var official = new Handler(_ => Ok());
    using (var c = new CurseForgeCatalog(secret, official, paths, mirrorHandler: new Handler(r => { Check(!r.Headers.Contains("x-api-key") && !r.Headers.Contains("Authorization"), "mirror receives no credentials"); return Ok(); }))) await Search(c);
    Check(official.Calls == 0, "mirror first successful");
    using (var c = new CurseForgeCatalog("", new Handler(_ => Ok()), paths, mirrorHandler: new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)))) await Fails(c, "CURSEFORGE_KEY_REJECTED");
    using (var c = new CurseForgeCatalog(secret, new Handler(_ => Ok()), paths, mirrorHandler: new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)))) Check((await Search(c)).Count == 0, "mirror to official fallback");
    DownloadTasks.For(paths).Configure(new NetworkOptions { CurseForgeApiMirror = "https://mirror.invalid/v1", ResourceSource = "official" });
    using (var c = new CurseForgeCatalog("", new Handler(_ => Ok()), paths, mirrorHandler: new Handler(_ => throw new Exception("disabled mirror called")))) await Fails(c, "CURSEFORGE_KEY_REQUIRED");

    var hit = new ResourceSearchHit("modrinth:test", "test", "", ["Modrinth"]);
    var providers = new Dictionary<string, Func<ResourceSearchQuery, CancellationToken, Task<ResourceSourcePage>>>
    {
        ["Modrinth"] = (_, _) => Task.FromResult(new ResourceSourcePage("Modrinth", [hit], false)),
        ["CurseForge"] = (_, _) => throw new CurseForgeRequestException("CURSEFORGE_KEY_REJECTED")
    };
    var result = await UnifiedResourceSearch.QueryAsync(new(ResourceKind.Mod, "", "", ""), providers);
    Check(result.Hits.Count == 1 && result.Sources.Single(x => x.Source == "CurseForge").ErrorCode == "CURSEFORGE_KEY_REJECTED", "source isolation and code");
    var page = new SearchPage("session", 0, false, 0, [], ["legacy status"], true) { Availability = [new("CurseForge", false, "CURSEFORGE_KEY_REJECTED", "safe", false)] };
    var json = JsonSerializer.Serialize(page);
    Check(JsonSerializer.Deserialize<SearchPage>(json)!.Availability[0].Code == "CURSEFORGE_KEY_REJECTED" && !json.Contains(secret), "protocol roundtrip");
    Check(JsonSerializer.Deserialize<SearchPage>("{\"Session\":\"old\",\"Page\":0,\"HasNext\":false,\"FilteredDuplicates\":0,\"Hits\":[],\"SourceStatus\":[],\"HasErrors\":false}")!.Availability.Length == 0, "old host payload compatibility");
    Console.WriteLine($"PASS {checks} CurseForge checks");
}
finally { Environment.SetEnvironmentVariable(CurseForgeAccess.KeyFileVariable, original); }

static HttpResponseMessage Ok(string text = "{\"data\":[]}") => new(HttpStatusCode.OK) { Content = new StringContent(text) };
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(respond(request)); }
}
