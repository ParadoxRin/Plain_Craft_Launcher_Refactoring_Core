using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Pcl.Core;
// [实现] 数据约定：ShaderMacReport 汇集 Status、RequiredFeatures、OptionalFeatures、UnsupportedRequiredFeatures、HasComputePrograms、HasMacGuards、Notes，供该模块传递状态。
public sealed record ShaderMacReport(
    string Status,
    IReadOnlyList<string> RequiredFeatures,
    IReadOnlyList<string> OptionalFeatures,
    IReadOnlyList<string> UnsupportedRequiredFeatures,
    bool HasComputePrograms,
    bool HasMacGuards,
    IReadOnlyList<string> Notes
)
{
    public bool CanActivate => UnsupportedRequiredFeatures.Count == 0;
}

// Mac 运行环境适配：匹配 Java 版本和 CPU 架构，检查原生库与旧版差异，生成适用于当前平台的启动条件。
public static class MacShaderCompatibility
{
    public const string Documentation = "https://shaders.properties/current/reference/miscellaneous/macos/";
    private static readonly HashSet<string> Unsupported = new(StringComparer.Ordinal)
    {
        "COMPUTE_SHADERS",
        "CUSTOM_IMAGES",
        "SSBO"
    };
    // [实现] Inspect 的实现：准备 var zip = ZipFile.OpenRead(zipPath)；准备 var entry = zip.GetEntry("shaders/shaders.properties")；按 entry is not null && entry.Length > 2 * 1024 * 1024 分支处理。
    public static ShaderMacReport Inspect(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("shaders/shaders.properties");
        if (entry is not null && entry.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("光影属性文件过大。");
        string properties = "";
        if (entry is not null)
        {
            // [实现] 条件成立（entry is not null）时：准备 var reader = new StreamReader( entry.Open() )；执行 properties = reader.ReadToEnd()。
            using var reader = new StreamReader(
                entry.Open()
            );
            properties = reader.ReadToEnd();
        }

        return Analyze(
            properties,
            zip.Entries.Any(
                x => x.FullName.EndsWith(".csh", StringComparison.OrdinalIgnoreCase)
            )
        );
    }

    // [实现] Analyze 的实现：准备 var required = new HashSet<string>()；准备 var optional = new HashSet<string>()；准备 var conditional = new HashSet<string>()。
    public static ShaderMacReport Analyze(string properties, bool computePrograms = false)
    {
        var required = new HashSet<string>();
        var optional = new HashSet<string>();
        var conditional = new HashSet<string>();
        var active = new Stack<(bool? Parent, bool? Condition)>();
        bool? current = true;
        // [实现] And 的实现：计算并返回 left == false || right == false ? false : left == true && right == true ? true : null。
        bool? And(bool? left, bool? right) => left == false || right == false
            ? false
            : left == true && right == true
                ? true
                : null;
        foreach (var raw in Regex.Replace(properties, @"\\\r?\n\s*", " ")
            .Split('\n'))
        {
            // [实现] 对 Regex.Replace(properties, @"\\\r?\n\s*", " ") .Split('\n') 中的每一项：准备 var line = raw.Trim()；按 line.StartsWith("#ifdef ") || line.StartsWith("#ifndef ") || line.StartsWith("#if ") 分支处理；按 line == "#else" && active.Count > 0 分支处理。
            var line = raw.Trim();
            if (line.StartsWith("#ifdef ") || line.StartsWith("#ifndef ") || line.StartsWith("#if "))
            {
                // [实现] 条件成立（line.StartsWith("#ifdef ") || line.StartsWith("#ifndef ") || line.StartsWith("#if ")）时：准备 bool? condition = line switch { "#ifdef MC_OS_MAC" => true, "#ifndef MC_OS_MAC" => false, "#if …；执行 active.Push((current, condition))；执行 current = And(current, condition)。
                bool? condition = line switch
                {
                    "#ifdef MC_OS_MAC" => true,
                    "#ifndef MC_OS_MAC" => false,
                    "#if 0" => false,
                    "#if 1" => true,
                    _ => null
                };
                active.Push((current, condition));
                current = And(current, condition);
                continue;
            }

            if (line == "#else" && active.Count > 0)
            {
                // [实现] 条件成立（line == "#else" && active.Count > 0）时：准备 var frame = active.Peek()；执行 current = And( frame.Parent, frame.Condition.HasValue ? !frame.Condition.Value : null )；执行 continue;。
                var frame = active.Peek();
                current = And(
                    frame.Parent,
                    frame.Condition.HasValue
                        ? !frame.Condition.Value
                        : null
                );
                continue;
            }

            if (line.StartsWith("#elif ") && active.Count > 0)
            {
                // [实现] 条件成立（line.StartsWith("#elif ") && active.Count > 0）时：执行 current = active.Peek() .Parent == false ? false : null；执行 continue;。
                current = active.Peek()
                    .Parent == false
                    ? false
                    : null;
                continue;
            }

            if (line.StartsWith("#endif") && active.Count > 0)
            {
                // [实现] 条件成立（line.StartsWith("#endif") && active.Count > 0）时：执行 current = active.Pop() .Parent；执行 continue;。
                current = active.Pop()
                    .Parent;
                continue;
            }

            if (current == false)
                continue;
            var match = Regex.Match(line, @"^iris\.features\.(required|optional)\s*[=:]\s*(.*?)\s*(?:#.*)?$");
            if (!match.Success)
                continue;
            foreach (var flag in match.Groups[2].Value.Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries))
            {
                // [实现] 对 match.Groups[2].Value.Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries) 中的每一项：按 match.Groups[1].Value == "optional" 分支处理。
                if (match.Groups[1].Value == "optional")
                    optional.Add(flag);
                else if (current == true)
                    required.Add(flag);
                else
                    conditional.Add(flag);
            }
        }

        var blocked = required.Where(Unsupported.Contains)
            .Order()
            .ToArray();
        var notes = new List<string>
        {
            "macOS 原生 OpenGL 上限 4.1；Iris 不为 Apple Silicon 驱动提供官方支持。"
        };
        if (conditional.Count > 0)
            notes.Add(
                "存在条件编译的必需功能，静态检查无法确认其实际启用状态：" + string.Join(", ", conditional)
            );
        if (computePrograms)
            notes.Add("包含计算着色器；可能存在可选回退，不能仅凭 .csh 文件判断整个包不兼容。");
        notes.Add("通过静态检查不等于世界内渲染通过，仍需实际游戏验证。");
        return new(
            blocked.Length > 0
                ? "declared_incompatible_with_native_macos_gl41"
                : "requires_runtime_validation",
            required.Order()
                .ToArray(),
            optional.Order()
                .ToArray(),
            blocked,
            computePrograms,
            properties.Contains("MC_OS_MAC", StringComparison.Ordinal),
            notes
        );
    }

