using Pcl.Domain;

namespace Pcl.Core;
/// <summary>Discovers actual files and configuration, then passes immutable candidates to the pure solver.</summary>
public static class FabricDependencySet
{
    public static FabricResolution Resolve(
        IEnumerable<string> files,
        string directory,
        IReadOnlyDictionary<string, string> builtins,
        CancellationToken ct = default
    )
    {
        var candidates = new Dictionary<string, FabricCandidate>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var mod in FabricModReader.Read(file, directory))
            {
                var candidate = new FabricCandidate(
                    mod.CandidateKey,
                    mod.Id,
                    mod.Version,
                    mod.Provides,
                    FabricMetadataParser.Kinds.SelectMany(mod.Dependencies)
                        .ToArray(),
                    mod.ParentKeys ?? [],
                    mod.Origin,
                    !mod.IsNested && mod.Environment != "server",
                    mod.Environment,
                    mod.Schema,
                    mod.NestingDepth
                );
                if (candidates.TryGetValue(candidate.Key, out var previous))
                {
                    candidates[candidate.Key] = previous with
                    {
                        Parents = previous.Parents.Concat(candidate.Parents)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray(),
                        Depth = Math.Min(previous.Depth, candidate.Depth)
                    };
                }
                else
                {
                    candidates.Add(candidate.Key, candidate);
                }

                if (candidates.Count > 2048)
                {
                    throw new NotSupportedException("Fabric 候选超过2048个；尚未判定兼容性。");
                }
            }
        }

        foreach (var builtin in builtins)
        {
            candidates.Add(
                "builtin:" + builtin.Key,
                new(
                    "builtin:" + builtin.Key,
                    builtin.Key,
                    builtin.Value,
                    [],
                    [],
                    [],
                    "内置组件",
                    true,
                    IsBuiltin: true
                )
            );
        }

        return FabricCandidateSolver.Resolve(
            candidates.Values.ToArray(),
            ct
        );
    }

    public static IReadOnlyList<string> Require(FabricResolution resolution)
    {
        if (resolution.Status == "satisfied")
        {
            return resolution.Warnings;
        }

        var detail = new FabricResolutionException(resolution);
        if (resolution.Status == "limit")
        {
            throw new NotSupportedException("Fabric 依赖搜索达到预算，尚未判定是否兼容。请缩小候选集合。", detail);
        }

        var lines = resolution.Conflicts.Take(12)
            .Select(
                x => x.Source + " [" + x.Origin + "] → " + x.Relation + " " + x.Target + " " + x.Range + (x.Candidates.Count == 0
                    ? ""
                    : "；候选：" + string.Join("、", x.Candidates))
            );
        throw new InvalidDataException(
            "Fabric 依赖冲突；已锁定的顶层模组不会被静默替换。\n" + string.Join("\n", lines),
            detail
        );
    }
}
