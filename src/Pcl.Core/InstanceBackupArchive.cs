using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：BackupVerification 汇集 SchemaVersion、Files、Bytes、HasFileHashes，供该模块传递状态。
public sealed record BackupVerification(int SchemaVersion, int Files, long Bytes, bool HasFileHashes)
{
    // [实现] ToString 的实现：计算并返回 HasFileHashes ? $"校验通过 · {Files} 个文件 · SHA-256 完整性一致" : $"旧版备份 · {Files} 个文件 · 无逐文件校验清单"。
    public override string ToString() => HasFileHashes
        ? $"校验通过 · {Files} 个文件 · SHA-256 完整性一致"
        : $"旧版备份 · {Files} 个文件 · 无逐文件校验清单";
}

/// <summary>Checks archive integrity before network access or registration. Hashes detect damage, not publisher authenticity.</summary>
public static class InstanceBackupArchive
{
    internal const long MaximumBytes = 100L * 1024 * 1024 * 1024;
    internal const int MaximumFiles = 200000;
    // [实现] AddAsync 的实现：准备 var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)；准备 var output = archive.CreateEntry("game/" + name, CompressionLevel.Fastest) .Open()；准备 var buffer = new byte[131072]。
    internal static async Task<JObject> AddAsync(ZipArchive archive, string name, Stream input, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = archive.CreateEntry("game/" + name, CompressionLevel.Fastest)
            .Open();
        var buffer = new byte[131072];
        long size = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            // [实现] 循环满足 (n = await input.ReadAsync(buffer, ct)) > 0 时：执行 size = checked(size + n)；按 size > MaximumBytes 分支处理；执行 hash.AppendData(buffer, 0, n)。
            size = checked(size + n);
            if (size > MaximumBytes)
                throw new InvalidDataException("备份文件超出体积限制。");
            hash.AppendData(buffer, 0, n);
            await output.WriteAsync(
                buffer.AsMemory(0, n),
                ct
            );
        }

