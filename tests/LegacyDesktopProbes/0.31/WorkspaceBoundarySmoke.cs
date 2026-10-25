using System.Text.Json;
using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pcl.Client;
using Pcl.Core;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 产品按钮实测：只允许隔离的离线根目录，所有写入仍穿过真实 Host 页面契约。
    private async Task WorkspaceBoundarySmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--workspace-report") + 1];
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }

            checks.Add(name);
        }

        Button Button(Window window, string title) => window.GetVisualDescendants()
            .OfType<Button>()
            .Single(
                x => x.Content?.ToString() == title
            );
        async Task Wait(Func<bool> pending, int seconds = 180)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (pending())
            {
                if (DateTime.UtcNow > end)
                {
                    throw new TimeoutException("workspace UI task timeout");
                }

                await Task.Delay(100);
            }
        }

        async Task Click(Window window, string title, Func<bool> pending)
        {
            Button(window, title)
                .RaiseEvent(
                    new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)
                );
            await Task.Delay(100);
            await Wait(pending);
        }

        try
        {
            if (remote is null || !CanWriteLegacy || accounts!.Accounts.Any(
                x => x.Kind != AccountKind.Offline
            ))
            {
                throw new Exception("ISOLATED_HOST_ROOT_REQUIRED");
            }

            var api = new WorkspaceApi(remote.Workspaces);
            var fixture = await api.CallAsync<View.GameProfile>(
                "workspace.instances.copy",
                "1.21.1",
                new
                {
                    name = "0.28 UI workspace"
                }
            );
            RefreshProfiles(fixture.Id);
            InstancesClick(
                null,
                new RoutedEventArgs()
            );
            await Task.Delay(100);
            var instances = OwnedWindows.OfType<InstanceWorkspace>()
                .Single();
            await Wait(
                () => instances.Busy
            );
            var group = instances.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.Name == "InstanceGroupName"
                );
            group.Text = "UI Host group";
            await Click(
                instances,
                "保存",
                () => instances.Busy
            );
            var snapshot = await api.CallAsync<View.InstanceSnapshot>("workspace.instances.list", null);
            Check(
                snapshot.Items.Single(
                    x => x.Profile.Id == fixture.Id
                )
                    .Preferences.Group == "UI Host group",
                "instance-page-save-button-persists-through-host"
            );
            await Click(
                instances,
                "启动设置",
                () => instances.Busy
            );
            instances.GetVisualDescendants()
                .OfType<CheckBox>()
                .Single(
                    x => x.Name == "Override_Width"
                )
                .IsChecked = true;
            instances.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.Name == "Launch_Width"
                )
                .Text = "1240";
            await Click(
                instances,
                "保存启动设置",
                () => instances.Busy
            );
            snapshot = await api.CallAsync<View.InstanceSnapshot>("workspace.instances.list", null);
            Check(
                snapshot.Items.Single(
                    x => x.Profile.Id == fixture.Id
                )
                    .Effective.Width == 1240,
                "instance-page-launch-setting-button-persists-through-host"
            );
            instances.Close();
            await Task.Delay(100);
            // 测试 JAR 是本测试创建的无代码元数据归档，不触碰现有模组或存档。
            var mods = Path.Combine(fixture.GameDirectory, "mods");
            Directory.CreateDirectory(mods);
            var jar = Path.Combine(mods, "ui-fixture.jar");
            using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry("fabric.mod.json")
                        .Open()
                );
                writer.Write("{\"schemaVersion\":1,\"id\":\"ui_fixture\",\"name\":\"UI Fixture\",\"version\":\"1.0.0\"}");
            }

            var profile = InstanceRegistry.List(service, true)
                .Single(
                    x => x.Id == fixture.Id
                );
            var local = Bootstrap.WorkspacePages.Local(this, service, profile);
            local.Show(this);
            await Task.Delay(100);
            await Wait(
                () => local.Busy
            );
            var list = local.GetVisualDescendants()
                .OfType<ListBox>()
                .First();
            list.SelectedIndex = 0;
            await Click(
                local,
                "停用",
                () => local.Busy
            );
            Check(
                File.Exists(jar + ".disabled"),
                "local-resources-disable-button-uses-host"
            );
            list.SelectedIndex = 0;
            await Click(
                local,
                "启用",
                () => local.Busy
            );
            Check(
                File.Exists(jar),
                "local-resources-enable-button-uses-host"
            );
            await Click(
                local,
                "检查依赖关系",
                () => local.Busy
            );
            Check(
                local.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(
                        x => x.Name == "LocalDependencyStatus"
                    )
                    .Text!.Contains("加载器"),
                "dependency-button-returns-host-analysis"
            );
            local.Close();
            var archive = Path.Combine(paths.Root, "ui-custom.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(
                    zip.CreateEntry("config/ui-marker.txt")
                        .Open()
                );
                writer.Write("UI installed through Host");
            }

            GameProfile? installed = null;
            var pack = Bootstrap.WorkspacePages.Pack(
                this,
                service,
                profile,
                result => installed = result
            );
            pack.Show(this);
            pack.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.PlaceholderText == "Minecraft 版本（普通 ZIP 必填）"
                )
                .Text = "1.21.1";
            await pack.SelectArchiveAsync(archive);
            await Click(
                pack,
                "安装为新实例",
                () => pack.Busy
            );
            Check(
                installed is not null && File.Exists(
                    Path.Combine(installed.GameDirectory, "config/ui-marker.txt")
                ),
                "pack-page-install-button-creates-real-instance"
            );
            await Click(
                pack,
                "保存导出配置",
                () => pack.Busy
            );
            await Click(
                pack,
                "读取导出配置",
                () => pack.Busy
            );
            Check(pack.IsVisible, "pack-export-options-save-and-load-buttons-complete");
            pack.Close();
            var browserProfile = await EnsureFabricAsync(default);
            Check(browserProfile.Loader == "fabric", "default-resource-browser-preparation-routes-to-host");
            var history = await remote.PageTasks.ListAsync();
            Check(
                history.Any(
                    x => x.Command.Method == "workspace.instances.saveSettings" && x.State == "succeeded"
                ) && history.Any(
                    x => x.Command.Method == "workspace.packs.install" && x.State == "succeeded"
                ),
                "ui-actions-have-durable-host-receipts"
            );
            File.WriteAllText(
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
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = DiagnosticsService.Redact(
                            ex.ToString()
                        )
                    }
                )
            );
        }

        foreach (var window in OwnedWindows.ToArray())
        {
            window.Close();
        }

        await RequestApplicationExitAsync();
    }
}
