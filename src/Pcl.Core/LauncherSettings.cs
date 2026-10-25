using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：LaunchSettings 汇集 ，供该模块传递状态。
public sealed record LaunchSettings
{
    public int MinimumMemoryMb { get; init; } = 256;
    public int MaximumMemoryMb { get; init; } = 2048;
    public int Width { get; init; } = 1100;
    public int Height { get; init; } = 700;
    public bool Fullscreen { get; init; }
    public bool DisableLwjglUnsafeAgent { get; init; }
    public string GameWindowTitle { get; init; } = "";
    public string WindowMode { get; init; } = "custom";
    public string ProcessPriority { get; init; } = "normal";
    public bool AutomaticMemory { get; init; }
    public string VersionInformation { get; init; } = "";
    public string GarbageCollector { get; init; } = "default";
    public string JavaExecutable { get; init; } = "";
    public string JvmArguments { get; init; } = "";
    public string GameArguments { get; init; } = "";
    public string ServerAddress { get; init; } = "";
    public string QuickPlayWorld { get; init; } = "";

    // [实现] Validate 的实现：按 GameWindowTitle.Length > 120 || GameWindowTitle.Any(char.IsControl) || WindowMode is not ("defa… 分支处理；按 VersionInformation.Length > 100 || VersionInformation.Any(char.IsControl) 分支处理；按 GarbageCollector is not ("default" or "g1" or "g1-tuned" or "zgc" or "generational") 分支处理。
    public void Validate()
    {
        if (GameWindowTitle.Length > 120 || GameWindowTitle.Any(char.IsControl) || WindowMode is not ("default" or "custom" or "launcher" or "maximized") || ProcessPriority is not ("normal" or "low" or "high"))
        {
            // [实现] 条件成立（GameWindowTitle.Length > 120 || GameWindowTitle.Any(char.IsControl) || WindowMode is not ("defa…）时：抛出 new InvalidDataException("游戏窗口或进程优先级设置无效。")。
            throw new InvalidDataException("游戏窗口或进程优先级设置无效。");
        }

        if (VersionInformation.Length > 100 || VersionInformation.Any(char.IsControl))
        {
            // [实现] 条件成立（VersionInformation.Length > 100 || VersionInformation.Any(char.IsControl)）时：抛出 new InvalidDataException("自定义信息最多 100 字，不能包含控制字符。")。
            throw new InvalidDataException("自定义信息最多 100 字，不能包含控制字符。");
        }

        if (GarbageCollector is not ("default" or "g1" or "g1-tuned" or "zgc" or "generational"))
        {
            // [实现] 条件成立（GarbageCollector is not ("default" or "g1" or "g1-tuned" or "zgc" or "generational")）时：抛出 new InvalidDataException("不支持的内存管理模式。")。
            throw new InvalidDataException("不支持的内存管理模式。");
        }

        if (GarbageCollector != "default" && ArgumentText.Parse(JvmArguments)
            .Any(
                x => x.StartsWith("-XX:") && (x.Contains("GC") || x.Contains("ZGenerational"))
            ))
        {
            // [实现] 条件成立（GarbageCollector != "default" && ArgumentText.Parse(JvmArguments) .Any( x => x.StartsWith("-XX:…）时：抛出 new InvalidDataException("已选择内存管理模式，请移除额外 JVM 参数中的 GC 选项，或选择不指定。")。
            throw new InvalidDataException("已选择内存管理模式，请移除额外 JVM 参数中的 GC 选项，或选择不指定。");
        }

        // 世界标识只能是当前实例 saves 下的目录名；不能同时自动进入单人与多人世界。
        if (QuickPlayWorld.Length > 200 || QuickPlayWorld is "." or ".." || QuickPlayWorld.Any(
            c => char.IsControl(c) || c is '/' or '\\'
        ))
        {
            // [实现] 条件成立（QuickPlayWorld.Length > 200 || QuickPlayWorld is "." or ".." || QuickPlayWorld.Any( c => char.I…）时：抛出 new InvalidDataException("快速进入世界需要填写当前实例的存档文件夹名称，不能填写路径。")。
            throw new InvalidDataException("快速进入世界需要填写当前实例的存档文件夹名称，不能填写路径。");
        }

        if (QuickPlayWorld.Length > 0 && (!string.IsNullOrWhiteSpace(ServerAddress) || ArgumentText.Parse(GameArguments)
            .Any(
                arg => arg.StartsWith("--quickPlay", StringComparison.Ordinal)
            )))
        {
            // [实现] 条件成立（QuickPlayWorld.Length > 0 && (!string.IsNullOrWhiteSpace(ServerAddress) || ArgumentText.Parse(G…）时：抛出 new InvalidDataException("快速进入存档不能同时配置服务器或重复的 Quick Play 参数。")。
            throw new InvalidDataException("快速进入存档不能同时配置服务器或重复的 Quick Play 参数。");
        }

        if (MinimumMemoryMb < 128 || MaximumMemoryMb < MinimumMemoryMb || MaximumMemoryMb > 262144)
            throw new InvalidDataException("内存范围应为 128–262144 MiB，最大值不能小于最小值。");
        if (Width is < 320 or > 16384 || Height is < 240 or > 16384)
            throw new InvalidDataException("窗口尺寸超出范围。");
        if (JavaExecutable.Length > 0 && !Path.IsPathFullyQualified(JavaExecutable))
            throw new InvalidDataException("Java 需要完整可执行文件路径。");
        foreach (var arg in ArgumentText.Parse(JvmArguments))
            if (!arg.StartsWith('-') || arg.StartsWith("-Xmx", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("-Xms", StringComparison.OrdinalIgnoreCase) || arg.Split('=', 2)[0] is "-cp" or "-classpath" or "--class-path" or "-jar" || arg.StartsWith('@'))
                throw new InvalidDataException("JVM 参数须独立以 - 开头；内存使用上方设置，不能覆盖 classpath 或入口。");
        foreach (var arg in ArgumentText.Parse(GameArguments))
            if (arg.Split('=', 2)[0] is "--accessToken" or "--uuid" or "--username" or "--userType" or "--gameDir" or "--assetsDir" or "--assetIndex" or "--demo")
                throw new InvalidDataException("附加参数不能覆盖账户、资源或实例目录。");
        if (ServerAddress.Length > 255 || ServerAddress.Any(char.IsWhiteSpace) || ServerAddress.Any(
            c => c is '\0' or '\r' or '\n'
        ))
            throw new InvalidDataException("服务器地址格式无效。");
    }
}

// [实现] 数据约定：InstancePreferences 汇集 Name、Description、Favorite、Hidden、Deleted、Group，供该模块传递状态。
public sealed record InstancePreferences(
    string Name = "",
    string Description = "",
    bool Favorite = false,
    bool Hidden = false,
    bool Deleted = false,
    string Group = ""
);
/// <summary>One authoritative store with per-field inheritance and a backed-up v1 migration.</summary>
public sealed class LauncherSettingsStore(AppPaths paths)
{
    private static readonly object Gate = new();
    private string FilePath => Path.Combine(paths.Root, "launch-settings.json");

