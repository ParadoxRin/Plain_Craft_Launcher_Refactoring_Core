using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using System.Text.Json;
using Pcl.Core.Contracts.Pages;

namespace Pcl.Desktop;
// 纯展示标记：Host 工作期间保留任务取消和查询入口，旧 Core 写页面仍保持禁用。
public interface IHostWorkspace
{
}

/// <summary>由 Contract 提供任务历史、取消和显式续做；关窗不会把后台操作当成已取消。</summary>
public sealed class PageTasksWindow : WorkspaceWindow, IHostWorkspace
{
    public PageTasksWindow(IPageTaskService tasks)
    {
        Title = "后台任务";
        Width = 760;
        Height = 530;
        var list = new ListBox
        {
            Height = 300
        };
        var status = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var refresh = new Button
        {
            Content = "刷新"
        };
        var cancel = new Button
        {
            Content = "取消所选任务"
        };
        var resume = new Button
        {
            Content = "续做所选任务"
        };
        var save = new Button
        {
            Content = "保存已完成文件"
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        refresh,
                        cancel,
                        resume,
                        save
                    }
                },
                status
            }
        };
        Content = new ScrollViewer
        {
            Content = panel
        };
        bool busy = false;
        CancellationTokenSource? resumeCancellation = null;
        async Task RefreshAsync()
        {
            var selected = (list.SelectedItem as TaskRow)?.Snapshot.OperationId;
            var rows = (await tasks.ListAsync()).OrderByDescending(
                x => x.Updated
            )
                .Select(
                    x => new TaskRow(x)
                )
                .ToArray();
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(
                x => x.Snapshot.OperationId == selected
            );
        }

        async Task Run(Func<Task> action)
        {
            if (busy)
            {
                return;
            }

            busy = true;
            refresh.IsEnabled = resume.IsEnabled = save.IsEnabled = false;
            try
            {
                await action();
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
            finally
            {
                busy = false;
                refresh.IsEnabled = resume.IsEnabled = save.IsEnabled = true;
            }
        }

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is TaskRow { Snapshot.DependencyFailure: { } failure })
            {
                status.Text = failure.DisplayText;
            }
        };
        Opened += async (_, _) => await Run(RefreshAsync);
        refresh.Click += async (_, _) => await Run(RefreshAsync);
        cancel.Click += async (_, _) =>
        {
            if (resumeCancellation is not null)
            {
                resumeCancellation.Cancel();
                status.Text = "已请求取消当前任务…";
                return;
            }

            if (list.SelectedItem is not TaskRow { Snapshot: var row })
            {
                return;
            }

            try
            {
                var changed = await tasks.CancelAsync(row.OperationId);
                status.Text = StateText(changed.State);
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        };
        resume.Click += async (_, _) => await Run(
            async () =>
            {
                if (list.SelectedItem is not TaskRow { Snapshot: var row } || row.State is not ("failed" or "cancelled" or "interrupted"))
                {
                    status.Text = "请选择失败、取消或中断的任务。";
                    return;
                }

                status.Text = "正在续做…";
                resumeCancellation = new CancellationTokenSource();
                cancel.Content = "取消本次续做";
                try
                {
                    var result = await tasks.ResumeAsync(row.OperationId, resumeCancellation.Token);
                    status.Text = StateText(result.State) + (result.DependencyFailure is { } dependency
                        ? "\n" + dependency.DisplayText
                        : result.ErrorCode is { } code
                            ? " · " + code
                            : "");
                }
                finally
                {
                    resumeCancellation.Dispose();
                    resumeCancellation = null;
                    cancel.Content = "取消所选任务";
                }
            }
        );
        save.Click += async (_, _) => await Run(
            async () =>
            {
                if (list.SelectedItem is not TaskRow { Snapshot: var row } || row.State != "succeeded" || row.Command.Method is not ("artifact.download" or "workspace.instances.backup" or "workspace.packs.export" or "workspace.browser.export" or "workspace.settings.export" or "workspace.catalog.icon" or "workspace.account.skinExport"))
                {
                    status.Text = "请选择已完成的下载、备份或整合包导出任务。";
                    return;
                }

                var export = row.Result!.Value.Deserialize<ArtifactExport>(
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                )!;
                var file = await StorageProvider.SaveFilePickerAsync(
                    new()
                    {
                        Title = "保存已完成的下载",
                        SuggestedFileName = export.FileName
                    }
                );
                if (file?.TryGetLocalPath()is not string path)
                {
                    return;
                }

                resumeCancellation = new CancellationTokenSource();
                cancel.Content = "取消保存";
                try
                {
                    var saved = await tasks.ExportAsync(row.OperationId, path, resumeCancellation.Token);
                    status.Text = "已校验并保存：" + saved.Path;
                }
                finally
                {
                    resumeCancellation.Dispose();
                    resumeCancellation = null;
                    cancel.Content = "取消所选任务";
                }
            }
        );
        Closing += (_, e) =>
        {
            if (busy)
            {
                e.Cancel = true;
                status.Text = "请等待任务结束；可取消正在执行的任务。";
            }
        };
    }

    // 协议状态保持稳定英文，界面只展示任务名称、状态与时间。
    private static string StateText(string state) => state switch
    {
        "queued" => "排队中",
        "running" => "执行中",
        "cancelling" => "正在取消",
        "succeeded" => "已完成",
        "cancelled" => "已取消",
        "failed" => "失败",
        "interrupted" => "已中断",
        _ => state
    };
    private static string WorkspaceName(string method)
    {
        var action = method.Split('.')
            .Last();
        var prefix = method.Contains(".settings.")
            ? "设置"
            : method.Contains(".search.")
                ? "多源搜索"
                : method.Contains(".instances.")
                    ? "实例"
                    : method.Contains(".packs.")
                        ? "整合包"
                        : method.Contains(".datapacks.")
                            ? "数据包"
                            : method.Contains(".java.")
                                ? "Java"
                                : "资源";
        var label = action switch
        {
            "list" or "scan" => "读取列表",
            "copy" => "复制",
            "repair" => "校验修复",
            "backup" => "备份",
            "verifyBackup" => "校验备份",
            "restoreBackup" => "恢复备份",
            "trash" => "移入回收站",
            "restoreTrash" => "恢复回收站",
            "listTrash" => "读取回收站",
            "import" => "导入",
            "install" => "安装",
            "update" => "更新",
            "page" => "搜索分页",
            "versions" => "查询兼容版本",
            "match" => "校验手动文件",
            "save" => "保存设置",
            "home" => "读取主页",
            "example" => "生成主页示例",
            "savePreset" => "保存主页预设",
            "removePreset" => "删除主页预设",
            "networkTargets" => "读取检测来源",
            "probe" => "检测网络",
            "cachePreview" => "预览缓存清理",
            "cacheMove" => "缓存移入回收目录",
            "cacheRestore" => "恢复临时缓存",
            "cacheTrash" => "读取缓存回收目录",
            "updates" => "检查更新",
            "enable" => "调整启用状态",
            "remove" => "移至备份",
            "validate" => "检查依赖",
            "history" => "读取历史",
            "undo" => "撤销应用",
            "ignore" => "忽略版本",
            "batchPrepare" => "准备批量更新",
            "batchApply" => "提交批量更新",
            "batchDiscard" => "放弃批量更新",
            "savePreferences" => "保存信息",
            "saveSettings" => "保存启动设置",
            "icon" => "读取图标",
            "saveIcon" => "保存图标",
            "removeIcon" => "恢复默认图标",
            "inspect" => "检查内容",
            "download" => "下载",
            "export" => "导出",
            "changes" => "检查改动",
            "exportOptions" => "读取导出配置",
            "saveExportOptions" => "保存导出配置",
            "worlds" => "读取世界",
            "discover" => "发现运行时",
            "ensureFabric" => "准备 Java 与加载器",
            "iris" => "安装光影依赖",
            "activate" => "应用",
            _ => "读取设置"
        };
        return prefix + " · " + label;
    }

    private sealed record TaskRow(PageSnapshot Snapshot)
    {
        public override string ToString()
        {
            var name = Snapshot.Command.Method switch
            {
                "component.versions" => "读取加载器版本",
                "component.preview" => "预览组件变更",
                "component.apply" => "调整加载器",
                "artifact.list" => "读取下载目录",
                "artifact.download" => "下载文件",
                "artifact.notes" => "读取更新说明",
                "instance.list" => "读取实例",
                var method when method.StartsWith("workspace.") => WorkspaceName(method),
                _ => "后台任务"
            };
            return name + " · " + StateText(Snapshot.State) + " · " + Snapshot.Updated.ToLocalTime()
                .ToString("MM-dd HH:mm:ss");
        }
    }
}
