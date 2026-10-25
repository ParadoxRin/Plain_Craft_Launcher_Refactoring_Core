using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Pcl.Core;

namespace Pcl.Desktop;
// [实现] 离线桌面验收：在隔离目录通过实际账户和启动控件测试离线身份、游戏进程与存档恢复。
public partial class MainWindow
{
    /// <summary>只在带标记的隔离目录运行；通过真实控件创建、切换并启动离线账户，不注入微软凭据。</summary>
    private async Task OfflineLaunchSmokeAsync()
    {
        var reentry = Program.Arguments.Contains("--offline-reentry");
        var uiOnly = Program.Arguments.Contains("--offline-ui-only");
        var phase = uiOnly
            ? "ui"
            : reentry
                ? "reentry"
                : "first";
        var evidence = Path.Combine(paths.Evidence, "offline-desktop-" + phase + ".json");
        try
        {
            // [实现] 在异常保护范围内：按 !File.Exists( Path.Combine(paths.Root, ".offline-acceptance") ) || accounts is null || accounts… 分支处理；准备 var timeout = new CancellationTokenSource( TimeSpan.FromMinutes(12) )；准备 var ct = timeout.Token。
            if (!File.Exists(
                Path.Combine(paths.Root, ".offline-acceptance")
            ) || accounts is null || accounts.Accounts.Any(
                a => a.Kind != AccountKind.Offline
            ))
            {
                // [实现] 条件成立（!File.Exists( Path.Combine(paths.Root, ".offline-acceptance") ) || accounts is null || accounts…）时：抛出 new InvalidOperationException("离线验收需要标记过的隔离目录，且不得含微软或外置账户。")。
                throw new InvalidOperationException("离线验收需要标记过的隔离目录，且不得含微软或外置账户。");
            }

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(12)
            );
            var ct = timeout.Token;
            var selected = fabric.Profiles()
                .Single(
                    p => p.Loader == "vanilla" && p.GameVersion == "1.21.1"
                );
            RefreshProfiles(selected.Id);
            await RefreshInstalledAsync();
            if (AccountTypeBox.SelectedIndex != 0)
            {
                // [实现] 条件成立（AccountTypeBox.SelectedIndex != 0）时：抛出 new InvalidOperationException("首次或记住离线账户后应显示离线模式。")。
                throw new InvalidOperationException("首次或记住离线账户后应显示离线模式。");
            }

            if (reentry)
            {
                // [实现] 条件成立（reentry）时：按 OfflineNameBox.Text != "PCL_Offline" || (AccountBox.SelectedItem as LauncherAccount)?.Name != "… 分支处理。
                if (OfflineNameBox.Text != "PCL_Offline" || (AccountBox.SelectedItem as LauncherAccount)?.Name != "PCL_Offline")
                {
                    // [实现] 条件成立（OfflineNameBox.Text != "PCL_Offline" || (AccountBox.SelectedItem as LauncherAccount)?.Name != "…）时：抛出 new InvalidOperationException("应用重开未恢复离线账户。")。
                    throw new InvalidOperationException("应用重开未恢复离线账户。");
                }
            }
            else
            {
                // 空名称必须禁用启动；通过真实登录按钮保存两条名称并检查账户切换。
                OfflineNameBox.Text = "";
                await Task.Delay(150, ct);
                if (LaunchButton.IsEnabled)
                {
                    // [实现] 条件成立（LaunchButton.IsEnabled）时：抛出 new InvalidOperationException("空离线名称不应可启动。")。
                    throw new InvalidOperationException("空离线名称不应可启动。");
                }

                foreach (var name in new[]
                {
                    "PCL_UI_First",
                    "PCL_UI_Second"
                }

                )
                {
                    // [实现] 对 new[] { "PCL_UI_First", "PCL_UI_Second" } 中的每一项：执行 OfflineNameBox.Text = name；执行 await Task.Delay(150, ct)；执行 LoginButton.RaiseEvent( new RoutedEventArgs(Button.ClickEvent) )。
                    OfflineNameBox.Text = name;
                    await Task.Delay(150, ct);
                    LoginButton.RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                    while (operation is not null)
                    {
                        // [实现] 循环满足 operation is not null 时：执行 await Task.Delay(100, ct)。
                        await Task.Delay(100, ct);
                    }
                }

                AccountBox.SelectedItem = accounts.Accounts.Single(
                    a => a.Name == "PCL_UI_First"
                );
                if (OfflineNameBox.Text != "PCL_UI_First")
                {
                    // [实现] 条件成立（OfflineNameBox.Text != "PCL_UI_First"）时：抛出 new InvalidOperationException("离线账户切换未恢复名称。")。
                    throw new InvalidOperationException("离线账户切换未恢复名称。");
                }

                AccountTypeBox.SelectedIndex = 1;
                await Task.Delay(150, ct);
                if (LaunchButton.IsEnabled)
                {
                    // [实现] 条件成立（LaunchButton.IsEnabled）时：抛出 new InvalidOperationException("未登录的微软模式不应可启动。")。
                    throw new InvalidOperationException("未登录的微软模式不应可启动。");
                }

                AccountTypeBox.SelectedIndex = 0;
                // 使用未保存的新名称直接点启动，检查无需先点“使用此名称”的实际路径。
                OfflineNameBox.Text = "PCL_Offline";
            }

            // Avalonia 的 TextChanged/布局通过调度器投递，等待真实控件完成更新再断言。
            await Task.Delay(200, ct);
            if (AccountText.Text != "离线账户 · PCL_Offline" || (!reentry && RemoveAccountButton.IsEnabled))
            {
                // [实现] 条件成立（AccountText.Text != "离线账户 · PCL_Offline" || (!reentry && RemoveAccountButton.IsEnabled)）时：抛出 new InvalidOperationException("离线名称展示或删除目标与实际启动身份不一致。")。
                throw new InvalidOperationException("离线名称展示或删除目标与实际启动身份不一致。");
            }

            new LauncherSettingsStore(paths).SavePatch(
                selected.GameDirectory,
                new LaunchSettingsPatch
                {
                    QuickPlayWorld = "PCL019_OfflineWorld"
                }
            );
            if (!installed || !LaunchButton.IsEnabled)
            {
                // [实现] 条件成立（!installed || !LaunchButton.IsEnabled）时：抛出 new InvalidOperationException("已安装实例与有效离线名称应可启动。")。
                throw new InvalidOperationException("已安装实例与有效离线名称应可启动。");
            }

            using (var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Bounds.Width, (int)Bounds.Height),
                new Vector(96, 96)
            ))
            {
                // [实现] 本段流程：执行 bitmap.Render(this)；执行 bitmap.Save( Path.Combine(paths.Evidence, "offline-desktop-" + phase + ".png"), PngBitmapEncode…。
                bitmap.Render(this);
                bitmap.Save(
                    Path.Combine(paths.Evidence, "offline-desktop-" + phase + ".png"),
                    PngBitmapEncoderOptions.Default
                );
            }

            if (uiOnly)
            {
                // [实现] 条件成立（uiOnly）时：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "offline-ui.json"), new { status = "passed", crea…；结束当前过程。
                JsonFiles.Write(
                    Path.Combine(paths.Evidence, "offline-ui.json"),
                    new
                    {
                        status = "passed",
                        createAndSwitch = true,
                        invalidNameDisabled = true,
                        microsoftModeRequiresCredential = true,
                        typedIdentityDisplayed = true,
                        unrelatedAccountRemovalDisabled = true,
                        directOfflineLaunchEnabled = true
                    }
                );
                return;
            }

            LaunchButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            while (game is null && operation is not null)
            {
                // [实现] 循环满足 game is null && operation is not null 时：执行 await Task.Delay(100, ct)。
                await Task.Delay(100, ct);
            }

            var running = game ?? throw new InvalidOperationException("离线界面启动失败：" + StatusText.Text);
            Console.WriteLine("OFFLINE_GUI_GAME_STARTED " + phase);
            await Task.WhenAny(
                running.Completion,
                Task.Delay(
                    TimeSpan.FromMinutes(10),
                    ct
                )
            );
            var stoppedByHarness = !running.Completion.IsCompleted;
            if (stoppedByHarness)
            {
                // [实现] 条件成立（stoppedByHarness）时：执行 gameStoppedByUser = true；执行 running.Stop()。
                gameStoppedByUser = true;
                await running.StopAsync();
            }

            var exit = await running.Completion;
            while (operation is not null)
            {
                // [实现] 循环满足 operation is not null 时：执行 await Task.Delay(100, ct)。
                await Task.Delay(100, ct);
            }

            var account = accounts.Accounts.Single(
                a => a.Id == accounts.SelectedId
            );
            var passed = exit == 0 && !stoppedByHarness && account.Kind == AccountKind.Offline && account.Name == "PCL_Offline";
            JsonFiles.Write(
                evidence,
                new
                {
                    status = passed
                        ? "passed"
                        : "failed",
                    accountKind = "offline",
                    microsoftOwnershipClaimed = false,
                    launchViaButton = true,
                    restoredAccountOnReentry = reentry,
                    accountSwitchingChecked = !reentry,
                    normalGameExit = exit == 0 && !stoppedByHarness,
                    exitCode = exit,
                    stoppedByHarness,
                    gameLog = running.LogFile,
                    worldVisualAcceptance = "pending"
                }
            );
            Environment.ExitCode = passed
                ? 0
                : 1;
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( evidence, new { status = "failed", errorType = ex.GetType() .Name } )；执行 Console.Error.WriteLine( "OFFLINE_GUI_FAILED " + ex.GetType() .Name )；按 game is not null 分支处理。
            JsonFiles.Write(
                evidence,
                new
                {
                    status = "failed",
                    errorType = ex.GetType()
                        .Name
                }
            );
            Console.Error.WriteLine(
                "OFFLINE_GUI_FAILED " + ex.GetType()
                    .Name
            );
            if (game is not null)
            {
                // [实现] 条件成立（game is not null）时：执行 gameStoppedByUser = true；执行 game.Stop()；执行 await game.Completion。
                gameStoppedByUser = true;
                await game.StopAsync();
                await game.Completion;
            }

            Environment.ExitCode = 1;
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 Close()。
            Close();
        }
    }
}
