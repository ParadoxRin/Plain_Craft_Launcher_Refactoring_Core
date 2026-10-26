using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：QuiltProvided 汇集 Group、Id、Version，供该模块传递状态。
public sealed record QuiltProvided(string Group, string Id, string Version);
// [实现] 数据约定：QuiltCandidate 汇集 Source、Id、Group、Version、Mandatory、LoadType、Environment、Fabric、Depends、Breaks、Provides，供该模块传递状态。
public sealed record QuiltCandidate(
    string Source,
    string Id,
    string Group,
    string Version,
    bool Mandatory,
    string LoadType,
    string Environment,
    bool Fabric,
    JArray Depends,
    JArray Breaks,
    IReadOnlyList<QuiltProvided> Provides,
    bool QuiltDependencySemantics = false
);
// [实现] 数据约定：QuiltResolution 汇集 Selected、Candidates、Warnings，供该模块传递状态。
public sealed record QuiltResolution(IReadOnlyList<QuiltCandidate> Selected, int Candidates, IReadOnlyList<string> Warnings);
// Quilt 依赖兼容：读取候选与版本条件，建立或检查约束，仅提交能同时满足当前实例条件的组合。
public static class QuiltDependencies
{
    // [实现] Text 的实现：按 entry.Length > 2 * 1024 * 1024 分支处理；准备 var r = new StreamReader( entry.Open() )；返回 r.ReadToEnd()。
    private static string Text(ZipArchiveEntry entry)
    {
        if (entry.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Quilt/Fabric 元数据过大。");
        using var r = new StreamReader(
            entry.Open()
        );
        return r.ReadToEnd();
    }

    // [实现] 功能：读取顶层及嵌套 Quilt/Fabric 模组候选。实现：递归检查 ZIP 元数据、环境和嵌套路径，以 SHA-256 去重并限制深度与总大小，保留每个候选的来源和依赖。
    public static IReadOnlyList<QuiltCandidate> Read(IEnumerable<string> files)
    {
        var result = new List<QuiltCandidate>();
        var nestedHashes = new HashSet<string>();
        long bytes = 0;
        // [实现] 功能：展开单个归档的候选图。实现：拒绝生产环境 JSON5、重复条目和不支持的插件，再读取当前模组并递归处理 jars；顶层文件标记为必选。
        void Archive(Stream input, string source, bool direct, int depth)
        {
            if (depth > 8 || result.Count > 2048)
                throw new NotSupportedException("Quilt 嵌套模组数量或深度超出支持范围。");
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
            QuiltMetadataJson.CheckProductionArchive(zip);
            // [实现] 功能：精确定位归档条目。实现：按完整路径收集匹配项；重复路径即失败，避免解析器与加载器读取不同字节。
            ZipArchiveEntry? Entry(string name)
            {
                var matches = zip.Entries.Where(
                    x => x.FullName == name
                )
                    .ToArray();
                if (matches.Length > 1)
                    throw new InvalidDataException("模组归档存在重复元数据或嵌套路径。");
                return matches.SingleOrDefault();
            }

            var entry = Entry("quilt.mod.json") ?? Entry("fabric.mod.json");
            if (entry is null)
            {
                // [实现] 条件成立（entry is null）时：按 direct 分支处理；结束当前过程。
                if (direct)
                    throw new InvalidDataException("模组缺少 Quilt/Fabric 元数据：" + source);
                return;
            }

            var fabric = entry.FullName == "fabric.mod.json";
            var root = fabric
                ? JObject.Parse(
                    Text(entry)
                )
                : QuiltMetadataJson.Parse(
                    Text(entry)
                );
            var data = fabric
                ? root
                : root["quilt_loader"] as JObject ?? throw new InvalidDataException("Quilt loader 元数据缺失。");
            if (!fabric && (int? )root["schema_version"] != 1 || fabric && (int? )root["schemaVersion"] != 1)
                throw new NotSupportedException("仅支持 Quilt schema 1 / Fabric schema 1 元数据。");
            if (root["quilt_loader"]?["plugins"] is not null || root["quilt_loader_plugin"] is not null)
                throw new NotSupportedException("自定义 Quilt 加载器插件需要官方加载器审查，不能静态判定兼容。");
            // [实现] 功能：读取必需的字符串字段。实现：同时检查类型和空白，字段缺失时在构建候选之前停止。
            string Required(string name)
            {
                var v = data[name];
                return v?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string? )v)
                    ? v.Value<string>()!
                    : throw new InvalidDataException("模组字段缺失：" + name);
            }

            var id = Required("id");
            if (!Regex.IsMatch(id, @"^[a-z][a-z0-9_-]{1,63}$"))
                throw new InvalidDataException("模组 ID 无效：" + id);
            var group = fabric
                ? "loader.fabric"
                : Required("group");
            var version = Required("version");
            if (!fabric && !Regex.IsMatch(group, @"^[a-zA-Z0-9_.-]+$"))
                throw new InvalidDataException("Quilt group 无效。");
            var env = (string? )(fabric
                ? root["environment"]
                : root["minecraft"]?["environment"]) ?? "*";
            if (env is not ("*" or "client" or "server" or "dedicated_server"))
                throw new InvalidDataException("模组环境无效。");
            var load = fabric
                ? "if_possible"
                : (string? )data["load_type"] ?? "if_required";
            if (load is not ("always" or "if_possible" or "if_required"))
                throw new InvalidDataException("Quilt load_type 无效。");
            // [实现] 功能：统一依赖数据结构。实现：Quilt 保留数组表达式，Fabric 对象映射转换为 id/versions 条目，交给对应的版本比较器。
            JArray Dependencies(string name)
            {
                if (data[name] is null)
                    return[];
                if (!fabric)
                    return data[name] as JArray ?? throw new InvalidDataException("Quilt 依赖声明必须是数组。");
                if (data[name] is not JObject obj)
                    throw new InvalidDataException("Fabric 依赖声明必须是对象。");
                return new JArray(
                    obj.Properties()
                        .Select(
                            p => new JObject
                            {
                                ["id"] = p.Name,
                                ["versions"] = p.Value.DeepClone()
                            }
                        )
                );
            }

            var provides = new List<QuiltProvided>();
            if (data["provides"] is { } provided)
            {
                // [实现] 条件成立（data["provides"] is { } provided）时：按 provided is not JArray array 分支处理；遍历 array 处理 value。
                if (provided is not JArray array)
                    throw new InvalidDataException("provides 必须是数组。");
                foreach (var value in array)
                {
                    // [实现] 对 array 中的每一项：准备 var alias = value.Type == JTokenType.String ? (string)value! : !fabric && value is JObject ? (s…；按 string.IsNullOrWhiteSpace(alias) 分支处理；准备 var parts = alias.Split(':')。
                    var alias = value.Type == JTokenType.String
                        ? (string)value!
                        : !fabric && value is JObject
                            ? (string? )value["id"]
                            : null;
                    if (string.IsNullOrWhiteSpace(alias))
                        throw new InvalidDataException("无效 provides。");
                    var parts = alias.Split(':');
                    if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
                        throw new InvalidDataException("无效 provides ID。");
                    provides.Add(
                        new(
                            parts.Length == 2
                                ? parts[0]
                                : group,
                            parts[^1],
                            value is JObject
                                ? (string? )value["version"] ?? version
                                : version
                        )
                    );
                }
            }

            result.Add(
                new(
                    source,
                    id,
                    group,
                    version,
                    direct,
                    load,
                    env,
                    fabric,
                    Dependencies("depends"),
                    Dependencies("breaks"),
                    provides
                )
            );
            if (data["jars"] is { } jars)
            {
                // [实现] 条件成立（data["jars"] is { } jars）时：按 jars is not JArray jarList 分支处理；遍历 jarList 处理 jar。
                if (jars is not JArray jarList)
                    throw new InvalidDataException("嵌套 jars 必须是数组。");
                foreach (var jar in jarList)
                {
                    // [实现] 对 jarList 中的每一项：准备 var path = fabric ? (string? )jar["file"] : jar.Type == JTokenType.String ? (string? )jar : nul…；按 path is null 分支处理；执行 DependencyArchive.ValidatePath(path)。
                    var path = fabric
                        ? (string? )jar["file"]
                        : jar.Type == JTokenType.String
                            ? (string? )jar
                            : null;
                    if (path is null)
                        throw new InvalidDataException("嵌套 JAR 路径缺失。");
                    DependencyArchive.ValidatePath(path);
                    var child = Entry(path) ?? throw new InvalidDataException("嵌套 JAR 缺失：" + path);
                    if (child.Length > 64 * 1024 * 1024 || (bytes += child.Length) > 512L * 1024 * 1024)
                        throw new NotSupportedException("嵌套 JAR 总体积超出检查上限。");
                    using var buffer = new MemoryStream();
                    using (var stream = child.Open())
                        stream.CopyTo(buffer);
                    if (!nestedHashes.Add(
                        Convert.ToHexString(
                            SHA256.HashData(
                                buffer.ToArray()
                            )
                        )
                    ))
                        continue;
                    buffer.Position = 0;
                    Archive(buffer, source + "!/" + path, false, depth + 1);
                }
            }
        }

