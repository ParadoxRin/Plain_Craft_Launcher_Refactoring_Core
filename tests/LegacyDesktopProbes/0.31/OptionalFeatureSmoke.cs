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
    // Exercises real desktop controls against an explicitly supplied isolated acceptance root.
    private async Task OptionalFeatureSmokeAsync()
    {
        try
        {
            // [实现] 在异常保护范围内：准备 var timeout = new CancellationTokenSource( TimeSpan.FromMinutes(20) )；准备 var ct = timeout.Token；准备 var argument = Array.IndexOf(Program.Arguments, "--acceptance-pack")。
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(20)
            );
            var ct = timeout.Token;
            var argument = Array.IndexOf(Program.Arguments, "--acceptance-pack");
            if (argument < 0 || argument + 1 >= Program.Arguments.Length)
                throw new ArgumentException("Missing acceptance pack");
            var archive = Path.GetFullPath(Program.Arguments[argument + 1]);
            var plan = PortableModpackService.Inspect(
                archive,
                new(IncludeOptional: true)
            );
            var selected = plan.OptionalFiles.Single(
                x => x.Id.Contains("fabric-api")
            )
                .Id;
            var excluded = plan.OptionalFiles.Single(
                x => x.Id.Contains("lithium")
            )
                .Id;
            // [实现] Wait 的实现：在 !condition() 条件下循环。
            async Task Wait(Func<bool> condition)
            {
                while (!condition())
                    await Task.Delay(150, ct);
            }

            // [实现] Button 的实现：计算并返回 w.GetVisualDescendants() .OfType<Button>() .Single( x => x.Content?.ToString() == text )。
            static Button Button(Window w, string text) => w.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == text
                );
            // [实现] Named 的实现：计算并返回 w.GetVisualDescendants() .OfType<T>() .Single( x => x.Name == name )。
            static T Named<T>(Window w, string name)
            where T : Control => w.GetVisualDescendants()
                .OfType<T>()
                .Single(
                    x => x.Name == name
                );
            // [实现] Click 的实现：执行 Button(w, text) .RaiseEvent( new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent) )。
            void Click(Window w, string text) => Button(w, text)
                .RaiseEvent(
                    new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)
                );
            // [实现] Capture 的实现：准备 var image = new RenderTargetBitmap( new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height), n…；执行 image.Render(w)；执行 image.Save( Path.Combine(paths.Evidence, name + ".png"), PngBitmapEncoderOptions.Default )。
            void Capture(Window w, string name)
            {
                using var image = new RenderTargetBitmap(
                    new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height),
                    new Vector(96, 96)
                );
                image.Render(w);
                image.Save(
                    Path.Combine(paths.Evidence, name + ".png"),
                    PngBitmapEncoderOptions.Default
                );
            }

            // [实现] Dialog 的实现：准备 Exception? failure = null；执行 w.Opened += async (_, _) => { try { await Task.Delay(400, ct); await action(); } catch (Excepti…；执行 await w.ShowDialog(this)。
            async Task Dialog(Window w, Func<Task> action)
            {
                Exception? failure = null;
                w.Opened += async (_, _) =>
                {
                    // [实现] 回调触发时：执行受异常处理保护的操作。
                    try
                    {
                        // [实现] 在异常保护范围内：执行 await Task.Delay(400, ct)；执行 await action()。
                        await Task.Delay(400, ct);
                        await action();
                    }
                    catch (Exception ex)
                    {
                        // [实现] 捕获 (Exception ex) 后：执行 failure = ex。
                        failure = ex;
                    }
                    finally
                    {
                        // [实现] 无论成功或失败，退出前：执行 w.Close()。
                        w.Close();
                    }
                };
                await w.ShowDialog(this);
                if (failure is not null)
                    throw failure;
            }

            var chooser = new PackSelectionWindow(
                Bootstrap.WorkspacePages.DescribePlan(plan)
            );
            string[]? choiceResult = null;
            Exception? choiceFailure = null;
            chooser.Opened += async (_, _) =>
            {
                // [实现] 回调触发时：执行受异常处理保护的操作。
                try
                {
                    // [实现] 在异常保护范围内：执行 await Task.Delay(400, ct)；执行 Click(chooser, "全选")；执行 Click(chooser, "全不选")。
                    await Task.Delay(400, ct);
                    Click(chooser, "全选");
                    Click(chooser, "全不选");
                    var box = chooser.GetVisualDescendants()
                        .OfType<CheckBox>()
                        .Single(
                            x => x.Tag?.ToString() == selected
                        );
                    box.IsChecked = true;
                    await Task.Delay(150, ct);
                    Capture(chooser, "optional-dialog");
                    Click(chooser, "按此选择安装");
                }
                catch (Exception ex)
                {
                    // [实现] 捕获 (Exception ex) 后：执行 choiceFailure = ex；执行 chooser.Close()。
                    choiceFailure = ex;
                    chooser.Close();
                }
            };
            choiceResult = await chooser.ShowDialog<string[]?>(this);
            if (choiceFailure is not null)
                throw choiceFailure;
            if (choiceResult is null || !choiceResult.SequenceEqual(
                new[]
                {
                    selected
                }
            ))
                throw new InvalidOperationException("Optional dialog did not return chosen file");
            var baseProfile = fabric.Profiles()
                .First();
            GameProfile? installedPack = null;
            var manager = Bootstrap.WorkspacePages.Pack(
                this,
                service,
                baseProfile,
                result => installedPack = result
            );
            await manager.SelectArchiveAsync(archive);
            await Dialog(
                manager,
                async () =>
                {
                    // [实现] 回调触发时：准备 var check = manager.GetVisualDescendants() .OfType<CheckBox>() .Single( x => x.Tag?.ToString() …；执行 check.IsChecked = true；执行 Click(manager, "安装为新实例")。
                    var check = manager.GetVisualDescendants()
                        .OfType<CheckBox>()
                        .Single(
                            x => x.Tag?.ToString() == selected
                        );
                    check.IsChecked = true;
                    Click(manager, "安装为新实例");
                    var button = Button(manager, "安装为新实例");
                    await Wait(
                        () => button.IsEnabled
                    );
                    if (installedPack is null)
                        throw new InvalidOperationException(
                            "Pack GUI installation failed: " + string.Join(
                                " | ",
                                manager.GetVisualDescendants()
                                    .OfType<TextBlock>()
                                    .Select(
                                        x => x.Text
                                    )
                            )
                        );
                    Capture(manager, "optional-installed");
                }
            );
            var profile = installedPack!;
            if (!File.Exists(
                Path.Combine(profile.GameDirectory, selected)
            ) || File.Exists(
                Path.Combine(profile.GameDirectory, excluded)
            ))
                throw new InvalidOperationException("Selected/excluded files mismatch");
            var store = new LauncherSettingsStore(paths);
            var workspace = Bootstrap.WorkspacePages.Instances(this, service, profile.Id);
            await Dialog(
                workspace,
                async () =>
                {
                    // [实现] 回调触发时：执行 Click(workspace, "启动设置")；执行 await Task.Delay(150, ct)；执行 Named<CheckBox>(workspace, "Override_MaximumMemoryMb") .IsChecked = true。
                    Click(workspace, "启动设置");
                    await Task.Delay(150, ct);
                    Named<CheckBox>(workspace, "Override_MaximumMemoryMb")
                        .IsChecked = true;
                    Named<TextBox>(workspace, "Launch_MaximumMemoryMb")
                        .Text = "3072";
                    Named<CheckBox>(workspace, "Override_Fullscreen")
                        .IsChecked = true;
                    Named<CheckBox>(workspace, "Launch_Fullscreen")
                        .IsChecked = false;
                    Click(workspace, "保存启动设置");
                    if (store.Patch(profile.GameDirectory)?.Fields.Count != 2)
                        throw new InvalidOperationException("GUI saved more than chosen fields");
                    Click(workspace, "全局启动设置");
                    Named<TextBox>(workspace, "Launch_Width")
                        .Text = "960";
                    Named<TextBox>(workspace, "Launch_Height")
                        .Text = "540";
                    Named<TextBox>(workspace, "Launch_MaximumMemoryMb")
                        .Text = "4096";
                    Click(workspace, "保存启动设置");
                    await Task.Delay(150, ct);
                    Capture(workspace, "global-settings");
                }
            );
            var reopened = Bootstrap.WorkspacePages.Instances(this, service, profile.Id);
            await Dialog(
                reopened,
                async () =>
                {
                    // [实现] 回调触发时：执行 Click(reopened, "启动设置")；执行 await Task.Delay(150, ct)；按 Named<TextBox>(reopened, "Launch_Width") .Text != "960" || Named<TextBox>(reopened, "Launch_Wid… 分支处理。
                    Click(reopened, "启动设置");
                    await Task.Delay(150, ct);
                    if (Named<TextBox>(reopened, "Launch_Width")
                        .Text != "960" || Named<TextBox>(reopened, "Launch_Width")
                        .IsEnabled || Named<TextBox>(reopened, "Launch_MaximumMemoryMb")
                        .Text != "3072")
                        throw new InvalidOperationException("Reopened GUI did not inherit only unselected values");
                    Capture(reopened, "instance-inheritance");
                }
            );
            var effective = store.Effective(profile.GameDirectory);
            var instanceManager = new InstanceManager(service);
            var backup = Path.Combine(paths.Evidence, "optional-instance-backup.zip");
            await instanceManager.BackupAsync(profile, backup, ct);
            var restored = await instanceManager.RestoreBackupAsync(backup, ct: ct);
            if (store.Patch(restored.GameDirectory)?.Fields.Count != 2 || store.Effective(restored.GameDirectory)
                .Width != 960 || File.ReadAllText(
                Path.Combine(restored.GameDirectory, "config/pcl-acceptance.txt")
            ) != "optional selection acceptance\n")
                throw new InvalidOperationException("Backup lost partial inheritance or config");
            var second = await new ModpackService(service).InstallAsync(
                archive,
                ct: ct,
                selectedOptionalFiles: []
            );
            if (second.GameDirectory == profile.GameDirectory || (Directory.Exists(
                Path.Combine(second.GameDirectory, "mods")
            ) && Directory.GetFiles(
                Path.Combine(second.GameDirectory, "mods"),
                "*.jar"
            )
                .Length != 0) || !File.Exists(
                Path.Combine(profile.GameDirectory, selected)
            ))
                throw new InvalidOperationException("Different selection overwrote original or installed unchosen file");
            RefreshProfiles(restored.Id);
            AccountTypeBox.SelectedIndex = 0;
            OfflineNameBox.Text = "PCLMacVerify";
            LoginButton.RaiseEvent(
                new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)
            );
            await Wait(
                () => operation is null
            );
            var installedVersion = InstanceRegistry.Load(service, restored);
            var java = await store.ResolveJavaAsync(service, installedVersion, restored.GameDirectory, ct: ct);
            var launchPlan = new LaunchService(service).CreatePlan(
                installedVersion,
                java,
                AccountManager.OfflineSession("PCLMacVerify"),
                gameDirectory: restored.GameDirectory
            );
            if (!launchPlan.Arguments.Contains("-Xmx3072M") || !launchPlan.Arguments.Contains("960") || !launchPlan.Arguments.Contains("540"))
                throw new InvalidOperationException(
                    "Effective settings missing from launch arguments: " + string.Join(" ", launchPlan.Arguments)
                );
            LaunchButton.RaiseEvent(
                new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)
            );
            await Wait(
                () => game is not null || operation is null
            );
            var running = game ?? throw new InvalidOperationException("GUI launch failed: " + StatusText.Text);
            Console.WriteLine("OPTIONAL_GAME_STARTED " + running.Pid);
            await Task.WhenAny(
                running.Completion,
                Task.Delay(
                    TimeSpan.FromSeconds(45),
                    ct
                )
            );
            var alive = !running.HasExited;
            var log = File.ReadAllText(running.LogFile);
            var graphics = log.Contains("Backend library") || log.Contains("OpenGL");
            var atlas = log.Contains("atlas");
            var loaded = log.Contains("fabric-api 0.116.17");
            if (alive)
            {
                // [实现] 条件成立（alive）时：执行 gameStoppedByUser = true；执行 running.Stop()。
                gameStoppedByUser = true;
                await running.StopAsync();
            }

            var exit = await running.Completion;
            await Wait(
                () => operation is null
            );
            if (!alive || !graphics || !atlas || !loaded)
                throw new InvalidOperationException("Restored optional pack graphics initialization failed: " + running.LogFile);
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "optional-features-result.json"),
                new
                {
                    status = "passed",
                    guiOptionalDialog = true,
                    guiPackInstall = true,
                    selected,
                    excluded,
                    original = profile.GameDirectory,
                    emptySelectionInstance = second.GameDirectory,
                    restored = restored.GameDirectory,
                    guiPerFieldSettings = true,
                    reopenedInheritance = true,
                    effective,
                    backupRestoreRetainsPatch = true,
                    launchViaButton = true,
                    javaMajor = java.MajorVersion,
                    architecture = java.Architecture,
                    graphics,
                    atlas,
                    fabricApiLoaded = loaded,
                    observationSeconds = 45,
                    aliveAtDeadline = alive,
                    stoppedByTest = true,
                    exitCode = exit,
                    log = running.LogFile,
                    worldAcceptance = "not performed in 0.9",
                    completed = DateTimeOffset.UtcNow
                }
            );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "optional-features-result.json"), new { status = …；执行 Console.Error.WriteLine(ex)。
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "optional-features-result.json"),
                new
                {
                    status = "failed",
                    error = ex.ToString()
                }
            );
            Console.Error.WriteLine(ex);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 game is not null && !game.HasExited 分支处理；执行 Close()。
            if (game is not null && !game.HasExited)
            {
                // [实现] 条件成立（game is not null && !game.HasExited）时：执行 gameStoppedByUser = true；执行 game.Stop()；执行 await game.Completion。
                gameStoppedByUser = true;
                await game.StopAsync();
                await game.Completion;
            }

            Close();
        }
    }
}
