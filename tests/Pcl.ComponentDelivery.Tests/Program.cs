using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Client;
using Pcl.Client.Updates;
using Pcl.Core.Contracts.Updates;

// Consume the actual publisher output with an independently provided key pin.
if (args.Length != 5) throw new ArgumentException("<signed-release> <old-host-executable> <pinned-key.pem> <fresh-root> <report.json>");
var packageRoot = Path.GetFullPath(args[0]);
var dataRoot = Path.GetFullPath(args[3]);
if (Directory.Exists(dataRoot)) throw new InvalidOperationException("FRESH_ROOT_REQUIRED");
Directory.CreateDirectory(dataRoot);
var checks = new List<string>();
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
using var pin = RSA.Create();
pin.ImportFromPem(File.ReadAllText(args[2]));
var indexBytes = File.ReadAllBytes(Path.Combine(packageRoot, "release-manifest.json"));
var envelope = JsonSerializer.Deserialize<HostUpdateEnvelope>(File.ReadAllBytes(Path.Combine(packageRoot, "release-manifest.signed.json")), json)!;
var payload = Convert.FromBase64String(envelope.Payload);
var signature = Convert.FromBase64String(envelope.Signature);
Check(payload.SequenceEqual(indexBytes) && pin.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "release-index-matches-publisher-pin");
var changed = payload.ToArray(); changed[0] ^= 1;
Check(!pin.VerifyData(changed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "tampered-index-rejected");
using var otherKey = RSA.Create(2048);
Check(!otherKey.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "wrong-publisher-key-rejected");
using var index = JsonDocument.Parse(indexBytes);
var hostEntry = index.RootElement.GetProperty("packages").GetProperty("host");
var hostZip = File.ReadAllBytes(Path.Combine(packageRoot, hostEntry.GetProperty("file").GetString()!));
var feed = File.ReadAllBytes(Path.Combine(packageRoot, hostEntry.GetProperty("signature").GetProperty("file").GetString()!));
var signedHost = JsonSerializer.Deserialize<HostUpdateEnvelope>(feed, json)!;
var hostPayload = Convert.FromBase64String(signedHost.Payload);
Check(pin.VerifyData(hostPayload, Convert.FromBase64String(signedHost.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "host-feed-matches-publisher-pin");
var hostManifest = JsonSerializer.Deserialize<HostUpdateManifest>(hostPayload, json)!;
Check(hostManifest.PackageSize == hostZip.Length && hostManifest.PackageSha256 == Convert.ToHexString(SHA256.HashData(hostZip)).ToLowerInvariant()
    && hostManifest.PackageSha256 == hostEntry.GetProperty("sha256").GetString(), "index-and-feed-reference-identical-host-bytes");
var policy = JsonSerializer.Deserialize<HostUpdatePolicy>(File.ReadAllBytes(Path.Combine(packageRoot, "host-update-policy.json")), json)!;
using var policyKey = RSA.Create(); policyKey.ImportFromPem(policy.PublicKeyPem);
Check(policyKey.ExportSubjectPublicKeyInfo().SequenceEqual(pin.ExportSubjectPublicKeyInfo()), "client-policy-pins-publisher-key");
using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(packageRoot, index.RootElement.GetProperty("packages").GetProperty("client").GetProperty("file").GetString()!)))
{
    using var reader = new StreamReader(zip.GetEntry("host-update-policy.json")!.Open());
    var bundledPolicy = JsonSerializer.Deserialize<HostUpdatePolicy>(reader.ReadToEnd(), json)!;
    Check(bundledPolicy.FeedUrl == policy.FeedUrl && bundledPolicy.PublicKeyPem == policy.PublicKeyPem, "client-archive-carries-pinned-update-policy");
    Check(!zip.Entries.Any(x => x.FullName.StartsWith("host/") || x.Name == "Pcl.Core.dll"), "signed-client-keeps-core-separated");
}
using var transport = new ArtifactTransport(policy.FeedUrl, hostManifest.PackageUrl, feed, hostZip);
using var http = new HttpClient(transport);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await using var client = new HostClient(Path.GetFullPath(args[1]), "", Path.Combine(dataRoot, "data"), true);
client.ConfigureUpdates(policy, Path.Combine(dataRoot, "updates"), http);
try
{
    var old = await client.HelloAsync(timeout.Token);
    Check(old.Build!.Sequence < hostManifest.Build.Sequence, "previous-host-started");
    Check((await client.CheckForUpdatesAsync(timeout.Token)).State == "staged", "publisher-output-staged-by-real-update-sdk");
    Check((await client.ApplyUpdateWhenIdleAsync(timeout.Token)).State == "current", "publisher-output-activated");
    var current = await client.HelloAsync(timeout.Token);
    Check(current.Build == hostManifest.Build && current.HostInstanceId != old.HostInstanceId, "published-self-contained-host-handshake");
    await client.ShutdownAsync(timeout.Token);
}
finally
{
    if (client.IsConnected) await client.ShutdownAsync();
}
File.WriteAllText(args[4], JsonSerializer.Serialize(new { status = "passed", checks, build = hostManifest.Build,
    scope = "Actual signed publisher assets and local transport; no public CDN or production credential acceptance" }, json));

sealed class ArtifactTransport(string feedUrl, string packageUrl, byte[] feed, byte[] package) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri!.AbsoluteUri;
        if (url != feedUrl && url != packageUrl) throw new InvalidOperationException("UNEXPECTED_UPDATE_URL");
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { RequestMessage = request,
            Content = new ByteArrayContent(url == feedUrl ? feed : package) });
    }
}
