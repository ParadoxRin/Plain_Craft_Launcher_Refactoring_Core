using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
/// <summary>Null means inherited. Empty strings, zero-like values and false remain explicit overrides.</summary>
public sealed record LaunchSettingsPatch
{
    public int? MinimumMemoryMb { get; init; }
    public int? MaximumMemoryMb { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool? Fullscreen { get; init; }
    public bool? DisableLwjglUnsafeAgent { get; init; }
    public string? GameWindowTitle { get; init; }
    public string? WindowMode { get; init; }
    public string? ProcessPriority { get; init; }
    public bool? AutomaticMemory { get; init; }
    public string? VersionInformation { get; init; }
    public string? GarbageCollector { get; init; }
    public string? JavaExecutable { get; init; }
    public string? JvmArguments { get; init; }
    public string? GameArguments { get; init; }
    public string? ServerAddress { get; init; }
    public string? QuickPlayWorld { get; init; }

    public static readonly string[] FieldNames = [
        nameof(MinimumMemoryMb),
        nameof(MaximumMemoryMb),
        nameof(Width),
        nameof(Height),
        nameof(Fullscreen),
        nameof(GameWindowTitle),
        nameof(DisableLwjglUnsafeAgent),
        nameof(WindowMode),
        nameof(ProcessPriority),
        nameof(AutomaticMemory),
        nameof(VersionInformation),
        nameof(GarbageCollector),
        nameof(JavaExecutable),
        nameof(JvmArguments),
        nameof(GameArguments),
        nameof(ServerAddress),
        nameof(QuickPlayWorld)
    ];
    // [实现] ToJson 的实现：计算并返回 JObject.FromObject( this, JsonSerializer.Create( new JsonSerializerSettings { NullValueHandling…。
    public JObject ToJson() => JObject.FromObject(
        this,
        JsonSerializer.Create(
            new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            }
        )
    );
    [JsonIgnore]
    public IReadOnlyList<string> Fields => ToJson()
        .Properties()
        .Select(
            x => x.Name
        )
        .ToArray();

    // [实现] Apply 的实现：准备 var json = JObject.FromObject(global)；执行 json.Merge( ToJson() )；准备 var result = json.ToObject<LaunchSettings>()!。
    public LaunchSettings Apply(LaunchSettings global)
    {
        var json = JObject.FromObject(global);
        json.Merge(
            ToJson()
        );
        var result = json.ToObject<LaunchSettings>()!;
        result.Validate();
        return result;
    }

    // [实现] From 的实现：准备 var selected = (fields ?? FieldNames).ToHashSet(StringComparer.Ordinal)；按 selected.Except(FieldNames) .Any() 分支处理；准备 var json = JObject.FromObject(value)。
    public static LaunchSettingsPatch From(LaunchSettings value, IEnumerable<string>? fields = null)
    {
        var selected = (fields ?? FieldNames).ToHashSet(StringComparer.Ordinal);
        if (selected.Except(FieldNames)
            .Any())
            throw new InvalidDataException("启动设置包含未知字段。");
        var json = JObject.FromObject(value);
        foreach (var property in json.Properties()
            .ToArray())
            if (!selected.Contains(property.Name))
                property.Remove();
        return json.ToObject<LaunchSettingsPatch>()!;
    }

    // [实现] Parse 的实现：按 json.Properties() .Any( x => !FieldNames.Contains(x.Name) ) 分支处理；返回 json.ToObject<LaunchSettingsPatch>()!。
    public static LaunchSettingsPatch Parse(JObject json)
    {
        if (json.Properties()
            .Any(
                x => !FieldNames.Contains(x.Name)
            ))
            throw new InvalidDataException("实例启动设置字段无法识别，请保留原文件。");
        return json.ToObject<LaunchSettingsPatch>()!;
    }

    // [实现] WithoutExecutableOverrides 的实现：计算并返回 this with { JavaExecutable = JavaExecutable is null ? null : "", JvmArguments = JvmArguments is…。
    public LaunchSettingsPatch WithoutExecutableOverrides() => this with
    {
        JavaExecutable = JavaExecutable is null
            ? null
            : "",
        JvmArguments = JvmArguments is null
            ? null
            : "",
        GameArguments = GameArguments is null
            ? null
            : "",
        ServerAddress = ServerAddress is null
            ? null
            : "",
        QuickPlayWorld = QuickPlayWorld is null
            ? null
            : ""
    };
}
