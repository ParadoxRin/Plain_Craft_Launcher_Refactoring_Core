using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pcl.Core;
using Pcl.Core.Contracts.Pages;
using Pcl.Desktop.Bootstrap;

namespace Pcl.Desktop;
/// <summary>隔离数据目录内执行迁移页面的真实适配流程；保留下载文件和新副本作为证据。</summary>
public partial class MainWindow
{
    private async Task PageBoundarySmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--page-report") + 1];
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }

            checks.Add(name);
        }

        try
        {
            if (!CanWriteLegacy || accounts!.Accounts.Any(
                x => x.Kind != AccountKind.Offline
            ))
            {
                throw new Exception("ISOLATED_OFFLINE_ROOT_REQUIRED");
            }

            // 从真实主窗口入口打开页面，避免仅测试工厂而遗漏事件处理器的接线。
            DownloadFilesClick(
                null,
                new RoutedEventArgs()
            );
            await Task.Delay(200);
            var routed = OwnedWindows.OfType<ArtifactDownloadWindow>()
                .Single();
            var buttons = routed.GetVisualDescendants()
                .OfType<Button>()
                .ToArray();
            var taskButton = buttons.Single(
                x => x.Content?.ToString() == "后台任务"
            );
            Check(taskButton is not null, "main-navigation-opens-host-backed-artifact-page");
            var refreshButton = buttons.Single(
                x => x.Content?.ToString() == "读取文件列表"
            );
            refreshButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!refreshButton.IsEnabled && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Check(
                routed.GetVisualDescendants()
                    .OfType<ListBox>()
                    .Single()
                    .ItemCount > 0,
                "artifact-page-refresh-button-uses-host-catalog"
            );
            taskButton!.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Task.Delay(300);
            var taskWindow = routed.OwnedWindows.OfType<PageTasksWindow>()
                .Single();
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (taskWindow.GetVisualDescendants()
                .OfType<ListBox>()
                .Single()
                .ItemCount == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Check(
                taskWindow.GetVisualDescendants()
                    .OfType<ListBox>()
                    .Single()
                    .ItemCount > 0,
                "page-task-window-loads-host-history"
            );
            taskWindow.Close();
            routed.Close();
            var profile = InstanceRegistry.List(service)
                .First(
                    x => x.Id == "1.21.1"
                );
            var components = LegacyPageAdapters.Components(service, profile, remote);
            Check(components is IPageTaskSource, "component-execution-uses-host");
            var versions = await components.VersionsAsync("vanilla", CancellationToken.None);
            Check(versions.Count == 1, "component-vanilla-versions");
            var marker = "page-contract-" + Guid.NewGuid()
                .ToString("N") + ".txt";
            var content = Guid.NewGuid()
                .ToString("N");
            File.WriteAllText(
                Path.Combine(profile.GameDirectory, marker),
                content
            );
            var preview = await components.PreviewAsync("vanilla", null, true, CancellationToken.None);
            Check(preview.CanApply && preview.FileCount > 0, "component-real-preview");
            var copied = await components.ApplyAsync(preview.PlanId, null, CancellationToken.None);
            var target = InstanceRegistry.List(service)
                .Single(
                    x => x.Id == copied.ProfileId
                );
            Check(
                File.ReadAllText(
                    Path.Combine(target.GameDirectory, marker)
                ) == content && File.ReadAllText(
                    Path.Combine(profile.GameDirectory, marker)
                ) == content,
                "component-copy-preserves-source-and-marker"
            );
            Check(
                File.Exists(copied.Backup) && File.Exists(copied.Receipt),
                "component-backup-and-receipt-exist"
            );
            bool stale = false;
            try
            {
                await components.ApplyAsync(preview.PlanId, null, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                stale = true;
            }

            Check(stale, "component-success-consumes-preview");
            var artifacts = LegacyPageAdapters.Artifacts(service, remote);
            Check(artifacts is IPageTaskSource, "artifact-execution-uses-host");
            var list = await artifacts.ListAsync("1.21.1", "client", CancellationToken.None);
            Check(list.Count == 1, "official-client-artifact-list");
            var destination = Path.Combine(
                Path.GetDirectoryName(report)!,
                "minecraft-client-1.21.1.jar"
            );
            var saved = await artifacts.SaveAsync(list[0].SelectionId, destination, CancellationToken.None);
            Check(
                File.Exists(saved.Path) && saved.Verification == "official_sha1",
                "official-jar-download-and-sha1"
            );
            var rows = await ((IPageTaskSource)artifacts).Tasks.ListAsync();
            var finished = rows.Last(
                x => x.Command.Method == "artifact.download" && x.State == "succeeded"
            );
            var exported = await ((IPageTaskSource)artifacts).Tasks.ExportAsync(finished.OperationId, destination + ".history.jar");
            Check(
                new FileInfo(exported.Path).Length == new FileInfo(saved.Path).Length,
                "completed-download-can-be-saved-from-history"
            );
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                bool cancelled = false;
                try
                {
                    await artifacts.SaveAsync(list[0].SelectionId, destination + ".cancelled.jar", cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }

                Check(
                    cancelled && !File.Exists(destination + ".cancelled.jar"),
                    "artifact-cancel-leaves-no-target"
                );
            }

            var windows = new Avalonia.Controls.Window[]
            {
                new ComponentManagerWindow(
                    components,
                    LegacyPageAdapters.Describe(profile)
                ),
                new ArtifactDownloadWindow(artifacts, "1.21.1")
            };
            foreach (var window in windows)
            {
                window.Show(this);
                await Task.Delay(200);
                Check(window.IsVisible, "contract-page-opens");
                window.Close();
            }

            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks
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
                        checks,
                        error = DiagnosticsService.Redact(
                            ex.ToString()
                        )
                    }
                )
            );
        }

        await RequestApplicationExitAsync();
    }
}
