using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core;

namespace Pcl.Desktop;
// 隔离目录内通过真实控件编辑和保存，然后在另一应用进程中检查恢复；不接触用户账号和原配置。
public partial class MainWindow
{
    // [实现] WindowsSettingsSmokeAsync 的实现：准备 var checks = new List<string>()；准备 bool reopen = Program.Arguments.Contains("--acceptance-settings-reopen")；执行 void Check(bool ok, string label) { if (!ok) { throw new InvalidOperationException(label); } ch…。
    private async Task WindowsSettingsSmokeAsync()
    {
        var checks = new List<string>();
        bool reopen = Program.Arguments.Contains("--acceptance-settings-reopen");
        // [实现] Check 的实现：按 !ok 分支处理；执行 checks.Add(label)。
        void Check(bool ok, string label)
        {
            if (!ok)
            {
                // [实现] 条件成立（!ok）时：抛出 new InvalidOperationException(label)。
                throw new InvalidOperationException(label);
            }

            checks.Add(label);
        }

        // [实现] Input 的实现：计算并返回 classicPage.GetVisualDescendants() .OfType<T>() .Single( x => Equals(x.Tag, tag) )。
        T Input<T>(string tag)
        where T : Control => classicPage.GetVisualDescendants()
            .OfType<T>()
            .Single(
                x => Equals(x.Tag, tag)
            );
        // [实现] Save 的实现：执行 classicPage.GetVisualDescendants() .OfType<Button>() .Single( x => x.Name == "ClassicSettingsSa…；执行 await Task.Delay(150)；执行 Check( classicPage.GetVisualDescendants() .OfType<TextBlock>() .Single( x => x.Name == "Classic…。
        async Task Save()
        {
            classicPage.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Name == "ClassicSettingsSave"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            await Task.Delay(150);
            Check(
                classicPage.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(
                        x => x.Name == "ClassicSettingsStatus"
                    )
                    .Text!.StartsWith("已保存"),
                "save reports actual success"
            );
        }

        // [实现] Capture 的实现：执行 await Task.Delay(500)；准备 var bitmap = new RenderTargetBitmap( new PixelSize((int)window.Bounds.Width, (int)window.Bounds…；执行 bitmap.Render(window)。
        async Task Capture(Window window, string name)
        {
            await Task.Delay(500);
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                new Vector(96, 96)
            );
            bitmap.Render(window);
            bitmap.Save(
                Path.Combine(paths.Evidence, name + ".png"),
                PngBitmapEncoderOptions.Default
            );
        }

