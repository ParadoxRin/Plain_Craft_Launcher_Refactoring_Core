using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 真实公开更新目录与隔离旧版模组；跨进程阶段只读取上轮已持久化的忽略记录。
    private async Task UpdateIgnoreAcceptanceAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--acceptance-report") + 1];
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }

            checks.Add(name);
        }

        async Task Wait(Func<bool> ready)
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (!ready())
            {
                if (DateTime.UtcNow >= until)
                {
                    throw new TimeoutException("update acceptance wait");
                }

                await Task.Delay(100);
            }
        }

        void Click(Window page, string label) => page.GetVisualDescendants()
            .OfType<Button>()
            .Single(
                x => Equals(x.Content, label)
            )
            .RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
        ListBox Updates(Window page) => page.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(
                x => x.Height == 120
            );
        string Status(Window page) => page.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(
                x => x.Name == "LocalDependencyStatus"
            )
            .Text ?? "";
        try
        {
            var api = new WorkspaceApi(service.Workspaces);
            var snapshot = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
            var profile = snapshot.Items.Single(
                x => x.Profile.Id == "update-acceptance"
            )
                .Profile;
            var page = new LocalResourcesWindow(service.Workspaces, profile);
            var shown = page.PresentAsync(this);
            await Wait(
                () => page.IsVisible && !page.Busy
            );
            Click(page, "检查更新");
            await Wait(
                () => !page.Busy && Status(page) == "更新检查完成"
            );
            if (Program.Arguments.Contains("--updates-restart"))
            {
                Check(
                    Updates(page)
                        .ItemCount == 1,
                    "RES-017-new-host-process-still-excludes-only-ignored-version"
                );
            }
            else
            {
                Check(
                    Updates(page)
                        .ItemCount == 2,
                    "RES-017-live-catalog-finds-two-old-mod-updates"
                );
                Updates(page)
                    .SelectedIndex = -1;
                Click(page, "忽略此版本");
                await Wait(
                    () => !page.Busy && Status(page) == "请选择要忽略的更新。"
                );
                Check(
                    Updates(page)
                        .ItemCount == 2,
                    "RES-017-no-selection-rejected-without-clearing-list"
                );
                var selected = (LocalResourceUpdate)Updates(page)
                    .Items[0]!;
                var other = (LocalResourceUpdate)Updates(page)
                    .Items[1]!;
                Updates(page)
                    .SelectedIndex = 0;
                Click(page, "忽略此版本");
                await Wait(
                    () => !page.Busy && Status(page) == "已忽略所选版本"
                );
                Check(
                    Updates(page)
                        .ItemCount == 1 && ((LocalResourceUpdate)Updates(page)
                        .Items[0]!).SelectionId == other.SelectionId,
                    "RES-017-only-selected-update-removed-from-list"
                );
                await page.RequestCloseAsync();
                await shown;
                page = new LocalResourcesWindow(service.Workspaces, profile);
                shown = page.PresentAsync(this);
                await Wait(
                    () => page.IsVisible && !page.Busy
                );
                Click(page, "检查更新");
                await Wait(
                    () => !page.Busy && Status(page) == "更新检查完成"
                );
                Check(
                    Updates(page)
                        .ItemCount == 1 && ((LocalResourceUpdate)Updates(page)
                        .Items[0]!).Current.Path == other.Current.Path,
                    "RES-017-reopened-page-requeries-and-keeps-other-mod-update"
                );
            }

            await page.RequestCloseAsync();
            await shown;
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = ex.ToString()
                    }
                )
            );
        }
        finally
        {
            await RequestApplicationExitAsync();
        }
    }
}
