using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 实例管理：读取实例记录和版本配置，执行隔离、备份、恢复与组件变更并校验结果。
public partial class MainWindow
{
    // [实现] VersionClick 的实现：执行 await ManageVersionsAsync()。
    private async void VersionClick(object? sender, RoutedEventArgs e) => await ManageVersionsAsync();
    // [实现] ManageVersionsAsync 的实现：计算并返回 RunAsync( async ct => { Console.WriteLine("VERSION_UI_FETCH"); StatusText.Text = "读取官方版本列表"; va…。
    private Task ManageVersionsAsync(string? initialVersion = null) => RunAsync(
        async ct =>
        {
            // [实现] 回调触发时：执行 Console.WriteLine("VERSION_UI_FETCH")；执行 StatusText.Text = "读取官方版本列表"；准备 var releases = (IReadOnlyList<Pcl.Core.Contracts.Workspaces.MinecraftRelease>)await DesktopApi.CallAsync<Pcl.Core.Contracts.Workspaces.MinecraftRelease[]>("workspace.desktop.versions", null, ct: ct)。
            Console.WriteLine("VERSION_UI_FETCH");
            StatusText.Text = "读取官方版本列表";
            var releases = (IReadOnlyList<Pcl.Core.Contracts.Workspaces.MinecraftRelease>)await DesktopApi.CallAsync<Pcl.Core.Contracts.Workspaces.MinecraftRelease[]>("workspace.desktop.versions", null, ct: ct);
            Console.WriteLine("VERSION_UI_CATALOG " + releases.Count);
            var query = new TextBox
            {
                PlaceholderText = "搜索版本，例如 1.20.1",
                Text = initialVersion ?? ""
            };
            var snapshots = new CheckBox
            {
                Content = "显示快照",
                IsChecked = false
            };
            var versions = new ListBox
            {
                Height = 280
            };
            // [实现] Filter 的实现：执行 versions.ItemsSource = releases.Where( x => (snapshots.IsChecked == true || x.Type == "release"…；执行 versions.SelectedIndex = 0。
            void Filter()
            {
                versions.ItemsSource = releases.Where(
                    x => (snapshots.IsChecked == true || x.Type == "release") && x.Id.Contains(query.Text ?? "", StringComparison.OrdinalIgnoreCase)
                )
                    .ToArray();
                versions.SelectedIndex = 0;
            }

            query.TextChanged += (_, _) => Filter();
            snapshots.IsCheckedChanged += (_, _) => Filter();
            Filter();
            var loaders = new ComboBox
            {
                ItemsSource = new[]
                {
                    "自动匹配（优先稳定 Fabric）",
                    "原版",
                    "Fabric",
                    "Forge",
                    "NeoForge",
                    "Quilt",
                    "OptiFine",
                    "LiteLoader"
                },
                SelectedIndex = 0,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            var panel = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 12
            };
            foreach (var control in new Control[]
            {
                query,
                snapshots,
                versions,
                loaders
            }

            )
                panel.Children.Add(control);
            var dialog = new WorkspaceWindow
            {
                Title = "下载 Minecraft",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = panel
            };
            var install = new Button
            {
                Content = "下载并安装",
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            install.Click += (_, _) =>
            {
                // [实现] 回调触发时：按 versions.SelectedItem is Pcl.Core.Contracts.Workspaces.MinecraftRelease release 分支处理。
                if (versions.SelectedItem is Pcl.Core.Contracts.Workspaces.MinecraftRelease release)
                    dialog.Close((release.Id, loaders.SelectedIndex));
            };
            panel.Children.Add(install);
            if (Program.Arguments.Contains("--phase-one-version-manager"))
            {
                dialog.Opened += async (_, _) =>
                {
                    query.Text = "1.21.1";
                    await Task.Delay(300);
                    versions.SelectedItem = versions.ItemsSource!.Cast<Pcl.Core.Contracts.Workspaces.MinecraftRelease>()
                        .Single(
                            x => x.Id == "1.21.1"
                        );
                    loaders.SelectedIndex = 1;
                    await Task.Delay(100);
                    if ((versions.SelectedItem as Pcl.Core.Contracts.Workspaces.MinecraftRelease)?.Id != "1.21.1")
                    {
                        throw new InvalidOperationException("测试选择器未选中精确版本。");
                    }

                    Console.WriteLine("PHASE_ONE_SELECTED 1.21.1");
                    install.RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                };
            }

            if (Program.Arguments.Contains("--smoke-versions"))
                dialog.Opened += async (_, _) =>
                {
                    // [实现] 回调触发时：执行 await Task.Delay( TimeSpan.FromSeconds(3) )；执行 Console.WriteLine("VERSION_UI_TEST_TICK")；准备 var snapshot = releases.First( x => x.Type == "snapshot" ) .Id。
                    await Task.Delay(
                        TimeSpan.FromSeconds(3)
                    );
                    Console.WriteLine("VERSION_UI_TEST_TICK");
                    var snapshot = releases.First(
                        x => x.Type == "snapshot"
                    )
                        .Id;
                    snapshots.IsChecked = true;
                    query.Text = snapshot;
                    await Task.Delay(300);
                    if (!versions.ItemsSource!.Cast<Pcl.Core.Contracts.Workspaces.MinecraftRelease>()
                        .Any(
                            x => x.Id == snapshot
                        ))
                        throw new InvalidOperationException("快照显示失败。");
                    snapshots.IsChecked = false;
                    await Task.Delay(300);
                    if (versions.ItemsSource!.Cast<Pcl.Core.Contracts.Workspaces.MinecraftRelease>()
                        .Any())
                        throw new InvalidOperationException("快照筛选失败。");
                    query.Text = "1.20.1";
                    await Task.Delay(300);
                    var matches = versions.ItemsSource!.Cast<Pcl.Core.Contracts.Workspaces.MinecraftRelease>()
                        .ToArray();
                    if (matches.Length != 1 || matches[0].Id != "1.20.1")
                        throw new InvalidOperationException("版本筛选失败。");
                    using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(
                        new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height),
                        new Vector(96, 96)
                    );
                    image.Render(dialog);
                    image.Save(
                        Path.Combine(paths.Evidence, "version-manager.png"),
                        Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default
                    );
                    dialog.Close(null);
                };
            dialog.Opened += (_, _) => Console.WriteLine("VERSION_UI_DIALOG_OPENED");
            var choice = await dialog.PresentAsync<(string Version, int Loader)?>(this);
            if (choice is null)
            {
                // [实现] 条件成立（choice is null）时：执行 StatusText.Text = ""；结束当前过程。
                StatusText.Text = "";
                return;
            }

            var loader = choice.Value.Loader switch
            {
                1 => "vanilla",
                2 => "fabric",
                3 => "forge",
                4 => "neoforge",
                5 => "quilt",
                6 => "optifine",
                7 => "liteloader",
                _ => "auto"
            };
            var profile = await InstallThroughApplicationAsync(choice.Value.Version, loader, ct);
            RefreshProfiles(profile.Id);
            StatusText.Text = profile.Name + " 已安装";
        }
    );
}
