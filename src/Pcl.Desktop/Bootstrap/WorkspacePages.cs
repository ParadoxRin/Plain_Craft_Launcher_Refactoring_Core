using Avalonia.Controls;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop.Bootstrap;
internal static class WorkspacePages
{
    internal static GameProfile Describe(GameProfile p) => p;
    internal static IWorkspaceOperations Operations(Window owner, LauncherSession session) => session.Workspaces;
    internal static IWorkspaceOperations AccountOperations(Window owner, AccountViewCache accounts) => PageComposition.Session(owner)!.Workspaces;
    internal static Pcl.Core.Contracts.Accounts.IAccountLogin Login(Window owner, AccountViewCache accounts) => PageComposition.Session(owner)!;
    internal static SkinManagerWindow Skins(Window owner, AccountViewCache accounts) => new(
        AccountOperations(owner, accounts)
    );
    internal static AccountToolsWindow Accounts(Window owner, AccountViewCache accounts, Action<string>? changed = null) => new(
        AccountOperations(owner, accounts),
        () => Skins(owner, accounts),
        changed
    );
    internal static MultiplayerWindow Multiplayer(Window owner, LauncherSession session, string? invitation = null) => new(session.Workspaces, invitation);
    internal static OptiFabricWindow OptiFabric(Window owner, LauncherSession session, Action<GameProfile>? changed = null) => new(session.Workspaces, changed);
    internal static CacheNetworkWindow Cache(Window owner, DesktopPaths paths) => new(
        PageComposition.Session(owner)!.Workspaces
    );
    internal static SettingsHelpWindow Settings(
        Window owner,
        LauncherSession session,
        Action<AppearanceSettings> apply,
        Action<string> open
    ) => new(session.Workspaces, session.DesktopState.Paths.Root, apply, open);
    private static GameProfile Profile(LauncherSession session, string id) => session.DesktopState.Profiles.Single(
        p => p.Id == id
    );
    internal static UnifiedSearchWindow Search(
        Window owner,
        LauncherSession session,
        GameProfile profile,
        Action<GameProfile> installed,
        Action<string> open,
        bool mcmodOnly = false
    ) => new(
        session.Workspaces,
        profile,
        id => installed(
            Profile(session, id)
        ),
        open,
        mcmodOnly
    );
    internal static CurseForgeWindow Curse(
        Window owner,
        LauncherSession session,
        GameProfile profile,
        Action<GameProfile> installed,
        Action<string> open
    ) => new(
        session.Workspaces,
        profile,
        id => installed(
            Profile(session, id)
        ),
        open
    );
    internal static PackManagerWindow Pack(Window owner, LauncherSession session, GameProfile profile, Action<GameProfile> installed) => new(
        session.Workspaces,
        profile,
        id => installed(
            Profile(session, id)
        )
    );
    internal static LocalResourcesWindow Local(Window owner, LauncherSession session, GameProfile profile) => new(session.Workspaces, profile);
    internal static InstanceWorkspace Instances(
        Window owner,
        LauncherSession session,
        string? selectedId = null,
        Action<string?>? changed = null
    ) => new(session.Workspaces, session.Components, selectedId, changed);
    internal static Task OpenInstancesAsync(
        Window owner,
        LauncherSession session,
        string? selectedId = null,
        Action<string?>? changed = null
    ) => Instances(owner, session, selectedId, changed)
        .PresentAsync(owner);
    internal static async Task<string[]> SelectPackAsync(Window owner, LauncherSession session, string path, bool includeAll, CancellationToken ct)
    {
        var preview = await new WorkspaceApi(session.Workspaces).CallAsync<PackInspection>(
            "workspace.packs.inspect",
            null,
            new
            {
                path
            },
            ct
        );
        return await PackSelectionWindow.SelectAsync(owner, preview.Plan, includeAll, ct);
    }
}
