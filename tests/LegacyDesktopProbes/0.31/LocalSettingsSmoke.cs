using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core;

namespace Pcl.Desktop;
// 通过真实设置控件修改、保存和再次打开；同时检查渲染属性、定时器与恢复路径。
public partial class MainWindow
{
    // [实现] LocalSettingsSmokeAsync 的实现：准备 var checks = new List<string>()；准备 var reopen = Program.Arguments.Contains("--acceptance-local-settings-reopen")；执行 void Check(bool value, string name) { if (!value) { throw new InvalidOperationException(name); …。
    private async Task LocalSettingsSmokeAsync()
    {
        var checks = new List<string>();
        var reopen = Program.Arguments.Contains("--acceptance-local-settings-reopen");
        // [实现] Check 的实现：按 !value 分支处理；执行 checks.Add(name)。
        void Check(bool value, string name)
        {
            if (!value)
            {
                // [实现] 条件成立（!value）时：抛出 new InvalidOperationException(name)。
                throw new InvalidOperationException(name);
            }

            checks.Add(name);
        }

        // [实现] Input 的实现：计算并返回 classicPage.GetVisualDescendants() .OfType<T>() .Single( x => Equals(x.Tag, name) )。
        T Input<T>(string name)
        where T : Control => classicPage.GetVisualDescendants()
            .OfType<T>()
            .Single(
                x => Equals(x.Tag, name)
            );
        // [实现] Save 的实现：执行 classicPage.GetVisualDescendants() .OfType<Button>() .Single( x => x.Name == "ClassicSettingsSa…；执行 await Task.Delay(200)；执行 Check( classicPage.GetVisualDescendants() .OfType<TextBlock>() .Single( x => x.Name == "Classic…。
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
            await Task.Delay(200);
            Check(
                classicPage.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Single(
                        x => x.Name == "ClassicSettingsStatus"
                    )
                    .Text!.StartsWith("已保存"),
                "settings save succeeded"
            );
        }

