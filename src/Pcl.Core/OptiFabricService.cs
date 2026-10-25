using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
/// <summary>A deliberately fixed, runtime-tested combination; not a generic OptiFine/Fabric resolver.</summary>
public sealed class OptiFabricService(MinecraftService service)
{
    public const string GameVersion = "1.20.1", LoaderVersion = "0.16.14", ModVersion = "1.14.3";
    public const string OptiFineFile = "OptiFine_1.20.1_HD_U_I6.jar", OptiFabricFile = "optifabric-1.14.3.jar";
    public const string AuthorFilePage = "https://www.curseforge.com/minecraft/mc-mods/optifabric/files/5025647";
    // Hash of the exact artifact used in compatibility acceptance; not presented as an official publisher signature.
    public const string TestedOptiFabricSha256 = "96ed4d1c33a61c4793825d244f0d1f853d432ef5b6c3be4b5e5aa0e69567c9ad";
    public const string TestedOptiFineSha256 = "0b67cb670aedf2e55a982f3d52b6d53e46791a0c984aa1d2ee58100fc9bfc650";
    // [实现] IsPreset 的实现：计算并返回 File.Exists( Path.Combine(directory, "pcl-optifabric.json") )。
    public static bool IsPreset(string directory) => File.Exists(
        Path.Combine(directory, "pcl-optifabric.json")
    );
    // [实现] ValidateOptiFabricJar 的实现：执行 CheckHash(path, TestedOptiFabricSha256, "OptiFabric 1.14.3")；准备 var mods = FabricModReader.Read(path)；按 !mods.Any( x => x.Id == "optifabric" && x.Version == ModVersion ) || !mods.Any( x => x.Id == "m… 分支处理。
    public static void ValidateOptiFabricJar(string path)
    {
        CheckHash(path, TestedOptiFabricSha256, "OptiFabric 1.14.3");
        var mods = FabricModReader.Read(path);
        if (!mods.Any(
            x => x.Id == "optifabric" && x.Version == ModVersion
        ) || !mods.Any(
            x => x.Id == "mm" && FabricVersionPredicate.Matches(x.Version, ">=2.0")
        ))
            throw new InvalidDataException("OptiFabric 文件或内置 Fabric-ASM 不匹配。");
    }

    // [实现] ValidatePreset 的实现：准备 var receipt = JsonFiles.Read( Path.Combine(directory, "profile.json") )；按 (string? )receipt["gameVersion"] != GameVersion || (string? )receipt["loader"] != "fabric" || (… 分支处理；执行 ValidateOptiFabricJar( ResourceInstaller.SafeTarget(directory, "mods/" + OptiFabricFile) )。
    public static void ValidatePreset(string directory)
    {
        var receipt = JsonFiles.Read(
            Path.Combine(directory, "profile.json")
        );
        if ((string? )receipt["gameVersion"] != GameVersion || (string? )receipt["loader"] != "fabric" || (string? )receipt["loaderVersion"] != LoaderVersion)
            throw new InvalidDataException("OptiFabric 固定组合的 Minecraft / Fabric 版本已变化，请使用独立兼容实例。");
        ValidateOptiFabricJar(
            ResourceInstaller.SafeTarget(directory, "mods/" + OptiFabricFile)
        );
        CheckHash(
            ResourceInstaller.SafeTarget(directory, "mods/" + OptiFineFile),
            TestedOptiFineSha256,
            "OptiFine 1.20.1 I6"
        );
        ValidateConflicts(directory);
    }

