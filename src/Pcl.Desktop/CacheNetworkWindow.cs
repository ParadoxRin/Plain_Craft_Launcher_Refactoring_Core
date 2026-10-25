using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 下载与网络管理：按来源与文件校验信息取得内容，跟踪进度、取消和失败，保留可恢复缓存。
public sealed class CacheNetworkWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private string? previewHandle;
    public bool Busy => operation is not null;

    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TabControl tabs = new();
    private readonly ListBox cacheFiles = new()
    {
        SelectionMode = SelectionMode.Multiple,
        MinHeight = 250
    };
    private readonly ListBox trash = new()
    {
        MinHeight = 180
    };
    private readonly TextBlock cacheTotal = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly ListBox networkResults = new()
    {
        MinHeight = 240
    };
    private CancellationTokenSource? operation;
    public CacheNetworkWindow(IWorkspaceOperations operations)
    {
        api = new(operations);
        Title = "网络检查与临时缓存";
        Width = 950;
        Height = 730;
        MinWidth = 730;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel
        {
            Margin = new Thickness(24)
        };
        root.Children.Add(
            Top(
                new TextBlock
                {
                    Text = Title,
                    FontSize = 24,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 0, 0, 16)
                }
            )
        );
        var footer = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(status);
        var cancel = new Button
        {
            Content = "取消"
        };
        cancel.Click += (_, _) => operation?.Cancel();
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;
        var network = new StackPanel
        {
            Spacing = 12
        };
        network.Children.Add(
            new TextBlock
            {
                Text = "每个地址最多读取 64 KiB，10 秒超时。小样本用于比较响应情况，不代表持续下载带宽或完整安装验证。使用当前代理设置。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        var targets = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0
        };
        Opened += async (_, _) =>
        {
            operation = new();
            tabs.IsEnabled = false;
            try
            {
                targets.ItemsSource = await api.CallAsync<NetworkProbeTarget[]>("workspace.settings.networkTargets", null, ct: operation.Token);
                targets.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                status.Text = WorkspaceErrors.Describe(ex);
            }
            finally
            {
                operation.Dispose();
                operation = null;
                tabs.IsEnabled = true;
            }
        };
        network.Children.Add(targets);
        var custom = new TextBox
        {
            PlaceholderText = "可选：自定义 HTTPS 文件地址（不含账号或令牌）"
        };
        network.Children.Add(custom);
        network.Children.Add(
            Button(
                "测试所选地址",
                async ct =>
                {
                    var target = !string.IsNullOrWhiteSpace(custom.Text)
                        ? new NetworkProbeTarget(
                            "自定义源",
                            custom.Text.Trim()
                        )
                        : targets.SelectedItem as NetworkProbeTarget ?? throw new InvalidOperationException("请选择测试地址。");
                    var result = await api.CallAsync<NetworkProbeResult>(
                        "workspace.settings.probe",
                        null,
                        new
                        {
                            target
                        },
                        ct
                    );
                    networkResults.ItemsSource = new[]
                    {
                        result
                    };
                    status.Text = result.Url + (result.RangeHonored
                        ? "\n服务器返回指定 Range"
                        : "\n读取达到上限后关闭响应流");
                }
            )
        );
        network.Children.Add(
            Button(
                "测试已配置来源",
                async ct =>
                {
                    var results = new List<NetworkProbeResult>();
                    foreach (var target in await api.CallAsync<NetworkProbeTarget[]>("workspace.settings.networkTargets", null, ct: ct))
                    {
                        status.Text = "正在测试 " + target.Name;
                        results.Add(
                            await api.CallAsync<NetworkProbeResult>(
                                "workspace.settings.probe",
                                null,
                                new
                                {
                                    target
                                },
                                ct
                            )
                        );
                        networkResults.ItemsSource = results.ToArray();
                    }

                    status.Text = $"已完成 {results.Count} 个有上限的样本请求";
                }
            )
        );
        network.Children.Add(networkResults);
        var cleanup = new StackPanel
        {
            Spacing = 12
        };
        cleanup.Children.Add(
            new TextBlock
            {
                Text = "仅列出超过 24 小时的下载临时文件；移动到回收目录，可恢复。保留已校验下载、游戏、存档、Java 和共享依赖。文件在预览后变化或正在使用时会跳过。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        cleanup.Children.Add(
            Button(
                "扫描临时文件",
                async ct =>
                {
                    var files = await PreviewAsync(ct);
                    cacheFiles.ItemsSource = files;
                    cacheTotal.Text = $"{files.Count} 个候选 · 合计 {files.Sum(
                            x => x.Size
                        ) / 1048576.0:F2} MiB\n可多选后移入回收目录";
                }
            )
        );
        cleanup.Children.Add(cacheTotal);
        cleanup.Children.Add(cacheFiles);
        cleanup.Children.Add(
            Button(
                "将所选文件移入回收目录",
                async ct =>
                {
                    var selected = cacheFiles.SelectedItems?.OfType<TemporaryCacheFile>()
                        .ToArray() ?? [];
                    if (selected.Length == 0)
                        throw new InvalidOperationException("请先选择临时文件。");
                    var result = await api.CallAsync<CacheCleanupResult>(
                        "workspace.settings.cacheMove",
                        null,
                        new
                        {
                            handle = previewHandle,
                            paths = selected.Select(
                                x => x.RelativePath
                            )
                                .ToArray()
                        },
                        ct
                    );
                    status.Text = $"已移动 {result.Batch?.Files.Count ?? 0} 个文件" + (result.Skipped.Count > 0
                        ? "\n跳过：" + string.Join(
                            "\n",
                            result.Skipped.Take(12)
                        )
                        : "");
                    cacheFiles.ItemsSource = await PreviewAsync(ct);
                    await RefreshTrashAsync(ct);
                }
            )
        );
        var restore = new StackPanel
        {
            Spacing = 12
        };
        restore.Children.Add(
            new TextBlock
            {
                Text = "恢复到原临时下载位置；已有同名文件或下载正在运行时保留回收副本。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        restore.Children.Add(
            Button(
                "刷新回收目录",
                async ct =>
                {
                    await RefreshTrashAsync(ct);
                }
            )
        );
        restore.Children.Add(trash);
        restore.Children.Add(
            Button(
                "恢复所选批次",
                async ct =>
                {
                    if (trash.SelectedItem is not CacheTrashBatch batch)
                        throw new InvalidOperationException("请选择回收批次。");
                    var result = await api.CallAsync<CacheCleanupResult>(
                        "workspace.settings.cacheRestore",
                        null,
                        new
                        {
                            id = batch.Id
                        },
                        ct
                    );
                    status.Text = result.Skipped.Count == 0
                        ? "已恢复全部文件"
                        : "部分文件仍保留在回收目录：\n" + string.Join(
                            "\n",
                            result.Skipped.Take(12)
                        );
                    await RefreshTrashAsync(ct);
                }
            )
        );
        tabs.ItemsSource = new[]
        {
            new TabItem
            {
                Header = "网络检查",
                Content = new ScrollViewer
                {
                    Content = network
                }
            },
            new TabItem
            {
                Header = "临时缓存",
                Content = new ScrollViewer
                {
                    Content = cleanup
                }
            },
            new TabItem
            {
                Header = "缓存回收目录",
                Content = new ScrollViewer
                {
                    Content = restore
                }
            }
        };
        Closing += (_, e) =>
        {
            if (operation is not null)
            {
                operation.Cancel();
                e.Cancel = true;
                status.Text = "正在取消，已移动的文件仍可在回收目录恢复。";
            }
        };
    }

    private static Control Top(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    private async Task RefreshTrashAsync(CancellationToken ct) => trash.ItemsSource = await api.CallAsync<CacheTrashBatch[]>("workspace.settings.cacheTrash", null, ct: ct);
    private async Task<IReadOnlyList<TemporaryCacheFile>> PreviewAsync(CancellationToken ct)
    {
        var preview = await api.CallAsync<CachePreview>("workspace.settings.cachePreview", null, ct: ct);
        previewHandle = preview.Handle;
        return preview.Items;
    }

    private Button Button(string label, Func<CancellationToken, Task> work)
    {
        var button = new Button
        {
            Content = label
        };
        button.Click += async (_, _) =>
        {
            if (operation is not null)
                return;
            operation = new();
            tabs.IsEnabled = false;
            status.Text = "正在处理…";
            try
            {
                await work(operation.Token);
                if (status.Text == "正在处理…")
                    status.Text = "操作完成";
            }
            catch (OperationCanceledException)
            {
                status.Text = "已取消，已移入回收目录的文件仍可恢复";
            }
            catch (Exception ex)
            {
                status.Text = WorkspaceErrors.Describe(ex);
            }
            finally
            {
                operation.Dispose();
                operation = null;
                tabs.IsEnabled = true;
            }
        };
        return button;
    }
}
