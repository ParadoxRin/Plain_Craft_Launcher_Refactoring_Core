using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：WorldDirectory 汇集 Name、Path，供该模块传递状态。
public sealed record WorldDirectory(string Name, string Path)
{
    // [实现] ToString 的实现：计算并返回 Name。
    public override string ToString() => Name;
}

// [实现] 数据约定：WorldDataPack 汇集 Name、Path、Enabled，供该模块传递状态。
public sealed record WorldDataPack(string Name, string Path, bool Enabled)
{
    // [实现] ToString 的实现：计算并返回 (Enabled ? "● " : "○ ") + Name。
    public override string ToString() => (Enabled
        ? "● "
        : "○ ") + Name;
}

// 世界数据包管理：检查数据包格式、目标存档与兼容范围，备份并更新存档配置。
public sealed class DataPackService(string gameDirectory)
{
    // [实现] Worlds 的实现：准备 var saves = ResourceInstaller.SafeTarget(gameDirectory, "saves")；按 !Directory.Exists(saves) 分支处理；返回 Directory.EnumerateDirectories(saves) .Where( x => (File.GetAttributes(x) & FileAttributes.Repa…。
    public IReadOnlyList<WorldDirectory> Worlds()
    {
        var saves = ResourceInstaller.SafeTarget(gameDirectory, "saves");
        if (!Directory.Exists(saves))
            return[];
        return Directory.EnumerateDirectories(saves)
            .Where(
                x => (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0 && File.Exists(
                    Path.Combine(x, "level.dat")
                )
            )
            .Select(
                x => new WorldDirectory(
                    Path.GetFileName(x),
                    x
                )
            )
            .ToArray();
    }

    // [实现] Folder 的实现：按 !Worlds() .Any( x => x.Path == world.Path ) 分支处理；返回 ResourceInstaller.SafeTarget(world.Path, "datapacks")。
    private string Folder(WorldDirectory world)
    {
        if (!Worlds()
            .Any(
                x => x.Path == world.Path
            ))
            throw new InvalidDataException("世界不属于当前实例。");
        return ResourceInstaller.SafeTarget(world.Path, "datapacks");
    }

    // [实现] List 的实现：准备 var folder = Folder(world)；按 !Directory.Exists(folder) 分支处理；返回 Directory.EnumerateFiles(folder) .Where( x => x.EndsWith(".zip", StringComparison.OrdinalIgnore…。
    public IReadOnlyList<WorldDataPack> List(WorldDirectory world)
    {
        var folder = Folder(world);
        if (!Directory.Exists(folder))
            return[];
        return Directory.EnumerateFiles(folder)
            .Where(
                x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase)
            )
            .Select(
                x => new WorldDataPack(
                    Path.GetFileName(x),
                    x,
                    !x.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                )
            )
            .ToArray();
    }

    // [实现] Validate 的实现：准备 var zip = ZipFile.OpenRead(path)；执行 PortableModpackService.ValidateArchive(zip)；准备 var meta = zip.GetEntry("pack.mcmeta") ?? throw new InvalidDataException("数据包缺少 pack.mcmeta。")。
    public static void Validate(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        PortableModpackService.ValidateArchive(zip);
        var meta = zip.GetEntry("pack.mcmeta") ?? throw new InvalidDataException("数据包缺少 pack.mcmeta。");
        if (meta.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("数据包元数据过大。");
        using var reader = new StreamReader(
            meta.Open()
        );
        var json = JObject.Parse(
            reader.ReadToEnd()
        );
        if (json["pack"] is not JObject || !zip.Entries.Any(
            x => x.FullName.StartsWith("data/", StringComparison.Ordinal) && !x.FullName.EndsWith('/')
        ))
            throw new InvalidDataException("不是有效的数据包。");
    }

    // [实现] InstallAsync 的实现：执行 Validate(source)；准备 var name = Path.GetFileName(source)；执行 ResourceInstaller.ValidateFileName(name)。
    public async Task<string> InstallAsync(WorldDirectory world, string source, CancellationToken ct = default)
    {
        Validate(source);
        var name = Path.GetFileName(source);
        ResourceInstaller.ValidateFileName(name);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("数据包需为 ZIP 文件。");
        var folder = Folder(world);
        Directory.CreateDirectory(folder);
        var target = ResourceInstaller.SafeTarget(folder, name);
        if (File.Exists(target) || File.Exists(target + ".disabled"))
            throw new IOException("世界已有同名数据包，请先移除或重命名。");
        var temporary = ResourceInstaller.SafeTarget(
            folder,
            ".install-" + Guid.NewGuid()
                .ToString("N")
        );
        try
        {
            // [实现] 在异常保护范围内：在作用域内使用并释放 var input = File.OpenRead(source)；执行 Validate(temporary)；执行 ct.ThrowIfCancellationRequested()。
            await using (var input = File.OpenRead(source))
                await using (var output = File.Create(temporary))
                    await input.CopyToAsync(output, ct);
            Validate(temporary);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target);
            return target;
        }
        catch
        {
            // [实现] 捕获 空 后：执行 File.Delete(temporary)；重新抛出当前异常。
            File.Delete(temporary);
            throw;
        }
    }

