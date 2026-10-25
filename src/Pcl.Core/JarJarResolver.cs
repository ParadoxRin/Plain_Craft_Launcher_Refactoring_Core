using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
// [实现] 数据约定：JarJarSelection 汇集 Coordinate、Version、Source、Sha256、Ranges，供该模块传递状态。
public sealed record JarJarSelection(
    string Coordinate,
    string Version,
    string Source,
    string Sha256,
    IReadOnlyList<string> Ranges
);
// [实现] 数据约定：JarJarResolution 汇集 Mods、Selected、Warnings，供该模块传递状态。
public sealed record JarJarResolution(
    IReadOnlyList<ForgeModMetadata> Mods,
    IReadOnlyList<JarJarSelection> Selected,
    IReadOnlyList<string> Warnings
);
// 模组依赖分析：读取归档中的声明和版本范围，选择兼容候选，报告缺失前置或冲突。
public static class JarJarResolver
{
    // [实现] 数据约定：Node 汇集 Source、Mods、Identity、Hash、Depth、MetadataError，供该模块传递状态。
    private sealed record Node(
        string Source,
        IReadOnlyList<ForgeModMetadata> Mods,
        string Identity,
        string Hash,
        int Depth,
        Exception? MetadataError
    );
    // [实现] 数据约定：Request 汇集 Coordinate、Range、Version、Node、DeclaredBy，供该模块传递状态。
    private sealed record Request(string Coordinate, string Range, string Version, Node? Node, string DeclaredBy);
    // [实现] Resolve 的实现：准备 var roots = new List<Node>()；准备 var requests = new List<Request>()；准备 var scanned = new Dictionary<string, Node>()。
    public static JarJarResolution Resolve(IEnumerable<string> files, string loader, string gameVersion, int javaMajor = 21)
    {
        var roots = new List<Node>();
        var requests = new List<Request>();
        var scanned = new Dictionary<string, Node>();
        long total = 0;
        // [实现] Text 的实现：按 entry.Length > 2 * 1024 * 1024 分支处理；准备 var r = new StreamReader( entry.Open() )；返回 r.ReadToEnd()。
        string Text(ZipArchiveEntry entry)
        {
            if (entry.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("JarJar 元数据过大。");
            using var r = new StreamReader(
                entry.Open()
            );
            return r.ReadToEnd();
        }

        // [实现] Scan 的实现：按 depth > 8 || scanned.Count > 2048 分支处理；准备 var hash = Convert.ToHexString( SHA256.HashData(stream) ) .ToLowerInvariant()；执行 stream.Position = 0。
        Node Scan(Stream stream, string source, int depth)
        {
            if (depth > 8 || scanned.Count > 2048)
                throw new NotSupportedException("JarJar 嵌套超出检查上限。");
            var hash = Convert.ToHexString(
                SHA256.HashData(stream)
            )
                .ToLowerInvariant();
            stream.Position = 0;
            if (depth > 0 && scanned.TryGetValue(hash, out var seen))
                return seen;
            IReadOnlyList<ForgeModMetadata> mods = [];
            Exception? metadataError = null;
            try
            {
                // [实现] 在异常保护范围内：执行 mods = ForgeModReader.ReadSingle(stream, loader, gameVersion, depth > 0)。
                mods = ForgeModReader.ReadSingle(stream, loader, gameVersion, depth > 0);
            }
            catch (Exception ex)when (depth > 0 && ex is InvalidDataException or NotSupportedException)
            {
                // [实现] 捕获 (Exception ex) 后：执行 metadataError = ex。
                metadataError = ex;
            }

            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            // [实现] Entry 的实现：准备 var all = zip.Entries.Where( x => x.FullName == name ) .ToArray()；按 all.Length > 1 分支处理；返回 all.SingleOrDefault()。
            ZipArchiveEntry? Entry(string name)
            {
                var all = zip.Entries.Where(
                    x => x.FullName == name
                )
                    .ToArray();
                if (all.Length > 1)
                    throw new InvalidDataException("重复的 JarJar 归档路径。");
                return all.SingleOrDefault();
            }

            var module = JarModuleIdentity.Read(zip, source, javaMajor);
            var identity = mods.Count > 0
                ? string.Concat(
                    mods.Select(
                        x => x.Id
                    )
                )
                : (loader == "neoforge"
                    ? "library:"
                    : "") + module;
            var node = new Node(source, mods, identity, hash, depth, metadataError);
            scanned.TryAdd(hash, node);
            if (Entry("META-INF/jarjar/metadata.json")is { } entry)
            {
                // [实现] 条件成立（Entry("META-INF/jarjar/metadata.json")is { } entry）时：准备 var data = JObject.Parse( Text(entry) )；按 data["jars"] is not JArray jars 分支处理；遍历 jars 处理 jar。
                var data = JObject.Parse(
                    Text(entry)
                );
                if (data["jars"] is not JArray jars)
                    throw new InvalidDataException("JarJar jars 声明缺失。");
                foreach (var jar in jars)
                {
                    // [实现] 对 jars 中的每一项：准备 var group = (string? )jar["identifier"]?["group"]；准备 var artifact = (string? )jar["identifier"]?["artifact"]；准备 var range = (string? )jar["version"]?["range"]。
                    var group = (string? )jar["identifier"]?["group"];
                    var artifact = (string? )jar["identifier"]?["artifact"];
                    var range = (string? )jar["version"]?["range"];
                    var version = (string? )jar["version"]?["artifactVersion"];
                    var path = (string? )jar["path"];
                    if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(artifact) || string.IsNullOrWhiteSpace(range) || string.IsNullOrWhiteSpace(version) || group.Contains(':') || artifact.Contains(':'))
                        throw new InvalidDataException("JarJar 坐标或版本声明无效。");
                    // JarSelector rejects a recommendation-only range; it requires actual restrictions.
                    if (range[0] is not ('[' or '('))
                        throw new InvalidDataException("JarJar 需要 Maven 硬版本范围：" + range);
                    _ = MavenVersionRange.Matches(version, range);
                    Node? child = null;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        // [实现] 条件成立（!string.IsNullOrWhiteSpace(path)）时：执行 DependencyArchive.ValidatePath(path)；准备 var childEntry = Entry(path) ?? throw new InvalidDataException("JarJar 缺少文件：" + path)；按 childEntry.Length > 64 * 1024 * 1024 || (total += childEntry.Length) > 512L * 1024 * 1024 分支处理。
                        DependencyArchive.ValidatePath(path);
                        var childEntry = Entry(path) ?? throw new InvalidDataException("JarJar 缺少文件：" + path);
                        if (childEntry.Length > 64 * 1024 * 1024 || (total += childEntry.Length) > 512L * 1024 * 1024)
                            throw new NotSupportedException("JarJar 内容超过检查上限。");
                        using var memory = new MemoryStream();
                        using (var input = childEntry.Open())
                            input.CopyTo(memory);
                        memory.Position = 0;
                        child = Scan(memory, source + "!/" + path, depth + 1);
                    }

                    requests.Add(
                        new(group + ":" + artifact, range, version, child, source)
                    );
                    if (requests.Count > 4096)
                        throw new NotSupportedException("JarJar 依赖声明过多。");
                }
            }

            return node;
        }

