using System.Text;
using Newtonsoft.Json.Linq;
using Pcl.Core.Adapters.Processes;
using Pcl.Core.Ports.Processes;

namespace Pcl.Core.Adapters.Compatibility;
/// <summary>Runs bounded compatibility helpers through the short-command port; never includes mods on the classpath.</summary>
public sealed class QuiltOverrideRuntime(string java, string? loaderJar = null, IShellCommandRunner? commands = null)
{
    private readonly IShellCommandRunner runner = commands ?? new SystemShellCommandRunner();
    /// <summary>Use the instance's configured or already installed matching Java; never invoke the macOS Java-install stub.</summary>
    public static QuiltOverrideRuntime ForInstance(string directory, int major, IEnumerable<QuiltCandidate> candidates, CancellationToken ct)
    {
        DirectoryInfo? current = new(
            Path.GetFullPath(directory)
        );
        for (var depth = 0; current is not null && depth < 12; depth++, current = current.Parent)
        {
            if (!Directory.Exists(
                Path.Combine(current.FullName, "runtime")
            ) || !Directory.Exists(
                Path.Combine(current.FullName, "instances")
            ))
                continue;
            var paths = new AppPaths(current.FullName);
            var configured = new LauncherSettingsStore(paths).Effective(directory)
                .JavaExecutable;
            var choices = string.IsNullOrWhiteSpace(configured)
                ? JavaDiscovery.Candidates(paths)
                : [
                    configured
                ];
            foreach (var executable in choices)
            {
                ct.ThrowIfCancellationRequested();
                JavaRuntime runtime;
                try
                {
                    runtime = JavaService.InspectAsync(executable, ct)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)when (string.IsNullOrWhiteSpace(configured) && ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
                {
                    continue;
                }

                if (runtime.MajorVersion != major || runtime.Architecture != TargetPlatform.Current.Architecture)
                    continue;
                var loader = candidates.FirstOrDefault(
                    x => x.Id == "quilt_loader" && File.Exists(x.Source)
                )?.Source;
                return new(executable, loader);
            }

            break;
        }

        throw new NotSupportedException("Quilt 正则或 Fabric 覆盖需要实例对应的 Java；请先完成 Java/加载器安装或配置有效 Java。");
    }

    /// <summary>Batch full-ID matching uses java.util.regex.Pattern, including Java quoting, flags and class intersections.</summary>
    public bool[][] Match(IReadOnlyList<string> patterns, IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (patterns.Count > 512 || ids.Count > 4096)
            throw new NotSupportedException("Quilt 选择器候选超过安全预算。");
        var input = new[]
        {
            patterns.Count.ToString(),
            ids.Count.ToString()
        }.Concat(
            patterns.Select(Encode)
        )
            .Concat(
                ids.Select(Encode)
            );
        var rows = Run("PclQuiltPatterns", input, false, ct);
        if (rows.Length != patterns.Count)
            throw new InvalidDataException("Quilt 正则匹配返回了不完整结果。");
        return rows.Select(
            (row, index) =>
            {
                if (!row.StartsWith("ROW:" + index + ":", StringComparison.Ordinal))
                    throw new InvalidDataException("Quilt 正则响应序号无效。");
                var bytes = Convert.FromBase64String(
                    row[(row.IndexOf(':', 4) + 1)..]
                );
                if (bytes.Length != (ids.Count + 7) / 8)
                    throw new InvalidDataException("Quilt 正则响应长度无效。");
                return Enumerable.Range(0, ids.Count)
                    .Select(
                        i => (bytes[i / 8] & (1 << (i % 8))) != 0
                    )
                    .ToArray();
            }
        )
            .ToArray();
    }

    /// <summary>Convert Fabric predicates with the selected Quilt Loader's actual parser, then expose normal Quilt interval expressions.</summary>
    public QuiltCandidate ConvertFabric(QuiltCandidate candidate, CancellationToken ct)
    {
        if (loaderJar is null || !File.Exists(loaderJar))
            throw new NotSupportedException("Fabric 覆盖转换需要经过实例校验的 Quilt 加载器 JAR。");
        var originals = candidate.Depends.Select(
            x => x
        )
            .Concat(
                candidate.Breaks.Select(
                    x => x
                )
            )
            .ToArray();
        var input = originals.Select(
            token =>
            {
                var value = token["versions"];
                var patterns = value is JArray array
                    ? array.Values<string>()
                        .Select(
                            x => x!
                        )
                        .ToArray()
                    : [
                        value?.Value<string>() ?? "*"
                    ];
                return patterns.Length == 0
                    ? "-"
                    : string.Join(
                        ',',
                        patterns.Select(Encode)
                    );
            }
        );
        var rows = originals.Length == 0
            ? []
            : Run("PclQuiltFabricRanges", input, true, ct);
        if (rows.Length != originals.Length)
            throw new InvalidDataException("Fabric 区间转换返回了不完整结果。");
        var converted = new List<JToken?>();
        for (var i = 0; i < rows.Length; i++)
        {
            var prefix = "ROW:" + i + ":";
            if (!rows[i].StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Fabric 区间响应序号无效。");
            var ranges = rows[i][prefix.Length..];
            if (ranges.Length == 0)
            {
                converted.Add(null); // Quilt's Fabric wrapper ignores impossible Fabric ranges.
                continue;
            }

            var intervals = ranges.Split(';')
                .Select(ParseInterval)
                .ToArray();
            JToken expression = intervals.Any(
                x => x.Type == JTokenType.String && (string? )x == "*"
            )
                ? new JValue("*")
                : intervals.Length == 1
                    ? intervals[0]
                    : new JObject
                    {
                        ["any"] = new JArray(intervals)
                    };
            converted.Add(
                new JObject
                {
                    ["id"] = originals[i]["id"]!.DeepClone(),
                    ["versions"] = expression
                }
            );
        }

        return candidate with
        {
            Group = "loader.fabric",
            Depends = new JArray(
                converted.Take(candidate.Depends.Count)
                    .Where(
                        x => x is not null
                    )
            ),
            Breaks = new JArray(
                converted.Skip(candidate.Depends.Count)
                    .Where(
                        x => x is not null
                    )
            ),
            QuiltDependencySemantics = true
        };
    }

    private static JToken ParseInterval(string text)
    {
        var fields = text.Split(',');
        if (fields.Length != 4)
            throw new InvalidDataException("Fabric 区间响应无效。");
        string? Decode(string value) => value == "-"
            ? null
            : Encoding.UTF8.GetString(
                Convert.FromBase64String(value)
            );
        var low = Decode(fields[0]);
        var high = Decode(fields[2]);
        if (low is null && high is null)
            return new JValue("*");
        if (low == high && fields[1] == "1" && fields[3] == "1")
            return new JValue("=" + low);
        var terms = new List<string>();
        if (low is not null)
            terms.Add(
                (fields[1] == "1"
                    ? ">="
                    : ">") + low
            );
        if (high is not null)
            terms.Add(
                (fields[3] == "1"
                    ? "<="
                    : "<") + high
            );
        return terms.Count == 1
            ? new JValue(terms[0])
            : new JObject
            {
                ["all"] = new JArray(terms)
            };
    }

    private static string Encode(string text) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes(text)
    );
    /// <summary>Private files contain metadata only. Timeout/cancellation kills the owned helper and always removes temporary files.</summary>
    private string[] Run(string helper, IEnumerable<string> lines, bool includeLoader, CancellationToken ct)
    {
        var temporary = Directory.CreateTempSubdirectory("pcl-quilt-compat-");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    temporary.FullName,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            var input = Path.Combine(temporary.FullName, "input.txt");
            var content = string.Join('\n', lines);
            if (Encoding.UTF8.GetByteCount(content) > 4 * 1024 * 1024)
                throw new NotSupportedException("Quilt 兼容输入超过4 MiB。");
            File.WriteAllText(
                input,
                content + "\n",
                new UTF8Encoding(false)
            );
            using (var resource = typeof(QuiltOverrideRuntime).Assembly.GetManifestResourceStream("Pcl.Core.Adapters.Compatibility.Java." + helper + ".class") ?? throw new InvalidDataException("Quilt 兼容桥接资源缺失。"))
                using (var output = File.Create(
                    Path.Combine(temporary.FullName, helper + ".class")
                ))
                    resource.CopyTo(output);
            var classpath = temporary.FullName + (includeLoader
                ? Path.PathSeparator + loaderJar
                : "");
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(
                TimeSpan.FromSeconds(10)
            );
            CommandResult result;
            try
            {
                result = runner.RunAsync(
                    new ProcessSpec(
                        java,
                        [
                            "-Xms16m",
                            "-Xmx64m",
                            "-cp",
                            classpath,
                            helper,
                            input
                        ],
                        temporary.FullName
                    ),
                    ct: bounded.Token
                )
                    .GetAwaiter()
                    .GetResult();
            }
            catch (OperationCanceledException)when (!ct.IsCancellationRequested)
            {
                throw new NotSupportedException("Quilt Java 匹配超过10秒预算，尚未判定兼容性。");
            }

            if (result.Truncated)
                throw new NotSupportedException("Quilt Java 匹配结果超过输出预算。");
            if (result.ExitCode != 0)
            {
                var syntax = result.StandardOutput.Split('\n')
                    .FirstOrDefault(
                        x => x.StartsWith("INVALID_PATTERN:", StringComparison.Ordinal)
                    );
                throw new InvalidDataException(
                    syntax is null
                        ? "Quilt Java 兼容转换失败；请检查加载器及版本声明。"
                        : "Quilt 正则语法无效（规则序号从0开始）：" + syntax.Split(':')[1].Trim()
                );
            }

            return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(
                    x => x.TrimEnd('\r')
                )
                .ToArray();
        }
        finally
        {
            temporary.Delete(true);
        }
    }
}
