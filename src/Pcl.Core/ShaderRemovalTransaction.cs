using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pcl.Core;
/// <summary>卸载单个受管理光影包：提交前完整校验包和清单，持久化后可向前恢复，不删除目录。</summary>
public sealed class ShaderRemovalTransaction
{
    private const string Journal = ".pcl-shader-removal.json";
    private const string Manifest = ".pcl-resources.json";
    private readonly string root;
    private readonly Action<string>? checkpoint;
    private sealed record Package(string Target, string Sha256, string Backup);
    private sealed record Receipt(int Format, string Id, Package[] Packages, string BeforeManifest, string AfterManifest);
    public ShaderRemovalTransaction(string root) : this(root, null)
    {
    }

    internal ShaderRemovalTransaction(string root, Action<string>? checkpoint)
    {
        this.root = root;
        this.checkpoint = checkpoint;
    }

    private string Safe(string relative) => ResourceInstaller.SafeTarget(root, relative);
    private static string Key(string operation) => Convert.ToHexString(
        SHA256.HashData(
            Encoding.UTF8.GetBytes(operation)
        )
    )
        .ToLowerInvariant();
    private string Hash(string relative)
    {
        using var input = File.OpenRead(
            Safe(relative)
        );
        return Convert.ToHexString(
            SHA256.HashData(input)
        )
            .ToLowerInvariant();
    }

