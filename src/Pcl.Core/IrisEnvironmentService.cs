using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JsonConvert = Newtonsoft.Json.JsonConvert;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Core;
/// <summary>跟踪 Iris 安装新增的组件，并在 Host 内按实际 JAR 依赖和其他用途计算安全卸载集合。</summary>
public sealed class IrisEnvironmentService(string root, string loader, string gameVersion)
{
    public const string IrisProject = "YL57xq9U";
    private const string EnvironmentFile = ".pcl-iris-environment.json";
    private const string Manifest = ".pcl-resources.json";
    private const string Journal = ".pcl-iris-uninstall.json";
    private sealed record Owned(string Project, string Path, string Sha1, string? Sha256);
    private sealed record EnvironmentReceipt(
        int Format,
        string Id,
        string State,
        Owned[] Introduced,
        string[] Preserved,
        string PlanHash
    );
    private sealed record BackupFile(string Path, string Sha256, string Backup);
    private sealed record UninstallReceipt(
        int Format,
        string Id,
        string Phase,
        BackupFile[] Removed,
        Dictionary<string, string> RetainedMods,
        string ManifestBefore,
        string ManifestAfter,
        string EnvironmentBefore,
        string EnvironmentAfter,
        string ClaimsHash,
        string[] RetainedReasons
    );
    private string Safe(string relative) => ResourceInstaller.SafeTarget(root, relative);
    private string Hash(string relative) => ResourceTransactionFiles.Hash(
        Safe(relative)
    );
    private void Write<T>(string relative, T value) => ResourceTransactionFiles.Write(
        root,
        relative,
        JsonSerializer.SerializeToUtf8Bytes(value)
    );
    private T Read<T>(string relative) => JsonSerializer.Deserialize<T>(
        File.ReadAllBytes(
            Safe(relative)
        )
    ) ?? throw new InvalidDataException("Iris 事务记录无效。");
    private FileStream Lease()
    {
        Directory.CreateDirectory(root);
        return new(
            Safe(".resources.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
    }

    private ManagedResource[] Managed() => File.Exists(
        Safe(Manifest)
    )
        ? JsonConvert.DeserializeObject<ManagedResource[]>(
            File.ReadAllText(
                Safe(Manifest)
            )
        ) ?? throw new InvalidDataException("资源清单无效。")
        : [];
    private static string Digest(string text) => Convert.ToHexString(
        SHA256.HashData(
            Encoding.UTF8.GetBytes(text)
        )
    )
        .ToLowerInvariant();
    private string ClaimsHash() => File.Exists(
        Safe(".pcl-resource-claims.json")
    )
        ? Hash(".pcl-resource-claims.json")
        : "absent";
    /// <summary>安装前固化“原本不存在”的文件身份。既有组件不归环境所有，不暗中升级共享文件。</summary>
    public async Task<IReadOnlyList<ManagedResource>> InstallAsync(
        ResourceInstallPlan plan,
        DownloadService downloads,
        IProgress<InstallProgress>? progress,
        CancellationToken ct
    )
    {
        RequireFabric();
        if (!plan.Releases.Any(
            x => x.ProjectId == IrisProject
        ))
        {
            throw new InvalidDataException("环境计划未包含 Iris。");
        }

        EnvironmentReceipt receipt;
        using (var lease = Lease())
        {
            Recover();
            ct.ThrowIfCancellationRequested();
            var managed = Managed();
            var planHash = Digest(
                JsonSerializer.Serialize(
                    plan.Releases.Select(
                        x => new
                        {
                            x.ProjectId,
                            x.Id,
                            x.File.Sha1
                        }
                    )
                        .OrderBy(
                            x => x.ProjectId
                        )
                )
            );
            if (File.Exists(
                Safe(EnvironmentFile)
            ))
            {
                receipt = Read<EnvironmentReceipt>(EnvironmentFile);
                Validate(receipt);
                if (receipt.State == "installed" && managed.Any(
                    x => x.ProjectId == IrisProject
                ))
                {
                    return managed;
                }

                if (receipt.State == "installing" && receipt.PlanHash != planHash)
                {
                    throw new IOException("未完成的 Iris 安装计划已变化，保留现场，请先处理原安装任务。");
                }
            }
            else
            {
                receipt = new(
                    1,
                    Guid.NewGuid()
                        .ToString("N"),
                    "new",
                    [],
                    [],
                    planHash
                );
            }

            if (receipt.State != "installing")
            {
                // 旧版本/手动安装的 Iris 没有可信的安装前快照，不能追认为本次新增。
                if (Directory.Exists(
                    Safe("mods")
                ) && Directory.GetFiles(
                    Safe("mods"),
                    "*.jar"
                )
                    .Any(
                        file => FabricModReader.Read(file)
                            .Any(
                                x => x.Id == "iris"
                            )
                    ))
                {
                    return managed;
                }

                var added = new List<Owned>();
                var preserved = new List<string>();
                foreach (var release in plan.Releases)
                {
                    var name = LocalResourcePreferences.FileName(
                        release.File.Name,
                        release.DisplayName,
                        downloads.Tasks?.Options.FileNameStyle ?? "original"
                    );
                    ResourceInstaller.ValidateFileName(name);
                    var path = "mods/" + name;
                    var existing = managed.FirstOrDefault(
                        x => x.ProjectId == release.ProjectId
                    );
                    if (existing is not null || File.Exists(
                        Safe(path)
                    ))
                    {
                        if (existing is null || existing.FileName != name || !existing.Enabled || existing.Sha1 != release.File.Sha1 || !DownloadService.IsValid(
                            new(
                                release.File.Url,
                                Safe(path),
                                release.File.Size,
                                release.File.Sha1
                            )
                        ))
                        {
                            throw new IOException("Iris 安装需要改动既有组件，未覆盖；请先单独处理：" + name);
                        }

                        preserved.Add(name + "：安装前已存在");
                    }
                    else
                    {
                        added.Add(
                            new(release.ProjectId, path, release.File.Sha1, null)
                        );
                    }
                }

                receipt = new(
                    1,
                    Guid.NewGuid()
                        .ToString("N"),
                    "installing",
                    added.ToArray(),
                    preserved.ToArray(),
                    planHash
                );
                Write(EnvironmentFile, receipt);
            }
        }

        // Host 串行写入通道持续拥有本操作；ResourceInstaller 自己取得文件锁，避免递归加锁。
        var store = new ResourceInstaller(root, downloads, loader, gameVersion);
        IReadOnlyList<ManagedResource> result;
        try
        {
            result = await store.InstallAsync(plan, progress, ct, "iris:" + receipt.Id);
        }
        catch
        {
            // 下载取消/失败且尚未引入组件时，恢复可启动状态；有部分写入则保留现场阻止启动。
            using var failedLease = Lease();
            if (receipt.Introduced.All(
                x => !File.Exists(
                    Safe(x.Path)
                )
            ))
            {
                Write(
                    EnvironmentFile,
                    receipt with
                    {
                        State = "uninstalled"
                    }
                );
            }

            throw;
        }

        using (var lease = Lease())
        {
            var introduced = receipt.Introduced.Select(
                x =>
                {
                    if (!File.Exists(
                        Safe(x.Path)
                    ) || !DownloadService.IsValid(
                        new(
                            new Uri("https://localhost/unused"),
                            Safe(x.Path),
                            -1,
                            x.Sha1
                        )
                    ))
                    {
                        throw new IOException("Iris 新增组件未完成校验：" + x.Path);
                    }

                    return x with
                    {
                        Sha256 = Hash(x.Path)
                    };
                }
            )
                .ToArray();
            Write(
                EnvironmentFile,
                receipt with
                {
                    State = "installed",
                    Introduced = introduced
                }
            );
        }

        return result;
    }

    private void RequireFabric()
    {
        if (loader != "fabric")
        {
            throw new NotSupportedException("环境卸载当前适用于有可信安装记录的 Fabric / Iris 实例，未修改其他后端。");
        }
    }

    private static void Validate(EnvironmentReceipt receipt)
    {
        if (receipt.Format != 1 || !Guid.TryParseExact(receipt.Id, "N", out _) || receipt.State is not ("installed" or "installing" or "uninstalled"))
        {
            throw new InvalidDataException("Iris 安装归属记录无效。");
        }

        foreach (var item in receipt.Introduced)
        {
            ValidateModPath(item.Path);
        }
    }

    private static void ValidateModPath(string path)
    {
        var parts = path.Split('/');
        if (parts.Length != 2 || parts[0] != "mods" || !parts[1].EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Iris 组件路径无效。");
        }

        ResourceInstaller.ValidateFileName(parts[1]);
    }

    private Dictionary<string, string> ModInventory()
    {
        if (!Directory.Exists(
            Safe("mods")
        ))
        {
            return new(StringComparer.Ordinal);
        }

        // 逐层拒绝链接，再枚举子目录，避免符号链接环或跨出实例目录。
        var inventory = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(
            Safe("mods")
        );
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(root, entry)
                    .Replace('\\', '/');
                _ = Safe(relative);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    inventory.Add(
                        relative,
                        Hash(relative)
                    );
                }
            }
        }

