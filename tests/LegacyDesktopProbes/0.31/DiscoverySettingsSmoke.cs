using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pcl.Client;
using Pcl.Core;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 以实际 Avalonia 按钮驱动本轮迁移页面；只在显式隔离根目录的 smoke 开关下启用。
    private async Task DiscoverySettingsSmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--discovery-report") + 1];
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new Exception(label);
            }

            checks.Add(label);
        }

        T Named<T>(Window window, string name)
        where T : Control => window.GetVisualDescendants()
            .OfType<T>()
            .Single(
                x => x.Name == name
            );
        async Task Wait(Func<bool> pending)
        {
            var until = DateTime.UtcNow.AddMinutes(4);
            while (pending())
            {
                if (DateTime.UtcNow > until)
                {
                    throw new TimeoutException("Discovery UI timeout");
                }

                await Task.Delay(100);
            }
        }

        async Task Click(Window window, string title, Func<bool> busy)
        {
            window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == title
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            await Task.Delay(100);
            await Wait(busy);
        }

        try
        {
            if (remote is null || !CanWriteLegacy || accounts!.Accounts.Any(
                x => x.Kind != AccountKind.Offline
            ) || !paths.Root.Contains("architecture-0.29", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Isolated offline Host test root required");
            }

            var api = new WorkspaceApi(
                Bootstrap.WorkspacePages.Operations(this, service)
            );
            var vanilla = InstanceRegistry.List(service, true)
                .Single(
                    x => x.Id == "1.21.1"
                );
            var search = Bootstrap.WorkspacePages.Search(
                this,
                service,
                vanilla,
                _ =>
                {
                },
                _ =>
                {
                },
                true
            );
            search.Show(this);
            Named<CheckBox>(search, "UnifiedOnline")
                .IsChecked = false;
            await Click(
                search,
                "搜索",
                () => search.Busy
            );
            var results = Named<ListBox>(search, "UnifiedResults");
            Check(
                results.Items.OfType<View.SearchHit>()
                    .Any(
                        x => x.McmodUrl is not null
                    ),
                "ui-offline-encyclopedia-search-button"
            );
            await Click(
                search,
                "下一页",
                () => search.Busy
            );
            Check(
                Named<TextBlock>(search, "UnifiedStatus")
                    .Text!.StartsWith("第 2 页"),
                "ui-next-search-page-button"
            );
            await Click(
                search,
                "上一页",
                () => search.Busy
            );
            Check(
                Named<TextBlock>(search, "UnifiedStatus")
                    .Text!.StartsWith("第 1 页"),
                "ui-previous-search-page-button"
            );
            search.Close();
            var cf = Bootstrap.WorkspacePages.Curse(
                this,
                service,
                vanilla,
                _ =>
                {
                },
                _ =>
                {
                }
            );
            cf.Show(this);
            await Click(
                cf,
                "搜索",
                () => cf.Busy
            );
            Check(
                cf.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Any(
                        x => x.Text?.Contains("请填写自己的 CurseForge API Key") == true
                    ),
                "ui-curseforge-without-key-shows-actionable-error"
            );
            cf.Close();
            var fabricView = await api.CallAsync<View.GameProfile>(
                "workspace.browser.ensureFabric",
                null,
                new
                {
                    gameVersion = "1.21.1",
                    selectedId = "1.21.1"
                }
            );
            var fabricProfile = InstanceRegistry.List(service, true)
                .Single(
                    x => x.Id == fabricView.Id
                );
            var liveSearch = Bootstrap.WorkspacePages.Search(
                this,
                service,
                fabricProfile,
                _ =>
                {
                },
                _ =>
                {
                }
            );
            liveSearch.Show(this);
            Named<TextBox>(liveSearch, "UnifiedQuery")
                .Text = "fabric api";
            await Click(
                liveSearch,
                "搜索",
                () => liveSearch.Busy
            );
            var liveResults = Named<ListBox>(liveSearch, "UnifiedResults");
            liveResults.SelectedItem = liveResults.Items.OfType<View.SearchHit>()
                .First(
                    x => x.Title == "Fabric API" && x.Sources.Contains("Modrinth")
                );
            await Task.Delay(100);
            await Wait(
                () => liveSearch.Busy
            );
            Check(
                Named<ComboBox>(liveSearch, "UnifiedVersions")
                    .Items.Count > 0,
                "ui-public-search-selection-loads-compatible-versions"
            );
            await Click(
                liveSearch,
                "安装所选版本（含前置）",
                () => liveSearch.Busy
            );
            Check(
                Named<TextBlock>(liveSearch, "UnifiedStatus")
                    .Text!.StartsWith("已安装并校验") && Directory.GetFiles(
                    Path.Combine(fabricView.GameDirectory, "mods"),
                    "*.jar"
                )
                    .Length > 0,
                "ui-public-search-install-button-downloads-real-mod"
            );
            liveSearch.Close();
            await NavigateClassicAsync("setup-launch");
            var width = this.GetVisualDescendants()
                .OfType<NumericUpDown>()
                .Single(
                    x => x.Tag?.ToString() == "Width"
                );
            width.Value = 1178;
            await Click(
                this,
                "保存设置",
                () => !Named<Button>(this, "ClassicSettingsSave")
                    .IsEnabled
            );
            var snapshot = await api.CallAsync<View.SettingsSnapshot>("workspace.settings.read", null);
            Check(
                snapshot.Launch.GetProperty("Width")
                    .GetInt32() == 1178 && Named<TextBlock>(this, "ClassicSettingsStatus")
                    .Text!.StartsWith("已保存"),
                "ui-classic-global-settings-save-through-host"
            );
            var settings = Bootstrap.WorkspacePages.Settings(
                this,
                service,
                ApplyAppearance,
                _ =>
                {
                }
            );
            settings.Show(this);
            await Task.Delay(100);
            await Wait(
                () => settings.Busy
            );
            var title = settings.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(
                    x => x.Text == snapshot.Appearance.Title
                );
            title.Text = "PCL 0.29 UI verified";
            await Click(
                settings,
                "保存并应用",
                () => settings.Busy
            );
            Check(
                Title == "PCL 0.29 UI verified" && (await api.CallAsync<View.SettingsSnapshot>("workspace.settings.read", null)).Appearance.Title == Title,
                "ui-settings-help-save-applies-and-persists"
            );
            var imageFolder = Path.Combine(paths.Root, "ui-background");
            Directory.CreateDirectory(imageFolder);
            await File.WriteAllBytesAsync(
                Path.Combine(imageFolder, "fixture.png"),
                Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEElEQVR4nGP4z8AARAwQCgAf7gP9i18U1AAAAABJRU5ErkJggg=="
                )
            );
            var media = (await api.CallAsync<View.SettingsSnapshot>("workspace.settings.read", null)).Appearance with
            {
                Background = "",
                BackgroundDirectory = imageFolder
            };
            await api.CallAsync<View.SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = media
                    }
                }
            );
            ApplyAppearance(
                Newtonsoft.Json.JsonConvert.DeserializeObject<AppearanceSettings>(
                    JsonSerializer.Serialize(media)
                )!
            );
            Check(
                BackgroundImage.Source is not null,
                "ui-background-directory-applies-selected-file-with-empty-single-image"
            );
            settings.Close();
            await NavigateClassicAsync("setup-ui");
            await Click(
                this,
                "生成教学文件",
                () => !this.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => x.Content?.ToString() == "生成教学文件"
                    )
                    .IsEnabled
            );
            Check(
                Directory.GetFiles(paths.Root, "home-example-*.json")
                    .Length > 0,
                "ui-home-example-created-by-host"
            );
            var partial = Path.Combine(paths.Root, "cache/ui-cleanup.part");
            Directory.CreateDirectory(
                Path.GetDirectoryName(partial)!
            );
            await File.WriteAllTextAsync(partial, "ui-cache-test");
            File.SetLastWriteTimeUtc(
                partial,
                DateTime.UtcNow.AddDays(-2)
            );
            var cache = Bootstrap.WorkspacePages.Cache(this, paths);
            cache.Show(this);
            await Task.Delay(100);
            await Wait(
                () => cache.Busy
            );
            var tabs = cache.GetVisualDescendants()
                .OfType<TabControl>()
                .Single();
            tabs.SelectedIndex = 1;
            await Task.Delay(100);
            await Click(
                cache,
                "扫描临时文件",
                () => cache.Busy
            );
            var candidates = cache.GetVisualDescendants()
                .OfType<ListBox>()
                .Single(
                    x => x.Items.OfType<View.TemporaryCacheFile>()
                        .Any()
                );
            candidates.SelectedItem = candidates.Items.OfType<View.TemporaryCacheFile>()
                .Single(
                    x => x.RelativePath == "cache/ui-cleanup.part"
                );
            await Click(
                cache,
                "将所选文件移入回收目录",
                () => cache.Busy
            );
            Check(
                !File.Exists(partial),
                "ui-cache-selected-file-moved-to-trash"
            );
            tabs.SelectedIndex = 2;
            await Task.Delay(100);
            var trash = cache.GetVisualDescendants()
                .OfType<ListBox>()
                .Single(
                    x => x.Items.OfType<View.CacheTrashBatch>()
                        .Any()
                );
            trash.SelectedIndex = 0;
            await Click(
                cache,
                "恢复所选批次",
                () => cache.Busy
            );
            Check(
                File.Exists(partial),
                "ui-cache-restore-button"
            );
            cache.Close();
            Check(
                Directory.GetFiles(
                    Path.Combine(paths.Root, "page-operations-v1"),
                    "*.json"
                )
                    .Length > 10,
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
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
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
