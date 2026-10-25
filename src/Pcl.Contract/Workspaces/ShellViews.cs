using Pcl.Core.Contracts.Accounts;

namespace Pcl.Core.Contracts.Workspaces;
// Read-only presentation state. Host supplies paths and capabilities; no configuration store or execution is exposed.
public sealed record DesktopPaths(string Root, string Game, string Runtime, string Evidence);
public sealed record DesktopSnapshot(
    DesktopPaths Paths,
    GameProfile[] Profiles,
    AppearanceSettings Appearance,
    AccountSnapshot Accounts,
    string? SelectedProfile,
    string DefaultVersion,
    bool MicrosoftConfigured,
    GameProfile[] ResourceTargets
);
public sealed record HomeLink(string Text, string Url, string Kind = "url");
public sealed record HomeDocument(string Title, string Body, HomeLink[] Links)
{
    public string? Markup { get; init; }
}

public sealed record ProfileAvailability(bool Installed);
public sealed record DropClassification(string? Kind);
public sealed record ReleaseNotice(bool Enabled, string[] Versions);
