using Avalonia.Controls;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    private readonly TextBlock releaseNotice = new()
    {
        IsVisible = false,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap
    };
    private bool checkingRelease;
    // Host owns network policy and comparison with installed versions; Shell only displays the result.
    private async Task CheckReleaseNotificationsAsync()
    {
        if (checkingRelease)
            return;
        checkingRelease = true;
        try
        {
            var result = await DesktopApi.CallAsync<ReleaseNotice>("workspace.shell.releaseNotice", null);
            releaseNotice.Text = result.Versions.Length == 0
                ? "当前已安装最新的所选类型版本。"
                : "可下载新版本：" + string.Join(" · ", result.Versions);
            releaseNotice.IsVisible = result.Enabled;
        }
        catch (Exception ex)
        {
            Log(
                Pcl.Client.WorkspaceErrors.Describe(ex)
            );
        }
        finally
        {
            checkingRelease = false;
        }
    }
}