        return inventory.ToDictionary(
            x => x.Key,
            x => x.Value,
            StringComparer.Ordinal
        );
    }

    private string Fingerprint(Dictionary<string, string> inventory) => Digest(
        JsonSerializer.Serialize(inventory) + Hash(EnvironmentFile) + Hash(Manifest) + ClaimsHash()
    );
    public View.ShaderEnvironmentPreview Preview()
    {
        using var lease = Lease();
        Recover();
        return Plan();
    }

    // 依赖闭包只做保守保留：保留任何用户组件、其他用途，以及仍被保留组件引用的候选；不猜可替代版本。
    private View.ShaderEnvironmentPreview Plan()
    {
        RequireFabric();
        if (!File.Exists(
            Safe(EnvironmentFile)
        ))
        {
            return new(
                "",
                [],
                [],
                [
                    "没有可验证的 Iris 环境安装前记录；旧版或手动安装的组件保持原样。"
                ]
            );
        }

        var receipt = Read<EnvironmentReceipt>(EnvironmentFile);
        Validate(receipt);
        if (receipt.State != "installed")
        {
            return new(
                "",
                [],
                [],
                [
                    receipt.State == "uninstalled"
                        ? "该环境已卸载。"
                        : "Iris 安装尚未完成，请先重试原安装流程。"
                ]
            );
        }

        // Fabric 官方允许用此文件替换依赖声明；未实现该语义前不能只按 JAR 计算删除集合。
        if (File.Exists(
            Safe("config/fabric_loader_dependencies.json")
        ))
        {
            return new(
                "",
                [],
                [],
                [
                    "检测到 Fabric 自定义依赖覆盖配置，不能可靠判断共享组件；环境未卸载。"
                ]
            );
        }

        var inventory = ModInventory();
        var conflicts = new List<string>();
        var retained = receipt.Preserved.ToList();
        var managed = Managed();
        var candidates = receipt.Introduced.ToDictionary(
            x => x.Path,
            StringComparer.Ordinal
        );
        foreach (var item in receipt.Introduced)
        {
            var row = managed.FirstOrDefault(
                x => x.ProjectId == item.Project
            );
            if (row is null || "mods/" + row.FileName != item.Path || !row.Enabled || row.Sha1 != item.Sha1 || !inventory.TryGetValue(item.Path, out var hash) || hash != item.Sha256)
            {
                conflicts.Add(item.Path + "：内容、位置、启用状态或清单归属已变化");
            }
        }

        if (conflicts.Count > 0)
        {
            return new(
                Fingerprint(inventory),
                [],
                retained.ToArray(),
                conflicts.ToArray()
            );
        }

        var claims = ResourceOwnership.Read(root);
        foreach (var item in receipt.Introduced)
        {
            if (claims.GetValueOrDefault(
                item.Project,
                []
            )
                .Any( // 每个实例只有一个 Iris 环境；已卸载的旧环境用途不能伪装为新的外部依赖。
                    owner => !owner.StartsWith("iris:", StringComparison.Ordinal)
                ))
            {
                candidates.Remove(item.Path);
                retained.Add(item.Path + "：其他显式安装用途");
            }
        }

        var mods = new Dictionary<string, IReadOnlyList<FabricModInfo>>(StringComparer.Ordinal);
        foreach (var file in inventory.Keys)
        {
            if (file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            {
                mods[file] = FabricModReader.Read(
                    Safe(file)
                );
            }
        }

        // 一个依赖 ID 可以同时由 Iris 的嵌套模块和独立 Fabric API 提供。
        // 先复用已保留且启用的满足版本约束的提供者，再优先保留非 Iris 提供者，避免误把 Iris 判成必需。
        bool Provides(FabricModInfo mod, Newtonsoft.Json.Linq.JProperty dependency) => (mod.Id == dependency.Name || mod.Provides.Contains(dependency.Name)) && FabricVersionPredicate.Matches(mod.Version, dependency.Value);
        bool changed;
        do
        {
            changed = false;
            var wanted = mods.Where(
                x => !candidates.ContainsKey(x.Key)
            )
                .SelectMany(
                    x => x.Value
                )
                .SelectMany(
                    x => x.Depends.Properties()
                )
                .ToArray();
            void Retain(string path)
            {
                if (candidates.Remove(path))
                {
                    retained.Add(path + "：被保留组件依赖（包括嵌套/别名/禁用组件）");
                    changed = true;
                }
            }

            foreach (var dependency in wanted)
            {
                var providers = mods.Where(
                    x => !x.Key.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && x.Value.Any(
                        m => Provides(m, dependency)
                    )
                )
                    .Select(
                        x => x.Key
                    )
                    .ToArray();
                if (providers.Any(
                    path => !candidates.ContainsKey(path)
                ))
                {
                    continue;
                }

                var alternatives = providers.Where(
                    path => candidates[path].Project != IrisProject
                )
                    .ToArray();
                foreach (var provider in alternatives.Length > 0
                    ? alternatives
                    : providers)
                {
                    Retain(provider);
                }
            }

            var keptProjects = managed.Where(
                x => !candidates.Values.Any(
                    c => c.Project == x.ProjectId
                )
            )
                .SelectMany(
                    x => x.Dependencies.Where(
                        d => d.Type == "required"
                    )
                )
                .ToArray();
            foreach (var item in candidates.Values.ToArray())
            {
                if (keptProjects.Any(
                    d => d.ProjectId == item.Project || (d.ProjectId is null && managed.Any(
                        m => m.ProjectId == item.Project && m.VersionId == d.VersionId
                    ))
                ))
                {
                    Retain(item.Path);
                }
            }
        }
        while (changed);
        if (!candidates.Values.Any(
            x => x.Project == IrisProject
        ))
        {
            conflicts.Add("Iris 有其他用途或被其他组件依赖，整次环境卸载未执行；请先处理依赖它的组件。");
        }

        if (candidates.Count > 128)
        {
            conflicts.Add("组件数量超过单次安全事务上限，未卸载。");
        }

        foreach (var file in inventory.Keys.Where(
            x => !candidates.ContainsKey(x)
        ))
        {
            if (!retained.Any(
                x => x.StartsWith(file + "：", StringComparison.Ordinal)
            ))
            {
                retained.Add(file + "：非本次环境可独占移除的文件");
            }
        }

        return new(
            Fingerprint(inventory),
            candidates.Keys.Order()
                .ToArray(),
            retained.ToArray(),
            conflicts.ToArray()
        );
    }

    public View.ShaderEnvironmentResult Uninstall(string fingerprint, string operationId, CancellationToken ct = default) => Uninstall(fingerprint, operationId, ct, null);
    internal View.ShaderEnvironmentResult Uninstall(string fingerprint, string operationId, CancellationToken ct, Action<string>? checkpoint)
    {
        using var lease = Lease();
        Recover();
        var id = Digest(operationId);
        var folder = ".pcl-backups/iris-uninstall-" + id;
        if (File.Exists(
            Safe(folder + "/completed.json")
        ))
        {
            var completed = Read<UninstallReceipt>(folder + "/completed.json");
            return new(
                completed.Removed.Select(
                    x => x.Path
                )
                    .ToArray(),
                completed.RetainedReasons,
                Safe(folder)
            );
        }

        ct.ThrowIfCancellationRequested();
        var plan = Plan();
        if (!plan.CanApply || plan.Remove.Length > 128 || plan.Fingerprint != fingerprint)
        {
            throw new IOException(
                "Iris 卸载计划已变化或存在冲突，未修改：" + string.Join("；", plan.Conflicts)
            );
        }

        if (Directory.Exists(
            Safe(folder)
        ))
        {
            Directory.Move(
                Safe(folder),
                Safe(
                    folder + "-abandoned-" + Guid.NewGuid()
                        .ToString("N")
                )
            );
        }

        Directory.CreateDirectory(
            Safe(folder)
        );
        var files = new List<BackupFile>();
        foreach (var path in plan.Remove)
        {
            var backup = files.Count + ".jar";
            using (var input = File.OpenRead(
                Safe(path)
            ))
                using (var output = new FileStream(
                    Safe(folder + "/" + backup),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None
                ))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }

            files.Add(
                new(
                    path,
                    Hash(path),
                    backup
                )
            );
        }

        var environment = Read<EnvironmentReceipt>(EnvironmentFile);
        ResourceTransactionFiles.Write(
            root,
            folder + "/manifest-before.json",
            File.ReadAllBytes(
                Safe(Manifest)
            )
        );
        ResourceTransactionFiles.Write(
            root,
            folder + "/environment-before.json",
            File.ReadAllBytes(
                Safe(EnvironmentFile)
            )
        );
        ResourceTransactionFiles.Write(
            root,
            folder + "/manifest-after.json",
            Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(
                    Managed()
                        .Where(
                            x => !plan.Remove.Contains("mods/" + x.FileName)
                        )
                        .ToArray()
                )
            )
        );
        Write(
            folder + "/environment-after.json",
            environment with
            {
                State = "uninstalled"
            }
        );
        var inventory = ModInventory();
        if (File.Exists(
            Safe("config/fabric_loader_dependencies.json")
        ))
        {
            throw new IOException("备份期间新增依赖覆盖配置，未卸载。");
        }

        if (Fingerprint(inventory) != fingerprint || files.Any(
            x => Hash(folder + "/" + x.Backup) != x.Sha256
        ))
        {
            throw new IOException("备份期间文件发生变化，未卸载。");
        }

        var retainedInventory = inventory.Where(
            x => !plan.Remove.Contains(x.Key)
        )
            .ToDictionary(
                x => x.Key,
                x => x.Value,
                StringComparer.Ordinal
            );
        var transaction = new UninstallReceipt(
            1,
            id,
            "committing",
            files.ToArray(),
            retainedInventory,
            Hash(folder + "/manifest-before.json"),
            Hash(folder + "/manifest-after.json"),
            Hash(folder + "/environment-before.json"),
            Hash(folder + "/environment-after.json"),
            ClaimsHash(),
            plan.Retain
        );
        ct.ThrowIfCancellationRequested();
        Write(Journal, transaction);
        checkpoint?.Invoke("prepared");
        Recover(checkpoint);
        ResourceHistory.Record(
            root,
            "卸载 Iris 环境（保留共享依赖与光影包）",
            plan.Remove,
            Safe(folder)
        );
        return new(
            plan.Remove,
            plan.Retain,
            Safe(folder)
        );
    }

    /// <summary>恢复先验证所有备份、剩余模组及用途清单；冲突时保留现场，禁止继续启动。</summary>
    private void Recover(Action<string>? checkpoint = null)
    {
        if (!File.Exists(
            Safe(Journal)
        ))
        {
            return;
        }

        var tx = Read<UninstallReceipt>(Journal);
        if (tx.Format != 1 || tx.Phase != "committing" || tx.Id.Length != 64 || !tx.Id.All(Uri.IsHexDigit) || tx.Removed.Length is 0 or > 128)
        {
            throw new InvalidDataException("Iris 卸载事务无效。");
        }

        var folder = ".pcl-backups/iris-uninstall-" + tx.Id;
        for (var i = 0; i < tx.Removed.Length; i++)
        {
            var item = tx.Removed[i];
            ValidateModPath(item.Path);
            if (item.Backup != i + ".jar" || Hash(folder + "/" + item.Backup) != item.Sha256 || (File.Exists(
                Safe(item.Path)
            ) && Hash(item.Path) != item.Sha256))
            {
                throw new IOException("Iris 卸载恢复发现组件/备份冲突：" + item.Path);
            }
        }

        foreach (var(file, expected)in new[]
        {
            ("manifest-before.json", tx.ManifestBefore),
            ("manifest-after.json", tx.ManifestAfter),
            ("environment-before.json", tx.EnvironmentBefore),
            ("environment-after.json", tx.EnvironmentAfter)
        }

        )
        {
            if (Hash(folder + "/" + file) != expected)
            {
                throw new IOException("Iris 卸载备份损坏，未继续：" + file);
            }
        }

        if (File.Exists(
            Safe("config/fabric_loader_dependencies.json")
        ))
        {
            throw new IOException("Iris 卸载恢复发现自定义依赖覆盖，保留现场，未继续。");
        }

        var remaining = ModInventory()
            .Where(
                x => !tx.Removed.Any(
                    r => r.Path == x.Key
                )
            )
            .OrderBy(
                x => x.Key
            )
            .ToArray();
        if (!remaining.SequenceEqual(
            tx.RetainedMods.OrderBy(
                x => x.Key
            )
        ) || ClaimsHash() != tx.ClaimsHash || (Hash(Manifest) != tx.ManifestBefore && Hash(Manifest) != tx.ManifestAfter) || (Hash(EnvironmentFile) != tx.EnvironmentBefore && Hash(EnvironmentFile) != tx.EnvironmentAfter))
        {
            throw new IOException("Iris 卸载恢复遇到新增模组、依赖用途或清单变化；未继续，启动已阻止。");
        }

        foreach (var item in tx.Removed)
        {
            File.Delete(
                Safe(item.Path)
            );
            checkpoint?.Invoke("component-removed");
        }

        ResourceTransactionFiles.Write(
            root,
            Manifest,
            File.ReadAllBytes(
                Safe(folder + "/manifest-after.json")
            )
        );
        checkpoint?.Invoke("manifest-written");
        ResourceTransactionFiles.Write(
            root,
            EnvironmentFile,
            File.ReadAllBytes(
                Safe(folder + "/environment-after.json")
            )
        );
        checkpoint?.Invoke("environment-written");
        Write(
            folder + "/completed.json",
            tx with
            {
                Phase = "completed"
            }
        );
        File.Delete(
            Safe(Journal)
        );
    }

    // 调用者已持有资源锁的写操作只恢复卸载，不把正在执行的安装误判为启动错误。
    internal static void RecoverUnderLease(string root) => new IrisEnvironmentService(root, "fabric", "").Recover();
    public static void RecoverBeforeLaunch(string root)
    {
        if (!File.Exists(
            ResourceInstaller.SafeTarget(root, Journal)
        ) && !File.Exists(
            ResourceInstaller.SafeTarget(root, EnvironmentFile)
        ))
        {
            return;
        }

        using var lease = new FileStream(
            ResourceInstaller.SafeTarget(root, ".resources.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        var service = new IrisEnvironmentService(root, "fabric", "");
        service.Recover();
        if (File.Exists(
            service.Safe(EnvironmentFile)
        ) && service.Read<EnvironmentReceipt>(EnvironmentFile)
            .State == "installing")
        {
            throw new IOException("Iris 环境安装尚未完成，请先重试安装；启动已阻止。");
        }
    }
}
