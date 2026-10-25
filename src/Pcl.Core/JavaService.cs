using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.RegularExpressions;
using MeloongCore;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// Mac 运行环境适配：匹配 Java 版本和 CPU 架构，检查原生库与旧版差异，生成适用于当前平台的启动条件。
public sealed class JavaService(MinecraftService service)
{
    public const string RuntimeIndexUrl = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
    // [实现] EnsureJava21Async 的实现：计算并返回 EnsureAsync(21, progress, cancellationToken)。
    public Task<JavaRuntime> EnsureJava21Async(IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) => EnsureAsync(21, progress, cancellationToken);
    // [实现] EnsureForAsync 的实现：计算并返回 EnsureAsync(version.JavaMajor, progress, ct)。
    public Task<JavaRuntime> EnsureForAsync(
        InstalledVersion version,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    ) => EnsureAsync(version.JavaMajor, progress, ct);
    // [实现] EnsureAsync 的实现：按 major is not (8 or 16 or 17 or 21 or 25) 分支处理；按 !OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64 分支处理；准备 var root = Path.Combine( service.Paths.Runtime, major == 21 ? "java-runtime-delta-macos-arm64" …。
    public async Task<JavaRuntime> EnsureAsync(
        int major,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (major is not (8 or 16 or 17 or 21 or 25))
            throw new NotSupportedException("尚未适配 Java " + major);
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("自动 Java 安装目前仅验证 macOS arm64。");
        var root = Path.Combine(
            service.Paths.Runtime,
            major == 21
                ? "java-runtime-delta-macos-arm64"
                : $"java-{major}-macos-arm64"
        );
        var stamp = Path.Combine(root, "pcl-java.json");
        if (File.Exists(stamp))
        {
            // [实现] 条件成立（File.Exists(stamp)）时：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：准备 var entry = JsonFiles.Read(stamp)；准备 var exe = AppPaths.SafeChild( root, (string? )entry["java"] ?? throw new InvalidDataException("…；准备 var runtime = await InspectAsync(exe, cancellationToken, service.Commands)。
                var entry = JsonFiles.Read(stamp);
                var exe = AppPaths.SafeChild(
                    root,
                    (string? )entry["java"] ?? throw new InvalidDataException("Java 记录缺少路径。")
                );
                var runtime = await InspectAsync(exe, cancellationToken, service.Commands);
                if (runtime.MajorVersion == major && runtime.Architecture == "aarch64")
                    return runtime;
            }
            catch (Exception ex)when (ex is IOException or InvalidDataException or Newtonsoft.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                // [实现] 捕获 (Exception ex) 后：执行 progress?.Report( new("修复 Java 运行时", 0, 0, 0, 0) )。
                progress?.Report(
                    new("修复 Java 运行时", 0, 0, 0, 0)
                );
            }
        }

