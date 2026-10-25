using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core;

namespace Pcl.Desktop;
// 桌面验收：在隔离数据目录驱动实际界面流程，校验结果并写入证据；测试动作不等同人工视觉或游戏验收。
public partial class MainWindow
{
    // [实现] WorkspaceSmokeAsync 的实现：执行 async Task Capture(Window window, string name, Func<Window, Task>? exercise = null) { Exception…；准备 var profile = fabric.Profiles() .First()；执行 await Capture( Bootstrap.WorkspacePages.Instances(this,service, profile.Id), "instance-workspace", async window =…。
    private async Task WorkspaceSmokeAsync()
    {
        // [实现] Capture 的实现：准备 Exception? failure = null；执行 window.Opened += async (_, _) => { try { await Task.Delay(700); if (exercise is not null) await…；执行 await window.ShowDialog(this)。
        async Task Capture(Window window, string name, Func<Window, Task>? exercise = null)
        {
            Exception? failure = null;
            window.Opened += async (_, _) =>
            {
                // [实现] 回调触发时：执行受异常处理保护的操作。
                try
                {
                    // [实现] 在异常保护范围内：执行 await Task.Delay(700)；按 exercise is not null 分支处理；执行 await Task.Delay(300)。
                    await Task.Delay(700);
                    if (exercise is not null)
                        await exercise(window);
                    await Task.Delay(300);
                    using var bitmap = new RenderTargetBitmap(
                        new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                        new Vector(96, 96)
                    );
                    bitmap.Render(window);
                    bitmap.Save(
                        Path.Combine(paths.Evidence, name + ".png"),
                        PngBitmapEncoderOptions.Default
                    );
                    Console.WriteLine("WORKSPACE_UI " + name);
                }
                catch (Exception ex)
                {
                    // [实现] 捕获 (Exception ex) 后：执行 failure = ex。
                    failure = ex;
                }
                finally
                {
                    // [实现] 无论成功或失败，退出前：执行 window.Close()。
                    window.Close();
                }
            };
            await window.ShowDialog(this);
            if (failure is not null)
                throw failure;
        }

        var profile = fabric.Profiles()
            .First();
        await Capture(
            Bootstrap.WorkspacePages.Instances(this, service, profile.Id),
            "instance-workspace",
            async window =>
            {
                // [实现] 回调触发时：准备 var name = window.GetVisualDescendants() .OfType<TextBox>() .FirstOrDefault( x => x.Placeholder…；执行 name.Text = "GUI 设置验证"；准备 var save = window.GetVisualDescendants() .OfType<Button>() .First( x => x.Content?.ToString() =…。
                var name = window.GetVisualDescendants()
                    .OfType<TextBox>()
                    .FirstOrDefault(
                        x => x.PlaceholderText == "名称"
                    ) ?? throw new InvalidOperationException("实例名称输入框缺失");
                name.Text = "GUI 设置验证";
                var save = window.GetVisualDescendants()
                    .OfType<Button>()
                    .First(
                        x => x.Content?.ToString() == "保存"
                    );
                save.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
                await Task.Delay(200);
                if (new LauncherSettingsStore(paths).Preferences(profile.GameDirectory)
                    .Name != "GUI 设置验证")
                    throw new InvalidOperationException("GUI 修改没有持久化");
            }
        );
        await Capture(
            Bootstrap.WorkspacePages.Pack(
                this,
                service,
                profile,
                _ =>
                {
                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                }
            ),
            "pack-workspace"
        );
        await Capture(
            Bootstrap.WorkspacePages.Local(this, service, profile),
            "local-resources"
        );
        await Capture(
            new TaskCenterWindow(paths, RunAsync, Open),
            "task-center",
            async window =>
            {
                // [实现] 回调触发时：准备 var tabs = window.GetVisualDescendants() .OfType<TabControl>() .Single()；执行 tabs.SelectedIndex = 2；执行 await Task.Delay(150)。
                var tabs = window.GetVisualDescendants()
                    .OfType<TabControl>()
                    .Single();
                tabs.SelectedIndex = 2;
                await Task.Delay(150);
                var checkbox = window.GetVisualDescendants()
                    .OfType<CheckBox>()
                    .Single(
                        x => x.Content?.ToString()?.StartsWith("大文件分段下载") == true
                    );
                checkbox.IsChecked = true;
                var save = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => x.Content?.ToString() == "保存网络设置"
                    );
                save.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
                if (!NetworkOptions.Load(paths)
                    .SegmentedDownloads)
                    throw new InvalidOperationException("网络设置未从GUI持久化");
                tabs.SelectedIndex = 1;
                await Task.Delay(100);
                var pause = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Single(
                        x => x.Content?.ToString() == "暂停下载"
                    );
                pause.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
                if (!DownloadTasks.For(paths)
                    .IsPaused)
                    throw new InvalidOperationException("暂停操作没有生效");
                pause.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
                if (DownloadTasks.For(paths)
                    .IsPaused)
                    throw new InvalidOperationException("继续操作没有生效");
                tabs.SelectedIndex = 2;
            }
        );
        await Capture(
            Bootstrap.WorkspacePages.Accounts(this, accounts!),
            "external-accounts"
        );
        await Capture(
            Bootstrap.WorkspacePages.Multiplayer(this, service),
            "multiplayer"
        );
        await Capture(
            Bootstrap.WorkspacePages.Cache(this, paths),
            "network-cache"
        );
        await Capture(
            Bootstrap.WorkspacePages.Skins(this, accounts!),
            "skin-manager"
        );
        await Capture(
            new ArtifactDownloadWindow(
                Bootstrap.LegacyPageAdapters.Artifacts(service, remote),
                profile.GameVersion
            ),
            "artifact-download"
        );
        await Capture(
            Bootstrap.WorkspacePages.Curse(
                this,
                service,
                profile,
                _ =>
                {
                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                },
                Open
            ),
            "curseforge"
        );
        await Capture(
            Bootstrap.WorkspacePages.Settings(this, service, ApplyAppearance, Open),
            "settings-light"
        );
        ApplyAppearance(
            new()
            {
                Theme = "dark"
            }
        );
        await Capture(
            Bootstrap.WorkspacePages.Settings(this, service, ApplyAppearance, Open),
            "settings-dark"
        );
        using var home = new RenderTargetBitmap(
            new PixelSize((int)Bounds.Width, (int)Bounds.Height),
            new Vector(96, 96)
        );
        home.Render(this);
        home.Save(
            Path.Combine(paths.Evidence, "home-dark.png"),
            PngBitmapEncoderOptions.Default
        );
        JsonFiles.Write(
            Path.Combine(paths.Evidence, "workspace-ui-result.json"),
            new
            {
                status = "passed",
                dialogs = 12,
                instanceNameEditedViaButtonAndReloaded = true,
                segmentedSettingSavedViaButton = true,
                pauseResumeViaButton = true,
                actualNetworkLogin = false,
                multiplayerConnected = false
            }
        );
    }
}