        return new()
        {
            ["path"] = name,
            ["size"] = size,
            ["sha256"] = Convert.ToHexString(
                hash.GetHashAndReset()
            )
                .ToLowerInvariant()
        };
    }

    // [实现] VerifyAsync 的实现：准备 var archive = ZipFile.OpenRead(file)；准备 var result = await ReadAsync(archive, null, ct)；返回 result.Verification。
    public static async Task<BackupVerification> VerifyAsync(string file, CancellationToken ct = default)
    {
        using var archive = ZipFile.OpenRead(file);
        var result = await ReadAsync(archive, null, ct);
        return result.Verification;
    }

    // [实现] ReadAsync 的实现：按 archive.Entries.Count > MaximumFiles + 1 分支处理；准备 var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)；准备 long total = 0。
    internal static async Task<(JObject Manifest, BackupVerification Verification)> ReadAsync(ZipArchive archive, string? extractTo, CancellationToken ct)
    {
        if (archive.Entries.Count > MaximumFiles + 1)
            throw new InvalidDataException("备份文件数量超出限制。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var entries = new List<ZipArchiveEntry>();
        foreach (var entry in archive.Entries)
        {
            // [实现] 对 archive.Entries 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var name = entry.FullName.TrimEnd('/')；按 name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.A… 分支处理。
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.TrimEnd('/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) || name.Split('/')
                .Any(
                    x => x is ".." or "." or ""
                ))
                throw new InvalidDataException("备份路径无效：" + entry.FullName);
            if (!names.Add(
                name.Normalize(NormalizationForm.FormC)
            ))
                throw new InvalidDataException("备份存在重复、大小写或 Unicode 冲突路径：" + name);
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("备份包含符号链接。");
            if (name != "pcl-backup.json" && name != "game" && !name.StartsWith("game/", StringComparison.Ordinal))
                throw new InvalidDataException("备份包含未知根目录文件。");
            total = checked(total + entry.Length);
            if (total > MaximumBytes)
                throw new InvalidDataException("备份体积超出限制。");
            if (name.StartsWith("game/", StringComparison.Ordinal) && !entry.FullName.EndsWith('/'))
                entries.Add(entry);
        }

        var header = archive.GetEntry("pcl-backup.json") ?? throw new InvalidDataException("不是 PCL Mac 实例备份。");
        if (header.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("备份清单过大。");
        JObject manifest;
        using (var reader = new StreamReader(
            header.Open()
        ))
            manifest = JObject.Parse(
                await reader.ReadToEndAsync(ct)
            );
        var schema = (int? )manifest["schemaVersion"] ?? 0;
        if ((string? )manifest["format"] != "pcl-mac-instance-backup" || schema is not (1 or 2 or 3))
            throw new InvalidDataException("备份格式不支持。");
        var expected = new Dictionary<string, JObject>(StringComparer.Ordinal);
        if (schema == 3)
        {
            // [实现] 条件成立（schema == 3）时：按 manifest["files"] is not JArray files || files.Count != entries.Count 分支处理；遍历 files 处理 value。
            if (manifest["files"] is not JArray files || files.Count != entries.Count)
                throw new InvalidDataException("备份文件清单与归档不一致。");
            foreach (var value in files)
            {
                // [实现] 对 files 中的每一项：按 value is not JObject row || row["path"]?.Type != JTokenType.String || row["size"]?.Type != JTok… 分支处理；准备 var path = (string)row["path"]!；准备 var hash = (string)row["sha256"]!。
                if (value is not JObject row || row["path"]?.Type != JTokenType.String || row["size"]?.Type != JTokenType.Integer || row["sha256"]?.Type != JTokenType.String)
                    throw new InvalidDataException("备份校验记录无效。");
                var path = (string)row["path"]!;
                var hash = (string)row["sha256"]!;
                if ((long)row["size"]! < 0 || hash.Length != 64 || hash.Any(
                    c => !Uri.IsHexDigit(c)
                ) || !expected.TryAdd(path, row))
                    throw new InvalidDataException("备份校验记录重复或无效。");
            }
        }

        long bytes = 0;
        foreach (var entry in entries)
        {
            // [实现] 对 entries 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var relative = entry.FullName[5..]；准备 JObject? record = null。
            ct.ThrowIfCancellationRequested();
            var relative = entry.FullName[5..];
            JObject? record = null;
            if (schema == 3 && (!expected.TryGetValue(relative, out record) || (long)record["size"]! != entry.Length))
                throw new InvalidDataException("备份文件大小或清单不匹配：" + relative);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = entry.Open();
            Stream? output = null;
            try
            {
                // [实现] 在异常保护范围内：按 extractTo is not null 分支处理；准备 long read = 0；准备 var buffer = new byte[131072]。
                if (extractTo is not null)
                {
                    // [实现] 条件成立（extractTo is not null）时：准备 var target = ResourceInstaller.SafeTarget(extractTo, relative)；执行 Directory.CreateDirectory( Path.GetDirectoryName(target)! )；执行 output = new FileStream(target, FileMode.CreateNew, FileAccess.Write)。
                    var target = ResourceInstaller.SafeTarget(extractTo, relative);
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(target)!
                    );
                    output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                }

                long read = 0;
                var buffer = new byte[131072];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    // [实现] 循环满足 (count = await input.ReadAsync(buffer, ct)) > 0 时：执行 read = checked(read + count)；按 read > entry.Length 分支处理；执行 hash.AppendData(buffer, 0, count)。
                    read = checked(read + count);
                    if (read > entry.Length)
                        throw new InvalidDataException("备份文件解压大小不匹配。");
                    hash.AppendData(buffer, 0, count);
                    if (output is not null)
                        await output.WriteAsync(
                            buffer.AsMemory(0, count),
                            ct
                        );
                }

                if (read != entry.Length)
                    throw new InvalidDataException("备份文件不完整：" + relative);
                if (record is not null && !string.Equals(
                    Convert.ToHexString(
                        hash.GetHashAndReset()
                    ),
                    (string? )record["sha256"],
                    StringComparison.OrdinalIgnoreCase
                ))
                    throw new InvalidDataException("备份 SHA-256 不匹配：" + relative);
                bytes += read;
            }
            finally
            {
                // [实现] 无论成功或失败，退出前：按 output is not null 分支处理。
                if (output is not null)
                    await output.DisposeAsync();
            }
        }

        return (manifest, new(schema, entries.Count, bytes, schema == 3));
    }
}
