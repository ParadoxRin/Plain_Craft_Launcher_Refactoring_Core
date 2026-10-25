using System.Security.Cryptography;
using System.Text.Json;

namespace Pcl.Core;
/// <summary>资源的显式用途登记。先登记再安装；失败留下的用途保守保留，绝不据此删除用户文件。</summary>
public static class ResourceOwnership
{
    private const string FileName = ".pcl-resource-claims.json";
    public static Dictionary<string, string[]> Read(string root)
    {
        var path = ResourceInstaller.SafeTarget(root, FileName);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                File.ReadAllBytes(path)
            ) ?? throw new InvalidDataException("资源归属清单无效。")
            : new(StringComparer.Ordinal);
    }

    public static void Claim(string root, IEnumerable<string> projects, string owner)
    {
        var claims = Read(root);
        foreach (var project in projects)
        {
            claims[project] = claims.GetValueOrDefault(
                project,
                []
            )
                .Append(owner)
                .Distinct(StringComparer.Ordinal)
                .Order()
                .ToArray();
        }

        ResourceTransactionFiles.Write(
            root,
            FileName,
            JsonSerializer.SerializeToUtf8Bytes(claims)
        );
    }
}

/// <summary>事务文件使用同目录临时文件、刷盘和原子替换；路径统一拒绝符号链接和越界。</summary>
internal static class ResourceTransactionFiles
{
    internal static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(
            SHA256.HashData(input)
        )
            .ToLowerInvariant();
    }

    internal static void Write(string root, string relative, byte[] data)
    {
        var target = ResourceInstaller.SafeTarget(root, relative);
        Directory.CreateDirectory(
            Path.GetDirectoryName(target)!
        );
        var temp = ResourceInstaller.SafeTarget(
            root,
            relative + ".txn-" + Guid.NewGuid()
                .ToString("N")
        );
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(data);
                output.Flush(true);
            }

            File.Move(temp, target, true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
