namespace Pcl.Core.Contracts.Diagnostics;
/// <summary>Bounded user-facing dependency evidence; contains archive labels, never host paths or authentication data.</summary>
public sealed record DependencyDiagnostic(string Code, string Summary, IReadOnlyList<string> Lines)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayText => Summary + (Lines.Count == 0
        ? ""
        : "\n" + string.Join("\n", Lines));
}
