namespace Pcl.Core;
// 实例管理：读取实例记录和版本配置，执行隔离、备份、恢复与组件变更并校验结果。
public static class InstanceRegistry
{
    // [实现] List 的实现：准备 var list = new List<GameProfile>()；准备 var versions = Path.Combine(service.Paths.Game, "versions")；按 Directory.Exists(versions) 分支处理。
    public static IReadOnlyList<GameProfile> List(MinecraftService service, bool includeHidden = false)
    {
        var list = new List<GameProfile>();
        var versions = Path.Combine(service.Paths.Game, "versions");
        if (Directory.Exists(versions))
            foreach (var directory in Directory.EnumerateDirectories(versions))
            {
                // [实现] 对 Directory.EnumerateDirectories(versions) 中的每一项：准备 var id = Path.GetFileName(directory)；按 !File.Exists( Path.Combine(directory, "pcl-mac-install.json") ) 分支处理；执行 list.Add( new( id, id + " · 原版", id, "vanilla", id == MinecraftService.Version ? service.Paths.…。
                var id = Path.GetFileName(directory);
                if (!File.Exists(
                    Path.Combine(directory, "pcl-mac-install.json")
                ))
                    continue;
                list.Add(
                    new(
                        id,
                        id + " · 原版",
                        id,
                        "vanilla",
                        id == MinecraftService.Version
                            ? service.Paths.Game
                            : Path.Combine(service.Paths.Root, "instances", "vanilla-" + id),
                        Path.Combine(directory, id + ".json")
                    )
                );
            }

        if (!list.Any(
            x => x.Id == MinecraftService.Version
        ))
            list.Insert(
                0,
                new(
                    MinecraftService.Version,
                    "1.21.1 · 原版",
                    MinecraftService.Version,
                    "vanilla",
                    service.Paths.Game,
                    Path.Combine(
                        service.Paths.VersionFolder(MinecraftService.Version),
                        "1.21.1.json"
                    )
                )
            );
        var root = Path.Combine(service.Paths.Root, "instances");
        if (Directory.Exists(root))
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                // [实现] 对 Directory.EnumerateDirectories(root) 中的每一项：按 Path.GetFileName(folder) .StartsWith("pending-pack-", StringComparison.Ordinal) || Path.GetFile… 分支处理；按 (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || !File.Exists( Path.Combine(f… 分支处理；执行受异常处理保护的操作。
                if (Path.GetFileName(folder)
                    .StartsWith("pending-pack-", StringComparison.Ordinal) || Path.GetFileName(folder)
                    .StartsWith('.'))
                    continue;
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || !File.Exists(
                    Path.Combine(folder, "profile.json")
                ))
                    continue;
                try
                {
                    // [实现] 在异常保护范围内：准备 var p = JsonFiles.Read( Path.Combine(folder, "profile.json") )；准备 var gameVersion = (string? )p["gameVersion"] ?? MinecraftService.Version；执行 VersionCatalog.ValidateId(gameVersion)。
                    var p = JsonFiles.Read(
                        Path.Combine(folder, "profile.json")
                    );
                    var gameVersion = (string? )p["gameVersion"] ?? MinecraftService.Version;
                    VersionCatalog.ValidateId(gameVersion);
                    var loader = (string? )p["loader"] ?? "fabric";
                    var version = (string? )p["loaderVersion"] ?? "";
                    list.Add(
                        new(
                            (string)p["id"]!,
                            (string? )p["displayName"] ?? $"{gameVersion} · {loader} {version}",
                            gameVersion,
                            loader,
                            folder,
                            Path.Combine(folder, "version.json")
                        )
                    );
                }
                catch (Exception ex)when (ex is IOException or Newtonsoft.Json.JsonException or InvalidCastException)
                {
                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                }
            }

        var settings = new LauncherSettingsStore(service.Paths);
        return list.Select(
            profile => (Profile: profile, Preferences: settings.Preferences(profile.GameDirectory))
        )
            .Where(
                x => !x.Preferences.Deleted && (includeHidden || !x.Preferences.Hidden)
            )
            .OrderByDescending(
                x => x.Preferences.Favorite
            )
            .ThenBy(
                x => x.Profile.Name,
                StringComparer.CurrentCulture
            )
            .Select(
                x => string.IsNullOrWhiteSpace(x.Preferences.Name)
                    ? x.Profile
                    : x.Profile with
                    {
                        Name = x.Preferences.Name
                    }
            )
            .ToArray();
    }

    // [实现] Load 的实现：准备 var parentService = service.ForVersion(profile.GameVersion)；准备 var parent = parentService.LoadInstalled()；按 profile.Loader == "vanilla" && !File.Exists( Path.Combine(profile.GameDirectory, "profile.json"… 分支处理。
    public static InstalledVersion Load(MinecraftService service, GameProfile profile)
    {
        using var parentService = service.ForVersion(profile.GameVersion);
        var parent = parentService.LoadInstalled();
        if (profile.Loader == "vanilla" && !File.Exists(
            Path.Combine(profile.GameDirectory, "profile.json")
        ))
            return parent;
        var receipt = JsonFiles.Read(
            Path.Combine(profile.GameDirectory, "profile.json")
        );
        var metadata = JsonFiles.Read(profile.JsonPath);
        var privateLibraries = (string? )receipt["libraryRoot"] == "instance";
        var jar = receipt["clientJar"] is null
            ? parent.JarPath
            : ResourceInstaller.SafeTarget(profile.GameDirectory, (string)receipt["clientJar"]!);
        if (!File.Exists(jar))
            throw new FileNotFoundException("实例客户端缺失，请修复安装。", jar);
        var libraries = MinecraftService.BuildLibraries(
            metadata,
            privateLibraries
                ? profile.GameDirectory
                : service.Paths.Game,
            service.Platform
        );
        return new(
            profile.Id,
            profile.JsonPath,
            jar,
            parent.AssetIndex,
            libraries,
            parent.JavaMajor,
            privateLibraries
                ? Path.Combine(profile.GameDirectory, "libraries")
                : null
        );
    }

    // [实现] ByFolder 的实现：计算并返回 List(service, true) .Single( x => Path.GetFileName(x.GameDirectory) == name )。
    public static GameProfile ByFolder(MinecraftService service, string name) => List(service, true)
        .Single(
            x => Path.GetFileName(x.GameDirectory) == name
        );
}
