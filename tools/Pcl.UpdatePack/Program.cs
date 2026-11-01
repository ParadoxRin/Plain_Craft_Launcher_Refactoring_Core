using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Updates;

// 在受信任发布机调用。签名私钥由本地文件传入，不生成、不上传、不内置任何私钥。
if (args.Length != 7)
{
    Console.Error.WriteLine("<publish-directory> <probe.json> <entrypoint> <package-https-url> <channel> <private-key.pem> <output-directory>");
    return 2;
}
var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[6]);
if (output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal) || output == source)
{
    throw new InvalidDataException("OUTPUT_MUST_BE_OUTSIDE_PUBLISH_DIRECTORY");
}
var hello = JsonSerializer.Deserialize<HostHello>(File.ReadAllBytes(args[1]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
if (hello.Build is null || new Uri(args[3]).Scheme != "https")
{
    throw new InvalidDataException("BUILD_METADATA_OR_HTTPS_REQUIRED");
}
using var key = RSA.Create();
key.ImportFromPem(File.ReadAllText(args[5]));
if (key.KeySize < 2048)
{
    throw new InvalidDataException("KEY_TOO_SMALL");
}
Directory.CreateDirectory(output);
var zip = Path.Combine(output, "host.zip");
var files = new List<HostUpdateFile>();
using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
{
    // 自包含发布目录必须先完成平台签名，再打包；文件内容随后不可改写。
    foreach (var file in Walk(source).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("SYMLINK_NOT_SUPPORTED");
        }
        var executable = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;
        var entry = archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
        entry.ExternalAttributes = (0x8000 | (executable ? 0x1c0 : 0x180)) << 16;
        using var stream = File.OpenRead(file);
        files.Add(new(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), executable));
    }
}
if (!files.Any(x => x.Path == args[2]))
{
    throw new InvalidDataException("ENTRYPOINT_MISSING");
}
using var zipped = File.OpenRead(zip);
var manifest = new HostUpdateManifest(1, args[4], hello.Build, hello.ProtocolVersion, hello.Capabilities,
    DateTimeOffset.UtcNow.AddDays(30), args[3], zipped.Length, Convert.ToHexString(SHA256.HashData(zipped)).ToLowerInvariant(),
    args[2], args[2].EndsWith(".dll", StringComparison.Ordinal), files.ToArray());
var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
var envelope = new HostUpdateEnvelope(Convert.ToBase64String(payload),
    Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
File.WriteAllBytes(Path.Combine(output, "feed.json"), JsonSerializer.SerializeToUtf8Bytes(envelope));
File.WriteAllText(Path.Combine(output, "public-key.pem"), key.ExportSubjectPublicKeyInfoPem());
Console.WriteLine("SIGNED_HOST_PACKAGE_CREATED");
return 0;

// 拒绝目录链接，避免发布机把包目录外的个人文件包含进更新包。
static IEnumerable<string> Walk(string folder)
{
    foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
    {
        if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("SYMLINK_NOT_SUPPORTED");
        }
        if (Directory.Exists(entry))
        {
            foreach (var child in Walk(entry))
            {
                yield return child;
            }
        }
        else
        {
            yield return entry;
        }
    }
}