    // [实现] ValidateConflicts 的实现：准备 var mods = ResourceInstaller.SafeTarget(directory, "mods")；按 !Directory.Exists(mods) 分支处理；遍历 ModFiles(mods) 处理 file。
    public static void ValidateConflicts(string directory)
    {
        var mods = ResourceInstaller.SafeTarget(directory, "mods");
        if (!Directory.Exists(mods))
            return;
        foreach (var file in ModFiles(mods))
        {
            // [实现] 对 ModFiles(mods) 中的每一项：执行 _ = ResourceInstaller.SafeTarget( directory, Path.GetRelativePath(directory, file) )；按 Path.GetFileName(file) == OptiFineFile 分支处理；遍历 FabricModReader.Read(file) 处理 mod。
            _ = ResourceInstaller.SafeTarget(
                directory,
                Path.GetRelativePath(directory, file)
            );
            if (Path.GetFileName(file) == OptiFineFile)
            {
                // [实现] 条件成立（Path.GetFileName(file) == OptiFineFile）时：执行 CheckHash(file, TestedOptiFineSha256, "OptiFine 1.20.1 I6")；执行 continue;。
                CheckHash(file, TestedOptiFineSha256, "OptiFine 1.20.1 I6");
                continue;
            }

            foreach (var mod in FabricModReader.Read(file))
            {
                // [实现] 对 FabricModReader.Read(file) 中的每一项：按 mod.Id is "sodium" or "iris" or "indium" or "no_fog" or "thallium" or "xradiation" || mod.Id ==… 分支处理；按 mod.Id == "optifabric" && (mod.Version != ModVersion || Path.GetFileName(file) != OptiFabricFil… 分支处理。
                if (mod.Id is "sodium" or "iris" or "indium" or "no_fog" or "thallium" or "xradiation" || mod.Id == "meteor-client" && FabricVersionPredicate.Matches(mod.Version, ">=0.4.1") || mod.Id == "architectury" && FabricVersionPredicate.Matches(mod.Version, ">1.2.72 <1.3.77") || mod.Id == "cardinal-components-item" && FabricVersionPredicate.Matches(mod.Version, "<2.4.2"))
                    throw new InvalidDataException("OptiFabric / OptiFine 与此启用模组冲突：" + mod.Id + "。请先在本地资源中停用。");
                if (mod.Id == "optifabric" && (mod.Version != ModVersion || Path.GetFileName(file) != OptiFabricFile))
                    throw new InvalidDataException("发现额外或不同版本的 OptiFabric。");
            }
        }
    }