    // [实现] InstanceKey 的实现：计算并返回 Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( Path.TrimEndingDirectorySeparator…。
    public static string InstanceKey(string gameDirectory) => Convert.ToHexString(
        SHA256.HashData(
            Encoding.UTF8.GetBytes(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(gameDirectory)
                )
            )
        )
    )
        .ToLowerInvariant();
    // [实现] Read 的实现：按 !File.Exists(FilePath) 分支处理；准备 var json = JsonFiles.Read(FilePath)；按 (int? )json["schemaVersion"] is not (1 or 2) 分支处理。
    private JObject Read()
    {
        if (!File.Exists(FilePath))
            return new JObject
            {
                ["schemaVersion"] = 1,
                ["global"] = JObject.FromObject(
                    new LaunchSettings()
                ),
                ["instances"] = new JObject()
            };
        var json = JsonFiles.Read(FilePath);
        if ((int? )json["schemaVersion"] is not (1 or 2))
            throw new InvalidDataException("不支持的启动设置版本。请保留设置文件并使用相应版本启动器。");
        return json;
    }

    public LaunchSettings Global
    {
        get
        {
            // [实现] 本段流程：锁定 Gate 后访问共享状态。
            lock (Gate)
            {
                // [实现] 本段流程：准备 var value = Read()["global"]?.ToObject<LaunchSettings>() ?? new()；执行 value.Validate()；返回 value。
                var value = Read()["global"]?.ToObject<LaunchSettings>() ?? new();
                value.Validate();
                return value;
            }
        }
    }

    // [实现] PatchFrom 的实现：准备 var row = json["instances"]?[InstanceKey(directory)]；按 (int? )json["schemaVersion"] == 1 分支处理；返回 row?["launchPatch"] is JObject patch ? LaunchSettingsPatch.Parse(patch) : null。
    private static LaunchSettingsPatch? PatchFrom(JObject json, string directory)
    {
        var row = json["instances"]?[InstanceKey(directory)];
        if ((int? )json["schemaVersion"] == 1)
            return row?["launch"] is JObject legacy
                ? LaunchSettingsPatch.From(
                    legacy.ToObject<LaunchSettings>()!
                )
                : null;
        return row?["launchPatch"] is JObject patch
            ? LaunchSettingsPatch.Parse(patch)
            : null;
    }