        try
        {
            // [实现] 在异常保护范围内：执行 await NavigateClassicAsync("setup-launch")；执行 await Task.Delay(400)；按 !reopen 分支处理。
            await NavigateClassicAsync("setup-launch");
            await Task.Delay(400);
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：执行 Input<NumericUpDown>("Width") .Value = 960；执行 Input<NumericUpDown>("Height") .Value = 600；执行 Input<NumericUpDown>("MaximumMemoryMb") .Value = 1536。
                Input<NumericUpDown>("Width")
                    .Value = 960;
                Input<NumericUpDown>("Height")
                    .Value = 600;
                Input<NumericUpDown>("MaximumMemoryMb")
                    .Value = 1536;
                Input<TextBox>("JvmArguments")
                    .Text = "-Dpcl.settings.acceptance=true";
                Input<TextBox>("VersionInformation")
                    .Text = "PCL settings acceptance";
                Input<ComboBox>("GarbageCollector")
                    .SelectedIndex = 1;
                await Save();
                var before = new LauncherSettingsStore(paths).Global;
                Input<TextBox>("JvmArguments")
                    .Text = "-Xmx99999M";
                classicPage.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => x.Name == "ClassicSettingsSave"
                    )
                    .RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                Check(
                    new LauncherSettingsStore(paths).Global == before,
                    "invalid JVM override rejected without changing disk"
                );
                Input<TextBox>("JvmArguments")
                    .Text = before.JvmArguments;
                await Save();
            }

            var launch = new LauncherSettingsStore(paths).Global;
            Check(
                launch.Width == 960 && launch.Height == 600 && launch.MaximumMemoryMb == 1536 && launch.GarbageCollector == "g1",
                "launch fields persisted"
            );
            Check(
                Input<NumericUpDown>("Width")
                    .Value == 960,
                "launch width restored into control"
            );
            await Capture(this, "settings-launch");
            await NavigateClassicAsync("setup-ui");
            await Task.Delay(300);
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：执行 classicPage.GetVisualDescendants() .OfType<Button>() .Single( x => Equals(x.Content, "生成教学文件") …；执行 await Task.Delay(150)；执行 Check( File.Exists( Input<TextBox>("HomeDocument") .Text ), "home example generated by real but…。
                classicPage.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => Equals(x.Content, "生成教学文件")
                    )
                    .RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                await Task.Delay(150);
                Check(
                    File.Exists(
                        Input<TextBox>("HomeDocument")
                            .Text
                    ),
                    "home example generated by real button"
                );
                Input<TextBox>("Background")
                    .Text = Path.Combine(paths.Evidence, "settings-launch.png");
                Input<ComboBox>("BackgroundFit")
                    .SelectedIndex = 1;
                Input<NumericUpDown>("BackgroundOpacity")
                    .Value = .25m;
                Input<NumericUpDown>("BackgroundBlur")
                    .Value = 2;
                Input<TextBox>("Title")
                    .Text = "PCL Settings Test";
                Input<ComboBox>("TitleMode")
                    .SelectedIndex = 2;
                Input<TextBox>("Accent")
                    .Text = "#008F9C";
                Input<NumericUpDown>("WindowOpacity")
                    .Value = .95m;
                Input<CheckBox>("MusicAutoPlay")
                    .IsChecked = false;
                Input<CheckBox>("HiddenRoute:modpack")
                    .IsChecked = true;
                await Save();
            }

            Check(
                Title == "PCL Settings Test" && Math.Abs(Opacity - .95) < .001 && classicBrand?.Text == "PCL Settings Test",
                "appearance immediately applied and restored"
            );
            Check(
                BackgroundImage.Source is Bitmap && BackgroundImage.Stretch == Avalonia.Media.Stretch.Uniform && BackgroundImage.Effect is Avalonia.Media.BlurEffect && BackgroundImage.Opacity == .25,
                "background file fit blur and opacity applied"
            );
            Check(
                new AppearanceService(paths).Load()
                    .Accent == "#008F9C",
                "accent persisted"
            );
            await Capture(this, "settings-appearance");
            await NavigateClassicAsync("launch");
            await Task.Delay(350);
            Check(
                classicCustomHome.IsVisible && classicCustomHome.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Any(
                        x => x.Text?.StartsWith("编辑正文") == true
                    ),
                "saved home rendered on launch page"
            );
            await NavigateClassicAsync("download");
            Check(
                !classicNavigation.Children.OfType<Button>()
                    .Any(
                        x => Equals(x.Tag, "modpack")
                    ),
                "hidden route actually removed"
            );
            await NavigateClassicAsync("setup-system");
            await Task.Delay(300);
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：执行 Input<NumericUpDown>("ConcurrentFiles") .Value = 3；执行 Input<NumericUpDown>("SpeedKiB") .Value = 512；执行 Input<CheckBox>("SegmentedDownloads") .IsChecked = true。
                Input<NumericUpDown>("ConcurrentFiles")
                    .Value = 3;
                Input<NumericUpDown>("SpeedKiB")
                    .Value = 512;
                Input<CheckBox>("SegmentedDownloads")
                    .IsChecked = true;
                Input<NumericUpDown>("SegmentWorkers")
                    .Value = 2;
                await Save();
            }

            var network = DownloadTasks.For(paths)
                .Options;
            Check(
                network.ConcurrentFiles == 3 && network.SpeedKiB == 512 && network.SegmentedDownloads && network.SegmentWorkers == 2,
                "active download service receives persisted options"
            );
            Check(
                Input<NumericUpDown>("SpeedKiB")
                    .Value == 512,
                "network limit restored into control"
            );
            await Capture(this, "settings-network");
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：准备 var selected = (GameProfile)ProfileBox.SelectedItem!；准备 var windows = new WorkspaceWindow[] { Bootstrap.WorkspacePages.Cache(this, paths), new TaskCenterWindow(paths…；遍历 windows 处理 window。
                var selected = (GameProfile)ProfileBox.SelectedItem!;
                var windows = new WorkspaceWindow[]
                {
                    Bootstrap.WorkspacePages.Cache(this, paths),
                    new TaskCenterWindow(paths, RunAsync, Open),
                    Bootstrap.WorkspacePages.Settings(this, service, ApplyAppearance, Open),
                    Bootstrap.WorkspacePages.Multiplayer(this, service),
                    Bootstrap.WorkspacePages.Search(
                        this,
                        service,
                        selected,
                        _ =>
                        {
                            // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                        },
                        Open
                    ),
                    Bootstrap.WorkspacePages.Instances(this, service),
                    Bootstrap.WorkspacePages.Pack(
                        this,
                        service,
                        selected,
                        _ =>
                        {
                            // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                        }
                    ),
                    Bootstrap.WorkspacePages.Local(this, service, selected),
                    new ComponentManagerWindow(
                        Bootstrap.LegacyPageAdapters.Components(service, selected, remote),
                        Bootstrap.LegacyPageAdapters.Describe(selected)
                    ),
                    new ArtifactDownloadWindow(
                        Bootstrap.LegacyPageAdapters.Artifacts(service, remote),
                        selected.GameVersion
                    ),
                    Bootstrap.WorkspacePages.OptiFabric(this, service),
                    Bootstrap.WorkspacePages.Curse(
                        this,
                        service,
                        selected,
                        _ =>
                        {
                            // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                        },
                        Open
                    ),
                    Bootstrap.WorkspacePages.Skins(this, accounts!),
                    Bootstrap.WorkspacePages.Accounts(this, accounts!),
                };
                foreach (var window in windows)
                {
                    // [实现] 对 windows 中的每一项：执行 window.Show(this)；执行 await Task.Delay(400)；执行 Check( window.WorkspaceStructured, window.GetType() .Name + " shared structure attached" )。
                    window.Show(this);
                    await Task.Delay(400);
                    Check(
                        window.WorkspaceStructured,
                        window.GetType()
                            .Name + " shared structure attached"
                    );
                    foreach (var tabs in window.GetVisualDescendants()
                        .OfType<TabControl>())
                    {
                        // [实现] 对 window.GetVisualDescendants() .OfType<TabControl>() 中的每一项：执行 Check( tabs.TabStripPlacement == Dock.Left, window.GetType() .Name + " left navigation" )。
                        Check(
                            tabs.TabStripPlacement == Dock.Left,
                            window.GetType()
                                .Name + " left navigation"
                        );
                    }

                    await Capture(
                        window,
                        window.GetType()
                            .Name
                    );
                    window.Close();
                }
            }

            JsonFiles.Write(
                Path.Combine(
                    paths.Evidence,
                    reopen
                        ? "settings-reopen.json"
                        : "settings-ui.json"
                ),
                new
                {
                    status = "passed",
                    checks,
                    separateProcess = reopen
                }
            );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine( paths.Evidence, reopen ? "settings-reopen.json" : "settings-ui.j…。
            JsonFiles.Write(
                Path.Combine(
                    paths.Evidence,
                    reopen
                        ? "settings-reopen.json"
                        : "settings-ui.json"
                ),
                new
                {
                    status = "failed",
                    checks,
                    error = ex.ToString()
                }
            );
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 Close()。
            Close();
        }
    }
}
