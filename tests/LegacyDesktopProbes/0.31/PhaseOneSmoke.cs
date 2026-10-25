using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Pcl.Core;

namespace Pcl.Desktop;
// 本轮真实 GUI 接线测试：点击已有安装按钮，等待真实结果或在安装中关闭窗口。
public partial class MainWindow
{
    private async Task PhaseOneSmokeAsync()
    {
        var closeDialog = Program.Arguments.Contains("--phase-one-close-dialog");
        var versionManager = Program.Arguments.Contains("--phase-one-version-manager");
        var cancelOnClose = Program.Arguments.Contains("--phase-one-close-cancel");
        var observations = new List<object>();
        string? failure = null;
        var resultPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts/architecture-phase-one",
            closeDialog
                ? "gui-dialog-close.json"
                : cancelOnClose
                    ? "gui-cancel.json"
                    : versionManager
                        ? "gui-version-manager.json"
                        : "gui-install.json"
        );
        void Save(string status, string? error = null)
        {
            if (status == "failed")
            {
                failure ??= error ?? "功能运行失败";
            }

            File.WriteAllText(
                resultPath,
                JsonSerializer.Serialize(
                    new
                    {
                        status = failure is null
                            ? status
                            : "failed",
                        error = failure,
                        observations
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }

        Closed += (_, _) =>
        {
            try
            {
                // 结果文件在数据目录外；根锁释放后不继续向被管理目录写测试数据。
                var events = File.ReadAllLines(
                    Path.Combine(paths.Evidence, "phase-one-lifecycle.jsonl")
                )
                    .Select(
                        line => JsonDocument.Parse(line)
                            .RootElement.GetProperty("name")
                            .GetString()
                    )
                    .ToArray();
                if (events.Count(
                    x => x == "minecraft.disposed"
                ) != 1 || events.Count(
                    x => x == "root-lock.releasing"
                ) != 1)
                {
                    throw new InvalidOperationException("共享资源或锁释放次数错误。");
                }

                var history = new InstallOperations(paths).Read();
                observations.Add(
                    new
                    {
                        closed = true,
                        viewReleased,
                        uiCompleted = PendingUiAction.IsCompleted,
                        events,
                        history
                    }
                );
                if (!viewReleased || !PendingUiAction.IsCompleted)
                {
                    throw new InvalidOperationException("窗口在 UI 收尾前关闭。");
                }

                if (cancelOnClose && (lastInstallationResult?.Error?.Code != "CANCELLED" || !history.Any(
                    x => x.State == "已取消"
                )))
                {
                    throw new InvalidOperationException("关闭安装窗口未得到真实取消结果。");
                }

                if (closeDialog && (history.Count != 0 || lastInstallationResult is not null))
                {
                    throw new InvalidOperationException("退出选择对话框不应触发安装。");
                }

                Save("passed");
            }
            catch (Exception ex)
            {
                Save(
                    "failed",
                    ex.ToString()
                );
                Environment.ExitCode = 1;
            }
        };
        try
        {
            if (installed && !versionManager && !closeDialog)
            {
                throw new InvalidOperationException("隔离测试目录必须让现有按钮进入新安装分支，而非修复分支。");
            }

            observations.Add(
                new
                {
                    entry = versionManager
                        ? "VersionButton.Click -> dialog.Install.Click"
                        : closeDialog
                            ? "VersionButton.Click -> application.TryShutdown"
                            : "InstallButton.Click",
                    SelectedGameVersion,
                    cancelOnClose
                }
            );
            ((versionManager || closeDialog)
                ? VersionButton
                : InstallButton).RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            if (closeDialog)
            {
                var until = DateTime.UtcNow.AddSeconds(45);
                while (!OwnedWindows.Any() && DateTime.UtcNow < until)
                {
                    await Task.Delay(50);
                }

                if (!OwnedWindows.Any())
                {
                    throw new InvalidOperationException("版本选择对话框未打开。");
                }

                observations.Add(
                    new
                    {
                        closeWithOwnedDialog = true
                    }
                );
                Save("running");
                ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!).TryShutdown();
                return;
            }

            if (cancelOnClose)
            {
                if (operation is null)
                {
                    throw new InvalidOperationException("安装任务尚未开始。");
                }

                observations.Add(
                    new
                    {
                        closeDuringInstall = true
                    }
                );
                Save("running");
                Close();
                return;
            }

            await PendingUiAction.WaitAsync(
                TimeSpan.FromMinutes(8)
            );
            var result = lastInstallationResult;
            if (result?.Instance is null || result.Error is not null || result.Instance.GameVersion != "1.21.1")
            {
                throw new InvalidOperationException("真实安装结果不符合请求：" + result?.Error?.Code + " version=" + result?.Instance?.GameVersion);
            }

            var history = new InstallOperations(paths).Read();
            if (!history.Any(
                x => x.Version == SelectedGameVersion && x.State == "已完成"
            ))
            {
                throw new InvalidOperationException("原安装历史未保留。");
            }

            observations.Add(
                new
                {
                    result.Instance,
                    history,
                    installed,
                    status = StatusText.Text
                }
            );
            Save("running");
            Close();
        }
        catch (Exception ex)
        {
            Save(
                "failed",
                ex.ToString()
            );
            Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
            // 保留失败结果，常规退出仍走生命周期协调，不强行释放活动依赖。
            await RequestApplicationExitAsync();
        }
    }
}