    // [实现] EnsureCanActivate 的实现：准备 var report = Inspect(zipPath)；按 !report.CanActivate 分支处理。
    public static void EnsureCanActivate(string zipPath)
    {
        var report = Inspect(zipPath);
        if (!report.CanActivate)
            throw new PlatformNotSupportedException(
                "此光影声明必需的功能在 macOS 原生 OpenGL 中不可用：" + string.Join(", ", report.UnsupportedRequiredFeatures)
            );
    }

    // [实现] ProfileOptions 的实现：准备 var zip = ZipFile.OpenRead(zipPath)；准备 var entry = zip.GetEntry("shaders/shaders.properties") ?? throw new InvalidDataException("缺少光影预…；按 entry.Length > 2 * 1024 * 1024 分支处理。
    public static IReadOnlyDictionary<string, string> ProfileOptions(string zipPath, string name)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("shaders/shaders.properties") ?? throw new InvalidDataException("缺少光影预设。");
        if (entry.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("光影属性过大。");
        using var reader = new StreamReader(
            entry.Open()
        );
        var text = Regex.Replace(
            reader.ReadToEnd(),
            @"\\\r?\n\s*",
            " "
        );
        var profiles = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            // [实现] 对 text.Split('\n') 中的每一项：准备 var m = Regex.Match(line, @"^\s*profile\.([A-Za-z0-9_]+)\s*[=:]\s*(.*)$")；按 m.Success 分支处理。
            var m = Regex.Match(line, @"^\s*profile\.([A-Za-z0-9_]+)\s*[=:]\s*(.*)$");
            if (m.Success)
                profiles[m.Groups[1].Value] = m.Groups[2].Value;
        }

