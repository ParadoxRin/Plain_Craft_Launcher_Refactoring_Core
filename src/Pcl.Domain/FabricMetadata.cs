using System.Text.Json;

namespace Pcl.Domain;
/// <summary>Each declaration is retained independently: duplicate ADD constraints are conjunctive.</summary>
public sealed record FabricDependency(string Kind, string Id, IReadOnlyList<string> Matchers);
public sealed record FabricMetadata(
    string Id,
    string Version,
    int Schema,
    string Environment,
    IReadOnlyList<string> Provides,
    IReadOnlyList<FabricDependency> Dependencies,
    IReadOnlyList<string> Jars
);
/// <summary>Pure translation of Fabric schema 0/1 dependency metadata; never loads mod code.</summary>
public static class FabricMetadataParser
{
    public static readonly string[] Kinds = [
        "depends",
        "recommends",
        "suggests",
        "conflicts",
        "breaks"
    ];
    public static FabricMetadata Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var schema = root.TryGetProperty("schemaVersion", out var value)
            ? value.GetInt32()
            : 0;
        if (schema is not (0 or 1))
        {
            throw new NotSupportedException("Unsupported Fabric metadata schema: " + schema);
        }

        var id = RequiredString(root, "id");
        var version = RequiredString(root, "version");
        var rules = new List<FabricDependency>();
        foreach (var property in root.EnumerateObject())
        {
            var kind = schema == 0
                ? property.Name switch
                {
                    "requires" => "depends",
                    "conflicts" => "breaks",
                    "recommends" => "suggests",
                    _ => null
                }
                : Kinds.Contains(property.Name)
                    ? property.Name
                    : null;
            if (kind is not null)
            {
                rules.AddRange(
                    ReadDependencies(property.Value, kind)
                );
            }
        }

        var environment = "*";
        if (root.TryGetProperty(
            schema == 0
                ? "side"
                : "environment",
            out value
        ))
        {
            environment = value.GetString() ?? throw new InvalidDataException("Fabric environment must be a string.");
            if (schema == 0 && environment is not ("client" or "server"))
            {
                // Schema 0 treats unknown sides as universal (Loader emits a warning).
                environment = "*";
            }

            if (schema == 1 && environment is not ("client" or "server" or "*"))
            {
                throw new InvalidDataException("Unknown Fabric environment.");
            }
        }

        var provides = schema == 1 && root.TryGetProperty("provides", out value)
            ? value.EnumerateArray()
                .Select(
                    x => x.GetString() ?? throw new InvalidDataException("Invalid provides ID.")
                )
                .ToArray()
            : [];
        var jars = schema == 1 && root.TryGetProperty("jars", out value)
            ? value.EnumerateArray()
                .Select(
                    x => RequiredString(x, "file")
                )
                .ToArray()
            : [];
        return new(id, version, schema, environment, provides, rules, jars);
    }

    internal static IReadOnlyList<FabricDependency> ReadDependencies(JsonElement container, string kind)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Fabric dependency container must be an object.");
        }

        var result = new List<FabricDependency>();
        foreach (var property in container.EnumerateObject())
        {
            var matchers = property.Value.ValueKind switch
            {
                JsonValueKind.String => new[]
                {
                    property.Value.GetString()!
                },
                JsonValueKind.Array => property.Value.EnumerateArray()
                    .Select(
                        x => x.ValueKind == JsonValueKind.String
                            ? x.GetString()!
                            : throw new InvalidDataException("Dependency ranges must be strings.")
                    )
                    .ToArray(),
                _ => throw new InvalidDataException("Dependency range must be a string or array.")
            };
            foreach (var matcher in matchers)
            {
                FabricVersions.Validate(matcher);
            }

            result.Add(
                new(kind, property.Name, matchers)
            );
        }

        return result;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(
            value.GetString()
        )
            ? value.GetString()!
            : throw new InvalidDataException("Missing Fabric " + name);
    }
}

/// <summary>Version 1 override precedence: replacement suppresses remove/add; otherwise remove then append.</summary>
public sealed class FabricOverrideRules
{
    private readonly Dictionary<string, Dictionary<string, IReadOnlyList<FabricDependency>>> entries = new(StringComparer.Ordinal);
    public static FabricOverrideRules Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject()
            .Any() || root.EnumerateObject()
            .First()
            .Name != "version" || !root.GetProperty("version")
            .TryGetInt32(out var format) || format != 1)
        {
            throw new InvalidDataException("Fabric dependency overrides must start with version: 1.");
        }

        var result = new FabricOverrideRules();
        foreach (var property in root.EnumerateObject()
            .Skip(1))
        {
            if (property.Name != "overrides" || property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Unknown Fabric override root field.");
            }

            foreach (var mod in property.Value.EnumerateObject())
            {
                var changes = new Dictionary<string, IReadOnlyList<FabricDependency>>(StringComparer.Ordinal);
                foreach (var change in mod.Value.EnumerateObject())
                {
                    var kind = change.Name.TrimStart('+', '-');
                    if (!FabricMetadataParser.Kinds.Contains(kind) || change.Name != kind && change.Name != "+" + kind && change.Name != "-" + kind)
                    {
                        throw new InvalidDataException("Unknown Fabric dependency override: " + change.Name);
                    }

                    var rules = FabricMetadataParser.ReadDependencies(change.Value, kind);
                    if (rules.Count != 0 || change.Name == kind)
                    {
                        changes[change.Name] = rules;
                    }
                }

                result.entries[mod.Name] = changes;
            }
        }

        return result;
    }

    public IReadOnlyList<FabricDependency> Apply(string id, IReadOnlyList<FabricDependency> original)
    {
        var result = original.ToList();
        if (!entries.TryGetValue(id, out var changes))
        {
            return result;
        }

        foreach (var kind in FabricMetadataParser.Kinds)
        {
            if (changes.TryGetValue(kind, out var replacement))
            {
                result.RemoveAll(
                    x => x.Kind == kind
                );
                result.AddRange(replacement);
                continue;
            }

            if (changes.TryGetValue("-" + kind, out var removals))
            {
                // Removal ignores its version matchers, as Fabric Loader does.
                result.RemoveAll(
                    x => x.Kind == kind && removals.Any(
                        r => r.Id == x.Id
                    )
                );
            }

            if (changes.TryGetValue("+" + kind, out var additions))
            {
                result.AddRange(additions);
            }
        }

        return result;
    }
}