    // [实现] InstallPlanAsync 的实现：按 plan.Kind != ResourceKind.DataPack 分支处理；准备 var folder = Folder(world)；执行 Directory.CreateDirectory(folder)。
    public async Task<IReadOnlyList<string>> InstallPlanAsync(
        WorldDirectory world,
        ResourceInstallPlan plan,
        DownloadService downloads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (plan.Kind != ResourceKind.DataPack)
            throw new ArgumentException("需要数据包安装计划。");
        var folder = Folder(world);
        Directory.CreateDirectory(folder);
        using var lease = new FileStream(
            ResourceInstaller.SafeTarget(folder, ".pcl-install.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        var cache = ResourceInstaller.SafeTarget(gameDirectory, ".pcl-datapack-downloads");
        Directory.CreateDirectory(cache);
        var prepared = new List<(string Source, string Target)>();
        var created = new List<string>();
        foreach (var release in plan.Releases)
        {
            // [实现] 对 plan.Releases 中的每一项：准备 var file = release.File；执行 ResourceInstaller.ValidateFileName(file.Name)；按 !System.Text.RegularExpressions.Regex.IsMatch(file.Sha1, "^[a-fA-F0-9]{40}$") || !file.Name.End… 分支处理。
            var file = release.File;
            ResourceInstaller.ValidateFileName(file.Name);
            if (!System.Text.RegularExpressions.Regex.IsMatch(file.Sha1, "^[a-fA-F0-9]{40}$") || !file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("数据包文件格式不正确。");
            var target = ResourceInstaller.SafeTarget(folder, file.Name);
            if (File.Exists(target) || File.Exists(target + ".disabled") || prepared.Any(
                x => x.Target.Equals(target, StringComparison.OrdinalIgnoreCase)
            ))
                throw new IOException("世界已有同名数据包：" + file.Name);
            var source = ResourceInstaller.SafeTarget(
                cache,
                file.Sha1.ToLowerInvariant()
            );
            progress?.Report(
                new("下载数据包 " + release.Name, prepared.Count, plan.Releases.Count, 0, plan.Bytes)
            );
            await downloads.DownloadAsync(
                new(file.Url, source, file.Size, file.Sha1),
                ct
            );
            Validate(source);
            prepared.Add((source, target));
        }

        try
        {
            // [实现] 在异常保护范围内：遍历 prepared 处理 item；返回 created。
            foreach (var item in prepared)
            {
                // [实现] 对 prepared 中的每一项：执行 ct.ThrowIfCancellationRequested()；执行 File.Copy(item.Source, item.Target, false)；执行 created.Add(item.Target)。
                ct.ThrowIfCancellationRequested();
                File.Copy(item.Source, item.Target, false);
                created.Add(item.Target);
            }

            return created;
        }
        catch
        {
            // [实现] 捕获 空 后：遍历 created 处理 path；重新抛出当前异常。
            foreach (var path in created)
                File.Delete(path);
            throw;
        }
    }

    // [实现] SetEnabled 的实现：准备 var current = List(world) .Single( x => x.Path == pack.Path )；按 current.Enabled == enabled 分支处理；准备 var target = enabled ? current.Path[..^9] : current.Path + ".disabled"。
    public void SetEnabled(WorldDirectory world, WorldDataPack pack, bool enabled)
    {
        var current = List(world)
            .Single(
                x => x.Path == pack.Path
            );
        if (current.Enabled == enabled)
            return;
        var target = enabled
            ? current.Path[..^9]
            : current.Path + ".disabled";
        ResourceInstaller.SafeTarget(
            Folder(world),
            Path.GetFileName(target)
        );
        File.Move(current.Path, target);
    }

    // [实现] Remove 的实现：准备 var current = List(world) .Single( x => x.Path == pack.Path )；准备 var folder = ResourceInstaller.SafeTarget(world.Path, ".pcl-datapack-backups")；执行 Directory.CreateDirectory(folder)。
    public string Remove(WorldDirectory world, WorldDataPack pack)
    {
        var current = List(world)
            .Single(
                x => x.Path == pack.Path
            );
        var folder = ResourceInstaller.SafeTarget(world.Path, ".pcl-datapack-backups");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(
            folder,
            Guid.NewGuid()
                .ToString("N") + "-" + current.Name
        );
        File.Move(current.Path, target);
        return target;
    }
}
