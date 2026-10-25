using System.IO.Compression;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class FabricVersionPredicate
{
    // [实现] Matches 的实现：计算并返回 predicate is JArray array ? array.Any( x => Matches(version, x) ) : Matches( version, predicate…。
    public static bool Matches(string version, JToken predicate) => predicate is JArray array
        ? array.Any(
            x => Matches(version, x)
        )
        : Matches(
            version,
            predicate.ToString()
        );
    // [实现] Matches 的实现：按 predicate is "" or "*" 分支处理；准备 var tokens = Regex.Matches(predicate, @"\s*(>=|<=|>|<|=|\^|~)?\s*([^\s<>=~^]+)")；遍历 tokens 处理 token。
    public static bool Matches(string version, string predicate)
    {
        if (predicate is "" or "*")
            return true;
        var tokens = Regex.Matches(predicate, @"\s*(>=|<=|>|<|=|\^|~)?\s*([^\s<>=~^]+)");
        foreach (Match token in tokens)
        {
            // [实现] 对 tokens 中的每一项：准备 var op = token.Groups[1].Value；准备 var target = token.Groups[2].Value；按 target == "*" 分支处理。
            var op = token.Groups[1].Value;
            var target = token.Groups[2].Value;
            if (target == "*")
                continue;
            var wanted = target.Split('+')[0].Split('-')[0].Split('.');
            var actual = version.Split('+')[0].Split('-')[0].Split('.');
            var wildcard = Array.FindIndex(
                wanted,
                x => x is "x" or "X" or "*"
            );
            if (wildcard >= 0)
            {
                // [实现] 条件成立（wildcard >= 0）时：按 op is not ("" or "=") 分支处理；按 actual.Length < wildcard || !actual.Take(wildcard) .SequenceEqual( wanted.Take(wildcard) ) 分支处理；执行 continue;。
                if (op is not ("" or "="))
                    throw new InvalidDataException("不支持的版本通配表达式：" + predicate);
                if (actual.Length < wildcard || !actual.Take(wildcard)
                    .SequenceEqual(
                        wanted.Take(wildcard)
                    ))
                    return false;
                continue;
            }

            var compare = Compare(version, target);
            if (!(op switch
            {
                ">=" => compare >= 0,
                ">" => compare > 0,
                "<=" => compare <= 0,
                "<" => compare < 0,
                "~" or "^" => compare >= 0 && Compare(
                    version,
                    Upper(target, op)
                ) < 0,
                "" or "=" => compare == 0,
                _ => false
            }))
                return false;
        }

        return tokens.Count > 0;
    }

    // [实现] Upper 的实现：准备 var p = version.Split('+')[0].Split('-')[0].Split('.') .Select(int.Parse) .ToList()；在 p.Count < 3 条件下循环；准备 var index = op == "~" ? (version.Split('.') .Length > 1 ? 1 : 0) : p.FindIndex( x => x != 0 )。
    private static string Upper(string version, string op)
    {
        var p = version.Split('+')[0].Split('-')[0].Split('.')
            .Select(int.Parse)
            .ToList();
        while (p.Count < 3)
            p.Add(0);
        var index = op == "~"
            ? (version.Split('.')
                .Length > 1
                ? 1
                : 0)
            : p.FindIndex(
                x => x != 0
            );
        if (index < 0)
            index = 2;
        p[index]++;
        for (var i = index + 1; i < p.Count; i++)
            p[i] = 0;
        return string.Join('.', p);
    }

    // [实现] Compare 的实现：执行 string[] Parts(string v) => v.Split('+')[0].Split('-', 2);；准备 var left = Parts(a)；准备 var right = Parts(b)。
    public static int Compare(string a, string b)
    {
        // [实现] Parts 的实现：计算并返回 v.Split('+')[0].Split('-', 2)。
        string[] Parts(string v) => v.Split('+')[0].Split('-', 2);
        var left = Parts(a);
        var right = Parts(b);
        var l = left[0].Split('.');
        var r = right[0].Split('.');
        if (!l.All(
            x => int.TryParse(x, out _)
        ) || !r.All(
            x => int.TryParse(x, out _)
        ))
            return string.Compare(a, b, StringComparison.Ordinal);
        for (var i = 0; i < Math.Max(l.Length, r.Length); i++)
        {
            // [实现] 每次迭代（i < Math.Max(l.Length, r.Length)）：准备 var compare = (i < l.Length ? int.Parse(l[i]) : 0).CompareTo( i < r.Length ? int.Parse(r[i]) : …；按 compare != 0 分支处理。
            var compare = (i < l.Length
                ? int.Parse(l[i])
                : 0).CompareTo(
                i < r.Length
                    ? int.Parse(r[i])
                    : 0
            );
            if (compare != 0)
                return compare;
        }

        if (left.Length == 1 && right.Length == 1)
            return 0;
        if (left.Length == 1)
            return 1;
        if (right.Length == 1)
            return -1;
        var lp = left[1].Split('.');
        var rp = right[1].Split('.');
        for (var i = 0; i < Math.Min(lp.Length, rp.Length); i++)
        {
            // [实现] 每次迭代（i < Math.Min(lp.Length, rp.Length)）：准备 var ln = int.TryParse(lp[i], out var li)；准备 var rn = int.TryParse(rp[i], out var ri)；准备 var compare = ln && rn ? li.CompareTo(ri) : ln != rn ? (ln ? -1 : 1) : string.Compare(lp[i], rp…。
            var ln = int.TryParse(lp[i], out var li);
            var rn = int.TryParse(rp[i], out var ri);
            var compare = ln && rn
                ? li.CompareTo(ri)
                : ln != rn
                    ? (ln
                        ? -1
                        : 1)
                    : string.Compare(lp[i], rp[i], StringComparison.Ordinal);
            if (compare != 0)
                return compare;
        }

        return lp.Length.CompareTo(rp.Length);
    }
}

