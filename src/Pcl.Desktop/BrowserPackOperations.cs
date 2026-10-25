using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 默认资源页与本地导入共用 Host 整合包流程，选择界面只消费 Contract 清单。
    private async Task<GameProfile> InstallBrowserPackAsync(string? releaseId, string? file, bool optional, CancellationToken ct)
    {
        var api = new WorkspaceApi(
            Bootstrap.WorkspacePages.Operations(this, service)
        );
        var preview = releaseId is null
            ? await api.CallAsync<View.PackInspection>(
                "workspace.packs.inspect",
                null,
                new
                {
                    path = file
                },
                ct
            )
            : await api.CallAsync<View.PackInspection>(
                "workspace.packs.download",
                null,
                new
                {
                    releaseId
                },
                ct
            );
        var selected = await PackSelectionWindow.SelectAsync(this, preview.Plan, optional, ct);
        var result = await api.CallAsync<View.GameProfile>(
            "workspace.packs.install",
            null,
            new
            {
                handle = preview.Handle,
                options = new View.PackImportOptions(SelectedOptionalFiles: selected)
            },
            ct
        );
        RefreshProfiles(result.Id);
        return result;
    }
}
