using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 隔离状态下验收真实 UI 与 Host。合成 JAR 仅用于扫描，不执行其代码。
    private async Task CloseoutThirdAcceptanceAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--acceptance-report") + 1];
        var javaInput = Program.Arguments[Array.IndexOf(Program.Arguments, "--java-input") + 1];
        var checks = new List<string>();
        var api = new WorkspaceApi(service.Workspaces);
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new InvalidOperationException(name);
            }

            checks.Add(name);
            File.AppendAllText(report + ".progress", name + "\n");
        }

        async Task Wait(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!ready())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("third acceptance wait");
                }

                await Task.Delay(100);
            }
        }

        Button Button(Window page, string text) => page.GetVisualDescendants()
            .OfType<Button>()
            .Single(
                x => Equals(x.Content, text)
            );
        void Click(Button button) => button.RaiseEvent(
            new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)
        );
        try
        {
            UiMotion.Configure(0);
            var profiles = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
            var profile = profiles.Items.Single(
                x => x.Profile.Id == "scan-acceptance"
            )
                .Profile;
            await NavigateClassicAsync("setup-ui");
            await Wait(
                () => this.GetVisualDescendants()
                    .OfType<CheckBox>()
                    .Any(
                        x => x.Tag?.ToString()?.StartsWith("HiddenRoute:") == true
                    )
            );
            var toggles = this.GetVisualDescendants()
                .OfType<CheckBox>()
                .Where(
                    x => x.Tag?.ToString()?.StartsWith("HiddenRoute:") == true
                )
                .ToArray();
            var routes = toggles.Select(
                x => x.Tag!.ToString()![12..]
            )
                .Order()
                .ToArray();
            Check(routes.Length == 22, "UI-005-all-22-supported-route-switches-present");
            foreach (var toggle in toggles)
            {
                toggle.IsChecked = true;
            }

            Click(
                Button(this, "保存设置")
            );
            await Wait(
                () => this.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Any(
                        x => x.Name == "ClassicSettingsStatus" && x.Text?.StartsWith("已保存并应用") == true
                    )
            );
            var settings = await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
            Check(
                settings.Appearance.HiddenRoutes.Order()
                    .SequenceEqual(routes),
                "UI-005-all-route-switches-persist-through-host"
            );
            Check(
                classicTabs.Children.OfType<Button>()
                    .Where(
                        x => x.Tag is string tag && routes.Contains(tag)
                    )
                    .All(
                        x => !x.IsVisible
                    ) && !VersionButton.IsVisible && !FabricButton.IsVisible && classicRecovery.IsVisible,
                "UI-005-main-tabs-and-version-actions-hidden-recovery-retained"
            );
            var local = new LocalResourcesWindow(service.Workspaces, profile);
            var shown = local.PresentAsync(this);
            await Wait(
                () => local.IsVisible && !local.Busy
            );
            Check(
                new[]
                {
                    "检查更新",
                    "安装所选更新",
                    "批量更新所选",
                    "忽略此版本"
                }.All(
                    x => !Button(local, x)
                        .IsVisible
                ),
                "UI-005-local-resource-update-actions-obey-hidden-setting"
            );
            await local.RequestCloseAsync();
            await shown;
            Click(classicRecovery);
            await Wait(
                () => !classicRecovery.IsVisible && VersionButton.IsVisible && FabricButton.IsVisible
            );
            settings = await api.CallAsync<SettingsSnapshot>("workspace.settings.read", null);
            Check(
                settings.Appearance.HiddenRoutes.Length == 0 && settings.Appearance.HiddenNavigation.Length == 0,
                "UI-005-recovery-clears-persisted-hidden-settings"
            );
            local = new LocalResourcesWindow(service.Workspaces, profile);
            shown = local.PresentAsync(this);
            await Wait(
                () => local.IsVisible && !local.Busy
            );
            Check(
                Button(local, "检查更新")
                    .IsVisible && Button(local, "忽略此版本")
                    .IsVisible,
                "UI-005-reopened-resource-page-restores-actions"
            );
            // 读取实际 JAR/ZIP 并展示错误；元数据识别不等同加载器兼容验收。
            var scan = await api.CallAsync<ResourceScan>("workspace.resources.scan", profile.Id);
            Check(scan.Items.Length == 10, "RES-014-host-scans-all-local-archives");
            foreach (var id in new[]
            {
                "accept_fabric",
                "accept_quilt",
                "accept_forge",
                "accept_neoforge",
                "accept_legacy",
                "accept_lite",
                "accept_disabled",
                "fabric-api",
                "modmenu"
            }

            )
            {
                Check(
                    scan.Items.Any(
                        x => x.ModIds.Contains(id) && x.Error is null
                    ),
                    "RES-014-identifies-" + id
                );
            }

            Check(
                scan.Items.Single(
                    x => Path.GetFileName(x.Path) == "broken.jar"
                )
                    .Error is not null,
                "RES-014-corrupt-archive-reported-without-aborting-scan"
            );
            var resourceRows = local.GetVisualDescendants()
                .OfType<ListBox>()
                .Single(
                    x => x.Height == 230
                );
            var filter = local.GetVisualDescendants()
                .OfType<ComboBox>()
                .Single(
                    x => x.Items.Cast<object>()
                        .Contains("已停用")
                );
            filter.SelectedIndex = 5;
            Check(
                resourceRows.ItemCount == 1 && !((LocalResourceInfo)resourceRows.Items[0]!).Enabled,
                "RES-014-disabled-filter-shows-disabled-archive"
            );
            filter.SelectedIndex = 6;
            Check(
                resourceRows.ItemCount == 1 && ((LocalResourceInfo)resourceRows.Items[0]!).Error is not null,
                "RES-014-error-filter-shows-corrupt-archive"
            );
            await local.RequestCloseAsync();
            await shown;
            // 帮助页使用随项目附带的实际文档，查询正文短语而不是仅命中文件名。
            var help = Bootstrap.WorkspacePages.Settings(
                this,
                service,
                ApplyAppearance,
                _ =>
                {
                }
            );
            shown = help.PresentAsync(this);
            await Wait(
                () => help.IsVisible && !help.Busy && help.GetVisualDescendants()
                    .OfType<TabControl>()
                    .Any()
            );
            help.GetVisualDescendants()
                .OfType<TabControl>()
                .Single()
                .SelectedIndex = 2;
            await Wait(
                () => help.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Any(
                        x => x.PlaceholderText == "搜索帮助内容"
                    )
            );
            var query = help.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.PlaceholderText == "搜索帮助内容"
                );
            var articles = help.GetVisualDescendants()
                .OfType<ListBox>()
                .Single(
                    x => x.Height == 200
                );
            var body = help.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.IsReadOnly
                );
            query.Text = "client secret";
            await Task.Delay(150);
            Check(articles.ItemCount > 0, "UI-006-help-search-matches-document-body");
            articles.SelectedIndex = 0;
            await Wait(
                () => body.Text?.Contains("client secret", StringComparison.OrdinalIgnoreCase) == true
            );
            Check(
                body.Text?.Contains("client secret", StringComparison.OrdinalIgnoreCase) == true,
                "UI-006-selected-help-displays-matching-body"
            );
            query.Text = "pcl-no-match-7fce93e9";
            await Task.Delay(150);
            Check(
                articles.ItemCount == 0 && string.IsNullOrEmpty(body.Text),
                "UI-006-no-result-clears-stale-body"
            );
            query.Text = ProductInfo.Version;
            await Task.Delay(150);
            Check(articles.ItemCount > 0, "UI-006-filename-search-remains-supported");
            query.Text = "";
            await Task.Delay(150);
            Check(articles.ItemCount > 0, "UI-006-clearing-query-restores-document-list");
            await help.RequestCloseAsync();
            await shown;
            // 实际执行已安装官方 Java 的版本检查；无 JVM 模拟程序。
            var java = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(javaInput)
            )!;
            var runtimes = await api.CallAsync<DiscoveredJava[]>(
                "workspace.java.discover",
                null,
                new
                {
                    paths = java.Values.Concat(java.Values)
                        .Append("/usr/bin/java")
                        .ToArray()
                }
            );
            foreach (var item in java)
            {
                Check(
                    runtimes.Any(
                        x => x.Executable == item.Value && x.Runtime?.MajorVersion == int.Parse(item.Key) && x.Runtime.Architecture == "aarch64"
                    ),
                    "JAVA-008-real-java-" + item.Key + "-arm64-inspected"
                );
            }

            Check(
                runtimes.Select(
                    x => x.Executable
                )
                    .Distinct()
                    .Count() == runtimes.Length && runtimes.All(
                    x => x.Executable != "/usr/bin/java"
                ),
                "JAVA-008-deduplicates-and-skips-system-install-stub"
            );
            var valid = runtimes.Where(
                x => x.Runtime?.Architecture == "aarch64"
            )
                .ToArray();
            Check(
                valid.Select(
                    x => x.Runtime!.MajorVersion
                )
                    .SequenceEqual(
                        valid.Select(
                            x => x.Runtime!.MajorVersion
                        )
                            .OrderDescending()
                    ),
                "JAVA-008-native-runtime-version-order-correct"
            );
            var instance = Bootstrap.WorkspacePages.Instances(this, service);
            shown = instance.PresentAsync(this);
            await Wait(
                () => instance.IsVisible && !instance.Busy
            );
            Click(
                Button(instance, "全局启动设置")
            );
            Click(
                Button(instance, "查找已安装 Java")
            );
            await Wait(
                () => instance.OwnedWindows.Any(
                    x => x.Title == "选择已安装 Java"
                )
            );
            var picker = instance.OwnedWindows.Single(
                x => x.Title == "选择已安装 Java"
            );
            var choices = picker.GetVisualDescendants()
                .OfType<ListBox>()
                .Single();
            var choice = choices.Items.Cast<DiscoveredJava>()
                .Single(
                    x => x.Executable == java["21"]
                );
            choices.SelectedItem = choice;
            Click(
                Button(picker, "使用所选 Java")
            );
            await Wait(
                () => !picker.IsVisible
            );
            Check(
                instance.GetVisualDescendants()
                    .OfType<TextBox>()
                    .Single(
                        x => x.Name == "Launch_JavaExecutable"
                    )
                    .Text == java["21"],
                "JAVA-008-ui-applies-discovered-java-selection"
            );
            Click(
                Button(instance, "保存启动设置")
            );
            await Wait(
                () => !instance.Busy
            );
            profiles = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
            Check(
                profiles.Global.JavaExecutable == java["21"],
                "JAVA-008-selected-java-persists-through-host"
            );
            await instance.RequestCloseAsync();
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
        catch (Exception error)
        {
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = error.ToString()
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