    // [实现] ModFiles 的实现：准备 var folders = new Stack<string>()；执行 folders.Push(root)；在 folders.TryPop(out var folder) 条件下循环。
    private static IEnumerable<string> ModFiles(string root)
    {
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.TryPop(out var folder))
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                // [实现] 对 new DirectoryInfo(folder).EnumerateFileSystemInfos() 中的每一项：按 (entry.Attributes & FileAttributes.ReparsePoint) != 0 分支处理；按 entry is DirectoryInfo 分支处理。
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("OptiFabric 模组目录包含符号链接，未检查：" + entry.Name);
                if (entry is DirectoryInfo)
                    folders.Push(entry.FullName);
                else if (entry.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    yield return entry.FullName;
            }
    }

    // [实现] CheckHash 的实现：按 !File.Exists(path) || new FileInfo(path).LinkTarget is not null 分支处理；准备 var file = File.OpenRead(path)；按 Convert.ToHexString( SHA256.HashData(file) ) .ToLowerInvariant() != expected 分支处理。
    private static void CheckHash(string path, string expected, string label)
    {
        if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException(label + " 文件缺失或为符号链接。");
        using var file = File.OpenRead(path);
        if (Convert.ToHexString(
            SHA256.HashData(file)
        )
            .ToLowerInvariant() != expected)
            throw new InvalidDataException(label + " 与已验证组合文件不一致；未自动采用其他构建。");
    }

    // [实现] InstallAsync 的实现：按 service.GameVersion != GameVersion || service.Platform.Os != "osx" || service.Platform.Architec… 分支处理；准备 var cache = Path.Combine(service.Paths.Root, "cache/optifabric")；执行 Directory.CreateDirectory(cache)。
    public async Task<GameProfile> InstallAsync(
        string? localOptiFabric = null,
        string? curseForgeApiKey = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (service.GameVersion != GameVersion || service.Platform.Os != "osx" || service.Platform.Architecture != "aarch64")
            throw new NotSupportedException("此 OptiFabric 方案仅适配 Apple Silicon 的 Minecraft 1.20.1。");
        var cache = Path.Combine(service.Paths.Root, "cache/optifabric");
        Directory.CreateDirectory(cache);
        var optifabric = localOptiFabric;
        var provenance = "user_selected_local_file_matching_tested_sha256";
        if (string.IsNullOrWhiteSpace(optifabric))
        {
            // [实现] 条件成立（string.IsNullOrWhiteSpace(optifabric)）时：按 string.IsNullOrWhiteSpace(curseForgeApiKey) 分支处理；准备 var client = new CurseForgeClient(curseForgeApiKey)；准备 var project = await client.Get("mods/322385", ct)。
            if (string.IsNullOrWhiteSpace(curseForgeApiKey))
                throw new InvalidOperationException("请选择作者发布的 OptiFabric 1.14.3 文件，或填写自己的 CurseForge API Key。");
            using var client = new CurseForgeClient(curseForgeApiKey);
            var project = await client.Get("mods/322385", ct);
            if ((bool? )project["allowModDistribution"] == false)
                throw new InvalidOperationException("作者禁用了第三方自动下载，请从作者页手动下载后选择文件。");
            var file = await client.FileAsync(322385, 5025647, ct);
            if (file.DownloadUrl is null)
                throw new InvalidOperationException("CurseForge 未提供下载地址，请从作者页手动下载后选择文件。");
            if (file.Name != OptiFabricFile)
                throw new InvalidDataException("CurseForge 文件名与固定组合不符。");
            optifabric = Path.Combine(cache, OptiFabricFile);
            progress?.Report(
                new("下载 OptiFabric", 0, 0, 0, 0)
            );
            await service.Downloads.DownloadAsync(
                new(file.DownloadUrl, optifabric, file.Size, file.Sha1),
                ct
            );
            provenance = "curseforge_api_sha1_and_tested_sha256";
        }

        ValidateOptiFabricJar(optifabric!);
        InstalledVersion parent;
        try
        {
            // [实现] 在异常保护范围内：执行 parent = service.LoadInstalled()。
            parent = service.LoadInstalled();
        }
        catch (FileNotFoundException)
        {
            // [实现] 捕获 (FileNotFoundException) 后：执行 parent = await service.InstallAsync(progress, ct)。
            parent = await service.InstallAsync(progress, ct);
        }

        await new JavaService(service).EnsureForAsync(parent, progress, ct);
        using var optifine = new OptiFineService(service);
        var optifinePath = Path.Combine(cache, OptiFineFile);
        await service.Downloads.DownloadAsync(
            await optifine.DownloadSpecAsync(OptiFineFile, optifinePath, ct),
            ct
        );
        CheckHash(optifinePath, TestedOptiFineSha256, "OptiFine 1.20.1 I6");
        var operation = Guid.NewGuid()
            .ToString("N");
        var pendingName = "pending-pack-optifabric-" + operation;
        var id = "optifabric-1.20.1-" + operation;
        var pending = Path.Combine(service.Paths.Root, "instances", pendingName);
        var target = Path.Combine(service.Paths.Root, "instances", id);
        try
        {
            // [实现] 在异常保护范围内：准备 var fabric = new FabricService(service, pendingName)；准备 var profile = await fabric.InstallAsync(LoaderVersion, progress, ct)；执行 ValidateConflicts(pending)。
            using var fabric = new FabricService(service, pendingName);
            var profile = await fabric.InstallAsync(LoaderVersion, progress, ct);
            ValidateConflicts(pending);
            Directory.CreateDirectory(
                Path.Combine(pending, "mods")
            );
            File.Copy(
                optifabric!,
                Path.Combine(pending, "mods", OptiFabricFile),
                false
            );
            File.Copy(
                optifinePath,
                Path.Combine(pending, "mods", OptiFineFile),
                false
            );
            var metadata = JsonFiles.Read(profile.JsonPath);
            metadata["id"] = id;
            JsonFiles.Write(profile.JsonPath, metadata);
            var receiptPath = Path.Combine(pending, "profile.json");
            var receipt = JsonFiles.Read(receiptPath);
            receipt["id"] = id;
            receipt["displayName"] = "1.20.1 · OptiFabric + OptiFine I6";
            JsonFiles.Write(receiptPath, receipt);
            JsonFiles.Write(
                Path.Combine(pending, "pcl-optifabric.json"),
                new
                {
                    gameVersion = GameVersion,
                    loaderVersion = LoaderVersion,
                    optifabricVersion = ModVersion,
                    optifine = OptiFineFile,
                    source = AuthorFilePage,
                    provenance,
                    optifabricSha256 = TestedOptiFabricSha256,
                    optifineSha256 = TestedOptiFineSha256,
                    acceptedScope = "base_client_render_initialization_only",
                    installedAt = DateTimeOffset.UtcNow
                }
            );
            ValidatePreset(pending);
            ct.ThrowIfCancellationRequested();
            Directory.Move(pending, target);
            return new(
                id,
                "1.20.1 · OptiFabric + OptiFine I6",
                GameVersion,
                "fabric",
                target,
                Path.Combine(target, "version.json")
            );
        }
        catch
        {
            // [实现] 捕获 空 后：按 Directory.Exists(pending) 分支处理；重新抛出当前异常。
            if (Directory.Exists(pending))
                Directory.Delete(pending, true);
            throw;
        }
    }
}
