using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Desktop;
// 任务诊断：读取任务和日志上下文，识别已知错误，脱敏后展示或导出诊断结果。
public sealed class TaskCenterWindow : WorkspaceWindow, IHostWorkspace
{
    private DownloadSnapshot tasks = new(
        [],
        false,
        new(),
        [],
        []
    );
    public TaskCenterWindow(
        Pcl.Client.LauncherSession session,
        Func<Func<CancellationToken, Task>, Task> run,
        Action<string> open,
        Action? cancelCurrent = null,
        IVersionInstallOperations? operations = null
    )
    {
        var paths = session.DesktopState.Paths;
        var api = new Pcl.Client.WorkspaceApi(session.Workspaces);
        var lifetime = new CancellationTokenSource();
        Task? refreshTask = null;
        Title = "任务与诊断";
        Width = 850;
        Height = 670;
        MinWidth = 650;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var tabs = new TabControl
        {
            Margin = new Thickness(20)
        };
        Content = tabs;
        var downloads = new StackPanel
        {
            Spacing = 12
        };
        var list = new ListBox
        {
            Height = 330
        };
        var detail = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        var summary = new TextBlock();
        downloads.Children.Add(summary);
        downloads.Children.Add(list);
        downloads.Children.Add(detail);
        var actions = new WrapPanel();
        var pause = new Button
        {
            Content = "暂停下载",
            Margin = new Thickness(0, 0, 8, 0)
        };
        var retry = new Button
        {
            Content = "重试所选文件"
        };
        actions.Children.Add(pause);
        actions.Children.Add(retry);
        var cancel = new Button
        {
            Content = "取消当前操作",
            IsVisible = cancelCurrent is not null,
            Margin = new Thickness(8, 0, 0, 0)
        };
        cancel.Click += (_, _) => cancelCurrent?.Invoke();
        actions.Children.Add(cancel);
        downloads.Children.Add(actions);
        downloads.Children.Add(
            new TextBlock
            {
                Text = "安装中断后，重试文件并回到实例管理修复安装。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        Task Refresh() => refreshTask is { IsCompleted: false }
            ? refreshTask
            : refreshTask = RefreshCore();
        async Task RefreshCore()
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            if (lifetime.IsCancellationRequested || ViewClosing)
                return;
            try
            {
                tasks = await session.Client.Call<DownloadSnapshot>(
                    "downloads.snapshot",
                    new { },
                    lifetime.Token
                );
                var selected = (list.SelectedItem as DownloadTaskRow)?.Id;
                var rows = tasks.Rows;
                list.ItemsSource = rows.Take(300)
                    .ToArray();
                list.SelectedItem = rows.FirstOrDefault(
                    x => x.Id == selected
                );
                summary.Text = $"{rows.Count(
                        x => x.State == "下载中"
                    )} 个下载中 · {rows.Count(
                        x => x.State == "失败" || x.State == "已中断"
                    )} 个需处理";
                pause.Content = tasks.IsPaused
                    ? "继续下载"
                    : "暂停下载";
            }
            catch (OperationCanceledException)when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                detail.Text = Pcl.Client.WorkspaceErrors.Describe(ex);
            }
        }

        list.SelectionChanged += (_, _) =>
        {
            // [实现] 回调触发时：按 list.SelectedItem is DownloadTaskRow row 分支处理。
            if (list.SelectedItem is DownloadTaskRow row)
                detail.Text = row.Item.Path + "\n" + row.Error;
        };
        pause.Click += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            // 暂停由 Host 控制；断线错误留在窗口内，不让 async void 事件退出应用。
            try
            {
                await session.Client.Call<bool>(
                    "downloads.pause",
                    new
                    {
                        paused = !tasks.IsPaused
                    },
                    lifetime.Token
                );
                await Refresh();
            }
            catch (OperationCanceledException)when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                detail.Text = Pcl.Client.WorkspaceErrors.Describe(ex);
            }
        };
        retry.Click += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            // [实现] 回调触发时：按 list.SelectedItem is not DownloadTaskRow row || row.State is "下载中" or "排队中" 分支处理；执行受异常处理保护的操作。
            if (list.SelectedItem is not DownloadTaskRow row || row.State is "下载中" or "排队中")
                return;
            try
            {
                await run(
                    async ct =>
                    {
                        await api.CallAsync<bool>(
                            "workspace.shell.downloadRetry",
                            null,
                            new
                            {
                                id = row.Id
                            },
                            ct
                        );
                    }
                );
                await Refresh();
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 detail.Text = ex.Message。
                detail.Text = ex.Message;
            }
        };
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        timer.Tick += async (_, _) => await Refresh();
        Opened += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            // [实现] 回调触发时：执行 Refresh()；执行 timer.Start()。
            await Refresh();
            if (!ViewClosing)
            {
                timer.Start();
            }
        };
        ViewLifetime.OnStop(
            () =>
            {
                timer.Stop();
                lifetime.Cancel();
                lifetime.Dispose();
                return Task.CompletedTask;
            }
        );
        var net = new StackPanel
        {
            Spacing = 12
        };
        var current = tasks.Options;
        // [实现] Field 的实现：执行 net.Children.Add( new TextBlock { Text = label } )；准备 var box = new TextBox { Text = value }；执行 net.Children.Add(box)。
        TextBox Field(string label, string value)
        {
            net.Children.Add(
                new TextBlock
                {
                    Text = label
                }
            );
            var box = new TextBox
            {
                Text = value
            };
            net.Children.Add(box);
            return box;
        }

        var parallel = Field(
            "同时下载文件数（1–32）",
            current.ConcurrentFiles.ToString()
        );
        parallel.Name = "NetworkParallel";
        var speed = Field(
            "总限速 KiB/s（0 为不限）",
            current.SpeedKiB.ToString()
        );
        var proxy = Field("文件下载代理（留空使用系统设置，重启后生效）", current.Proxy);
        var host = Field("可选镜像：原始主机名", current.MirrorHost);
        var mirror = Field("可选镜像：HTTPS 根地址", current.MirrorBase);
        var prefer = new CheckBox
        {
            Content = "优先使用镜像，失败回退官方源",
            IsChecked = current.PreferMirror
        };
        net.Children.Add(prefer);
        var segmented = new CheckBox
        {
            Content = "大文件分段下载（可选，默认关闭）",
            IsChecked = current.SegmentedDownloads
        };
        net.Children.Add(segmented);
        var segments = Field(
            "单文件分段并发（2–8；仅适用有完整哈希的大文件）",
            current.SegmentWorkers.ToString()
        );
        var save = new Button
        {
            Content = "保存网络设置"
        };
        var message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        net.Children.Add(save);
        net.Children.Add(message);
        var inspect = new Button
        {
            Content = "测速与临时缓存管理"
        };
        inspect.Click += async (_, _) => await Bootstrap.WorkspacePages.Cache(this, paths)
            .PresentAsync(this);
        net.Children.Add(inspect);
        save.Click += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            // [实现] 回调触发时：执行受异常处理保护的操作。
            try
            {
                // 仅修改这个窗口负责的字段，保留主设置页的来源、命名与调试选项。
                await api.CallAsync<SettingsSnapshot>(
                    "workspace.settings.save",
                    null,
                    new
                    {
                        changes = new
                        {
                            Network = tasks.Options with
                            {
                                ConcurrentFiles = int.Parse(parallel.Text ?? ""),
                                SpeedKiB = int.Parse(speed.Text ?? ""),
                                Proxy = proxy.Text?.Trim() ?? "",
                                MirrorBase = mirror.Text?.Trim() ?? "",
                                MirrorHost = host.Text?.Trim() ?? "",
                                PreferMirror = prefer.IsChecked == true,
                                SegmentedDownloads = segmented.IsChecked == true,
                                SegmentWorkers = int.Parse(segments.Text ?? "")
                            }
                        }
                    },
                    lifetime.Token
                );
                message.Text = "已保存。代理将在新下载服务或重启后生效；分段下载仅用于至少 32 MiB 且有完整哈希的文件，不支持 Range 时自动回退。";
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 message.Text = ex.Message。
                message.Text = ex.Message;
            }
        };
        var diagnostic = new StackPanel
        {
            Spacing = 12
        };
        var logFiles = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var analysis = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 320
        };
        analysis.Name = "DiagnosticAnalysis";
        string? log = null;
        async Task Load(string selected)
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            log = selected;
            analysis.Text = await api.CallAsync<string>(
                "workspace.shell.diagnostic",
                null,
                new
                {
                    path = selected
                },
                lifetime.Token
            );
        }

        Opened += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            await Refresh();
            logFiles.ItemsSource = tasks.Logs;
            parallel.Text = tasks.Options.ConcurrentFiles.ToString();
            speed.Text = tasks.Options.SpeedKiB.ToString();
            proxy.Text = tasks.Options.Proxy;
            host.Text = tasks.Options.MirrorHost;
            mirror.Text = tasks.Options.MirrorBase;
            prefer.IsChecked = tasks.Options.PreferMirror;
            segmented.IsChecked = tasks.Options.SegmentedDownloads;
            segments.Text = tasks.Options.SegmentWorkers.ToString();
            logFiles.SelectedIndex = 0;
        };
        logFiles.SelectionChanged += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            // [实现] 回调触发时：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：按 logFiles.SelectedItem is string selected 分支处理。
                if (logFiles.SelectedItem is string selected)
                    await Load(selected);
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 analysis.Text = ex.Message。
                analysis.Text = ex.Message;
            }
        };
        // 初始选择在 Host 日志列表到达后设置，避免空列表预选 0 吞掉选择事件。
        var import = new Button
        {
            Content = "导入日志"
        };
        import.Click += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            // [实现] 回调触发时：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：准备 var files = await StorageProvider.OpenFilePickerAsync( new() { Title = "选择游戏日志", AllowMultiple …；按 files.FirstOrDefault()?.TryGetLocalPath()is string file 分支处理。
                var files = await StorageProvider.OpenFilePickerAsync(
                    new()
                    {
                        Title = "选择游戏日志",
                        AllowMultiple = false
                    }
                );
                if (files.FirstOrDefault()?.TryGetLocalPath()is string file)
                    await Load(file);
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 analysis.Text = ex.Message。
                analysis.Text = ex.Message;
            }
        };
        var export = new Button
        {
            Content = "导出脱敏诊断包"
        };
        export.Click += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            // [实现] 回调触发时：按 log is null 分支处理；执行受异常处理保护的操作。
            if (log is null)
                return;
            try
            {
                // [实现] 在异常保护范围内：准备 var file = await StorageProvider.SaveFilePickerAsync( new() { SuggestedFileName = "PCL-diagnost…；按 file?.TryGetLocalPath()is string target 分支处理。
                var file = await StorageProvider.SaveFilePickerAsync(
                    new()
                    {
                        SuggestedFileName = "PCL-diagnostics.zip"
                    }
                );
                if (file?.TryGetLocalPath()is string target)
                {
                    await api.ExportAsync(
                        "workspace.shell.diagnosticExport",
                        null,
                        new
                        {
                            path = log
                        },
                        target,
                        lifetime.Token
                    );
                    analysis.Text += "\n已导出：" + target;
                }
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 analysis.Text = ex.Message。
                analysis.Text = ex.Message;
            }
        };
        foreach (var control in new Control[]
        {
            logFiles,
            import,
            analysis,
            export
        }

        )
            diagnostic.Children.Add(control);
        var installations = new StackPanel
        {
            Spacing = 12
        };
        var installList = new ListBox
        {
            Height = 340,
            ItemsSource = tasks.Installations
        };
        Opened += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            await Refresh();
            installList.ItemsSource = tasks.Installations;
        };
        var installStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        installations.Children.Add(
            new TextBlock
            {
                Text = "继续未完成的游戏安装会重新校验文件、复用缓存并执行剩余安装步骤。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        installations.Children.Add(installList);
        installations.Children.Add(installStatus);
        var resume = new Button
        {
            Content = "继续所选安装"
        };
        installations.Children.Add(resume);
        installList.SelectionChanged += (_, _) => installStatus.Text = (installList.SelectedItem as InstallOperation)?.Error;
        resume.Click += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            // [实现] 回调触发时：按 installList.SelectedItem is not InstallOperation item || item.State is "已完成" or "安装中" 分支处理；执行 await run( async ct => { using var service = new MinecraftService(paths); await new InstallOper…；执行 installList.ItemsSource = tasks.Installations。
            if (installList.SelectedItem is not InstallOperation item || item.State is "已完成" or "安装中")
                return;
            await run(
                async ct =>
                {
                    // [实现] 回调触发时：准备 var service = new MinecraftService(paths)；执行 await new InstallOperations(paths).InstallAsync(service, item.Version, item.Loader, ct: ct, tas…。
                    if (operations is null)
                    {
                        throw new InvalidOperationException("此入口尚未接入操作服务");
                    }

                    var platform = session.Target;
                    var accepted = await operations.AcceptAsync(
                        new(
                            Guid.NewGuid()
                                .ToString("N"),
                            new(item.Version, item.Loader, platform)
                        ),
                        ct
                    );
                    var completed = await WaitOperation(accepted.OperationId, ct);
                    installStatus.Text = completed.State + (completed.ErrorCode is null
                        ? ""
                        : " · " + completed.ErrorCode);
                }
            );
            installList.ItemsSource = tasks.Installations;
        };
        // 任务中心沿用主窗口的忙碌协调；关此视图不取消任务，明确取消按钮才发送 operation.cancel。
        async Task<OperationSnapshot> WaitOperation(string id, CancellationToken ct)
        {
            var completion = operations!.WaitAsync(id);
            bool sent = false;
            while (!completion.IsCompleted)
            {
                if (ct.IsCancellationRequested && !sent)
                {
                    await operations.CancelAsync(id);
                    sent = true;
                }

                await Task.WhenAny(
                    completion,
                    Task.Delay(100)
                );
            }

            return await completion;
        }

        // 新操作页直接查询应用权威快照；关窗只停止界面刷新，不取消后台操作。
        var operationPanel = new StackPanel
        {
            Spacing = 10
        };
        var operationList = new ListBox
        {
            Name = "OperationList",
            Height = 330
        };
        var operationStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };
        var refreshOperations = new Button
        {
            Name = "OperationRefresh",
            Content = "刷新操作状态"
        };
        var cancelOperation = new Button
        {
            Name = "OperationCancel",
            Content = "取消所选操作"
        };
        var resumeOperation = new Button
        {
            Name = "OperationResume",
            Content = "校验并续做所选操作"
        };
        operationPanel.Children.Add(operationList);
        operationPanel.Children.Add(operationStatus);
        operationPanel.Children.Add(refreshOperations);
        operationPanel.Children.Add(cancelOperation);
        operationPanel.Children.Add(resumeOperation);
        async Task RefreshOperations()
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            if (operations is null)
            {
                operationStatus.Text = "此入口尚未接入操作服务";
                return;
            }

            var selected = (operationList.SelectedItem as OperationSnapshot)?.OperationId;
            var rows = await operations.ListAsync();
            operationList.ItemsSource = rows;
            operationList.SelectedItem = rows.FirstOrDefault(
                x => x.OperationId == selected
            );
        }

        operationList.SelectionChanged += (_, _) =>
        {
            if (operationList.SelectedItem is OperationSnapshot row)
            {
                operationStatus.Text = $"{row.OperationId} · {row.State} · {row.Stage} · 水位 {row.Sequence}" + (row.ErrorCode is null
                    ? ""
                    : " · " + row.ErrorCode);
            }
        };
        refreshOperations.Click += async (_, _) => await RefreshOperations();
        cancelOperation.Click += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            if (operations is not null && operationList.SelectedItem is OperationSnapshot row)
            {
                await operations.CancelAsync(row.OperationId);
                await RefreshOperations();
            }
        };
        resumeOperation.Click += async (_, _) =>
        {
            if (ViewClosing)
            {
                return;
            }

            using var viewActivity = ViewLifetime.Begin(lifetime.Cancel);
            if (operations is null || operationList.SelectedItem is not OperationSnapshot row)
            {
                return;
            }

            try
            {
                await run(
                    async ct =>
                    {
                        var accepted = await operations.AcceptAsync(
                            new(
                                Guid.NewGuid()
                                    .ToString("N"),
                                row.Request,
                                row.OperationId
                            ),
                            ct
                        );
                        operationStatus.Text = "已受理续做 · " + accepted.OperationId;
                        await RefreshOperations();
                        var completed = await WaitOperation(accepted.OperationId, ct);
                        operationStatus.Text = completed.State + (completed.ErrorCode is null
                            ? ""
                            : " · " + completed.ErrorCode);
                        await RefreshOperations();
                    }
                );
            }
            catch (OperationRejectedException ex)
            {
                operationStatus.Text = "无法续做 · " + ex.Code;
            }
        };
        Opened += async (_, _) => await RefreshOperations();
        tabs.ItemsSource = new[]
        {
            new TabItem
            {
                Header = "操作与恢复",
                Content = operationPanel
            },
            new TabItem
            {
                Header = "安装记录",
                Content = installations
            },
            new TabItem
            {
                Header = "下载任务",
                Content = downloads
            },
            new TabItem
            {
                Header = "网络",
                Content = new ScrollViewer
                {
                    Content = net
                }
            },
            new TabItem
            {
                Header = "日志诊断",
                Content = diagnostic
            }
        };
    }
}
