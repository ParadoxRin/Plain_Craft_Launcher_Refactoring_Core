namespace Pcl.Core;
// [实现] 数据约定：DiscoveredJava 汇集 Executable、Runtime、Error，供该模块传递状态。
public sealed record DiscoveredJava(string Executable, JavaRuntime? Runtime, string? Error)
{
    // [实现] ToString 的实现：计算并返回 Runtime is null ? "不可用 · " + Executable + "\n" + Error : $"Java {Runtime.MajorVersion} · {Runti…。
    public override string ToString() => Runtime is null
        ? "不可用 · " + Executable + "\n" + Error
        : $"Java {Runtime.MajorVersion} · {Runtime.Architecture}\n{Executable}";
}

/// <summary>Inspects known runtime locations without executing macOS's installation-prompt stub.</summary>
public static class JavaDiscovery
{
    // [实现] Candidates 的实现：准备 var candidates = new HashSet<string>(StringComparer.Ordinal)；执行 void Add(string? path) { if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path…；按 Directory.Exists(paths.Runtime) 分支处理。
    public static IReadOnlyList<string> Candidates(AppPaths paths, IEnumerable<string>? extraExecutables = null)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        // [实现] Add 的实现：按 string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) 分支处理；执行 path = Path.GetFullPath(path)；按 path is "/usr/bin/java" or "/System/Library/Frameworks/JavaVM.framework/Versions/Current/Comman… 分支处理。
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return;
            path = Path.GetFullPath(path);
            if (path is "/usr/bin/java" or "/System/Library/Frameworks/JavaVM.framework/Versions/Current/Commands/java")
                return;
            if (File.Exists(path))
                candidates.Add(path);
        }

        if (Directory.Exists(paths.Runtime))
            foreach (var folder in Directory.EnumerateDirectories(paths.Runtime)
                .Take(100))
            {
                // [实现] 对 Directory.EnumerateDirectories(paths.Runtime) .Take(100) 中的每一项：准备 var receipt = Path.Combine(folder, "pcl-java.json")；执行受异常处理保护的操作。
                var receipt = Path.Combine(folder, "pcl-java.json");
                try
                {
                    // [实现] 在异常保护范围内：按 File.Exists(receipt) && (string? )JsonFiles.Read(receipt)["java"] is string relative 分支处理。
                    if (File.Exists(receipt) && (string? )JsonFiles.Read(receipt)["java"] is string relative)
                        Add(
                            AppPaths.SafeChild(folder, relative)
                        );
                }
                catch (Exception ex)when (ex is IOException or InvalidDataException or Newtonsoft.Json.JsonException or ArgumentException)
                {
                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                }
            }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[]
        {
            "/Library/Java/JavaVirtualMachines",
            Path.Combine(home, "Library/Java/JavaVirtualMachines")
        }

        )
        {
            // [实现] 对 new[] { "/Library/Java/JavaVirtualMachines", Path.Combine(home, "Library/Java/JavaVirtualMachin… 中的每一项：按 !Directory.Exists(root) 分支处理；执行受异常处理保护的操作。
            if (!Directory.Exists(root))
                continue;
            try
            {
                // [实现] 在异常保护范围内：遍历 Directory.EnumerateDirectories(root) .Take(100) 处理 folder。
                foreach (var folder in Directory.EnumerateDirectories(root)
                    .Take(100))
                {
                    // [实现] 对 Directory.EnumerateDirectories(root) .Take(100) 中的每一项：执行 Add( Path.Combine(folder, "Contents/Home/bin/java") )；执行 Add( Path.Combine(folder, "bin/java") )。
                    Add(
                        Path.Combine(folder, "Contents/Home/bin/java")
                    );
                    Add(
                        Path.Combine(folder, "bin/java")
                    );
                }
            }
            catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
            {
                // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
            }
        }

        foreach (var root in new[]
        {
            "/opt/homebrew/opt",
            "/usr/local/opt"
        }

        )
        {
            // [实现] 对 new[] { "/opt/homebrew/opt", "/usr/local/opt" } 中的每一项：按 !Directory.Exists(root) 分支处理；执行受异常处理保护的操作。
            if (!Directory.Exists(root))
                continue;
            try
            {
                // [实现] 在异常保护范围内：遍历 Directory.EnumerateDirectories(root, "openjdk*") .Take(100) 处理 folder。
                foreach (var folder in Directory.EnumerateDirectories(root, "openjdk*")
                    .Take(100))
                {
                    // [实现] 对 Directory.EnumerateDirectories(root, "openjdk*") .Take(100) 中的每一项：执行 Add( Path.Combine(folder, "bin/java") )；执行 Add( Path.Combine(folder, "libexec/openjdk.jdk/Contents/Home/bin/java") )。
                    Add(
                        Path.Combine(folder, "bin/java")
                    );
                    Add(
                        Path.Combine(folder, "libexec/openjdk.jdk/Contents/Home/bin/java")
                    );
                }
            }
            catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
            {
                // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
            }
        }

        if (Environment.GetEnvironmentVariable("JAVA_HOME")is string javaHome && Path.IsPathFullyQualified(javaHome))
            Add(
                Path.Combine(javaHome, "bin/java")
            );
        foreach (var path in extraExecutables ?? [])
            Add(path);
        return candidates.Order(StringComparer.Ordinal)
            .ToArray();
    }

    // [实现] InspectAsync 的实现：准备 var result = new List<DiscoveredJava>()；遍历 Candidates(paths, extraExecutables) 处理 path；返回 result.OrderByDescending( x => x.Runtime?.Architecture == Pcl.Core.Adapters.Platform.HostPlatform.Detect().Architecture )…。
    public static async Task<IReadOnlyList<DiscoveredJava>> InspectAsync(
        AppPaths paths,
        IEnumerable<string>? extraExecutables = null,
        CancellationToken ct = default
    )
    {
        var result = new List<DiscoveredJava>();
        foreach (var path in Candidates(paths, extraExecutables))
        {
            // [实现] 对 Candidates(paths, extraExecutables) 中的每一项：执行 ct.ThrowIfCancellationRequested()；执行受异常处理保护的操作。
            ct.ThrowIfCancellationRequested();
            try
            {
                // [实现] 在异常保护范围内：执行 result.Add( new( path, await JavaService.InspectAsync(path, ct), null ) )。
                result.Add(
                    new(
                        path,
                        await JavaService.InspectAsync(path, ct),
                        null
                    )
                );
            }
            catch (Exception ex)when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 result.Add( new(path, null, ex.Message) )。
                result.Add(
                    new(path, null, ex.Message)
                );
            }
        }

        return result.OrderByDescending(
            x => x.Runtime?.Architecture == Pcl.Core.Adapters.Platform.HostPlatform.Detect()
                .Architecture
        )
            .ThenByDescending(
                x => x.Runtime?.MajorVersion ?? 0
            )
            .ThenBy(
                x => x.Executable
            )
            .ToArray();
    }
}
