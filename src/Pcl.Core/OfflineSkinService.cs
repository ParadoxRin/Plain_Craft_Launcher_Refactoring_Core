using System.IO.Compression;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：OfflineSkin 汇集 AccountId、FileName、Sha256，供该模块传递状态。
public sealed record OfflineSkin(
    string AccountId,
    string FileName,
    string Sha256,
    string Model = "auto",
    string PlayerName = ""
);
/// <summary>Like PCL2's offline skin path, changes local default textures through a generated resource pack.</summary>
public sealed class OfflineSkinService(AppPaths paths)
{
    public const string PackName = "PCL Mac Offline Skin.zip";
    private string Index => Path.Combine(paths.Root, "offline-skins", "index.json");

    // [实现] Get 的实现：计算并返回 Read() .FirstOrDefault( x => x.AccountId == account.Id )。
    public OfflineSkin? Get(LauncherAccount account) => Read()
        .FirstOrDefault(
            x => x.AccountId == account.Id
        );
    // [实现] ImagePath 的实现：计算并返回 Get(account)is { } skin ? ResourceInstaller.SafeTarget( Path.Combine(paths.Root, "offline-skins…。
    public string? ImagePath(LauncherAccount account) => Get(account)is { } skin
        ? ResourceInstaller.SafeTarget(
            Path.Combine(paths.Root, "offline-skins"),
            skin.FileName
        )
        : null;
    // [实现] Read 的实现：计算并返回 File.Exists(Index) ? JsonConvert.DeserializeObject<List<OfflineSkin>>( File.ReadAllText(Index) …。
    private List<OfflineSkin> Read() => File.Exists(Index)
        ? JsonConvert.DeserializeObject<List<OfflineSkin>>(
            File.ReadAllText(Index)
        ) ?? throw new InvalidDataException("离线皮肤索引损坏。")
        : [];
    // [实现] Save 的实现：执行 RequireOffline(account)；执行 ExternalAuthentication.ValidatePng(png, "skin")；准备 var hash = Convert.ToHexString( SHA256.HashData(png) ) .ToLowerInvariant()。
    public void Save(LauncherAccount account, byte[] png, string model = "auto", string playerName = "")
    {
        RequireOffline(account);
        if (model is not ("auto" or "wide" or "slim"))
        {
            throw new InvalidDataException("无效皮肤模型。");
        }

        ExternalAuthentication.ValidatePng(png, "skin");
        var hash = Convert.ToHexString(
            SHA256.HashData(png)
        )
            .ToLowerInvariant();
        var filename = hash + ".png";
        var target = ResourceInstaller.SafeTarget(
            Path.Combine(paths.Root, "offline-skins"),
            filename
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(target)!
        );
        var temporary = target + "." + Guid.NewGuid()
            .ToString("N") + ".partial";
        try
        {
            // [实现] 在异常保护范围内：执行 File.WriteAllBytes(temporary, png)；执行 File.Move(temporary, target, true)。
            File.WriteAllBytes(temporary, png);
            File.Move(temporary, target, true);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 File.Exists(temporary) 分支处理。
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        JsonFiles.Write(
            Index,
            Read()
                .Where(
                    x => x.AccountId != account.Id
                )
                .Append(
                    new OfflineSkin(account.Id, filename, hash, model, playerName)
                )
        );
    }

    // 导出持久化皮肤；先写临时文件再替换，失败不会截断用户的目标文件。
    public void Export(LauncherAccount account, string destination)
    {
        var source = ImagePath(account) ?? throw new InvalidOperationException("尚未保存皮肤。");
        if (Path.GetFullPath(source) == Path.GetFullPath(destination))
        {
            return;
        }

        var png = File.ReadAllBytes(source);
        ExternalAuthentication.ValidatePng(png, "skin");
        var temporary = destination + "." + Guid.NewGuid()
            .ToString("N") + ".partial";
        try
        {
            File.WriteAllBytes(temporary, png);
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    // [实现] Remove 的实现：执行 RequireOffline(account)；执行 JsonFiles.Write( Index, Read() .Where( x => x.AccountId != account.Id ) .ToArray() )。
    public void Remove(LauncherAccount account)
    {
        RequireOffline(account);
        JsonFiles.Write(
            Index,
            Read()
                .Where(
                    x => x.AccountId != account.Id
                )
                .ToArray()
        );
    }

    // [实现] RequireOffline 的实现：按 account.Kind != AccountKind.Offline || account.Id != "offline-" + account.Uuid 分支处理。
    private static void RequireOffline(LauncherAccount account)
    {
        if (account.Kind != AccountKind.Offline || account.Id != "offline-" + account.Uuid)
            throw new InvalidOperationException("本地离线皮肤仅适用于离线账户。");
    }

    // [实现] Prepare 的实现：执行 Directory.CreateDirectory(gameDirectory)；准备 var target = ResourceInstaller.SafeTarget(gameDirectory, "resourcepacks/" + PackName)；准备 var marker = ResourceInstaller.SafeTarget(gameDirectory, ".pcl-offline-skin.json")。
    public void Prepare(
        LauncherAccount? account,
        InstalledVersion version,
        string gameDirectory,
        string? gameVersion = null,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(gameDirectory);
        var target = ResourceInstaller.SafeTarget(gameDirectory, "resourcepacks/" + PackName);
        var marker = ResourceInstaller.SafeTarget(gameDirectory, ".pcl-offline-skin.json");
        var skin = account?.Kind == AccountKind.Offline
            ? Get(account)
            : null;
        var versionId = gameVersion ?? version.Id;
        if (File.Exists(target) && !File.Exists(marker))
        {
            // [实现] 条件成立（File.Exists(target) && !File.Exists(marker)）时：按 skin is null 分支处理；抛出 new IOException("离线皮肤资源包文件名已被其他文件占用，请先重命名该文件。")。
            if (skin is null)
                return;
            throw new IOException("离线皮肤资源包文件名已被其他文件占用，请先重命名该文件。");
        }

        if (skin is null)
        {
            // [实现] 条件成立（skin is null）时：执行 SetEnabled(gameDirectory, versionId, false)；按 File.Exists(target) 分支处理；按 File.Exists(marker) 分支处理。
            SetEnabled(gameDirectory, versionId, false);
            if (File.Exists(target))
                File.Delete(target);
            if (File.Exists(marker))
                File.Delete(marker);
            return;
        }

        var source = ResourceInstaller.SafeTarget(
            Path.Combine(paths.Root, "offline-skins"),
            skin.FileName
        );
        var png = File.ReadAllBytes(source);
        if (Convert.ToHexString(
            SHA256.HashData(png)
        )
            .ToLowerInvariant() != skin.Sha256)
            throw new InvalidDataException("离线皮肤校验失败，请重新导入。");
        ExternalAuthentication.ValidatePng(png, "skin");
        var legacySkin = Version.TryParse(versionId, out var skinGame) && skinGame.Major == 1 && skinGame.Minor < 8;
        png = legacySkin
            ? SkinPixels.Legacy(png)
            : SkinPixels.Modern(png);
        if (BinaryPrimitives.ReadUInt32BigEndian(
            png.AsSpan(16, 4)
        ) != 64 || BinaryPrimitives.ReadUInt32BigEndian(
            png.AsSpan(20, 4)
        ) != (legacySkin
            ? 32
            : 64))
            throw new InvalidDataException(
                legacySkin
                    ? "此旧版离线皮肤需要 64×32 PNG，请重新导入。"
                    : "离线资源包皮肤需要 64×64 PNG，请重新导入。"
            );
        using var client = ZipFile.OpenRead(version.JarPath);
        var textures = client.Entries.Where(
            x => x.FullName is "assets/minecraft/textures/entity/steve.png" or "assets/minecraft/textures/entity/alex.png" || (x.FullName.StartsWith("assets/minecraft/textures/entity/player/wide/", StringComparison.Ordinal) || x.FullName.StartsWith("assets/minecraft/textures/entity/player/slim/", StringComparison.Ordinal)) && x.FullName.EndsWith(".png", StringComparison.Ordinal)
        )
            .Select(
                x => x.FullName
            )
            .ToArray();
        if (textures.Length == 0)
            throw new NotSupportedException("此版本未发现受支持的默认皮肤资源；自定义离线皮肤未应用。");
        var pack = PackMetadata(client, versionId);
        var temporary = target + "." + Guid.NewGuid()
            .ToString("N") + ".partial";
        Directory.CreateDirectory(
            Path.GetDirectoryName(target)!
        );
        try
        {
            // [实现] 在异常保护范围内：在作用域内使用并释放 var archive = ZipFile.Open(temporary, ZipArchiveMode.Create)；执行 ct.ThrowIfCancellationRequested()；执行 File.Move(temporary, target, true)。
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                // [实现] 本段流程：在作用域内使用并释放 var writer = new StreamWriter( archive.CreateEntry("pack.mcmeta") .Open() )；遍历 textures 处理 texture。
                using (var writer = new StreamWriter(
                    archive.CreateEntry("pack.mcmeta")
                        .Open()
                ))
                    writer.Write(
                        new JObject
                        {
                            ["pack"] = pack
                        }.ToString()
                    );
                foreach (var texture in textures)
                {
                    // [实现] 对 textures 中的每一项：执行 ct.ThrowIfCancellationRequested()；准备 var output = archive.CreateEntry(texture, CompressionLevel.Fastest) .Open()；执行 output.Write(png)。
                    ct.ThrowIfCancellationRequested();
                    using var output = archive.CreateEntry(texture, CompressionLevel.Fastest)
                        .Open();
                    output.Write(png);
                }
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
            JsonFiles.Write(
                marker,
                new
                {
                    accountId = account!.Id,
                    skin.Sha256,
                    gameVersion = versionId
                }
            );
            SetEnabled(gameDirectory, versionId, true);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 File.Exists(temporary) 分支处理。
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // [实现] PackMetadata 的实现：准备 JToken? format = null；按 client.GetEntry("version.json")is { } entry 分支处理；准备 var major = format is JObject ? (int? )format["resource"] ?? (int? )format["resource_major"] : …。
    private static JObject PackMetadata(ZipArchive client, string versionId)
    {
        JToken? format = null;
        if (client.GetEntry("version.json")is { } entry)
        {
            // [实现] 条件成立（client.GetEntry("version.json")is { } entry）时：按 entry.Length > 1024 * 1024 分支处理；准备 var reader = new StreamReader( entry.Open() )；执行 format = JObject.Parse( reader.ReadToEnd() )["pack_version"]。
            if (entry.Length > 1024 * 1024)
                throw new InvalidDataException("游戏版本信息过大。");
            using var reader = new StreamReader(
                entry.Open()
            );
            format = JObject.Parse(
                reader.ReadToEnd()
            )["pack_version"];
        }

        var major = format is JObject
            ? (int? )format["resource"] ?? (int? )format["resource_major"]
            : (int? )format;
        if (major is null)
        {
            // [实现] 条件成立（major is null）时：按 !Version.TryParse(versionId, out var parsed) || parsed.Major != 1 || parsed.Minor is < 6 or > 1… 分支处理；执行 major = parsed.Minor switch { <= 8 => 1, <= 10 => 2, <= 12 => 3, <= 14 => 4, 15 => 5, 16 => par…。
            if (!Version.TryParse(versionId, out var parsed) || parsed.Major != 1 || parsed.Minor is < 6 or > 18)
                throw new NotSupportedException("此版本没有可识别的资源包格式信息。");
            major = parsed.Minor switch
            {
                <= 8 => 1,
                <= 10 => 2,
                <= 12 => 3,
                <= 14 => 4,
                15 => 5,
                16 => parsed.Build <= 1
                    ? 5
                    : 6,
                17 => 7,
                18 => 8,
                _ => throw new NotSupportedException()
            };
        }

        var result = new JObject
        {
            ["description"] = "PCL Mac 本地离线皮肤",
            ["pack_format"] = major
        };
        if (format is JObject formats && formats["resource_major"] is not null)
        {
            // [实现] 条件成立（format is JObject formats && formats["resource_major"] is not null）时：准备 var minimum = new JArray(major.Value, (int? )formats["resource_minor"] ?? 0)；执行 result["min_format"] = minimum；执行 result["max_format"] = minimum.DeepClone()。
            var minimum = new JArray(major.Value, (int? )formats["resource_minor"] ?? 0);
            result["min_format"] = minimum;
            result["max_format"] = minimum.DeepClone();
        }

        return result;
    }

    // [实现] SetEnabled 的实现：准备 var options = ResourceInstaller.SafeTarget(directory, "options.txt")；按 !enabled && !File.Exists(options) 分支处理；准备 var lines = File.Exists(options) ? File.ReadAllLines(options) .ToList() : []。
    private static void SetEnabled(string directory, string versionId, bool enabled)
    {
        var options = ResourceInstaller.SafeTarget(directory, "options.txt");
        if (!enabled && !File.Exists(options))
            return;
        var lines = File.Exists(options)
            ? File.ReadAllLines(options)
                .ToList()
            : [];
        var old = lines.FirstOrDefault(
            x => x.StartsWith("resourcePacks:", StringComparison.Ordinal)
        );
        var packs = old is null
            ? new List<string>()
            : JsonConvert.DeserializeObject<List<string>>(old[14..]) ?? throw new InvalidDataException("资源包设置损坏。");
        var legacy = Version.TryParse(versionId, out var game) && game.Major == 1 && game.Minor < 13;
        var removed = packs.RemoveAll(
            x => x == PackName || x == "file/" + PackName
        );
        if (!enabled && removed == 0)
            return;
        if (enabled)
        {
            // [实现] 条件成立（enabled）时：按 !legacy && packs.Count == 0 分支处理；执行 packs.Add( (legacy ? "" : "file/") + PackName )。
            if (!legacy && packs.Count == 0)
                packs.Add("vanilla");
            packs.Add(
                (legacy
                    ? ""
                    : "file/") + PackName
            );
        }

        lines.RemoveAll(
            x => x.StartsWith("resourcePacks:", StringComparison.Ordinal)
        );
        lines.Add(
            "resourcePacks:" + JsonConvert.SerializeObject(packs)
        );
        MeloongCore.FileUtils.WriteAtomic(
            options,
            string.Join(Environment.NewLine, lines) + Environment.NewLine
        );
    }
}