        // [实现] Capture 的实现：执行 await Task.Delay(250)；准备 var bitmap = new RenderTargetBitmap( new PixelSize((int)Bounds.Width, (int)Bounds.Height), new …；执行 bitmap.Render(this)。
        async Task Capture(string name)
        {
            await Task.Delay(250);
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Bounds.Width, (int)Bounds.Height),
                new Vector(96, 96)
            );
            bitmap.Render(this);
            bitmap.Save(
                Path.Combine(paths.Evidence, name + ".png"),
                PngBitmapEncoderOptions.Default
            );
        }

        try
        {
            // [实现] 在异常保护范围内：执行 await NavigateClassicAsync("setup-launch")；执行 await Task.Delay(200)；按 !reopen 分支处理。
            await NavigateClassicAsync("setup-launch");
            await Task.Delay(200);
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：执行 Input<TextBox>("GameWindowTitle") .Text = "PCL 本机设置验收"；执行 Input<ComboBox>("ProcessPriority") .SelectedIndex = 1；执行 Input<ComboBox>("WindowMode") .SelectedIndex = 3。
                Input<TextBox>("GameWindowTitle")
                    .Text = "PCL 本机设置验收";
                Input<ComboBox>("ProcessPriority")
                    .SelectedIndex = 1;
                Input<ComboBox>("WindowMode")
                    .SelectedIndex = 3;
                Input<ComboBox>("LauncherVisibility")
                    .SelectedIndex = 4;
                Input<ComboBox>("GarbageCollector")
                    .SelectedIndex = 2;
                await Save();
            }

            var launch = new LauncherSettingsStore(paths).Global;
            Check(
                launch.GameWindowTitle == "PCL 本机设置验收" && launch.ProcessPriority == "low" && launch.WindowMode == "maximized" && launch.GarbageCollector == "g1-tuned",
                "new launch fields persist and reopen"
            );
            await Capture("local-launch");
            await NavigateClassicAsync("setup-ui");
            await Task.Delay(200);
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：执行 Input<TextBox>("Background") .Text = Path.Combine(paths.Evidence, "local-launch.png")；执行 Input<TextBox>("TitleImage") .Text = Path.Combine(paths.Evidence, "local-launch.png")；执行 Input<ComboBox>("TitleMode") .SelectedIndex = 3。
                Input<TextBox>("Background")
                    .Text = Path.Combine(paths.Evidence, "local-launch.png");
                Input<TextBox>("TitleImage")
                    .Text = Path.Combine(paths.Evidence, "local-launch.png");
                Input<ComboBox>("TitleMode")
                    .SelectedIndex = 3;
                Input<ComboBox>("BackgroundFit")
                    .SelectedIndex = 4;
                Input<CheckBox>("BackgroundTint")
                    .IsChecked = true;
                Input<ComboBox>("AccentMode")
                    .SelectedIndex = 1;
                Input<ComboBox>("HomeMode")
                    .SelectedIndex = 2;
                Input<ComboBox>("HomePreset")
                    .SelectedIndex = 1;
                Input<CheckBox>("ShowStartupLogo")
                    .IsChecked = true;
                await Save();
            }

            Check(
                classicTitleImage.IsVisible && classicTitleImage.Source is not null && classicBrand?.IsVisible == false,
                "title image rendered instead of text"
            );
            Check(
                classicBackground.Background is ImageBrush { TileMode: TileMode.Tile } && classicTint.Opacity > 0,
                "tile brush and tint consumed"
            );
            var color = classicHeader?.Background?.ToString();
            await Task.Delay(300);
            Check(
                classicHeader?.Background?.ToString() != color,
                "dynamic accent changes rendered header"
            );
            Check(
                (await new AppearanceService(paths).ReadHomeAsync()).Links.Length > 0,
                "home preset supplies working actions"
            );
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：执行 foreach (var(index, x, y)in new[] { (5, AlignmentX.Left, AlignmentY.Top), (13, AlignmentX.Right…；执行 Input<NumericUpDown>("AnimationSpeed") .Value = 0；执行 await Save()。
                foreach (var(index, x, y)in new[]
                {
                    (5, AlignmentX.Left, AlignmentY.Top),
                    (13, AlignmentX.Right, AlignmentY.Bottom)
                }

                )
                {
                    // [实现] 本段流程：执行 Input<ComboBox>("BackgroundFit") .SelectedIndex = index；执行 await Save()；执行 Check( classicBackground.Background is ImageBrush b && b.AlignmentX == x && b.AlignmentY == y, …。
                    Input<ComboBox>("BackgroundFit")
                        .SelectedIndex = index;
                    await Save();
                    Check(
                        classicBackground.Background is ImageBrush b && b.AlignmentX == x && b.AlignmentY == y,
                        "background anchor " + index
                    );
                }

                Input<NumericUpDown>("AnimationSpeed")
                    .Value = 0;
                await Save();
                color = classicHeader?.Background?.ToString();
                await Task.Delay(300);
                Check(
                    classicHeader?.Background?.ToString() == color && !accentTimer.IsEnabled,
                    "zero animation speed stops timer"
                );
                Input<NumericUpDown>("AnimationSpeed")
                    .Value = 1;
                Input<ComboBox>("BackgroundFit")
                    .SelectedIndex = 4;
                await Save();
                // 真正隐藏设置和启动导航后，恢复按钮仍可点击并原子保存空隐藏列表。
                foreach (var check in classicPage.GetVisualDescendants()
                    .OfType<CheckBox>()
                    .Where(
                        x => x.Tag is string tag && tag.StartsWith("HiddenRoute:")
                    ))
                {
                    // [实现] 对 classicPage.GetVisualDescendants() .OfType<CheckBox>() .Where( x => x.Tag is string tag && tag.… 中的每一项：执行 check.IsChecked = true。
                    check.IsChecked = true;
                }

                await Save();
                Check(
                    classicRecovery.IsVisible && !VersionButton.IsVisible && !FabricButton.IsVisible,
                    "hidden management actions and recovery available"
                );
                classicRecovery.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
                await Task.Delay(300);
                Check(
                    !classicRecovery.IsVisible && new AppearanceService(paths).Load()
                        .HiddenRoutes.Length == 0 && VersionButton.IsVisible,
                    "recovery restores and persists hidden routes"
                );
                await NavigateClassicAsync("setup-ui");
                await Task.Delay(250);
                classicPage.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => Equals(x.Content, "清空图片")
                    )
                    .RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                await Save();
                Check(
                    classicTitleImage.Source is null && !classicTitleImage.IsVisible,
                    "clear title image releases rendered source"
                );
                Input<TextBox>("TitleImage")
                    .Text = Path.Combine(paths.Evidence, "local-launch.png");
                await Save();
            }

            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：准备 var backgroundFolder = Path.Combine(paths.Root, "backgrounds")；执行 Directory.CreateDirectory(backgroundFolder)；执行 File.Copy( Path.Combine(paths.Evidence, "local-launch.png"), Path.Combine(backgroundFolder, "fi…。
                var backgroundFolder = Path.Combine(paths.Root, "backgrounds");
                Directory.CreateDirectory(backgroundFolder);
                File.Copy(
                    Path.Combine(paths.Evidence, "local-launch.png"),
                    Path.Combine(backgroundFolder, "first.png"),
                    true
                );
                File.Copy(
                    Path.Combine(paths.Evidence, "local-launch.png"),
                    Path.Combine(backgroundFolder, "second.png"),
                    true
                );
                Input<TextBox>("BackgroundDirectory")
                    .Text = backgroundFolder;
                await Save();
                var first = displayedBackground;
                classicPage.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => Equals(x.Content, "刷新背景图片")
                    )
                    .RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                await Task.Delay(200);
                Check(
                    displayedBackground != first && classicBackground.Background is ImageBrush,
                    "refresh background scans and switches actual image"
                );
                Input<TextBox>("BackgroundDirectory")
                    .Text = "";
                await Save();
            }

            await Capture("local-appearance");
            await NavigateClassicAsync("setup-system");
            await Task.Delay(200);
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：执行 Input<ComboBox>("ManifestSource") .SelectedIndex = 1；执行 Input<TextBox>("ManifestMirror") .Text = "https://metadata.example.test/manifest.json"；执行 Input<ComboBox>("ResourceSource") .SelectedIndex = 2。
                Input<ComboBox>("ManifestSource")
                    .SelectedIndex = 1;
                Input<TextBox>("ManifestMirror")
                    .Text = "https://metadata.example.test/manifest.json";
                Input<ComboBox>("ResourceSource")
                    .SelectedIndex = 2;
                Input<TextBox>("ResourceApiMirror")
                    .Text = "https://metadata.example.test/v2";
                Input<ComboBox>("FileNameStyle")
                    .SelectedIndex = 1;
                Input<ComboBox>("ModNameStyle")
                    .SelectedIndex = 1;
                Input<CheckBox>("DisableLocalCopy")
                    .IsChecked = true;
                Input<CheckBox>("DebugMode")
                    .IsChecked = true;
                Input<NumericUpDown>("ArtificialDelayMs")
                    .Value = 50;
                await Save();
            }

            var options = DownloadTasks.For(paths)
                .Options;
            Check(
                options.ManifestSource == "fallback" && options.ResourceSource == "mirror" && options.FileNameStyle == "brackets" && options.ModNameStyle == "file" && options.DisableLocalCopy && options.DebugMode && options.ArtificialDelayMs == 50,
                "source naming and debug controls persist and reopen"
            );
            if (!reopen)
            {
                // [实现] 条件成立（!reopen）时：准备 var taskWindow = new TaskCenterWindow( paths, RunAsync, Open, () => operation?.Cancel() )；执行 taskWindow.Show(this)；执行 await Task.Delay(250)。
                var taskWindow = new TaskCenterWindow(
                    paths,
                    RunAsync,
                    Open,
                    () => operation?.Cancel()
                );
                taskWindow.Show(this);
                await Task.Delay(250);
                taskWindow.GetVisualDescendants()
                    .OfType<TabControl>()
                    .First()
                    .SelectedIndex = 2;
                await Task.Delay(250);
                taskWindow.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => Equals(x.Content, "保存网络设置")
                    )
                    .RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                Check(
                    DownloadTasks.For(paths)
                        .Options == options,
                    "auxiliary network save preserves new source naming and debug fields"
                );
                taskWindow.Close();
            }

            if (!reopen && Program.Arguments.Contains("--acceptance-release-notice"))
            {
                // 临时使用官方清单实测通知，随后恢复本轮来源设置；不携带账户令牌。
                DownloadTasks.For(paths)
                    .Configure(
                        options with
                        {
                            ManifestSource = "official",
                            NotifyRelease = true,
                            NotifySnapshot = true
                        }
                    );
                await CheckReleaseNotificationsAsync();
                Check(
                    releaseNotice.IsVisible && releaseNotice.Text!.StartsWith("可下载新版本："),
                    "live release and snapshot notification shown"
                );
                DownloadTasks.For(paths)
                    .Configure(options);
                await CheckReleaseNotificationsAsync();
                Check(!releaseNotice.IsVisible, "disabling notifications hides notice without new request");
            }

            await Capture("local-system");
            JsonFiles.Write(
                Path.Combine(
                    paths.Evidence,
                    reopen
                        ? "local-settings-reopen.json"
                        : "local-settings-ui.json"
                ),
                new
                {
                    status = "passed",
                    checks
                }
            );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine( paths.Evidence, reopen ? "local-settings-reopen.json" : "local-s…。
            JsonFiles.Write(
                Path.Combine(
                    paths.Evidence,
                    reopen
                        ? "local-settings-reopen.json"
                        : "local-settings-ui.json"
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
