using Pcl.Core.Contracts.Accounts;

namespace Pcl.Core.Contracts.Workspaces;
// Public account metadata only. No session, password, refresh token, or OAuth secret crosses this boundary.
public enum AccountKind
{
    Offline,
    Microsoft,
    External
}

public sealed record LauncherAccount(
    string Id,
    AccountKind Kind,
    string Name,
    string Uuid,
    string? ClientId = null,
    bool Remembered = false,
    string? Server = null,
    string? Identifier = null,
    string? ServerName = null
)
{
    public override string ToString() => Name + (Kind == AccountKind.External
        ? " · " + (ServerName ?? Server)
        : "");
}

public sealed record AccountPageSnapshot(LauncherAccount[] Accounts, string? SelectedId);
public sealed record SkinState(string PlayerName, string Model, byte[]? Png);
public sealed record SkinPreview(string Handle, string PlayerName, string Model, byte[] Png);
public sealed record SkinAppearance(AccountAppearance Profile, byte[]? Png);
public sealed record ExternalServer(string ApiRoot, string Name);
public sealed record ExternalProfile(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ExternalChallenge(string Handle, ExternalProfile[] Profiles, ExternalProfile? Selected);
public sealed record ExternalSkin(string ServerName, string ApiRoot, string[] Uploadable, byte[]? Png);