        Directory.CreateDirectory(root);
        using var lease = new FileStream(
            Path.Combine(root, ".install.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None
        );
        progress?.Report(
            new($"读取 Apple Silicon Java {major} 清单", 0, 0, 0, 0)
        );
        var index = await service.GetMetadataAsync(
            RuntimeIndexUrl,
            Path.Combine(service.Paths.Root, "cache/java-index.json"),
            cancellationToken: cancellationToken
        );
        var selected = index["mac-os-arm64"]?.Children<JProperty>()
            .SelectMany(
                x => x.Value.OfType<JObject>()
            )
            .FirstOrDefault(
                x => ((string? )x["version"]?["name"])?.StartsWith(major + ".", StringComparison.Ordinal) == true
            );
        if (selected is null)
            return await InstallZuluAsync(major, root, stamp, progress, cancellationToken);
        var manifest = selected["manifest"]!;
        var json = await service.GetMetadataAsync(
            (string)manifest["url"]!,
            Path.Combine(root, "manifest.json"),
            (string)manifest["sha1"]!,
            (long)manifest["size"]!,
            cancellationToken
        );
        var entries = ((JObject)json["files"]!).Properties()
            .ToArray();
        var files = new List<DownloadItem>();
        foreach (var entry in entries)
        {
            // [实现] 对 entries 中的每一项：准备 var path = AppPaths.SafeChild(root, entry.Name)；准备 var type = (string)entry.Value["type"]!；按 type == "directory" 分支处理。
            var path = AppPaths.SafeChild(root, entry.Name);
            var type = (string)entry.Value["type"]!;
            if (type == "directory")
            {
                // [实现] 条件成立（type == "directory"）时：执行 Directory.CreateDirectory(path)；执行 continue;。
                Directory.CreateDirectory(path);
                continue;
            }

            if (type != "file")
                continue;
            var raw = entry.Value["downloads"]!["raw"]!;
            files.Add(
                new(
                    new Uri((string)raw["url"]!),
                    path,
                    (long)raw["size"]!,
                    (string)raw["sha1"]!
                )
            );
        }

        await service.Downloads.DownloadManyAsync(files, progress, cancellationToken);
        // Java manifests explicitly describe POSIX executables and symbolic links.
        foreach (var entry in entries.Where(
            x => (string? )x.Value["type"] == "file"
        ))
        {
            // [实现] 对 entries.Where( x => (string? )x.Value["type"] == "file" ) 中的每一项：准备 var path = AppPaths.SafeChild(root, entry.Name)；准备 var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMo…；按 (bool? )entry.Value["executable"] == true 分支处理。
            var path = AppPaths.SafeChild(root, entry.Name);
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            if ((bool? )entry.Value["executable"] == true)
                mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }

        foreach (var entry in entries.Where(
            x => (string? )x.Value["type"] == "link"
        ))
        {
            // [实现] 对 entries.Where( x => (string? )x.Value["type"] == "link" ) 中的每一项：准备 var path = AppPaths.SafeChild(root, entry.Name)；准备 var target = (string)entry.Value["target"]!；准备 var resolved = Path.GetFullPath( Path.Combine( Path.GetDirectoryName(path)!, target ) )。
            var path = AppPaths.SafeChild(root, entry.Name);
            var target = (string)entry.Value["target"]!;
            var resolved = Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(path)!,
                    target
                )
            );
            if (!PathUtils.IsParentOf(root, resolved))
                throw new InvalidDataException("Java 链接超出安装目录。");
            Directory.CreateDirectory(
                Path.GetDirectoryName(path)!
            );
            if (File.Exists(path) || Directory.Exists(path))
                continue;
            if (Directory.Exists(resolved))
                Directory.CreateSymbolicLink(path, target);
            else
                File.CreateSymbolicLink(path, target);
        }

        var executable = AppPaths.SafeChild(
            root,
            entries.Single(
                x => (string? )x.Value["type"] == "file" && (x.Name == "bin/java" || x.Name.EndsWith("/bin/java", StringComparison.Ordinal))
            )
                .Name
        );
        var result = await InspectAsync(executable, cancellationToken, service.Commands);
        if (result.MajorVersion != major || result.Architecture != "aarch64")
            throw new InvalidDataException("Java 版本或架构与目标不匹配。");
        JsonFiles.Write(
            stamp,
            new
            {
                java = Path.GetRelativePath(root, executable),
                result.MajorVersion,
                result.Architecture,
                source = RuntimeIndexUrl,
                manifestSha1 = (string)manifest["sha1"]!,
                files = files.Count,
                installedAt = DateTimeOffset.UtcNow
            }
        );
        return result;
    }

    // [实现] InstallZuluAsync 的实现：准备 var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) }；准备 var url = $"https://api.azul.com/metadata/v1/zulu/packages/?java_version={major}&os=macos&arch=…；准备 var packages = JArray.Parse( await http.GetStringAsync(url, ct) )。
    private async Task<JavaRuntime> InstallZuluAsync(
        int major,
        string root,
        string stamp,
        IProgress<InstallProgress>? progress,
        CancellationToken ct
    )
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        var url = $"https://api.azul.com/metadata/v1/zulu/packages/?java_version={major}&os=macos&arch=aarch64&archive_type=tar.gz&java_package_type=jre&release_status=ga&availability_types=CA&latest=true";
        var packages = JArray.Parse(
            await http.GetStringAsync(url, ct)
        );
        var item = packages.OrderBy(
            x => ((string? )x["name"])?.Contains("-fx-") == true
        )
            .FirstOrDefault() ?? throw new NotSupportedException("未找到适合 ARM64 Mac 的 Java " + major);
        var detailUrl = "https://api.azul.com/metadata/v1/zulu/packages/" + (string)item["package_uuid"]!;
        var detail = JObject.Parse(
            await http.GetStringAsync(detailUrl, ct)
        );
        var hash = (string? )detail["sha256_hash"] ?? "";
        var download = new Uri((string)detail["download_url"]!);
        if (!Regex.IsMatch(hash, "^[0-9a-fA-F]{64}$") || download.Scheme != "https" || download.Host != "cdn.azul.com" || (int? )detail["hw_bitness"] != 64 || (int? )detail["java_version"]?[0] != major || (string? )detail["os"] != "macos" || (string? )detail["arch"] is not ("arm" or "aarch64"))
            throw new InvalidDataException("Azul Java 元数据不匹配。");
        var archive = Path.Combine(
            service.Paths.Root,
            "cache/java",
            hash.ToLowerInvariant() + ".tar.gz"
        );
        Directory.CreateDirectory(
            Path.GetDirectoryName(archive)!
        );
        // [实现] ValidArchive 的实现：按 !File.Exists(archive) 分支处理；准备 var input = File.OpenRead(archive)；返回 Convert.ToHexString( SHA256.HashData(input) ) .Equals(hash, StringComparison.OrdinalIgnoreCase)。
        bool ValidArchive()
        {
            if (!File.Exists(archive))
                return false;
            using var input = File.OpenRead(archive);
            return Convert.ToHexString(
                SHA256.HashData(input)
            )
                .Equals(hash, StringComparison.OrdinalIgnoreCase);
        }

        if (File.Exists(archive) && !ValidArchive())
            File.Delete(archive);
        progress?.Report(
            new($"下载 ARM64 Java {major}（Azul）", 0, 0, 0, 0)
        );
        await service.Downloads.DownloadAsync(
            new(
                download,
                archive,
                -1,
                hash.ToLowerInvariant()
            ),
            ct
        );
        if (!ValidArchive())
        {
            // [实现] 条件成立（!ValidArchive()）时：执行 File.Delete(archive)；抛出 new InvalidDataException("Java 压缩包 SHA-256 不匹配。")。
            File.Delete(archive);
            throw new InvalidDataException("Java 压缩包 SHA-256 不匹配。");
        }

        var stage = Path.Combine(
            root,
            ".extract-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(stage);
        try
        {
            // [实现] 在异常保护范围内：执行 ExtractRuntimeArchive(archive, stage, ct)；准备 var executable = FindRuntimeExecutable(stage)；准备 var result = await InspectAsync(executable, ct)。
            ExtractRuntimeArchive(archive, stage, ct);
            var executable = FindRuntimeExecutable(stage);
            var result = await InspectAsync(executable, ct, service.Commands);
            if (result.MajorVersion != major || result.Architecture != "aarch64")
                throw new InvalidDataException("Azul Java 的真实版本或架构不匹配。");
            var destination = Path.Combine(
                root,
                "azul-" + Guid.NewGuid()
                    .ToString("N")
            );
            var relative = Path.GetRelativePath(stage, executable);
            Directory.Move(stage, destination);
            result = result with
            {
                Executable = Path.Combine(destination, relative)
            };
            JsonFiles.Write(
                stamp,
                new
                {
                    java = Path.GetRelativePath(root, result.Executable),
                    result.MajorVersion,
                    result.Architecture,
                    source = detailUrl,
                    archiveSha256 = hash,
                    apiSize = (long)detail["size"]!,
                    actualSize = new FileInfo(archive).Length,
                    installedAt = DateTimeOffset.UtcNow
                }
            );
            return result;
        }
        catch
        {
            // [实现] 捕获 空 后：按 Directory.Exists(stage) 分支处理；重新抛出当前异常。
            if (Directory.Exists(stage))
                Directory.Move(stage, stage + ".failed");
            throw;
        }
    }

    // [实现] FindRuntimeExecutable 的实现：计算并返回 Directory.EnumerateFiles( root, "java", new EnumerationOptions { RecurseSubdirectories = true, …。
    public static string FindRuntimeExecutable(string root) => Directory.EnumerateFiles(
        root,
        "java",
        new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }
    )
        .Single(
            x => Path.GetFileName(
                Path.GetDirectoryName(x)
            ) == "bin"
        );
    // [实现] ExtractRuntimeArchive 的实现：按 OperatingSystem.IsWindows() 分支处理；准备 var input = File.OpenRead(archive)；准备 var gzip = new GZipStream(input, CompressionMode.Decompress)。
    public static void ExtractRuntimeArchive(string archive, string destination, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("此解压器保留 POSIX Java 权限。 ");
        using var input = File.OpenRead(archive);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var links = new List<(string Path, string Target)>();
        long total = 0;
        int count = 0;
        while (reader.GetNextEntry()is { } entry)
        {
            // [实现] 循环满足 reader.GetNextEntry()is { } entry 时：执行 ct.ThrowIfCancellationRequested()；按 ++count > 30000 || (total += entry.Length) > 2L * 1024 * 1024 * 1024 分支处理；准备 var relative = entry.Name.TrimEnd('/')。
            ct.ThrowIfCancellationRequested();
            if (++count > 30000 || (total += entry.Length) > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Java 压缩包内容过大。");
            var relative = entry.Name.TrimEnd('/');
            while (relative.StartsWith("./"))
                relative = relative[2..];
            if (relative is "" or ".")
                continue;
            var target = ResourceInstaller.SafeTarget(destination, relative);
            if (entry.EntryType == TarEntryType.Directory)
                Directory.CreateDirectory(target);
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                // [实现] 条件成立（entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile）时：执行 Directory.CreateDirectory( Path.GetDirectoryName(target)! )；准备 var output = File.Create(target)；执行 entry.DataStream?.CopyTo(output)。
                Directory.CreateDirectory(
                    Path.GetDirectoryName(target)!
                );
                using var output = File.Create(target);
                entry.DataStream?.CopyTo(output);
                File.SetUnixFileMode(target, entry.Mode & (UnixFileMode)0x1ff);
            }
            else if (entry.EntryType == TarEntryType.SymbolicLink)
            {
                // [实现] 条件成立（entry.EntryType == TarEntryType.SymbolicLink）时：准备 var resolved = Path.GetFullPath( Path.Combine( Path.GetDirectoryName(target)!, entry.LinkName )…；按 Path.IsPathRooted(entry.LinkName) || !PathUtils.IsParentOf(destination, resolved) 分支处理；执行 links.Add((target, entry.LinkName))。
                var resolved = Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName(target)!,
                        entry.LinkName
                    )
                );
                if (Path.IsPathRooted(entry.LinkName) || !PathUtils.IsParentOf(destination, resolved))
                    throw new InvalidDataException("Java 链接越界。");
                links.Add((target, entry.LinkName));
            }
            else
                throw new InvalidDataException("Java 压缩包含不支持的条目类型。");
        }

        foreach (var link in links)
        {
            // [实现] 对 links 中的每一项：执行 Directory.CreateDirectory( Path.GetDirectoryName(link.Path)! )；执行 File.CreateSymbolicLink(link.Path, link.Target)。
            Directory.CreateDirectory(
                Path.GetDirectoryName(link.Path)!
            );
            File.CreateSymbolicLink(link.Path, link.Target);
        }
    }

    // [实现] ParseInspection 的实现：准备 var output = stderr + "\n" + stdout；准备 var version = Regex.Match( output, "^\\s*(?:openjdk|java)\\s+(?:version\\s+)?[\"]?([0-9]+)(?=[\…；按 version.Success && version.Groups[1].Value == "1" 分支处理。
    public static JavaRuntime ParseInspection(string executable, string stdout, string stderr)
    {
        var output = stderr + "\n" + stdout;
        var version = Regex.Match(
            output,
            "^\\s*(?:openjdk|java)\\s+(?:version\\s+)?[\"]?([0-9]+)(?=[\" .+\\r\\n]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline
        );
        if (version.Success && version.Groups[1].Value == "1")
            version = Regex.Match(
                output,
                "^\\s*(?:openjdk|java)\\s+(?:version\\s+)?[\"]?1[.]([0-9]+)",
                RegexOptions.IgnoreCase | RegexOptions.Multiline
            );
        var arch = Regex.Match(output, @"^\s*os\.arch\s*=\s*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Groups[1].Value.ToLowerInvariant();
        arch = arch switch
        {
            "arm64" or "aarch64" => "aarch64",
            "x86_64" or "x64" or "amd64" => "amd64",
            _ => arch
        };
        if (!version.Success || !int.TryParse(version.Groups[1].Value, out var major) || major < 5 || arch is not ("aarch64" or "amd64"))
            throw new InvalidDataException("无法确认 Java 主版本和 64 位架构：" + executable);
        return new(
            Path.GetFullPath(executable),
            arch,
            major
        );
    }

    // Java 检测通过短命令 Port 运行，十五秒超时后回收进程，继续复用原属性解析与架构验证。
    public static async Task<JavaRuntime> InspectAsync(
        string executable,
        CancellationToken cancellationToken = default,
        Pcl.Core.Ports.Processes.IShellCommandRunner? commands = null
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(
            TimeSpan.FromSeconds(15)
        );
        Pcl.Core.Ports.Processes.CommandResult result;
        try
        {
            result = await (commands ?? new Pcl.Core.Adapters.Processes.SystemShellCommandRunner()).RunAsync(
                new(
                    Path.GetFullPath(executable),
                    [
                        "-XshowSettings:properties",
                        "-version"
                    ],
                    Environment.CurrentDirectory
                ),
                ct: timeout.Token
            )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException("Java 检查超时：" + executable);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"Java 检查失败（返回值 {result.ExitCode}）：{executable}");
        }

        return ParseInspection(executable, result.StandardOutput, result.StandardError);
    }
}
