using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 仅由显式测试参数启用，在隔离根驱动真实窗口事件和 Host，不访问用户账户。
    private async Task CloseoutSmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--closeout-report") + 1];
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }

            checks.Add(name);
            File.AppendAllText(report + ".progress", name + "\n");
        }

        async Task Wait(Func<bool> ready)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!ready())
            {
                if (DateTime.UtcNow >= until)
                {
                    throw new TimeoutException("closeout UI wait");
                }

                await Task.Delay(40);
            }
        }

        CheckBox Flag(InstanceWorkspace page, string text) => page.GetVisualDescendants()
            .OfType<CheckBox>()
            .Single(
                x => Equals(x.Content, text)
            );
        ComboBox Filter(InstanceWorkspace page) => page.GetVisualDescendants()
            .OfType<ComboBox>()
            .Single(
                x => x.Items.Cast<object>()
                    .Contains("仅收藏")
            );
        ListBox Rows(InstanceWorkspace page) => page.GetVisualDescendants()
            .OfType<ListBox>()
            .Single(
                x => x.Name == "InstanceList"
            );
        async Task Save(InstanceWorkspace page)
        {
            page.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => Equals(x.Content, "保存")
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            await Wait(
                () => !page.Busy && page.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(
                        x => x.Name == "InstanceStatus"
                    )
                    .Text == "操作完成"
            );
        }

        try
        {
            UiMotion.Configure(0);
            var page = Bootstrap.WorkspacePages.Instances(this, service);
            var shown = page.PresentAsync(this);
            await Wait(
                () => page.IsVisible && !page.Busy && Rows(page)
                    .ItemCount > 0
            );
            var selected = ((GameProfile)Rows(page)
                .SelectedItem!).Id;
            Flag(page, "收藏")
                .IsChecked = true;
            Flag(page, "在首页隐藏")
                .IsChecked = true;
            await Save(page);
            Filter(page)
                .SelectedIndex = 1;
            Check(
                Rows(page)
                    .Items.Cast<GameProfile>()
                    .Any(
                        x => x.Id == selected
                    ),
                "INST-003-favorite-filter-after-ui-save"
            );
            Filter(page)
                .SelectedIndex = 3;
            Check(
                !Rows(page)
                    .Items.Cast<GameProfile>()
                    .Any(
                        x => x.Id == selected
                    ),
                "INST-003-hidden-excluded-from-visible-filter"
            );
            await page.RequestCloseAsync();
            await shown;
            // 重建页面后从 Host 重读，避免仅验证同一控件的内存状态。
            page = Bootstrap.WorkspacePages.Instances(this, service);
            shown = page.PresentAsync(this);
            await Wait(
                () => page.IsVisible && !page.Busy && Rows(page)
                    .ItemCount > 0
            );
            Filter(page)
                .SelectedIndex = 2;
            Check(
                Rows(page)
                    .Items.Cast<GameProfile>()
                    .Any(
                        x => x.Id == selected
                    ) && Flag(page, "收藏")
                    .IsChecked == true && Flag(page, "在首页隐藏")
                    .IsChecked == true,
                "INST-003-reopened-page-restores-favorite-and-hidden"
            );
            Flag(page, "收藏")
                .IsChecked = false;
            Flag(page, "在首页隐藏")
                .IsChecked = false;
            await Save(page);
            Filter(page)
                .SelectedIndex = 3;
            Check(
                Rows(page)
                    .Items.Cast<GameProfile>()
                    .Any(
                        x => x.Id == selected
                    ),
                "INST-003-unhide-restores-visible-instance"
            );
            Filter(page)
                .SelectedIndex = 1;
            Check(
                !Rows(page)
                    .Items.Cast<GameProfile>()
                    .Any(
                        x => x.Id == selected
                    ),
                "INST-003-unfavorite-removes-from-favorites"
            );
            await page.RequestCloseAsync();
            await shown;
            // 设置导入导出走同一个公开 Contract，验证净化、备份和拒绝非法文件的真实文件效果。
            var api = new WorkspaceApi(service.Workspaces);
            var initial = await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
            var changed = initial.Appearance with
            {
                Title = "Closeout acceptance",
                Theme = "dark"
            };
            await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = changed
                    }
                }
            );
            var exported = Path.Combine(
                Path.GetDirectoryName(report)!,
                "settings-export.json"
            );
            await api.ExportAsync(
                "workspace.settings.export",
                null,
                new { },
                exported
            );
            var text = await File.ReadAllTextAsync(exported);
            Check(
                text.Contains(changed.Title) && !text.Contains("access_token") && !text.Contains("refresh_token"),
                "UI-007-real-host-export-no-token-fields"
            );
            await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = new AppearanceSettings()
                    }
                }
            );
            var restored = await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.import",
                null,
                new
                {
                    path = exported
                }
            );
            Check(
                restored.Appearance.Title == changed.Title && restored.Appearance.Theme == changed.Theme,
                "UI-007-reset-and-import-restores-settings"
            );
            var backups = await api.CallAsync<string>("workspace.settings.backups", null);
            Check(
                Directory.GetFiles(backups, "*.json", SearchOption.AllDirectories)
                    .Length > 0,
                "UI-007-import-creates-backup"
            );
            var invalid = Path.Combine(
                Path.GetDirectoryName(report)!,
                "invalid-settings.json"
            );
            await File.WriteAllTextAsync(invalid, "{\"schemaVersion\":999}");
            var rejected = false;
            try
            {
                await api.CallAsync<SettingsSnapshot>(
                    "workspace.settings.import",
                    null,
                    new
                    {
                        path = invalid
                    }
                );
            }
            catch
            {
                rejected = true;
            }

            var retained = await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
            Check(
                rejected && retained.Appearance.Title == changed.Title,
                "UI-007-invalid-import-preserves-current-settings"
            );
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
