using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 人工只操作原生文件选择器；其后的 Host 状态、图片和重开结果自动核对。
    private async Task FilePickerAcceptanceAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--acceptance-report") + 1];
        var files = Program.Arguments[Array.IndexOf(Program.Arguments, "--acceptance-files") + 1];
        var checks = new List<string>();
        var api = new WorkspaceApi(service.Workspaces);
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }

            checks.Add(name);
            File.AppendAllText(report + ".progress", name + "\n");
        }

        async Task Wait(Func<Task<bool>> ready, int seconds = 30)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!await ready())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("file picker acceptance wait");
                }

                await Task.Delay(150);
            }
        }

        void Click(Window page, string text) => page.GetVisualDescendants()
            .OfType<Button>()
            .Single(
                x => Equals(x.Content, text)
            )
            .RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
        void Step(string name) => File.WriteAllText(report + ".step", name);
        try
        {
            UiMotion.Configure(0);
            var page = Bootstrap.WorkspacePages.Instances(this, service);
            var showing = page.PresentAsync(this);
            await Wait(
                () => Task.FromResult(
                    page.IsVisible && !page.Busy && page.GetVisualDescendants()
                        .OfType<CheckBox>()
                        .Any()
                )
            );
            var selected = (GameProfile)page.GetVisualDescendants()
                .OfType<ListBox>()
                .Single(
                    x => x.Name == "InstanceList"
                )
                .SelectedItem!;
            Check(
                await api.ReadIconAsync(selected.Id)is null,
                "INST-004-isolated-instance-has-no-icon"
            );
            Step("choose-icon");
            Click(page, "更换图标");
            await Wait(
                async () => !page.Busy && await api.ReadIconAsync(selected.Id)is not null,
                600
            );
            var before = await api.ReadIconAsync(selected.Id);
            Check(
                before is { Length: > 0 } && page.GetVisualDescendants()
                    .OfType<Image>()
                    .Any(
                        x => x.Source is not null
                    ),
                "INST-004-native-picker-saves-and-renders-icon"
            );
            await page.RequestCloseAsync();
            await showing;
            page = Bootstrap.WorkspacePages.Instances(this, service);
            showing = page.PresentAsync(this);
            await Wait(
                () => Task.FromResult(
                    page.IsVisible && !page.Busy && page.GetVisualDescendants()
                        .OfType<Image>()
                        .Any(
                            x => x.Source is not null
                        )
                )
            );
            Check(
                (await api.ReadIconAsync(selected.Id))!.SequenceEqual(before!),
                "INST-004-reopened-icon-bytes-preserved"
            );
            Click(page, "恢复默认图标");
            await Wait(
                async () => !page.Busy && await api.ReadIconAsync(selected.Id)is null
            );
            Check(
                !page.GetVisualDescendants()
                    .OfType<Image>()
                    .Any(
                        x => x.Source is not null
                    ),
                "INST-004-restore-default-removes-custom-image"
            );
            await page.RequestCloseAsync();
            await showing;
            // 导出前写入可区分的隔离设置；导入前改掉标题，避免假阳性。
            var snapshot = await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
            var target = snapshot.Appearance with
            {
                Title = "Native picker acceptance",
                Theme = "dark"
            };
            await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = target
                    }
                }
            );
            var settings = Bootstrap.WorkspacePages.Settings(
                this,
                service,
                _ =>
                {
                },
                _ =>
                {
                }
            );
            var settingsShown = settings.PresentAsync(this);
            await Wait(
                () => Task.FromResult(
                    settings.IsVisible && !settings.Busy && settings.GetVisualDescendants()
                        .OfType<TabControl>()
                        .Any()
                )
            );
            settings.GetVisualDescendants()
                .OfType<TabControl>()
                .Single()
                .SelectedIndex = 1;
            await Task.Delay(150);
            Step("save-settings");
            Click(settings, "导出设置");
            var exported = Path.Combine(files, "PCL-Mac-settings.json");
            await Wait(
                () => Task.FromResult(
                    !settings.Busy && File.Exists(exported)
                ),
                600
            );
            Check(
                File.ReadAllText(exported)
                    .Contains(target.Title),
                "UI-007-native-save-picker-exports-settings"
            );
            await api.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = target with
                        {
                            Title = "Changed before import"
                        }
                    }
                }
            );
            Step("open-settings");
            Click(settings, "导入设置");
            await Wait(
                async () => !settings.Busy && (await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null)).Appearance.Title == target.Title,
                600
            );
            Check(true, "UI-007-native-open-picker-imports-settings");
            await settings.RequestCloseAsync();
            await settingsShown;
            settings = Bootstrap.WorkspacePages.Settings(
                this,
                service,
                _ =>
                {
                },
                _ =>
                {
                }
            );
            settingsShown = settings.PresentAsync(this);
            await Wait(
                () => Task.FromResult(
                    settings.IsVisible && !settings.Busy && settings.GetVisualDescendants()
                        .OfType<TextBox>()
                        .Any(
                            x => x.Text == target.Title
                        )
                )
            );
            Check(true, "UI-007-reopened-settings-renders-imported-title");
            await settings.RequestCloseAsync();
            await settingsShown;
            Step("complete");
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
            Step("failed");
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