    // [实现] Patch 的实现：锁定 Gate 后访问共享状态。
    public LaunchSettingsPatch? Patch(string directory)
    {
        lock (Gate)
            return PatchFrom(
                Read(),
                directory
            );
    }

    // Compatibility API returns a resolved snapshot. Copy/migration use Patch to preserve inheritance.
    public LaunchSettings? Override(string directory)
    {
        lock (Gate)
        {
            // [实现] 本段流程：准备 var patch = PatchFrom( Read(), directory )；返回 patch is null ? null : patch.Apply(Global)。
            var patch = PatchFrom(
                Read(),
                directory
            );
            return patch is null
                ? null
                : patch.Apply(Global);
        }
    }

    // [实现] Effective 的实现：锁定 Gate 后访问共享状态。
    public LaunchSettings Effective(string directory)
    {
        lock (Gate)
        {
            // [实现] 本段流程：准备 var json = Read()；准备 var global = json["global"]?.ToObject<LaunchSettings>() ?? new()；执行 global.Validate()。
            var json = Read();
            var global = json["global"]?.ToObject<LaunchSettings>() ?? new();
            global.Validate();
            return PatchFrom(json, directory)?.Apply(global) ?? global;
        }
    }

    // [实现] Preferences 的实现：锁定 Gate 后访问共享状态。
    public InstancePreferences Preferences(string directory)
    {
        lock (Gate)
        {
            // 旧设置没有 Group 字段，按未分组读取；统一名称，避免空格与 Unicode 表示差异。
            var value = Read()["instances"]?[InstanceKey(directory)]?["preferences"]?.ToObject<InstancePreferences>() ?? new();
            return value with
            {
                Group = NormalizeGroup(value.Group)
            };
        }
    }

    /// <summary>分组只作为显示元数据，不参与目录拼接；限制长度和控制字符并规范化 Unicode。</summary>
    public static string NormalizeGroup(string? value)
    {
        value ??= "";
        if (value.Length > 60 || value.Any(char.IsControl))
        {
            // [实现] 条件成立（value.Length > 60 || value.Any(char.IsControl)）时：抛出 new InvalidDataException("分组名称最多 60 个字符，不能包含换行或控制字符。")。
            throw new InvalidDataException("分组名称最多 60 个字符，不能包含换行或控制字符。");
        }

        return value.Trim()
            .Normalize(NormalizationForm.FormC);
    }

    // [实现] Upgrade 的实现：按 (int? )json["schemaVersion"] == 2 分支处理；按 File.Exists(FilePath) 分支处理；按 json["instances"] is JObject instances 分支处理。
    private void Upgrade(JObject json)
    {
        if ((int? )json["schemaVersion"] == 2)
            return;
        if (File.Exists(FilePath))
        {
            // [实现] 条件成立（File.Exists(FilePath)）时：准备 var backup = Path.Combine( paths.Root, "settings-backups", "launch-schema1-" + DateTime.UtcNow.…；执行 Directory.CreateDirectory( Path.GetDirectoryName(backup)! )；执行 File.Copy(FilePath, backup)。
            var backup = Path.Combine(
                paths.Root,
                "settings-backups",
                "launch-schema1-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid()
                    .ToString("N") + ".json"
            );
            Directory.CreateDirectory(
                Path.GetDirectoryName(backup)!
            );
            File.Copy(FilePath, backup);
        }

        if (json["instances"] is JObject instances)
            foreach (var row in instances.Properties()
                .Select(
                    x => x.Value
                )
                .OfType<JObject>())
            {
                // [实现] 对 instances.Properties() .Select( x => x.Value ) .OfType<JObject>() 中的每一项：按 row["launch"] is JObject legacy 分支处理；执行 row.Remove("launch")。
                if (row["launch"] is JObject legacy)
                    row["launchPatch"] = LaunchSettingsPatch.From(
                        legacy.ToObject<LaunchSettings>()!
                    )
                        .ToJson();
                row.Remove("launch");
            }

        json["schemaVersion"] = 2;
    }

