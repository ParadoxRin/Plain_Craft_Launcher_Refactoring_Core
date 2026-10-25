using System.Text.Json;
using Pcl.Core;

namespace Pcl.Desktop;
// 只在显式开发测试标记下启动隔离实例；游戏进入世界与真人视觉验收不在本轮范围。
public partial class MainWindow
{
    private bool phaseTwoSmokeStarted;
    private async Task PhaseTwoSmokeAsync()
    {
        using var auth = new MicrosoftAuthentication();
        var detached = Program.Arguments.Contains("--phase-two-detached");
        var report = Path.Combine(
            Directory.GetCurrentDirectory(),
            Program.Arguments.Contains("--phase-three-next")
                ? "artifacts/architecture-phase-three-next"
                : "artifacts/architecture-phase-two",
            detached
                ? "gui-detached.json"
                : "gui-managed.json"
        );
        try
        {
            if (accounts is null || accounts.Accounts.Any(
                x => x.Kind != AccountKind.Offline
            ))
            {
                throw new InvalidOperationException("游戏测试只允许隔离离线账户。");
            }

            var account = accounts.AddOffline("StageTwoMac");
            accounts.Select(account.Id);
            RefreshAccounts(account.Id);
            var settings = appearance with
            {
                LauncherVisibility = detached
                    ? "close"
                    : "hide-show"
            };
            new AppearanceService(paths).Save(settings);
            ApplyAppearance(settings);
            var session = await accounts.ResolveAsync(account.Id, auth);
            Task launch;
            if (Program.Arguments.Contains("--phase-three-next-primary"))
            {
                AccountTypeBox.SelectedIndex = 0;
                OfflineNameBox.Text = account.Name;
                LaunchButton.RaiseEvent(
                    new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)
                );
                launch = PendingUiAction;
            }
            else
            {
                launch = LaunchAsync(session);
            }

            if (detached)
            {
                await launch;
                File.WriteAllText(
                    report,
                    JsonSerializer.Serialize(
                        new
                        {
                            status = "started",
                            mode = "close",
                            note = "外部验证器在启动器退出后继续检查游戏日志。"
                        }
                    )
                );
                return;
            }

            var until = DateTime.UtcNow.AddMinutes(3);
            while (game is null && !launch.IsCompleted && DateTime.UtcNow < until)
            {
                await Task.Delay(100);
            }

            var running = game ?? throw new InvalidOperationException("未创建托管游戏。");
            Close();
            bool hidden = !IsVisible;
            bool scopeKept = !File.ReadAllText(
                Path.Combine(paths.Evidence, "phase-one-lifecycle.jsonl")
            )
                .Contains("scope.stopping");
            while (!running.HasExited && DateTime.UtcNow < until)
            {
                var text = File.Exists(running.LogFile)
                    ? File.ReadAllText(running.LogFile)
                    : "";
                if (text.Contains("Created:") && text.Contains("atlas"))
                {
                    break;
                }

                await Task.Delay(250);
            }

            var logs = File.ReadAllText(running.LogFile);
            bool rendered = logs.Contains("Created:") && logs.Contains("atlas");
            if (!hidden || !scopeKept || !rendered || running.HasExited)
            {
                throw new InvalidOperationException("隐藏策略、进程或图形资源初始化未通过。");
            }

            gameStoppedByUser = true;
            await running.StopAsync();
            await launch;
            bool restored = IsVisible && game is null;
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = restored
                            ? "passed"
                            : "failed",
                        rendered,
                        hidden,
                        scopeKept,
                        restored,
                        exitCode = await running.Completion,
                        log = running.LogFile
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
            if (!restored)
            {
                Environment.ExitCode = 1;
            }

            Close();
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        error = DiagnosticsService.Redact(ex.Message)
                    }
                )
            );
            Environment.ExitCode = 1;
            if (game is not null && !game.HasExited)
            {
                await game.StopAsync();
            }

            await RequestApplicationExitAsync();
        }
    }
}