        foreach (var file in files)
        {
            // [实现] 对 files 中的每一项：准备 var stream = File.OpenRead(file)；执行 roots.Add( Scan(stream, file, 0) )。
            using var stream = File.OpenRead(file);
            roots.Add(
                Scan(stream, file, 0)
            );
        }

        var selected = new List<JarJarSelection>();
        var modsResult = roots.SelectMany(
            x => x.Mods
        )
            .ToList();
        var claimed = roots.Select(
            x => x.Identity
        )
            .ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var group in requests.GroupBy(
            x => x.Coordinate
        )
            .OrderBy(
                x => x.Key,
                StringComparer.Ordinal
            ))
        {
            // [实现] 对 requests.GroupBy( x => x.Coordinate ) .OrderBy( x => x.Key, StringComparer.Ordinal ) 中的每一项：准备 var restrictions = group.Select( x => x.Range ) .Distinct() .ToArray()；准备 var valid = group.Where( x => x.Node is not null && restrictions.All( r => MavenVersionRange.Ma…；按 valid.Length == 0 分支处理。
            var restrictions = group.Select(
                x => x.Range
            )
                .Distinct()
                .ToArray();
            var valid = group.Where(
                x => x.Node is not null && restrictions.All(
                    r => MavenVersionRange.Matches(x.Version, r)
                )
            )
                .OrderByDescending(
                    x => x.Version,
                    Comparer<string>.Create(MavenVersionRange.Compare)
                )
                .ThenBy(
                    x => x.Node!.Depth
                )
                .ThenBy(
                    x => x.Node!.Source,
                    StringComparer.Ordinal
                )
                .ToArray();
            if (valid.Length == 0)
                throw new InvalidDataException(
                    "JarJar 无法选择兼容版本：" + group.Key + "；要求 " + string.Join(" ∩ ", restrictions) + "；提供 " + string.Join(
                        ", ",
                        group.Select(
                            x => x.Version
                        )
                    )
                );
            var winner = valid[0];
            var same = valid.Where(
                x => MavenVersionRange.Compare(x.Version, winner.Version) == 0
            )
                .ToArray();
            if (same.Select(
                x => x.Node!.Hash
            )
                .Distinct()
                .Count() > 1)
                throw new InvalidDataException("JarJar 相同坐标和版本包含不同内容，无法可靠选择：" + group.Key);
            var node = winner.Node!;
            if (!claimed.Add(node.Identity))
            {
                // [实现] 条件成立（!claimed.Add(node.Identity)）时：执行 warnings.Add("JarJar " + group.Key + " 由同身份顶层/已选文件覆盖。")；执行 continue;。
                warnings.Add("JarJar " + group.Key + " 由同身份顶层/已选文件覆盖。");
                continue;
            }

            if (node.MetadataError is not null)
                throw new InvalidDataException("选中的 JarJar 模组元数据无效：" + node.Source, node.MetadataError);
            selected.Add(
                new(group.Key, winner.Version, node.Source, node.Hash, restrictions)
            );
            modsResult.AddRange(node.Mods);
        }

        return new(modsResult, selected, warnings);
    }
}