    // [实现] SaveGlobal 的实现：执行 settings.Validate()；锁定 Gate 后访问共享状态。
    public void SaveGlobal(LaunchSettings settings)
    {
        settings.Validate();
        lock (Gate)
        {
            // [实现] 本段流程：准备 var json = Read()；按 json["instances"] is JObject instances 分支处理；执行 Upgrade(json)。
            var json = Read();
            if (json["instances"] is JObject instances)
                foreach (var row in instances.Properties()
                    .Select(
                        x => x.Value
                    )
                    .OfType<JObject>())
                {
                    // [实现] 对 instances.Properties() .Select( x => x.Value ) .OfType<JObject>() 中的每一项：准备 var patch = (int? )json["schemaVersion"] == 1 ? (row["launch"] is JObject old ? LaunchSettingsP…；执行受异常处理保护的操作。
                    var patch = (int? )json["schemaVersion"] == 1
                        ? (row["launch"] is JObject old
                            ? LaunchSettingsPatch.From(
                                old.ToObject<LaunchSettings>()!
                            )
                            : null)
                        : (row["launchPatch"] is JObject value
                            ? LaunchSettingsPatch.Parse(value)
                            : null);
                    try
                    {
                        // [实现] 在异常保护范围内：执行 patch?.Apply(settings)。
                        patch?.Apply(settings);
                    }
                    catch (InvalidDataException ex)
                    {
                        // [实现] 捕获 (InvalidDataException ex) 后：抛出 new InvalidDataException("全局值与某个实例覆盖冲突，请先调整该实例：" + (string? )row["directory"] + "。" + ex.Messag…。
                        throw new InvalidDataException("全局值与某个实例覆盖冲突，请先调整该实例：" + (string? )row["directory"] + "。" + ex.Message, ex);
                    }
                }

            Upgrade(json);
            json["global"] = JObject.FromObject(settings);
            JsonFiles.Write(FilePath, json);
        }
    }

    // [实现] SaveOverride 的实现：执行 settings?.Validate()；执行 SavePatch( directory, settings is null ? null : LaunchSettingsPatch.From(settings) )。
    public void SaveOverride(string directory, LaunchSettings? settings)
    {
        settings?.Validate();
        SavePatch(
            directory,
            settings is null
                ? null
                : LaunchSettingsPatch.From(settings)
        );
    }

    // [实现] SavePatch 的实现：锁定 Gate 后访问共享状态。
    public void SavePatch(string directory, LaunchSettingsPatch? patch)
    {
        lock (Gate)
        {
            // [实现] 本段流程：执行 patch?.Apply(Global)；执行 Update( directory, row => row["launchPatch"] = patch is null || patch.Fields.Count == 0 ? null …。
            patch?.Apply(Global);
            Update(
                directory,
                row => row["launchPatch"] = patch is null || patch.Fields.Count == 0
                    ? null
                    : patch.ToJson()
            );
        }
    }

    // [实现] SavePreferences 的实现：执行 preferences = preferences with { Group = NormalizeGroup(preferences.Group) }；按 preferences.Name.Length > 100 || preferences.Description.Length > 2000 || preferences.Name.Any(… 分支处理；执行 Update( directory, row => row["preferences"] = JObject.FromObject(preferences) )。
    public void SavePreferences(string directory, InstancePreferences preferences)
    {
        preferences = preferences with
        {
            Group = NormalizeGroup(preferences.Group)
        };
        if (preferences.Name.Length > 100 || preferences.Description.Length > 2000 || preferences.Name.Any(
            c => char.IsControl(c)
        ))
            throw new InvalidDataException("实例名称或描述超出范围。");
        Update(
            directory,
            row => row["preferences"] = JObject.FromObject(preferences)
        );
    }

    // [实现] ValidateInstance 的实现：执行 _ = NormalizeGroup(preferences.Group)；按 preferences.Name.Length > 100 || preferences.Description.Length > 2000 || preferences.Name.Any(… 分支处理；执行 patch?.Apply(Global)。
    public void ValidateInstance(InstancePreferences preferences, LaunchSettingsPatch? patch)
    {
        _ = NormalizeGroup(preferences.Group);
        if (preferences.Name.Length > 100 || preferences.Description.Length > 2000 || preferences.Name.Any(char.IsControl))
            throw new InvalidDataException("实例名称或描述超出范围。");
        patch?.Apply(Global);
    }