    private string Folder(string id) => ".pcl-backups/shader-remove-" + id;
    private void Copy(string from, string to)
    {
        var path = Safe(to);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!
        );
        using var input = File.OpenRead(
            Safe(from)
        );
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }

    private void Write(string relative, byte[] bytes)
    {
        var path = Safe(relative);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!
        );
        var temporary = Safe(
            relative + ".txn-" + Guid.NewGuid()
                .ToString("N")
        );
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Host 操作重放只返回已持久化的结果，不卸载后续重新安装的同名资源。
    public bool Completed(string? operationId)
    {
        if (operationId is null)
        {
            return false;
        }

        var path = Safe(
            Folder(
                Key(operationId)
            ) + "/completed.json"
        );
        return File.Exists(path) && JsonSerializer.Deserialize<Receipt>(
            File.ReadAllBytes(path)
        )?.Id == Key(operationId);
    }

    public string RemoveManaged(
        IReadOnlyList<ManagedResource> selected,
        CancellationToken ct = default,
        string? operationId = null
    )
    {
        Recover();
        ct.ThrowIfCancellationRequested();
        var id = Key(
            operationId ?? Guid.NewGuid()
                .ToString("N")
        );
        if (Completed(operationId))
        {
            return Safe(
                Folder(id)
            );
        }

        var manifestBytes = File.ReadAllBytes(
            Safe(Manifest)
        );
        var owned = Newtonsoft.Json.JsonConvert.DeserializeObject<ManagedResource[]>(
            Encoding.UTF8.GetString(manifestBytes)
        ) ?? [];
        if (selected.Count == 0 || selected.Count > 128 || selected.Any(
            item => item.Kind != ResourceKind.Shader || !owned.Any(
                x => x.ProjectId == item.ProjectId && x.FileName == item.FileName && x.Sha1 == item.Sha1 && x.Enabled == item.Enabled
            )
        ))
        {
            throw new IOException("只允许卸载清单中受管理的光影包，未修改任何文件。");
        }

        var conflicts = new List<string>();
        var targets = selected.Select(
            item => "shaderpacks/" + item.FileName + (item.Enabled
                ? ""
                : ".disabled")
        )
            .ToArray();
        for (var i = 0; i < selected.Count; i++)
        {
            ValidateTarget(targets[i]);
            var item = selected[i];
            if (!DownloadService.IsValid(
                new(
                    new Uri("https://localhost/unused"),
                    Safe(targets[i]),
                    item.Size,
                    item.Sha1
                )
            ))
            {
                conflicts.Add(targets[i]);
            }
        }

        if (conflicts.Count > 0)
        {
            throw new IOException(
                "光影包已修改或缺失，整次卸载未执行：" + string.Join("、", conflicts)
            );
        }

        if (Directory.Exists(
            Safe(
                Folder(id)
            )
        ))
        {
            Directory.Move(
                Safe(
                    Folder(id)
                ),
                Safe(
                    Folder(id) + "-abandoned-" + Guid.NewGuid()
                        .ToString("N")
                )
            );
        }

        var folder = Folder(id);
        var packages = new List<Package>();
        for (var i = 0; i < targets.Length; i++)
        {
            var backup = i + ".zip";
            Copy(targets[i], folder + "/" + backup);
            packages.Add(
                new(
                    targets[i],
                    Hash(targets[i]),
                    backup
                )
            );
        }

        Write(folder + "/manifest-before.json", manifestBytes);
        Write(
            folder + "/manifest-after.json",
            Encoding.UTF8.GetBytes(
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    owned.Where(
                        x => !selected.Any(
                            item => item.ProjectId == x.ProjectId
                        )
                    )
                        .ToArray()
                )
            )
        );
        var receipt = new Receipt(
            1,
            id,
            packages.ToArray(),
            Hash(folder + "/manifest-before.json"),
            Hash(folder + "/manifest-after.json")
        );
        // 备份期间的变化同样在提交意图之前拒绝。
        if (Hash(Manifest) != receipt.BeforeManifest || packages.Any(
            x => Hash(folder + "/" + x.Backup) != x.Sha256
        ))
        {
            throw new IOException("卸载备份期间文件变化，未修改实例。");
        }

        for (var i = 0; i < selected.Count; i++)
        {
            if (!DownloadService.IsValid(
                new(
                    new Uri("https://localhost/unused"),
                    Safe(targets[i]),
                    selected[i].Size,
                    selected[i].Sha1
                )
            ))
            {
                throw new IOException("备份期间光影包发生变化，未卸载：" + targets[i]);
            }
        }

        ct.ThrowIfCancellationRequested();
        Write(
            Journal,
            JsonSerializer.SerializeToUtf8Bytes(receipt)
        );
        checkpoint?.Invoke("prepared");
        Recover();
        ResourceHistory.Record(
            root,
            "卸载光影包（保留配置与环境）",
            targets,
            Safe(folder)
        );
        return Safe(folder);
    }

    private static void ValidateTarget(string target)
    {
        var parts = target.Split('/');
        if (parts.Length != 2 || parts[0] != "shaderpacks" || parts[1].Length == 0 || !(target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("光影卸载路径无效。");
        }
    }

    /// <summary>先验证所有目标与备份，再进行任何恢复写入；冲突时保留整个现场并阻止启动。</summary>
    public void Recover()
    {
        if (!File.Exists(
            Safe(Journal)
        ))
        {
            return;
        }

        var receipt = JsonSerializer.Deserialize<Receipt>(
            File.ReadAllBytes(
                Safe(Journal)
            )
        ) ?? throw new InvalidDataException("卸载回执缺失。");
        if (receipt.Format != 1 || receipt.Id.Length != 64 || !receipt.Id.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("卸载回执无效。");
        }

        var folder = Folder(receipt.Id);
        if (receipt.Packages.Length == 0 || receipt.Packages.Length > 128)
        {
            throw new InvalidDataException("卸载清单无效。");
        }

        for (var i = 0; i < receipt.Packages.Length; i++)
        {
            var package = receipt.Packages[i];
            ValidateTarget(package.Target);
            if (package.Backup != i + ".zip" || Hash(folder + "/" + package.Backup) != package.Sha256)
            {
                throw new InvalidDataException("光影备份已变化，恢复未执行。");
            }
        }

        if (Hash(folder + "/manifest-before.json") != receipt.BeforeManifest || Hash(folder + "/manifest-after.json") != receipt.AfterManifest)
        {
            throw new InvalidDataException("卸载清单备份已变化，恢复未执行。");
        }

        var manifestHash = Hash(Manifest);
        var conflicts = receipt.Packages.Where(
            x => File.Exists(
                Safe(x.Target)
            ) && Hash(x.Target) != x.Sha256
        )
            .Select(
                x => x.Target
            )
            .ToList();
        if (manifestHash != receipt.BeforeManifest && manifestHash != receipt.AfterManifest)
        {
            conflicts.Add(Manifest);
        }

        if (conflicts.Count > 0)
        {
            throw new IOException(
                "卸载事务冲突：" + string.Join("、", conflicts) + "；未覆盖，启动已阻止。"
            );
        }

        foreach (var package in receipt.Packages)
        {
            File.Delete(
                Safe(package.Target)
            );
            checkpoint?.Invoke("package-removed");
        }

        Write(
            Manifest,
            File.ReadAllBytes(
                Safe(folder + "/manifest-after.json")
            )
        );
        checkpoint?.Invoke("manifest-written");
        Write(
            folder + "/completed.json",
            JsonSerializer.SerializeToUtf8Bytes(receipt)
        );
        File.Delete(
            Safe(Journal)
        );
    }

    public static void RecoverBeforeLaunch(string gameDirectory)
    {
        if (!File.Exists(
            ResourceInstaller.SafeTarget(gameDirectory, Journal)
        ))
        {
            return;
        }

        using var lease = new FileStream(
            ResourceInstaller.SafeTarget(gameDirectory, ".resources.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        new ShaderRemovalTransaction(gameDirectory).Recover();
    }
}
