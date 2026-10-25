using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pcl.Core;
/// <summary>光影/资源包的配置事务。只恢复两个受控配置文件，不卸载资源、Iris 或用户文件。</summary>
public sealed class ActivationTransaction
{
    private readonly string root;
    private readonly Action<string>? checkpoint;
    private const string Journal = ".pcl-activation-transaction.json";
    private const string Active = ".pcl-last-activation.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private sealed record Activation(
        int Format,
        string Id,
        string Target,
        bool HadFile,
        string? BeforeSha256,
        string AppliedSha256
    );
    private sealed record Transaction(int Format, string Kind, string OperationId, Activation Activation);
    public ActivationTransaction(string gameDirectory) : this(gameDirectory, null)
    {
    }

    // 故障注入只供程序集内测试；生产入口不接受脚本或来自 RPC 的故障参数。
    internal ActivationTransaction(string gameDirectory, Action<string>? checkpoint)
    {
        root = gameDirectory;
        this.checkpoint = checkpoint;
    }

    private string Safe(string path) => ResourceInstaller.SafeTarget(root, path);
    private static string Hash(byte[] bytes) => Convert.ToHexString(
        SHA256.HashData(bytes)
    )
        .ToLowerInvariant();
    private string Folder(Activation activation) => ".pcl-backups/activation-" + activation.Id;
    private byte[] Read(string relative)
    {
        var path = Safe(relative);
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("配置事务文件过大，未修改配置。");
        }

        return File.ReadAllBytes(path);
    }

    private string? Current(string relative) => File.Exists(
        Safe(relative)
    )
        ? Hash(
            Read(relative)
        )
        : null;
    // 同目录临时文件先刷盘再原子替换。中断后旧文件或新文件完整存在，绝不原地截断。
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

    private void WriteJson<T>(string path, T value) => Write(
        path,
        JsonSerializer.SerializeToUtf8Bytes(value, Json)
    );
    // 回执不信任路径或内容；旧格式缺少备份哈希时保留文件，要求人工核查而不盲目覆盖。
    private void Validate(Activation activation)
    {
        if (activation.Format != 2 || !Guid.TryParseExact(activation.Id, "N", out _) || activation.Target is not ("config/iris.properties" or "options.txt"))
        {
            throw new InvalidDataException("配置事务记录无效或来自旧版，请保留备份并检查，未覆盖配置。");
        }

        if (Hash(
            Read(
                Folder(activation) + "/applied.config"
            )
        ) != activation.AppliedSha256 || (activation.HadFile && Hash(
            Read(
                Folder(activation) + "/previous.config"
            )
        ) != activation.BeforeSha256) || (!activation.HadFile && activation.BeforeSha256 is not null))
        {
            throw new InvalidDataException("配置备份校验失败，未覆盖配置。");
        }
    }

    /// <summary>调用者持有资源锁；先完成旧事务，再记录本次不可变备份和提交意图。</summary>
    public void Apply(string relative, string text, CancellationToken ct = default)
    {
        Recover();
        if (relative is not ("config/iris.properties" or "options.txt"))
        {
            throw new InvalidDataException("不允许修改此配置。");
        }

        ct.ThrowIfCancellationRequested();
        var before = File.Exists(
            Safe(relative)
        )
            ? Read(relative)
            : null;
        var after = Encoding.UTF8.GetBytes(text);
        if (after.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("配置超过事务大小限制，未修改。");
        }

        var activation = new Activation(
            2,
            Guid.NewGuid()
                .ToString("N"),
            relative,
            before is not null,
            before is null
                ? null
                : Hash(before),
            Hash(after)
        );
        if (before is not null)
        {
            Write(
                Folder(activation) + "/previous.config",
                before
            );
        }

        Write(
            Folder(activation) + "/applied.config",
            after
        );
        ct.ThrowIfCancellationRequested();
        // 提交意图落盘后不再观察取消；这段只有配置和小回执的原子替换。
        WriteJson(
            Journal,
            new Transaction(1, "apply", activation.Id, activation)
        );
        checkpoint?.Invoke("prepared");
        Recover();
    }

    /// <summary>撤销以独立操作身份记账；冲突检查在写入意图之前，重试不会再次修改文件。</summary>
    public void Undo(CancellationToken ct = default, string? operationId = null)
    {
        Recover();
        ct.ThrowIfCancellationRequested();
        var key = Hash(
            Encoding.UTF8.GetBytes(
                operationId ?? Guid.NewGuid()
                    .ToString("N")
            )
        );
        var completed = ".pcl-backups/undo-" + key + ".json";
        if (File.Exists(
            Safe(completed)
        ))
        {
            var prior = JsonSerializer.Deserialize<Transaction>(
                Read(completed),
                Json
            );
            if (prior?.Kind != "undo" || prior.OperationId != key || prior.Format != 1)
            {
                throw new InvalidDataException("撤销回执无效。");
            }

            return;
        }

        if (!File.Exists(
            Safe(Active)
        ))
        {
            throw new InvalidOperationException("没有可撤销的应用记录。");
        }

        var activation = JsonSerializer.Deserialize<Activation>(
            Read(Active),
            Json
        ) ?? throw new InvalidDataException("配置回执缺失。");
        Validate(activation);
        if (Current(activation.Target) != activation.AppliedSha256)
        {
            throw new IOException("游戏或其他程序已修改配置，未覆盖；请保留备份并检查。");
        }

        ct.ThrowIfCancellationRequested();
        WriteJson(
            Journal,
            new Transaction(1, "undo", key, activation)
        );
        checkpoint?.Invoke("prepared");
        Recover();
        ResourceHistory.Record(
            root,
            "撤销应用",
            [
                activation.Target
            ]
        );
    }

    /// <summary>进程中断后向已记录的提交方向续做；当前内容既非前态也非后态时阻止启动。</summary>
    public void Recover()
    {
        if (!File.Exists(
            Safe(Journal)
        ))
        {
            return;
        }

        var transaction = JsonSerializer.Deserialize<Transaction>(
            Read(Journal),
            Json
        ) ?? throw new InvalidDataException("配置事务缺失。");
        if (transaction.Format != 1 || transaction.Kind is not ("apply" or "undo") || transaction.OperationId.Length is not (32 or 64) || !transaction.OperationId.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("配置事务格式无效，启动已阻止。");
        }

        var activation = transaction.Activation;
        Validate(activation);
        var current = Current(activation.Target);
        var before = activation.HadFile
            ? activation.BeforeSha256
            : null;
        var desired = transaction.Kind == "apply"
            ? activation.AppliedSha256
            : before;
        var expected = transaction.Kind == "apply"
            ? before
            : activation.AppliedSha256;
        if (current != expected && current != desired)
        {
            throw new IOException("配置事务恢复遇到外部修改，启动已阻止；原文件及备份已保留。");
        }

        if (current != desired)
        {
            if (desired is null)
            {
                File.Delete(
                    Safe(activation.Target)
                );
            }
            else
            {
                Write(
                    activation.Target,
                    Read(
                        Folder(activation) + (transaction.Kind == "apply"
                            ? "/applied.config"
                            : "/previous.config")
                    )
                );
            }
        }

        checkpoint?.Invoke("target-written");
        if (transaction.Kind == "apply")
        {
            WriteJson(Active, activation);
        }
        else
        {
            File.Delete(
                Safe(Active)
            );
            WriteJson(".pcl-backups/undo-" + transaction.OperationId + ".json", transaction);
        }

        checkpoint?.Invoke("receipt-written");
        WriteJson(
            Folder(activation) + "/" + transaction.Kind + "-completed.json",
            transaction
        );
        File.Delete(
            Safe(Journal)
        );
    }

    /// <summary>启动准备与资源写入共用 OS 独占锁；无法恢复时异常传播，游戏不被创建。</summary>
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
        new ActivationTransaction(gameDirectory).Recover();
    }
}
