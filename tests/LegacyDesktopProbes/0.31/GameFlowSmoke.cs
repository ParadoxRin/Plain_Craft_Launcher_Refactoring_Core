using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Pcl.Core;

namespace Pcl.Desktop;
// 桌面验收：在隔离数据目录驱动实际界面流程，校验结果并写入证据；测试动作不等同人工视觉或游戏验收。
public partial class MainWindow
{
    // [实现] GameFlowSmokeAsync 的实现：执行受异常处理保护的操作。
    private async Task GameFlowSmokeAsync()
    {
        try
        {
            // [实现] 在异常保护范围内：准备 var timeout = new CancellationTokenSource( TimeSpan.FromMinutes(45) )；执行 async Task Wait(Func<bool> condition) { while (!condition()) await Task.Delay(200, timeout.Toke…；准备 var begin = DateTimeOffset.UtcNow。
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(45)
            );
            // [实现] Wait 的实现：在 !condition() 条件下循环。
            async Task Wait(Func<bool> condition)
            {
                while (!condition())
                    await Task.Delay(200, timeout.Token);
            }

            var begin = DateTimeOffset.UtcNow;
            InstallButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Wait(
                () => operation is null
            );
            if (!installed)
                throw new InvalidOperationException("GUI 安装失败：" + StatusText.Text);
            AccountTypeBox.SelectedIndex = 0;
            OfflineNameBox.Text = "PCLMacVerify";
            LoginButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Wait(
                () => operation is null
            );
            if (!LaunchButton.IsEnabled)
                throw new InvalidOperationException("离线启动按钮不可用。");
            LaunchButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Wait(
                () => game is not null || operation is null
            );
            var running = game ?? throw new InvalidOperationException("GUI 启动失败：" + StatusText.Text);
            Console.WriteLine("GUI_GAME_STARTED " + running.Pid);
            await Task.Delay(400, timeout.Token);
            if (!StopGameButton.IsVisible)
                throw new InvalidOperationException("游戏进程管理入口未显示。");
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Bounds.Width, (int)Bounds.Height),
                new Vector(96, 96)
            );
            bitmap.Render(this);
            bitmap.Save(
                Path.Combine(paths.Evidence, "gui-install-launch.png"),
                PngBitmapEncoderOptions.Default
            );
            var seconds = Program.Arguments.Contains("--manual-world")
                ? 180
                : 45;
            await Task.WhenAny(
                running.Completion,
                Task.Delay(
                    TimeSpan.FromSeconds(seconds),
                    timeout.Token
                )
            );
            var alive = !running.HasExited;
            var log = File.ReadAllText(running.LogFile);
            var graphics = log.Contains("Backend library") || log.Contains("OpenGL") || log.Contains("LWJGL Version:");
            var atlas = log.Contains("atlas");
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
            var passed = graphics && atlas && (alive || exit == 0);
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "gui-install-launch.json"),
                new
                {
                    status = passed
                        ? "passed"
                        : "failed",
                    installationViaButton = true,
                    accountAddedViaButton = true,
                    launchViaButton = true,
                    offline = true,
                    demo = false,
                    graphics,
                    atlas,
                    aliveAtDeadline = alive,
                    observationSeconds = seconds,
                    stoppedByTest = alive,
                    exitCode = exit,
                    worldPlay = "requires_human_confirmation",
                    started = begin,
                    completed = DateTimeOffset.UtcNow,
                    log = running.LogFile
                }
            );
            if (!passed)
                throw new InvalidOperationException("游戏图形初始化未通过。");
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "gui-install-launch-error.json"), new { status = …；执行 Console.Error.WriteLine(ex)。
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "gui-install-launch-error.json"),
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