// [实现] 数据约定：FabricModInfo 汇集 Id、Version、Depends、Breaks、Provides、IsNested，供该模块传递状态。
public sealed record FabricModInfo(
    string Id,
    string Version,
    JObject Depends,
    JObject Breaks,
    IReadOnlyList<string> Provides,
    bool IsNested = false
);
// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class FabricModReader
{
    // [实现] Read 的实现：准备 var stream = File.OpenRead(file)；返回 ReadArchive(stream, 0)。
    public static IReadOnlyList<FabricModInfo> Read(string file)
    {
        using var stream = File.OpenRead(file);
        return ReadArchive(stream, 0);
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
        var json = JObject.Parse(
            reader.ReadToEnd()
        );
        var result = new List<FabricModInfo>
        {
            new(
                (string)json["id"]!,
                (string)json["version"]!,
                (JObject? )json["depends"] ?? new(),
                (JObject? )json["breaks"] ?? new(),
                json["provides"]?.Values<string>()
                    .OfType<string>()
                    .ToArray() ?? Array.Empty<string>()
            )
        };
        foreach (var nested in json["jars"] ?? new JArray())
        {
            // [实现] 对 json["jars"] ?? new JArray() 中的每一项：准备 var path = (string)nested["file"]!；准备 var jar = archive.GetEntry(path) ?? throw new InvalidDataException("缺少嵌套模组：" + path)；按 jar.Length > 64 * 1024 * 1024 分支处理。
            var path = (string)nested["file"]!;
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
    private readonly Dictionary<string, IReadOnlyList<FabricModInfo>> inspected = new();
    // [实现] Inspect 的实现：按 inspected.TryGetValue(release.Id, out var cached) 分支处理；准备 var path = ResourceInstaller.SafeTarget(cacheRoot, release.File.Sha1)；执行 await downloads.DownloadAsync( new(release.File.Url, path, release.File.Size, release.File.Sha1…。
    private async Task<IReadOnlyList<FabricModInfo>> Inspect(ResourceRelease release, CancellationToken ct)
    {
        if (inspected.TryGetValue(release.Id, out var cached))
            return cached;
        var path = ResourceInstaller.SafeTarget(cacheRoot, release.File.Sha1);
        await downloads.DownloadAsync(
            new(release.File.Url, path, release.File.Size, release.File.Sha1),
            ct
        );
        return inspected[release.Id] = FabricModReader.Read(path);
    }

    // [实现] ResolveAsync 的实现：准备 var preferred = new Dictionary<string, ResourceRelease>()；准备 var seen = new HashSet<string>()；在 round < 24 条件下迭代。
    public async Task<ResourceInstallPlan> ResolveAsync(
        ResourceRelease root,
        string gameVersion,
        IReadOnlyList<ManagedResource>? installed = null,
        string? gameDirectory = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var preferred = new Dictionary<string, ResourceRelease>();
        var seen = new HashSet<string>();
        for (var round = 0; round < 24; round++)
        {
            // [实现] 每次迭代（round < 24）：准备 var plan = await catalog.ResolveAsync(root, ResourceKind.Mod, gameVersion, ct: ct, preferred: p…；准备 var key = string.Join( ',', plan.Releases.OrderBy( x => x.ProjectId ) .Select( x => x.Id ) )；按 !seen.Add(key) 分支处理。
            var plan = await catalog.ResolveAsync(root, ResourceKind.Mod, gameVersion, ct: ct, preferred: preferred);
            var key = string.Join(
                ',',
                plan.Releases.OrderBy(
                    x => x.ProjectId
                )
                    .Select(
                        x => x.Id
                    )
            );
            if (!seen.Add(key))
                throw new InvalidDataException("前置约束无法同时满足，请选择其他模组版本。");
            var infos = new List<(string Project, FabricModInfo Mod)>();
            foreach (var release in plan.Releases)
            {
                // [实现] 对 plan.Releases 中的每一项：执行 progress?.Report( new("检查前置兼容性 · " + release.Name, 0, 0, 0, 0) )；执行 infos.AddRange( (await Inspect(release, ct)).Select( x => (release.ProjectId, x) ) )。
                progress?.Report(
                    new("检查前置兼容性 · " + release.Name, 0, 0, 0, 0)
                );
                infos.AddRange(
                    (await Inspect(release, ct)).Select(
                        x => (release.ProjectId, x)
                    )
                );
            }

            foreach (var old in installed ?? Array.Empty<ManagedResource>())
            {
                // [实现] 对 installed ?? Array.Empty<ManagedResource>() 中的每一项：按 !old.Enabled || old.Kind != ResourceKind.Mod || plan.Releases.Any( x => x.ProjectId == old.Proj… 分支处理；准备 var path = ResourceInstaller.SafeTarget(gameDirectory, "mods/" + old.FileName)；按 File.Exists(path) 分支处理。
                if (!old.Enabled || old.Kind != ResourceKind.Mod || plan.Releases.Any(
                    x => x.ProjectId == old.ProjectId
                ) || gameDirectory is null)
                    continue;
                var path = ResourceInstaller.SafeTarget(gameDirectory, "mods/" + old.FileName);
                if (File.Exists(path))
                    infos.AddRange(
                        FabricModReader.Read(path)
                            .Select(
                                x => (old.ProjectId, x)
                            )
                    );
            }

            var javaMajor = gameDirectory is not null && File.Exists(
                Path.Combine(gameDirectory, "version.json")
            )
                ? VersionCatalog.JavaMajor(
                    JsonFiles.Read(
                        Path.Combine(gameDirectory, "version.json")
                    )
                )
                : 21;
            var versions = new Dictionary<string, string>
            {
                ["minecraft"] = gameVersion,
                ["fabricloader"] = loaderVersion,
                ["java"] = javaMajor.ToString()
            };
            var owners = new Dictionary<string, string>();
            var topLevel = new HashSet<string>();
            foreach (var info in infos)
                foreach (var id in new[]
                {
                    info.Mod.Id
                }.Concat(info.Mod.Provides))
                {
                    // [实现] 对 new[] { info.Mod.Id }.Concat(info.Mod.Provides) 中的每一项：按 !info.Mod.IsNested && id == info.Mod.Id && !topLevel.Add(id) 分支处理；按 versions.TryGetValue(id, out var previous) && FabricVersionPredicate.Compare(previous, info.Mod… 分支处理；执行 versions[id] = info.Mod.Version。
                    if (!info.Mod.IsNested && id == info.Mod.Id && !topLevel.Add(id))
                        throw new InvalidDataException("重复模组 ID：" + id);
                    if (versions.TryGetValue(id, out var previous) && FabricVersionPredicate.Compare(previous, info.Mod.Version) >= 0)
                        continue;
                    versions[id] = info.Mod.Version;
                    owners[id] = info.Project;
                }

            var predicates = new Dictionary<string, List<(string From, JToken Predicate, bool Breaks)>>();
            foreach (var info in infos.Where(
                x => versions[x.Mod.Id] == x.Mod.Version
            ))
            {
                // [实现] 对 infos.Where( x => versions[x.Mod.Id] == x.Mod.Version ) 中的每一项：遍历 info.Mod.Depends.Properties() 处理 dependency；遍历 info.Mod.Breaks.Properties() 处理 dependency。
                foreach (var dependency in info.Mod.Depends.Properties())
                    Add(dependency.Name, info.Mod.Id, dependency.Value, false);
                foreach (var dependency in info.Mod.Breaks.Properties())
                    Add(dependency.Name, info.Mod.Id, dependency.Value, true);
            }

            // [实现] Add 的实现：按 !predicates.TryGetValue(id, out var list) 分支处理；执行 list.Add((from, predicate, breaks))。
            void Add(string id, string from, JToken predicate, bool breaks)
            {
                if (!predicates.TryGetValue(id, out var list))
                    predicates[id] = list = new();
                list.Add((from, predicate, breaks));
            }

            // [实现] Fails 的实现：准备 var exists = versions.TryGetValue(id, out var version)；返回 p.Breaks ? exists && FabricVersionPredicate.Matches(version!, p.Predicate) : !exists || !Fabric…。
            bool Fails(string id, (string From, JToken Predicate, bool Breaks) p)
            {
                var exists = versions.TryGetValue(id, out var version);
                return p.Breaks
                    ? exists && FabricVersionPredicate.Matches(version!, p.Predicate)
                    : !exists || !FabricVersionPredicate.Matches(version!, p.Predicate);
            }

            var mismatch = predicates.FirstOrDefault(
                pair => pair.Value.Any(
                    p => !p.Breaks && Fails(pair.Key, p)
                )
            );
            if (mismatch.Key is null)
                mismatch = predicates.FirstOrDefault(
                    pair => pair.Value.Any(
                        p => Fails(pair.Key, p)
                    )
                );
            if (mismatch.Key is null)
                return plan;
            if (!owners.TryGetValue(mismatch.Key, out var project) || project == root.ProjectId || !plan.Releases.Any(
                x => x.ProjectId == project
            ))
                throw new InvalidDataException(
                    "模组要求不满足：" + mismatch.Key + " · " + string.Join(
                        " / ",
                        mismatch.Value.Select(
                            x => x.From + " " + x.Predicate.ToString(Newtonsoft.Json.Formatting.None)
                        )
                    )
                );
            if (plan.Releases.Any(
                x => x.Dependencies.Any(
                    d => d.Type == "required" && d.ProjectId == project && d.VersionId is not null
                )
            ))
                throw new InvalidDataException("指定的前置版本与包内声明冲突：" + mismatch.Key);
            var candidates = await catalog.VersionsAsync(project, ResourceKind.Mod, gameVersion, ct: ct);
            ResourceRelease? selected = null;
            foreach (var candidate in candidates.OrderBy(
                x => x.Channel == "release"
                    ? 0
                    : 1
            ))
            {
                // [实现] 对 candidates.OrderBy( x => x.Channel == "release" ? 0 : 1 ) 中的每一项：准备 var candidateInfo = await Inspect(candidate, ct)；准备 var matching = candidateInfo.FirstOrDefault( x => x.Id == mismatch.Key || x.Provides.Contains(m…；按 matching is null 分支处理。
                var candidateInfo = await Inspect(candidate, ct);
                var matching = candidateInfo.FirstOrDefault(
                    x => x.Id == mismatch.Key || x.Provides.Contains(mismatch.Key)
                );
                if (matching is null)
                    continue;
                if (mismatch.Value.All(
                    p => p.Breaks
                        ? !FabricVersionPredicate.Matches(matching.Version, p.Predicate)
                        : FabricVersionPredicate.Matches(matching.Version, p.Predicate)
                ))
                {
                    // [实现] 条件成立（mismatch.Value.All( p => p.Breaks ? !FabricVersionPredicate.Matches(matching.Version, p.Predica…）时：执行 selected = candidate；执行 break;。
                    selected = candidate;
                    break;
                }
            }

            if (selected is null)
                throw new InvalidDataException("找不到满足约束的前置版本：" + mismatch.Key);
            preferred[project] = selected;
        }

        throw new InvalidDataException("前置约束超过解析次数限制。");
    }
}
