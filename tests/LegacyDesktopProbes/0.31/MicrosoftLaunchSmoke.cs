using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Pcl.Core;

namespace Pcl.Desktop;
// [实现] 微软正版桌面验收：从钥匙串恢复真实账户，通过实际启动按钮检查游戏初始化与退出，并记录不含令牌的证据。
public partial class MainWindow
{
    /// <summary>从钥匙串恢复真实微软账户，经实际启动按钮验证正版启动；证据只记录状态，不写令牌。</summary>
    private async Task MicrosoftLaunchSmokeAsync()
    {
        var evidence = Path.Combine(paths.Evidence, "microsoft-desktop-launch-0.18.json");
        var started = DateTimeOffset.UtcNow;
        try
        {
            // [实现] 在异常保护范围内：准备 var timeout = new CancellationTokenSource( TimeSpan.FromMinutes(12) )；准备 var ct = timeout.Token；准备 var account = accounts?.Accounts.SingleOrDefault( item => item.Id == accounts.SelectedId && ite…。
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(12)
            );
            var ct = timeout.Token;
            var account = accounts?.Accounts.SingleOrDefault(
                item => item.Id == accounts.SelectedId && item.Kind == AccountKind.Microsoft && item.Remembered
            ) ?? throw new InvalidOperationException("请先完成微软登录并记住账户，再进行桌面启动验收。");
            var profile = fabric.Profiles()
                .Single(
                    item => item.Loader == "vanilla" && item.GameVersion == "1.21.1"
                );
            RefreshProfiles(profile.Id);
            RefreshAccounts(account.Id);
            await RefreshInstalledAsync();
            if (!installed || !LaunchButton.IsEnabled)
            {
                // [实现] 条件成立（!installed || !LaunchButton.IsEnabled）时：抛出 new InvalidOperationException("正版启动验收需要已安装的 Minecraft 1.21.1。")。
                throw new InvalidOperationException("正版启动验收需要已安装的 Minecraft 1.21.1。");
            }

            // 触发产品自身事件，内部 ResolveAsync 必须重新交换令牌、检查权益与档案，验收不注入会话。
            LaunchButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            while (game is null && operation is not null)
            {
                // [实现] 循环满足 game is null && operation is not null 时：执行 await Task.Delay(200, ct)。
                await Task.Delay(200, ct);
            }

            var running = game ?? throw new InvalidOperationException("微软启动失败：" + StatusText.Text);
            Console.WriteLine("MICROSOFT_GUI_GAME_STARTED");
            // 从真实游戏日志确认图形与材质加载完成；不将启动命令或账户身份写入公开验收结果。
            var graphics = false;
            var atlas = false;
            for (var attempt = 0; attempt < 120 && !running.Completion.IsCompleted; attempt++)
            {
                // [实现] 每次迭代（attempt < 120 && !running.Completion.IsCompleted）：执行 await Task.Delay(1000, ct)；准备 var log = File.Exists(running.LogFile) ? await File.ReadAllTextAsync(running.LogFile, ct) : ""；执行 graphics = log.Contains("Backend library") || log.Contains("OpenGL") || log.Contains("LWJGL Ver…。
                await Task.Delay(1000, ct);
                var log = File.Exists(running.LogFile)
                    ? await File.ReadAllTextAsync(running.LogFile, ct)
                    : "";
                graphics = log.Contains("Backend library") || log.Contains("OpenGL") || log.Contains("LWJGL Version:");
                atlas = log.Contains("Created:") && log.Contains("atlas");
                if (graphics && atlas)
                {
                    // [实现] 条件成立（graphics && atlas）时：执行 break;。
                    break;
                }
            }

            if (!graphics || !atlas || running.Completion.IsCompleted)
            {
                // [实现] 条件成立（!graphics || !atlas || running.Completion.IsCompleted）时：抛出 new InvalidOperationException("游戏未完成图形与材质初始化，请检查本地游戏日志。")。
                throw new InvalidOperationException("游戏未完成图形与材质初始化，请检查本地游戏日志。");
            }

            using (var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Bounds.Width, (int)Bounds.Height),
                new Vector(96, 96)
            ))
            {
                // [实现] 本段流程：执行 bitmap.Render(this)；执行 bitmap.Save( Path.Combine(paths.Evidence, "microsoft-desktop-launch-0.18.png"), PngBitmapEncode…。
                bitmap.Render(this);
                bitmap.Save(
                    Path.Combine(paths.Evidence, "microsoft-desktop-launch-0.18.png"),
                    PngBitmapEncoderOptions.Default
                );
            }

            JsonFiles.Write(
                evidence,
                new
                {
                    status = "game_running",
                    keychainRestore = true,
                    launchViaButton = true,
                    graphics,
                    atlas,
                    started
                }
            );
            Console.WriteLine("MICROSOFT_GUI_GRAPHICS_READY");
            // 人工验收留出十分钟正常保存退出；自动复测只观察三十秒，再使用应用自身的结束入口。
            var manual = Program.Arguments.Contains("--manual-world");
            await Task.WhenAny(
                running.Completion,
                Task.Delay(
                    TimeSpan.FromSeconds(
                        manual
                            ? 600
                            : 30
                    ),
                    ct
                )
            );
            ct.ThrowIfCancellationRequested();
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

            var passed = manual
                ? !stoppedByHarness && exit == 0
                : stoppedByHarness || exit == 0;
            JsonFiles.Write(
                evidence,
                new
                {
                    status = passed
                        ? "passed"
                        : "failed",
                    keychainRestore = true,
                    launchViaButton = true,
                    javaOwnershipRequired = true,
                    gameVersion = profile.GameVersion,
                    graphics,
                    atlas,
                    stoppedByHarness,
                    normalGameExit = !stoppedByHarness && exit == 0,
                    exitCode = exit,
                    manualWorldVerification = "requires_user_confirmation",
                    started,
                    completed = DateTimeOffset.UtcNow
                }
            );
            Environment.ExitCode = passed
                ? 0
                : 1;
            Console.WriteLine(
                passed
                    ? "MICROSOFT_DESKTOP_ACCEPTANCE_PASSED"
                    : "MICROSOFT_DESKTOP_ACCEPTANCE_FAILED"
            );
        }
        catch (Exception ex)
        {
            // 仅保留异常类别；接口错误原文或进程参数不进入可分享的证据文件。
            JsonFiles.Write(
                evidence,
                new
                {
                    status = "failed",
                    errorType = ex.GetType()
                        .Name,
                    started
                }
            );
            Console.Error.WriteLine(
                "MICROSOFT_DESKTOP_ACCEPTANCE_FAILED " + ex.GetType()
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
