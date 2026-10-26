using System.IO.Compression;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class FabricVersionPredicate
{
    // JSON arrays are alternatives; separate dependency declarations remain conjunctive.
    public static bool Matches(string version, JToken predicate) => predicate is JArray array
        ? array.Any(
            x => Matches(version, x)
        )
        : Pcl.Domain.FabricVersions.Matches(
            version,
            predicate.Value<string>()!
        );
    public static bool Matches(string version, string predicate) => Pcl.Domain.FabricVersions.Matches(version, predicate);
    public static int Compare(string left, string right) => Pcl.Domain.FabricVersions.Compare(left, right);
}

// [实现] 数据约定：FabricModInfo 汇集 Id、Version、Depends、Breaks、Provides、IsNested，供该模块传递状态。
public sealed record FabricModInfo(
    string Id,
    string Version,
    JObject Depends,
    JObject Breaks,
    IReadOnlyList<string> Provides,
    bool IsNested = false,
    string Environment = "*",
    IReadOnlyList<Pcl.Domain.FabricDependency>? EffectiveDependencies = null
)
{
    // Legacy constructors remain supported; actual archives carry all five dependency kinds.
    public IEnumerable<Pcl.Domain.FabricDependency> Dependencies(string kind) => EffectiveDependencies is not null
        ? EffectiveDependencies.Where(
            x => x.Kind == kind
        )
        : (kind == "depends"
            ? Depends
            : kind == "breaks"
                ? Breaks
                : new JObject()).Properties()
            .Select(
                x => new Pcl.Domain.FabricDependency(
                    kind,
                    x.Name,
                    x.Value is JArray a
                        ? a.Values<string>()
                            .Select(
                                v => v!
                            )
                            .ToArray()
                        : [
                            x.Value.Value<string>()!
                        ]
                )
            );
}

// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class FabricModReader
{
    // [实现] Read 的实现：准备 var stream = File.OpenRead(file)；返回 ReadArchive(stream, 0)。
    public static IReadOnlyList<FabricModInfo> Read(string file, string? gameDirectory = null)
    {
        using var stream = File.OpenRead(file);
        var mods = ReadArchive(stream, 0);
        return gameDirectory is null
            ? mods
            : FabricDependencyOverrides.Read(gameDirectory)
                .Apply(mods);
    }

    // [实现] ReadArchive 的实现：按 depth > 8 分支处理；准备 var archive = new ZipArchive(stream, ZipArchiveMode.Read, true)；准备 var entry = archive.GetEntry("fabric.mod.json")。
    private static IReadOnlyList<FabricModInfo> ReadArchive(Stream stream, int depth)
    {
        if (depth > 8)
            throw new InvalidDataException("嵌套模组过深。");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        var entry = archive.GetEntry("fabric.mod.json");
        if (entry is null)
            throw new InvalidDataException("文件不是 Fabric 模组。");
        if (entry.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("模组元数据过大。");
        using var reader = new StreamReader(
            entry.Open()
        );
        var metadata = Pcl.Domain.FabricMetadataParser.Parse(
            reader.ReadToEnd()
        );
        JObject Map(string kind)
        {
            var values = new JObject();
            foreach (var rule in metadata.Dependencies.Where(
                x => x.Kind == kind
            ))
            {
                values[rule.Id] = new JArray(rule.Matchers);
            }

            return values;
        }

        var result = new List<FabricModInfo>
        {
            new(
                metadata.Id,
                metadata.Version,
                Map("depends"),
                Map("breaks"),
                metadata.Provides,
                Environment: metadata.Environment,
                EffectiveDependencies: metadata.Dependencies
            )
        };
        foreach (var path in metadata.Jars)
        {
            // [实现] 对 json["jars"] ?? new JArray() 中的每一项：准备 var path = (string)nested["file"]!；准备 var jar = archive.GetEntry(path) ?? throw new InvalidDataException("缺少嵌套模组：" + path)；按 jar.Length > 64 * 1024 * 1024 分支处理。
            var jar = archive.GetEntry(path) ?? throw new InvalidDataException("缺少嵌套模组：" + path);
            if (jar.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("嵌套模组过大。");
            using var data = new MemoryStream();
            using (var source = jar.Open())
                source.CopyTo(data);
            data.Position = 0;
            result.AddRange(
                ReadArchive(data, depth + 1)
                    .Select(
                        x => x with
                        {
                            IsNested = true
                        }
                    )
            );
        }

        return result;
    }
}

// Modrinth's dependency edges do not always encode the version predicates in fabric.mod.json.
// Resolve official project dependencies, then reconcile those predicates against downloaded JARs.
public sealed class FabricModPlanner(ResourceCatalog catalog, DownloadService downloads, string cacheRoot, string loaderVersion)
{
    public async Task<ResourceInstallPlan> ResolveAsync(
        ResourceRelease root,
        string gameVersion,
        IReadOnlyList<ManagedResource>? installed = null,
        string? gameDirectory = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        // Historical callers without an instance get an isolated, explicit loader context.
        var temporary = gameDirectory is null;
        var directory = gameDirectory ?? ResourceInstaller.SafeTarget(
            cacheRoot,
            "planner-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(directory);
        if (temporary)
        {
            JsonFiles.Write(
                Path.Combine(directory, "profile.json"),
                new
                {
                    loaderVersion
                }
            );
        }

        try
        {
            return await ResourceDependencyPlanner.ResolveAsync(
                catalog,
                downloads,
                root,
                ResourceKind.Mod,
                new GameProfile(
                    "planning",
                    "planning",
                    gameVersion,
                    "fabric",
                    directory,
                    Path.Combine(directory, "version.json")
                ),
                progress,
                ct
            );
        }
        finally
        {
            if (temporary)
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
