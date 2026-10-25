using Avalonia.Controls;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop.Bootstrap;
// Window ownership lookup only; there is no local Core adapter or fallback.
internal static class PageComposition
{
    internal static IArtifactPageService Artifacts(LauncherSession source, LauncherSession? remote) => source.Artifacts();
    internal static IComponentPageService Components(LauncherSession source, GameProfile profile, LauncherSession? remote) => source.Components(profile.Id);
    internal static LauncherSession? Session(Window window)
    {
        for (Window? current = window; current is not null; current = current.Owner as Window)
            if (current is MainWindow main)
                return main.RemoteSession;
        return null;
    }

    internal static ComponentSource Describe(GameProfile p) => new(p.Id, p.Name, p.GameVersion, p.Loader);
}
