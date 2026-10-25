using Avalonia.Controls;
using Pcl.Core;

namespace Pcl.Desktop;
// [实现] 启动器窗口验收：在隔离目录启动真实游戏，验证退出、隐藏和最小化策略。。
public partial class MainWindow
{
    // 隔离目录中以真实游戏进程检查五种窗口策略。结束测试只停止本次创建的游戏。
    private async Task VisibilitySmokeAsync()
    {
        var index = Array.IndexOf(Program.Arguments, "--visibility-mode");
        var mode = index >= 0
            ? Program.Arguments[index + 1]
            : "keep";
        var file = Path.Combine(paths.Evidence, "visibility-" + mode + ".json");
        var observed = new List<string>();
        try
        {
            // [实现] 在异常保护范围内：按 !File.Exists( Path.Combine(paths.Root, ".visibility-acceptance") ) || accounts is null || accou… 分支处理；准备 var selected = fabric.Profiles() .Single( x => x.Loader == "vanilla" && x.GameVersion == "1.21.…；执行 RefreshProfiles(selected.Id)。
            if (!File.Exists(
                Path.Combine(paths.Root, ".visibility-acceptance")
            ) || accounts is null || accounts.Accounts.Any(
                x => x.Kind != AccountKind.Offline
            ))
            {
                // [实现] 条件成立（!File.Exists( Path.Combine(paths.Root, ".visibility-acceptance") ) || accounts is null || accou…）时：抛出 new InvalidOperationException("窗口验收需要隔离目录及离线测试账户。")。
                throw new InvalidOperationException("窗口验收需要隔离目录及离线测试账户。");
            }

            var selected = fabric.Profiles()
                .Single(
                    x => x.Loader == "vanilla" && x.GameVersion == "1.21.1"
                );
            RefreshProfiles(selected.Id);
            var account = accounts.AddOffline("PCL_Visibility");
            accounts.Select(account.Id);
            ApplyAppearance(
                appearance with
                {
                    LauncherVisibility = mode
                }
            );
            Closed += (_, _) =>
            {
                // [实现] 回调触发时：按 mode == "close" 分支处理。
                if (mode == "close")
                {
                    // [实现] 条件成立（mode == "close"）时：准备 var result = JsonFiles.Read( Path.Combine(paths.Evidence, "detached-launch.json") )；准备 var child = System.Diagnostics.Process.GetProcessById((int)result["pid"]!)；执行 JsonFiles.Write( file, new { status = child.HasExited ? "failed" : "passed", mode, pid = child.…。
                    var result = JsonFiles.Read(
                        Path.Combine(paths.Evidence, "detached-launch.json")
                    );
                    using var child = System.Diagnostics.Process.GetProcessById((int)result["pid"]!);
                    JsonFiles.Write(
                        file,
                        new
                        {
                            status = child.HasExited
                                ? "failed"
                                : "passed",
                            mode,
                            pid = child.Id,
                            checks = new[]
                            {
                                "launcher closed while detached game alive"
                            }
                        }
                    );
                }
                else if (mode == "hide-close")
                {
                    // [实现] 条件成立（mode == "hide-close"）时：执行 JsonFiles.Write( file, new { status = game is null && observed.Count > 0 ? "passed" : "failed",…。
                    JsonFiles.Write(
                        file,
                        new
                        {
                            status = game is null && observed.Count > 0
                                ? "passed"
                                : "failed",
                            mode,
                            checks = observed.Append("launcher closed after game exit")
                                .ToArray()
                        }
                    );
                }
            };
            var running = RunGameAsync(
                AccountManager.OfflineSession(account.Name),
                default
            );
            if (mode == "close")
            {
                // [实现] 条件成立（mode == "close"）时：执行 await running；结束当前过程。
                await running;
                return;
            }

            for (var i = 0; i < 1200 && game is null && !running.IsCompleted; i++)
            {
                // [实现] 每次迭代（i < 1200 && game is null && !running.IsCompleted）：执行 await Task.Delay(100)。
                await Task.Delay(100);
            }

            if (game is null)
            {
                // [实现] 条件成立（game is null）时：执行 await running；抛出 new IOException("游戏未启动。")。
                await running;
                throw new IOException("游戏未启动。");
            }

            var valid = mode switch
            {
                "hide-show" or "hide-close" => !IsVisible,
                "minimize" => WindowState == WindowState.Minimized,
                _ => IsVisible && WindowState != WindowState.Minimized
            };
            if (!valid)
            {
                // [实现] 条件成立（!valid）时：抛出 new Exception("启动器窗口策略未应用")。
                throw new Exception("启动器窗口策略未应用");
            }

            observed.Add("actual launcher state: " + mode);
            var launched = game;
            await Task.Delay(20000);
            var log = File.ReadAllText(launched.LogFile);
            if (!log.Contains("gui.png-atlas"))
            {
                // [实现] 条件成立（!log.Contains("gui.png-atlas")）时：抛出 new Exception("游戏尚未完成 GUI 纹理加载")。
                throw new Exception("游戏尚未完成 GUI 纹理加载");
            }

            observed.Add("actual Minecraft GUI textures loaded");
            if (!running.IsCompleted)
            {
                // [实现] 条件成立（!running.IsCompleted）时：执行 launched.Stop()。
                await launched.StopAsync();
            }

            await running;
            if (mode == "hide-close")
            {
                // [实现] 条件成立（mode == "hide-close"）时：结束当前过程。
                return;
            }

            if (mode == "hide-show" && !IsVisible)
            {
                // [实现] 条件成立（mode == "hide-show" && !IsVisible）时：抛出 new Exception("游戏结束后未恢复窗口")。
                throw new Exception("游戏结束后未恢复窗口");
            }

            observed.Add("game exited; launcher state preserved/restored");
            JsonFiles.Write(
                file,
                new
                {
                    status = "passed",
                    mode,
                    checks = observed
                }
            );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 game?.Stop()；执行 JsonFiles.Write( file, new { status = "failed", mode, checks = observed, error = ex.ToString() …。
            if (game is not null)
            {
                await game.StopAsync();
            }

            JsonFiles.Write(
                file,
                new
                {
                    status = "failed",
                    mode,
                    checks = observed,
                    error = ex.ToString()
                }
            );
        }

        if (mode != "hide-close")
        {
            // [实现] 条件成立（mode != "hide-close"）时：执行 Close()。
            Close();
        }
    }
}
