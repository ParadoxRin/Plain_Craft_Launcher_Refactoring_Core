using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Desktop;
/// <summary>隔离目录的真实 Avalonia 事件测试：验证 Host 根锁、旧写入接口移除、安装入口及窗口/游戏生命周期。</summary>
public partial class MainWindow
{
    private bool remoteSmokeStarted;
    private async Task RemoteDesktopSmokeAsync()
    {
        if (remoteSmokeStarted)
        {
            return;
        }

        remoteSmokeStarted = true;
        var destination = Program.Arguments[Array.IndexOf(Program.Arguments, "--remote-report") + 1];
        var detached = Program.Arguments.Contains("--remote-detached");
        try
        {
            if (remote is null || accounts!.Accounts.Any(
                x => x.Kind != AccountKind.Offline
            ))
            {
                throw new InvalidOperationException("REMOTE_ISOLATION_REQUIRED");
            }

            var hostPid = remote.Client.HostPid;
            if (hostPid == Environment.ProcessId)
            {
                throw new Exception("HOST_NOT_INDEPENDENT");
            }

            bool rootLocked = false, admissionBlocked = false;
            try
            {
                using var lease = new FileStream(
                    Path.Combine(paths.Root, ".launcher.lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
            }
            catch (IOException)
            {
                rootLocked = true;
            }

            // Contract-only desktop no longer holds a writer grant. The former API must be unavailable.
            try
            {
                await remote.Client.Call<System.Text.Json.JsonElement>(
                    "desktop.acquire",
                    new { }
                );
            }
            catch (OperationRejectedException)
            {
                admissionBlocked = true;
            }

            if (!rootLocked || !admissionBlocked)
            {
                throw new Exception("WRITER_EXCLUSION_FAILED");
            }

            if (Program.Arguments.Contains("--remote-cancel"))
            {
                RefreshProfiles("1.21.1");
                await DesktopApi.CallAsync<SettingsSnapshot>(
                    "workspace.settings.save",
                    null,
                    new
                    {
                        changes = new
                        {
                            Network = new NetworkOptions
                            {
                                ArtificialDelayMs = 1000,
                                DisableLocalCopy = true
                            }
                        }
                    }
                );
                // 仅从隔离副本移除一个官方 asset，确保取消命令发生在真实下载尚未完成时。
                var index = JsonDocument.Parse(
                    File.ReadAllText(
                        Directory.GetFiles(
                            Path.Combine(paths.Game, "assets", "indexes"),
                            "*.json"
                        )
                            .First()
                    )
                );
                var hash = index.RootElement.GetProperty("objects")
                    .EnumerateObject()
                    .First()
                    .Value.GetProperty("hash")
                    .GetString()!;
                var asset = Path.Combine(paths.Game, "assets", "objects", hash[..2], hash);
                File.Delete(asset);
                var old = (await installation.Operations!.ListAsync()).Select(
                    x => x.OperationId
                )
                    .ToHashSet();
                InstallButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
                Pcl.Core.Contracts.Operations.OperationSnapshot? row = null;
                var cancelDeadline = DateTime.UtcNow.AddMinutes(2);
                while (DateTime.UtcNow < cancelDeadline)
                {
                    row = (await installation.Operations.ListAsync()).FirstOrDefault(
                        x => !old.Contains(x.OperationId)
                    );
                    if (row?.Stage == "planned" && !row.Terminal)
                    {
                        break;
                    }

                    await Task.Delay(10);
                }

                if (row is null || row.Terminal)
                {
                    throw new Exception("MISSED_CANCEL_WINDOW");
                }

                // 真实关窗路径显式取消安装，等待 UI 收尾和 Host 退出。
                var id = row.OperationId;
                await RequestApplicationExitAsync();
                var receipt = Directory.GetFiles(
                    Path.Combine(paths.Root, "operations-v3"),
                    id + ".json"
                )
                    .Single();
                var final = JsonDocument.Parse(
                    File.ReadAllText(receipt)
                )
                    .RootElement.GetProperty("Snapshot")
                    .GetProperty("State")
                    .GetString();
                File.WriteAllText(
                    destination,
                    JsonSerializer.Serialize(
                        new
                        {
                            status = final == "cancelled"
                                ? "passed"
                                : "failed",
                            operationId = id,
                            state = final,
                            entry = "InstallButton then window close",
                            uiCompleted = PendingUiAction.IsCompleted,
                            hostPid
                        }
                    )
                );
                return;
            }

            if (Program.Arguments.Contains("--remote-resume"))
            {
                // 重开应用查询前次快照，再点任务中心的真实续做按钮；关任务视图不取消工作。
                await DesktopApi.CallAsync<SettingsSnapshot>(
                    "workspace.settings.save",
                    null,
                    new
                    {
                        changes = new
                        {
                            Network = new NetworkOptions()
                        }
                    }
                );
                var beforeResume = await installation.Operations!.ListAsync();
                var existingIds = beforeResume.Select(
                    x => x.OperationId
                )
                    .ToHashSet();
                var parent = beforeResume.OrderByDescending(
                    x => x.Updated
                )
                    .First(
                        x => x.State == "cancelled"
                    );
                var window = new TaskCenterWindow(service, RunAsync, Open, operations: installation.Operations);
                window.Show(this);
                await Task.Delay(300);
                var controls = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window)
                    .Distinct()
                    .ToArray();
                var list = controls.OfType<ListBox>()
                    .Single(
                        x => x.Name == "OperationList"
                    );
                list.SelectedItem = list.ItemsSource!.Cast<Pcl.Core.Contracts.Operations.OperationSnapshot>()
                    .Single(
                        x => x.OperationId == parent.OperationId
                    );
                controls.OfType<Button>()
                    .Single(
                        x => x.Name == "OperationResume"
                    )
                    .RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent)
                    );
                await Task.Delay(150);
                window.Close();
                await PendingUiAction;
                var child = (await installation.Operations.ListAsync()).Single(
                    x => x.ResumeOfOperationId == parent.OperationId && !existingIds.Contains(x.OperationId)
                );
                if (child.State != "succeeded" || !remote.IsAvailable)
                {
                    throw new Exception("RESUME_FAILED:" + child.ErrorCode);
                }

                File.WriteAllText(
                    destination,
                    JsonSerializer.Serialize(
                        new
                        {
                            status = "passed",
                            parent = parent.OperationId,
                            resumed = child.OperationId,
                            viewClosedBeforeCompletion = true,
                            hostPid
                        }
                    )
                );
                await RequestApplicationExitAsync();
                return;
            }

            // 从真实按钮发起安装/修复，并用这次新增的 Host 收据证明完成，不能借用历史成功状态。
            RefreshProfiles("1.21.1");
            var beforeInstall = (await installation.Operations!.ListAsync()).Select(
                x => x.OperationId
            )
                .ToHashSet();
            var beforePages = (await remote.PageTasks.ListAsync()).Select(
                x => x.OperationId
            )
                .ToHashSet();
            InstallButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await PendingUiAction;
            var rows = await installation.Operations!.ListAsync();
            var newRepair = (await remote.PageTasks.ListAsync()).Any(
                x => !beforePages.Contains(x.OperationId) && x.Command.Method == "workspace.instances.repair" && x.State == "succeeded"
            );
            var newInstall = rows.Any(
                x => !beforeInstall.Contains(x.OperationId) && x.State == "succeeded"
            );
            if (!remote.IsAvailable || !(newRepair || newInstall))
            {
                throw new Exception("REMOTE_INSTALL_FAILED:" + StatusText.Text);
            }

            var account = await AddOfflineAccountAsync("RemoteHostMac", CancellationToken.None);
            RefreshAccounts(account.Id);
            AccountTypeBox.SelectedIndex = 0;
            OfflineNameBox.Text = account.Name;
            var settings = appearance with
            {
                LauncherVisibility = detached
                    ? "close"
                    : "hide-show"
            };
            await DesktopApi.CallAsync<SettingsSnapshot>(
                "workspace.settings.save",
                null,
                new
                {
                    changes = new
                    {
                        Appearance = settings
                    }
                }
            );
            ApplyAppearance(settings);
            LaunchButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            var launch = PendingUiAction;
            if (detached)
            {
                await launch;
                File.WriteAllText(
                    destination,
                    JsonSerializer.Serialize(
                        new
                        {
                            status = "started",
                            hostPid,
                            rootLocked,
                            admissionBlocked,
                            remoteInstall = true,
                            mode = "close"
                        }
                    )
                );
                return;
            }

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (game is null && !launch.IsCompleted && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            var running = game ?? throw new Exception("REMOTE_GAME_MISSING:" + StatusText.Text);
            Close();
            bool hidden = !IsVisible;
            while (!running.HasExited && DateTime.UtcNow < deadline)
            {
                var log = File.Exists(running.LogFile)
                    ? File.ReadAllText(running.LogFile)
                    : "";
                if (log.Contains("Created:") && log.Contains("atlas"))
                {
                    break;
                }

                await Task.Delay(250);
            }

            var rendered = File.ReadAllText(running.LogFile)
                .Contains("atlas");
            if (!hidden || !rendered || !remote.IsAvailable || remote.Client.Attachment?.LegacyGrant is not null || LaunchButton.IsEnabled || ProfileBox.IsEnabled)
            {
                throw new Exception("REMOTE_MANAGED_STATE_INVALID");
            }

            gameStoppedByUser = true;
            await running.StopAsync();
            await launch;
            var restored = IsVisible && game is null && remote.IsAvailable;
            File.WriteAllText(
                destination,
                JsonSerializer.Serialize(
                    new
                    {
                        status = restored
                            ? "passed"
                            : "failed",
                        hostPid,
                        rootLocked,
                        admissionBlocked,
                        remoteInstall = true,
                        hidden,
                        rendered,
                        restored,
                        exitCode = await running.Completion
                    }
                )
            );
            if (!restored)
            {
                Environment.ExitCode = 1;
            }

            await RequestApplicationExitAsync();
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                destination,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        error = Pcl.Client.WorkspaceErrors.Describe(ex)
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