        foreach (var file in files)
        {
            // [实现] 对 files 中的每一项：准备 var stream = File.OpenRead(file)；执行 Archive(stream, file, true, 0)。
            using var stream = File.OpenRead(file);
            Archive(stream, file, true, 0);
        }

        return result;
    }

    // [实现] 功能：判断整个模组集合能否同时加载。实现：补入 Minecraft/Java/加载器候选并应用实例覆盖，编码必选、环境、身份互斥、depends/breaks，交由有界加权求解器择版；无解或超预算均不会返回兼容。
    public static QuiltResolution Resolve(
        IEnumerable<QuiltCandidate> input,
        string game,
        string loaderVersion,
        int javaMajor,
        CancellationToken ct = default,
        string? gameDirectory = null,
        string? runtimeContextDirectory = null,
        Pcl.Core.Adapters.Compatibility.QuiltOverrideRuntime? overrideRuntime = null
    )
    {
        var candidates = input.ToList();
        // [实现] Builtin 的实现：计算并返回 new( "builtin:" + id, id, "", version, true, "always", "*", false, [], [], [] )。
        QuiltCandidate Builtin(string id, string version) => new(
            "builtin:" + id,
            id,
            "",
            version,
            true,
            "always",
            "*",
            false,
            [],
            [],
            []
        );
        candidates.Add(
            Builtin("minecraft", game)
        );
        candidates.Add(
            Builtin(
                "java",
                javaMajor.ToString()
            )
        );
        if (!candidates.Any(
            x => x.Id == "quilt_loader"
        ))
            candidates.Add(
                Builtin("quilt_loader", loaderVersion)
            );
        // 在线下载计划、本地事务与启动预检共用同一覆盖入口，防止预览与提交使用不同约束。
        var warnings = new List<string>();
        if (gameDirectory is not null)
            candidates = QuiltDependencyOverrides.Read(gameDirectory, runtimeContextDirectory, javaMajor, overrideRuntime, ct)
                .Apply(candidates, warnings)
                .ToList();
        var sat = new ConstraintSolver();
        var variables = new Dictionary<QuiltCandidate, int>();
        foreach (var grouping in candidates.GroupBy(
            x => x.Id
        ))
        {
            // [实现] 对 candidates.GroupBy( x => x.Id ) 中的每一项：准备 int rank = 0；遍历 grouping.OrderBy( x => x.Version, Comparer<string>.Create(QuiltVersionRange.Compare) ) 处理 candidate。
            int rank = 0;
            foreach (var candidate in grouping.OrderBy(
                x => x.Version,
                Comparer<string>.Create(QuiltVersionRange.Compare)
            ))
            {
                // [实现] 对 grouping.OrderBy( x => x.Version, Comparer<string>.Create(QuiltVersionRange.Compare) ) 中的每一项：准备 var v = sat.Variable( (candidate.LoadType == "if_possible" ? -1000 : 1000) - rank++ )；执行 variables.Add(candidate, v)；按 candidate.Environment is "server" or "dedicated_server" 分支处理。
                var v = sat.Variable(
                    (candidate.LoadType == "if_possible"
                        ? -1000
                        : 1000) - rank++
                );
                variables.Add(candidate, v);
                if (candidate.Environment is "server" or "dedicated_server")
                    sat.Add(-v);
                else if (candidate.Mandatory)
                    sat.Add(v);
            }
        }

        var identities = candidates.SelectMany(
            x => new[]
            {
                new QuiltProvided(x.Group, x.Id, x.Version)
            }.Concat(x.Provides)
                .Select(
                    p => (Owner: x, Identity: p)
                )
        )
            .ToArray();
        foreach (var group in identities.GroupBy(
            x => x.Identity.Id
        ))
        {
            // [实现] 对 identities.GroupBy( x => x.Identity.Id ) 中的每一项：准备 var vars = group.Select( x => variables[x.Owner] ) .Distinct() .ToArray()；在 i < vars.Length 条件下迭代；按 group.Any( x => x.Owner.LoadType == "always" && x.Owner.Environment is not ("server" or "dedica… 分支处理。
            var vars = group.Select(
                x => variables[x.Owner]
            )
                .Distinct()
                .ToArray();
            for (int i = 0; i < vars.Length; i++)
                for (int j = i + 1; j < vars.Length; j++)
                    sat.Add(-vars[i], -vars[j]);
            if (group.Any(
                x => x.Owner.LoadType == "always" && x.Owner.Environment is not ("server" or "dedicated_server")
            ))
                sat.Add(vars);
        }

        var truth = sat.Variable();
        sat.Add(truth);
        // [实现] 功能：把一个依赖条件转换为布尔变量。实现：按 id/group 和版本区间找候选，再组合 OR/AND、optional、unless 和环境例外，供 SAT 规则引用。
        int Dependency(JToken token, bool fabric, bool breaks, int depth)
        {
            if (depth > 32)
                throw new InvalidDataException("Quilt 条件依赖嵌套过深。");
            if (token is JArray array)
            {
                // [实现] 条件成立（token is JArray array）时：准备 var terms = array.Select( x => Dependency(x, fabric, breaks, depth + 1) ) .ToArray()；返回 breaks ? sat.All(terms) : sat.Any(terms)。
                var terms = array.Select(
                    x => Dependency(x, fabric, breaks, depth + 1)
                )
                    .ToArray();
                return breaks
                    ? sat.All(terms)
                    : sat.Any(terms);
            }

            var obj = token as JObject;
            var id = token.Type == JTokenType.String
                ? (string)token!
                : obj?["id"]?.Type == JTokenType.String
                    ? (string? )obj["id"]
                    : null;
            if (string.IsNullOrWhiteSpace(id) || id.Split(':')
                .Length > 2)
                throw new InvalidDataException("Quilt 依赖 ID 缺失或无效。");
            var parts = id.Split(':');
            var group = parts.Length == 2
                ? parts[0]
                : null;
            var name = parts[^1];
            var versions = obj?["versions"];
            if (!fabric)
                _ = QuiltVersionRange.Matches("0", versions);
            var available = identities.Where(
                x => x.Identity.Id == name
            )
                .ToArray();
            var matching = available.Where(
                x => (group is null || x.Identity.Group == group) && (fabric
                    ? versions is null || FabricVersionPredicate.Matches(x.Identity.Version, versions)
                    : QuiltVersionRange.Matches(x.Identity.Version, versions))
            )
                .ToArray();
            int match = sat.Any(
                matching.Select(
                    x => variables[x.Owner]
                )
            );
            if (!breaks && matching.Length > 0 && matching.All(
                x => x.Owner.Environment is "server" or "dedicated_server"
            ))
                match = truth;
            if (!breaks && obj?["optional"] is { } optional)
            {
                // [实现] 条件成立（!breaks && obj?["optional"] is { } optional）时：按 optional.Type != JTokenType.Boolean 分支处理；按 (bool)optional 分支处理。
                if (optional.Type != JTokenType.Boolean)
                    throw new InvalidDataException("Quilt optional 必须是布尔值。");
                if ((bool)optional)
                    match = sat.Any(
                        [
                            match,
                            -sat.Any(
                                available.Select(
                                    x => variables[x.Owner]
                                )
                            )
                        ]
                    );
            }

            if (obj?["unless"] is { } unless)
            {
                // [实现] 条件成立（obj?["unless"] is { } unless）时：准备 var condition = Dependency(unless, fabric, false, depth + 1)；执行 match = breaks ? sat.All( [ match, -condition ] ) : sat.Any( [ match, condition ] )。
                var condition = Dependency(unless, fabric, false, depth + 1);
                match = breaks
                    ? sat.All(
                        [
                            match,
                            -condition
                        ]
                    )
                    : sat.Any(
                        [
                            match,
                            condition
                        ]
                    );
            }

            return match;
        }

        foreach (var candidate in candidates)
        {
            // [实现] 对 candidates 中的每一项：准备 var v = variables[candidate]；遍历 candidate.Depends 处理 dependency；遍历 candidate.Breaks 处理 conflict。
            var v = variables[candidate];
            foreach (var dependency in candidate.Depends)
                sat.Add(
                    -v,
                    Dependency(dependency, candidate.Fabric && !candidate.QuiltDependencySemantics, false, 0)
                );
            foreach (var conflict in candidate.Breaks)
                sat.Add(
                    -v,
                    -Dependency(conflict, candidate.Fabric && !candidate.QuiltDependencySemantics, true, 0)
                );
        }

        var selected = sat.Solve(ct);
        if (selected is null)
            throw new InvalidDataException(
                "Quilt 依赖无法同时满足，操作已撤回。候选：" + string.Join(
                    "；",
                    candidates.Where(
                        x => x.Mandatory
                    )
                        .Take(20)
                        .Select(
                            x => x.Id + " " + x.Version + " depends=" + x.Depends.ToString(Newtonsoft.Json.Formatting.None)
                        )
                )
            );
        return new(
            candidates.Where(
                x => selected[variables[x]] && !x.Source.StartsWith("builtin:")
            )
                .ToArray(),
            candidates.Count,
            warnings.Distinct()
                .ToArray()
        );
    }

    // [实现] 功能：预检实例当前启用的模组。实现：读取 JAR 与经过校验的加载器内置模组，使用同一实例配置执行全局求解，返回覆盖诊断。
    public static QuiltResolution Validate(
        string directory,
        string game,
        string loaderVersion,
        int javaMajor,
        CancellationToken ct = default,
        string? libraryContextDirectory = null
    )
    {
        return Resolve(
            Read(
                InstalledFiles(directory, libraryContextDirectory)
            ),
            game,
            loaderVersion,
            javaMajor,
            ct,
            directory,
            libraryContextDirectory
        );
    }

    // [实现] 功能：确定本地依赖分析输入。实现：收集启用的 JAR 并校验版本清单中的 Quilt 加载器；批量暂存时使用调用方提供的原实例库位置，配置和模组仍从暂存目录读取。
    internal static IReadOnlyList<string> InstalledFiles(string directory, string? libraryContextDirectory = null)
    {
        var folder = ResourceInstaller.SafeTarget(directory, "mods");
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.jar")
                .Select(
                    x => ResourceInstaller.SafeTarget(
                        directory,
                        Path.GetRelativePath(directory, x)
                    )
                )
                .ToList()
            : [];
        var metadataPath = Path.Combine(directory, "version.json");
        if (File.Exists(metadataPath))
        {
            // [实现] 条件成立（File.Exists(metadataPath)）时：准备 var metadata = JsonFiles.Read(metadataPath)；准备 var lib = metadata["libraries"]?.FirstOrDefault( x => ((string? )x["name"])?.StartsWith("org.qu…；准备 var artifact = lib?["downloads"]?["artifact"]。
            var metadata = JsonFiles.Read(metadataPath);
            var lib = metadata["libraries"]?.FirstOrDefault(
                x => ((string? )x["name"])?.StartsWith("org.quiltmc:quilt-loader:") == true
            );
            var artifact = lib?["downloads"]?["artifact"];
            if (artifact is not null)
            {
                // [实现] 条件成立（artifact is not null）时：准备 var context = libraryContextDirectory ?? directory；准备 var receipt = JsonFiles.Read( Path.Combine(context, "profile.json") )；准备 var root = (string? )receipt["libraryRoot"] == "instance" ? context : Path.Combine( Directory.G…。
                var context = libraryContextDirectory ?? directory;
                var receipt = JsonFiles.Read(
                    Path.Combine(context, "profile.json")
                );
                var root = (string? )receipt["libraryRoot"] == "instance"
                    ? context
                    : Path.Combine(
                        Directory.GetParent(context)!.Parent!.FullName,
                        "minecraft"
                    );
                var path = ResourceInstaller.SafeTarget(root, "libraries/" + (string)artifact["path"]!);
                if (!DownloadService.IsValid(
                    new(
                        new Uri((string)artifact["url"]!),
                        path,
                        (long? )artifact["size"] ?? -1,
                        (string? )artifact["sha1"]
                    )
                ))
                    throw new InvalidDataException("Quilt 加载器内置依赖文件未通过校验，请修复实例。");
                files.Add(path);
            }
        }

        return files;
    }
}
