namespace Pcl.Core.Contracts.Workspaces;
// Display-only catalog contracts. Downloads and dependency plans remain in Host.
public sealed record ResourceProject(
    string Id,
    string Slug,
    string Title,
    string Description,
    string Author,
    long Downloads,
    ResourceKind Kind,
    string WebUrl,
    string WikiUrl,
    string? IconHandle
)
{
    public override string ToString() => Title;
}

public sealed record ResourceSearch(ResourceProject[] Projects, int Offset, int Total, string TranslatedQuery);
public sealed record ResourceFile(string Name, long Size);
public sealed record ResourceRelease(
    string Id,
    string ProjectId,
    string Name,
    string Number,
    string Channel,
    DateTimeOffset Published,
    string[] GameVersions,
    string[] Loaders,
    ResourceFile File
)
{
    public override string ToString() => $"{Name} · {Channel} · {Published:yyyy-MM-dd}";
}

public sealed record ManagedResource(
    string ProjectId,
    string VersionId,
    string Name,
    string Version,
    ResourceKind Kind,
    string FileName,
    long Size,
    bool Enabled,
    string Display
);
