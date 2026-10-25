using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// Display projection only; no index file, keychain, refresh token or write API.
public sealed class AccountViewCache(LauncherSession session)
{
    internal LauncherAccount[] Accounts => session.DesktopState.Accounts.Accounts.Select(
        a => new LauncherAccount(
            a.Id,
            (AccountKind)a.Kind,
            a.Name,
            a.Uuid,
            a.ClientId,
            a.Remembered,
            a.Server,
            a.Identifier,
            a.ServerName
        )
    )
        .ToArray();
    internal string? SelectedId => session.DesktopState.Accounts.SelectedId;

    internal bool HasMicrosoftCredential(string id) => session.HasMicrosoftCredential(id);
}
