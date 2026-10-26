using System.IO.Compression;
using System.Security.Cryptography;
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
    IReadOnlyList<Pcl.Domain.FabricDependency>? EffectiveDependencies = null,
    string CandidateKey = "",
    IReadOnlyList<string>? ParentKeys = null,
    string Origin = "",
    int Schema = 1,
    int NestingDepth = 0
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
    // 以顶层文件名和嵌套内容哈希标记候选；配置覆盖在读取完整图后统一应用。
    public static IReadOnlyList<FabricModInfo> Read(string file, string? gameDirectory = null)
    {
        using var stream = File.OpenRead(file);
        var name = Path.GetFileName(file);
        var mods = ReadArchive(
            stream,
            0,
            "root:" + name,
            null,
            name,
            new ReadBudget()
        );
        return gameDirectory is null
            ? mods
            : FabricDependencyOverrides.Read(gameDirectory)
                .Apply(mods);
    }

    // 每个根包共享展开预算，避免递归压缩包耗尽内存或阻塞界面。
    private sealed class ReadBudget
    {
        public int Archives;
        public long ExpandedBytes;
    }

    // Retain archive identity and parent edges. Equal nested bytes may be supplied by several enclosing mods.
    private static IReadOnlyList<FabricModInfo> ReadArchive(Stream stream, int depth, string key, string? parent, string origin, ReadBudget budget)
    {
        if (depth > 8 || ++budget.Archives > 2048)
        {
            throw new NotSupportedException("嵌套模组发现超过深度8或2048个归档；尚未判定兼容性。");
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        var entry = archive.GetEntry("fabric.mod.json");
        if (entry is null)
        {
            if (parent is not null)
            {
                return[]; // Nested ordinary libraries are not mod candidates.
            }

            throw new InvalidDataException("文件不是 Fabric 模组。");
        }

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
                EffectiveDependencies: metadata.Dependencies,
                CandidateKey: key,
                ParentKeys: parent is null
                    ? []
                    : [
                        parent
                    ],
                Origin: origin,
                Schema: metadata.Schema,
                NestingDepth: depth,
                IsNested: parent is not null
            )
        };
        if (metadata.Environment == "server")
        {
            return result; // The Loader does not discover descendants of an environment-disabled parent.
        }

        foreach (var path in metadata.Jars.Distinct(StringComparer.Ordinal))
        {
            var jar = archive.GetEntry(path);
            if (jar is null)
            {
                continue; // Production Loader ignores declared nested files that do not exist.
            }

            if (jar.Length > 64 * 1024 * 1024)
                throw new InvalidDataException("嵌套模组过大。");
            budget.ExpandedBytes = checked(budget.ExpandedBytes + jar.Length);
            if (budget.ExpandedBytes > 256L * 1024 * 1024)
            {
                throw new NotSupportedException("单个模组的嵌套展开超过256 MiB；尚未判定兼容性。");
            }

            using var data = new MemoryStream();
            using (var source = jar.Open())
                source.CopyTo(data);
            data.Position = 0;
            var nestedKey = "nested:" + Convert.ToHexString(
                SHA256.HashData(data)
            );
            data.Position = 0;
            result.AddRange(
                ReadArchive(data, depth + 1, nestedKey, key, origin + "!" + path, budget)
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