        var options = new Dictionary<string, string>();
        var visiting = new HashSet<string>();
        // [实现] Add 的实现：按 !visiting.Add(profile) || visiting.Count > 24 分支处理；按 !profiles.TryGetValue(profile, out var value) 分支处理；遍历 value.Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries) 处理 token。
        void Add(string profile)
        {
            if (!visiting.Add(profile) || visiting.Count > 24)
                throw new InvalidDataException("光影预设循环。");
            if (!profiles.TryGetValue(profile, out var value))
                throw new InvalidDataException("缺少光影预设：" + profile);
            foreach (var token in value.Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries))
            {
                // [实现] 对 value.Split((char[]? )null, StringSplitOptions.RemoveEmptyEntries) 中的每一项：按 token.StartsWith("profile.") 分支处理；准备 var pair = token.Split( new[] { '=', ':' }, 2 )；准备 var key = pair[0].TrimStart('!')。
                if (token.StartsWith("profile."))
                {
                    // [实现] 条件成立（token.StartsWith("profile.")）时：执行 Add(token[8..])；执行 continue;。
                    Add(token[8..]);
                    continue;
                }

                var pair = token.Split(
                    new[]
                    {
                        '=',
                        ':'
                    },
                    2
                );
                var key = pair[0].TrimStart('!');
                if (!Regex.IsMatch(key, "^[A-Za-z0-9_]+$"))
                    throw new InvalidDataException("无效光影选项。");
                options[key] = pair.Length == 2
                    ? pair[1]
                    : token.StartsWith('!')
                        ? "false"
                        : "true";
            }

            visiting.Remove(profile);
        }

        Add(name);
        return options;
    }
}

// Mac 运行环境适配：匹配 Java 版本和 CPU 架构，检查原生库与旧版差异，生成适用于当前平台的启动条件。
public static class PropertySettings
{
    // [实现] Merge 的实现：准备 var lines = File.Exists(path) ? File.ReadAllLines(path) .ToList() : new List<string>()；执行 foreach (var(key, value)in settings) { if (key.IndexOfAny( new[] { '\r', '\n', '=', ':' } ) >= …；执行 Directory.CreateDirectory( Path.GetDirectoryName(path)! )。
    public static void Merge(
        string path,
        IReadOnlyDictionary<string, string> settings,
        char separator = '=',
        bool onlyMissing = false
    )
    {
        var lines = File.Exists(path)
            ? File.ReadAllLines(path)
                .ToList()
            : new List<string>();
        foreach (var(key, value)in settings)
        {
            // [实现] 本段流程：按 key.IndexOfAny( new[] { '\r', '\n', '=', ':' } ) >= 0 || value.IndexOfAny( new[] { '\r', '\n' }… 分支处理；执行 bool Matches(string line) => line.TrimStart() .StartsWith(key + separator, StringComparison.Ord…；按 onlyMissing && lines.Any(Matches) 分支处理。
            if (key.IndexOfAny(
                new[]
                {
                    '\r',
                    '\n',
                    '=',
                    ':'
                }
            ) >= 0 || value.IndexOfAny(
                new[]
                {
                    '\r',
                    '\n'
                }
            ) >= 0)
                throw new InvalidDataException("配置键值不能换行。");
            // [实现] Matches 的实现：计算并返回 line.TrimStart() .StartsWith(key + separator, StringComparison.Ordinal)。
            bool Matches(string line) => line.TrimStart()
                .StartsWith(key + separator, StringComparison.Ordinal);
            if (onlyMissing && lines.Any(Matches))
                continue;
            lines.RemoveAll(Matches);
            lines.Add(key + separator + value);
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!
        );
        MeloongCore.FileUtils.WriteAtomic(
            path,
            string.Join(Environment.NewLine, lines) + Environment.NewLine
        );
    }
}
