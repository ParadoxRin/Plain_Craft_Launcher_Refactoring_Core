using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core;
// 安装器恢复接缝：只从本次可信元数据和新生成文件形成 checkpoint，不从遗留 .failed 目录恢复执行状态。
public sealed class LoaderRecoveryContext(Action<string, string, string, IReadOnlyList<DownloadItem>> checkpoint)
{
    public void Report(
        string stage,
        string artifactHash,
        string metadataHash,
        IReadOnlyList<DownloadItem>? files = null
    ) => checkpoint(
        stage,
        artifactHash,
        metadataHash,
        files ?? []
    );
    // 已发布实例的恢复只替换重新验证的安装器文件；复制和校验在可取消阶段，最后逐文件原子替换、元数据最后发布。
    public void Publish(string stage, string target, CancellationToken ct = default)
    {
        ValidatePath(stage);
        ValidatePath(target);
        if (!Directory.Exists(target))
        {
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
            return;
        }

        var expectedProfile = JsonFiles.Read(
            Path.Combine(stage, "profile.json")
        );
        var actualProfile = JsonFiles.Read(
            Path.Combine(target, "profile.json")
        );
        foreach (var key in new[]
        {
            "id",
            "gameVersion",
            "loader",
            "loaderVersion"
        }

        )
        {
            if (!JToken.DeepEquals(expectedProfile[key], actualProfile[key]))
            {
                throw new OperationInstallException("RECOVERY_TARGET_CONFLICT");
            }
        }

        if (actualProfile["displayName"] is not null)
        {
            expectedProfile["displayName"] = actualProfile["displayName"]!.DeepClone();
            JsonFiles.Write(
                Path.Combine(stage, "profile.json"),
                expectedProfile
            );
        }

        var files = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories)
            .Where(
                x => Path.GetRelativePath(stage, x)
                    .StartsWith("libraries/", StringComparison.Ordinal) || Path.GetDirectoryName(x) == stage && (Path.GetFileName(x)is "version.json" or "profile.json" || Path.GetFileName(x)
                    .EndsWith(".jar", StringComparison.Ordinal))
            )
            .OrderBy(
                x => Path.GetFileName(x) == "profile.json"
                    ? 2
                    : Path.GetFileName(x) == "version.json"
                        ? 1
                        : 0
            )
            .ToArray();
        var prepared = new List<(string Temporary, string Target)>();
        try
        {
            foreach (var source in files)
            {
                ct.ThrowIfCancellationRequested();
                ValidatePath(source);
                var relative = Path.GetRelativePath(stage, source);
                var destination = ResourceInstaller.SafeTarget(target, relative);
                CheckNoLinks(target, destination);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)!
                );
                var temporary = destination + ".pcl-recovery-" + Guid.NewGuid()
                    .ToString("N");
                prepared.Add((temporary, destination));
                using (var input = File.OpenRead(source))
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[131072];
                        int count;
                        while ((count = input.Read(buffer)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, count);
                        }

                        output.Flush(true);
                    }

                if (!SHA256.HashData(
                    File.ReadAllBytes(source)
                )
                    .AsSpan()
                    .SequenceEqual(
                        SHA256.HashData(
                            File.ReadAllBytes(temporary)
                        )
                    ))
                {
                    throw new InvalidDataException("RECOVERY_COPY_INVALID");
                }
            }

            ct.ThrowIfCancellationRequested();
            // 此后不再取消；不得把未完成复制/解压包含在不可中断提交区间。
            foreach (var file in prepared)
            {
                CheckNoLinks(target, file.Target);
                File.Move(file.Temporary, file.Target, true);
            }

            Directory.Delete(stage, true);
        }
        finally
        {
            foreach (var file in prepared)
            {
                if (File.Exists(file.Temporary))
                {
                    File.Delete(file.Temporary);
                }
            }
        }
    }

    private static void CheckNoLinks(string root, string path)
    {
        for (string? node = path; node is not null; node = Path.GetDirectoryName(node))
        {
            if (new FileInfo(node).LinkTarget is not null)
            {
                throw new InvalidDataException("RECOVERY_PATH_UNSAFE");
            }
        }
    }

    public static void ValidatePath(string path) => CheckNoLinks(
        "",
        Path.GetFullPath(path)
    );
    public static string Hash(JToken metadata) => Convert.ToHexString(
        SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                metadata.ToString(Newtonsoft.Json.Formatting.None)
            )
        )
    );
}
