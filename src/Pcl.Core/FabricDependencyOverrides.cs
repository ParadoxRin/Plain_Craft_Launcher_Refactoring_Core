using Pcl.Domain;

namespace Pcl.Core;
/// <summary>File boundary for Fabric overrides; parsing and precedence belong to the pure Domain.</summary>
public sealed class FabricDependencyOverrides(FabricOverrideRules rules)
{
    public const string RelativePath = "config/fabric_loader_dependencies.json";
    public static FabricDependencyOverrides Read(string directory)
    {
        var path = ResourceInstaller.SafeTarget(directory, RelativePath);
        if (!File.Exists(path))
        {
            return new(
                FabricOverrideRules.Parse("{\"version\":1}")
            );
        }

        if (new FileInfo(path).Length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException("Fabric 依赖覆盖超过 2 MiB。");
        }

        return new(
            FabricOverrideRules.Parse(
                File.ReadAllText(path)
            )
        );
    }

    public IReadOnlyList<FabricModInfo> Apply(IEnumerable<FabricModInfo> mods) => mods.Select(
        mod => mod with
        {
            EffectiveDependencies = rules.Apply(
                mod.Id,
                FabricMetadataParser.Kinds.SelectMany(mod.Dependencies)
                    .ToArray()
            )
        }
    )
        .ToArray();
}
