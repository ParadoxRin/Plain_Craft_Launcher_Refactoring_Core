using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Pcl.Core;

namespace Pcl.Desktop;
// 显式开发标记：真实安装按钮接入 v3 操作，再测试关闭应用时 runner/存储/共享服务的单一释放。
public partial class MainWindow
{
    private async Task PhaseThreeSmokeAsync()
    {
        if (Program.Arguments.Contains("--phase-three-resume"))
        {
            await PhaseThreeResumeSmokeAsync();
            return;
        }

        var cancel = Program.Arguments.Contains("--phase-three-cancel");
        var report = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts/architecture-phase-three",
            cancel
                ? "gui-cancel.json"
                : "gui-install.json"
        );
        try
        {
            var operations = installation.Operations ?? throw new InvalidOperationException("未注入 v3 操作入口");
            if (installed || accounts!.Accounts.Count != 0)
            {
                throw new InvalidOperationException("测试必须使用未发布安装的隔离目录");
            }

            InstallButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            var until = DateTime.UtcNow.AddMinutes(3);
            Pcl.Core.Contracts.Operations.OperationSnapshot? snapshot = null;
            while (DateTime.UtcNow < until)
            {
                snapshot = (await operations.ListAsync()).FirstOrDefault();
                if (snapshot is not null && (snapshot.Stage == "planned" || snapshot.Terminal))
                {
                    break;
                }

                await Task.Delay(20);
            }

            if (snapshot is null)
            {
                throw new InvalidOperationException("按钮未受理操作");
            }

            if (cancel)
            {
                if (snapshot.Terminal)
                {
                    throw new InvalidOperationException("取消测试未赶上运行阶段");
                }

                // 显式退出采用应用已有关闭流程，不能仅把关窗 token 当作取消后的停止证明。
                await RequestApplicationExitAsync();
            }
            else
            {
                await PendingUiAction.WaitAsync(
                    TimeSpan.FromMinutes(5)
                );
                var finished = await operations.GetAsync(snapshot.OperationId);
                if (finished?.State != "succeeded" || lastInstallationResult?.Instance is null)
                {
                    throw new InvalidOperationException("按钮安装未成功");
                }

                // 实例已安装后打开真实任务中心，确认操作页能初始化且关窗不改变操作终态。
                var tasks = new TaskCenterWindow(paths, RunAsync, Open, operations: operations);
                tasks.Show(this);
                await Task.Delay(300);
                tasks.Close();
                await RequestApplicationExitAsync();
            }

            var final = await operations.GetAsync(snapshot.OperationId);
            var events = File.ReadAllLines(
                Path.Combine(paths.Evidence, "phase-one-lifecycle.jsonl")
            )
                .Select(
                    x => JsonDocument.Parse(x)
                        .RootElement.GetProperty("name")
                        .GetString()
                )
                .ToArray();
            bool released = events.Count(
                x => x == "operation-store.disposed"
            ) == 1 && events.Count(
                x => x == "minecraft.disposed"
            ) == 1 && Array.IndexOf(events, "operation-store.disposed") < Array.IndexOf(events, "root-lock.releasing");
            if (!released || final?.State != (cancel
                ? "cancelled"
                : "succeeded"))
            {
                throw new InvalidOperationException("退出状态或资源释放顺序错误");
            }

            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        entry = "InstallButton.Click",
                        operationId = snapshot.OperationId,
                        final.State,
                        final.Sequence,
                        released,
                        uiCompleted = PendingUiAction.IsCompleted,
                        taskWindowOpened = !cancel
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
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
            await RequestApplicationExitAsync();
        }
    }

    // 从上一轮取消记录重开真实应用；点任务中心续做，关闭该视图，确认后台任务继续并发布成功。
    private async Task PhaseThreeResumeSmokeAsync()
    {
        var report = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts/architecture-phase-three/gui-resume.json"
        );
        try
        {
            var operations = installation.Operations!;
            var parent = (await operations.ListAsync()).First(
                x => x.State == "cancelled"
            );
            DownloadTasks.For(paths)
                .Configure(
                    new NetworkOptions
                    {
                        DisableLocalCopy = true
                    }
                );
            var tasks = new TaskCenterWindow(paths, RunAsync, Open, operations: operations);
            tasks.Show(this);
            await Task.Delay(300);
            var list = tasks.GetLogicalDescendants()
                .OfType<ListBox>()
                .Distinct()
                .Single(
                    x => x.Name == "OperationList"
                );
            list.SelectedItem = list.ItemsSource!.Cast<Pcl.Core.Contracts.Operations.OperationSnapshot>()
                .Single(
                    x => x.OperationId == parent.OperationId
                );
            tasks.GetLogicalDescendants()
                .OfType<Button>()
                .Distinct()
                .Single(
                    x => x.Name == "OperationResume"
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            var until = DateTime.UtcNow.AddSeconds(10);
            Pcl.Core.Contracts.Operations.OperationSnapshot? resumed = null;
            while (resumed is null && DateTime.UtcNow < until)
            {
                resumed = (await operations.ListAsync()).FirstOrDefault(
                    x => x.ResumeOfOperationId == parent.OperationId
                );
                await Task.Delay(20);
            }

            if (resumed is null)
            {
                throw new InvalidOperationException("任务中心续做按钮未受理操作");
            }

            tasks.Close();
            await PendingUiAction.WaitAsync(
                TimeSpan.FromMinutes(5)
            );
            var final = await operations.GetAsync(resumed.OperationId);
            if (final?.State != "succeeded" || (await operations.GetAsync(parent.OperationId))!.State != "cancelled" || !installed)
            {
                throw new InvalidOperationException("视图关闭取消了操作，或续做未完成");
            }

            await RequestApplicationExitAsync();
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        entry = "TaskCenter.OperationResume.Click",
                        parent = parent.OperationId,
                        resumed = resumed.OperationId,
                        viewClosedBeforeCompletion = true,
                        parentTerminalPreserved = true,
                        final.State,
                        installed,
                        uiCompleted = PendingUiAction.IsCompleted
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
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
            await RequestApplicationExitAsync();
        }
    }
}
