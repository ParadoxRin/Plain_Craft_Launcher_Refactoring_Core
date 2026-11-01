using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Core;
using Pcl.Core.Adapters.Content;

// Publisher/admin CLI. Trust policy is supplied separately, never taken from a downloaded package.
if (args is ["policy", var policyKey, var policyChannel, var policyOutput])
{
    using var key = RSA.Create();
    key.ImportFromPem(File.ReadAllText(policyKey));
    if (key.KeySize < 2048) throw new InvalidDataException("KEY_TOO_SMALL");
    using var file = new FileStream(policyOutput, FileMode.CreateNew);
    JsonSerializer.Serialize(file, new ContentPolicy(policyChannel, key.ExportSubjectPublicKeyInfoPem()));
    return 0;
}
if (args is ["verify", var packageFile, var trustedPolicy, var hostVersion])
{
    var document = ContentPack.Verify(ContentPack.ReadBounded(packageFile), ContentPack.ReadPolicy(trustedPolicy), Version.Parse(hostVersion));
    Console.WriteLine(JsonSerializer.Serialize(new { document.Sequence, document.Provenance, Count = document.Entries.Length }));
    return 0;
}
if (args is ["export", var output])
{
    using var file = new FileStream(output, FileMode.CreateNew);
    JsonSerializer.Serialize(file, ContentPack.ExportBuiltIn());
    return 0;
}
if (args is ["sign", var rows, var sequence, var minHost, var maxHost, var channel, var provenance, var privateKey, var destination])
{
    using var key = RSA.Create();
    key.ImportFromPem(File.ReadAllText(privateKey));
    if (key.KeySize < 2048) throw new InvalidDataException("KEY_TOO_SMALL");
    var document = new ContentDocument(1, "wiki-project-index", channel, long.Parse(sequence), minHost, maxHost,
        provenance, JsonSerializer.Deserialize<ContentRow[]>(ContentPack.ReadBounded(rows))!);
    var payload = JsonSerializer.SerializeToUtf8Bytes(document);
    var bytes = JsonSerializer.SerializeToUtf8Bytes(new ContentEnvelope(Convert.ToBase64String(payload),
        Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
    ContentPack.Verify(bytes, new(channel, key.ExportSubjectPublicKeyInfoPem()), Version.Parse(minHost));
    using var file = new FileStream(destination, FileMode.CreateNew);
    file.Write(bytes);
    Console.WriteLine("CONTENT_SIGNED " + document.Entries.Length);
    return 0;
}
if (args.Length >= 4 && args[0] is "install" or "fetch" or "status" or "rollback")
{
    var policy = ContentPack.ReadPolicy(args[2]);
    var version = Version.Parse(args[3]);
    switch (args[0])
    {
        case "fetch" when args.Length == 5:
            var uri = new Uri(args[4], UriKind.Absolute);
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("HTTPS_CONTENT_URL_REQUIRED");
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("CONTENT_DOWNLOAD_FAILED");
                if (response.Content.Headers.ContentLength > ContentPack.MaxBytes) throw new InvalidDataException("CONTENT_TOO_LARGE");
                using var incoming = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var bytes = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await incoming.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (bytes.Length + count > ContentPack.MaxBytes) throw new InvalidDataException("CONTENT_TOO_LARGE");
                    bytes.Write(buffer, 0, count);
                }
                ContentPack.Install(args[1], bytes.ToArray(), policy, version);
            }
            break;
        case "install" when args.Length == 5:
            ContentPack.Install(args[1], ContentPack.ReadBounded(args[4]), policy, version);
            break;
        case "rollback" when args.Length == 4:
            ContentPack.Rollback(args[1], policy, version);
            break;
        case "status" when args.Length == 4: break;
        default: throw new ArgumentException("INVALID_ARGUMENTS");
    }
    var selected = ContentPack.Load(args[1], policy, version);
    Console.WriteLine(JsonSerializer.Serialize(new { selected.Source, selected.Sequence, selected.Rejection, Count = selected.Entries.Count,
        Activation = "next Host start" }));
    return 0;
}
Console.Error.WriteLine("export <rows.json>\nsign <rows.json> <sequence> <min-host> <max-host> <channel> <provenance> <private.pem> <package.json>\ninstall <store> <trusted-policy.json> <host-version> <package.json>\nfetch <store> <trusted-policy.json> <host-version> <https-url>\nstatus|rollback <store> <trusted-policy.json> <host-version>");
return 2;