    // [实现] SaveInstance 的实现：执行 preferences = preferences with { Group = NormalizeGroup(preferences.Group) }；锁定 Gate 后访问共享状态。
    public void SaveInstance(string directory, InstancePreferences preferences, LaunchSettingsPatch? patch)
    {
        preferences = preferences with
        {
            Group = NormalizeGroup(preferences.Group)
        };
        lock (Gate)
        {
            // [实现] 本段流程：执行 ValidateInstance(preferences, patch)；执行 Update( directory, row => { row["preferences"] = JObject.FromObject(preferences); row["launchPa…。
            ValidateInstance(preferences, patch);
            Update(
                directory,
                row =>
                {
                    // [实现] 回调触发时：执行 row["preferences"] = JObject.FromObject(preferences)；执行 row["launchPatch"] = patch is null || patch.Fields.Count == 0 ? null : patch.ToJson()。
                    row["preferences"] = JObject.FromObject(preferences);
                    row["launchPatch"] = patch is null || patch.Fields.Count == 0
                        ? null
                        : patch.ToJson();
                }
            );
        }
    }

    // [实现] Update 的实现：锁定 Gate 后访问共享状态。
    private void Update(string directory, Action<JObject> action)
    {
        lock (Gate)
        {
            // [实现] 本段流程：准备 var json = Read()；执行 Upgrade(json)；按 json["instances"] is not JObject 分支处理。
            var json = Read();
            Upgrade(json);
            if (json["instances"] is not JObject)
                json["instances"] = new JObject();
            var rows = (JObject)json["instances"]!;
            var key = InstanceKey(directory);
            if (rows[key] is not JObject)
                rows[key] = new JObject
                {
                    ["directory"] = Path.GetFullPath(directory)
                };
            action((JObject)rows[key]!);
            JsonFiles.Write(FilePath, json);
        }
    }

    // [实现] ResolveJavaAsync 的实现：准备 var settings = Effective(directory)；准备 var java = string.IsNullOrWhiteSpace(settings.JavaExecutable) ? await new JavaService(service).…；按 java.MajorVersion != version.JavaMajor || java.Architecture != service.Platform.Architecture 分支处理。
    public async Task<JavaRuntime> ResolveJavaAsync(
        MinecraftService service,
        InstalledVersion version,
        string directory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var settings = Effective(directory);
        var java = string.IsNullOrWhiteSpace(settings.JavaExecutable)
            ? await new JavaService(service).EnsureForAsync(version, progress, ct)
            : await JavaService.InspectAsync(settings.JavaExecutable, ct, service.Commands);
        if (java.MajorVersion != version.JavaMajor || java.Architecture != service.Platform.Architecture)
            throw new InvalidDataException(
                $"此实例需要 {service.Platform.Architecture} Java {version.JavaMajor}，所选为 {java.Architecture} Java {java.MajorVersion}。"
            );
        return java;
    }
}

// 设置和个性化：读取、校验并应用用户配置，更新界面或处理本地文件入口，保存可恢复设置。
public static class ArgumentText
{
    /// <summary>Tokenizes argument text without shell expansion, command substitution or environment expansion.</summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        if (text.Length > 32768 || text.Contains('\0'))
            throw new InvalidDataException("参数过长或包含空字符。");
        var args = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        bool escaping = false, started = false;
        foreach (var c in text)
        {
            // [实现] 对 text 中的每一项：按 escaping 分支处理；按 c == '\\' && quote != '\'' 分支处理；按 quote != '\0' 分支处理。
            if (escaping)
            {
                // [实现] 条件成立（escaping）时：执行 current.Append(c)；执行 escaping = false；执行 started = true。
                current.Append(c);
                escaping = false;
                started = true;
                continue;
            }

            if (c == '\\' && quote != '\'')
            {
                // [实现] 条件成立（c == '\\' && quote != '\''）时：执行 escaping = true；执行 started = true；执行 continue;。
                escaping = true;
                started = true;
                continue;
            }

            if (quote != '\0')
            {
                // [实现] 条件成立（quote != '\0'）时：按 c == quote 分支处理；执行 started = true；执行 continue;。
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
                started = true;
                continue;
            }

            if (c is '\'' or '"')
            {
                // [实现] 条件成立（c is '\'' or '"'）时：执行 quote = c；执行 started = true；执行 continue;。
                quote = c;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                // [实现] 条件成立（char.IsWhiteSpace(c)）时：按 started 分支处理；执行 continue;。
                if (started)
                {
                    // [实现] 条件成立（started）时：执行 args.Add( current.ToString() )；执行 current.Clear()；执行 started = false。
                    args.Add(
                        current.ToString()
                    );
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(c);
            started = true;
        }

        if (escaping || quote != '\0')
            throw new InvalidDataException("参数引号或转义未结束。");
        if (started)
            args.Add(
                current.ToString()
            );
        return args;
    }
}
