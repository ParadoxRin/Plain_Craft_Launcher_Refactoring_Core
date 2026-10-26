using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Client;

namespace Pcl.Desktop;
/// <summary>离线脚本导出视图：只提交名称与实例 ID，Host 准备启动计划，Client 校验并保存文件。</summary>
public sealed class OfflineScriptWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private readonly string instanceId;
    private CancellationTokenSource? pending;
    public bool Busy => pending is not null;
    internal string? ExportedPath { get; private set; }

    private readonly TextBox player = new()
    {
        Name = "OfflineScriptPlayer",
        Text = "Player"
    };
    private readonly TextBox destination = new()
    {
        Name = "OfflineScriptDestination"
    };
    private readonly TextBlock status = new()
    {
        Name = "OfflineScriptStatus",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button export = new()
    {
        Content = "导出脚本"
    };
    private readonly Button browse = new()
    {
        Content = "选择保存位置"
    };
    private readonly Button cancel = new()
    {
        Content = "取消",
        IsVisible = false
    };
    public OfflineScriptWindow(WorkspaceApi api, string instanceId)
    {
        this.api = api;
        this.instanceId = instanceId;
        NavigationContext = instanceId;
        Title = "导出离线启动脚本";
        Width = 620;
        Height = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        destination.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Minecraft-offline.command"
        );
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "离线玩家名"
                },
                player,
                new TextBlock
                {
                    Text = "保存位置"
                },
                destination,
                new TextBlock
                {
                    Text = "双击脚本直接启动本机实例。更改 Java、路径或启动设置后请重新导出。沿用同名离线账户的皮肤，不运行前置命令。",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        browse,
                        export,
                        cancel
                    }
                },
                status
            }
        };
        browse.Click += async (_, _) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    SuggestedFileName = "Minecraft-offline.command",
                    FileTypeChoices = [
                        new FilePickerFileType("Mac 启动脚本")
                        {
                            Patterns = [
                                "*.command"
                            ]
                        }
                    ]
                }
            );
            if (file?.TryGetLocalPath()is string path)
            {
                destination.Text = path;
            }
        };
        export.Click += async (_, _) => await ExportAsync();
        cancel.Click += (_, _) => pending?.Cancel();
        // 关闭只请求取消；异步处理器持有 CTS 并负责唯一释放，收尾完成后才真正关窗。
    }

    private async Task ExportAsync()
    {
        if (pending is not null || ViewClosing)
        {
            return;
        }

        pending = new CancellationTokenSource();
        using var viewActivity = ViewLifetime.Begin(pending.Cancel);
        export.IsEnabled = browse.IsEnabled = player.IsEnabled = destination.IsEnabled = false;
        cancel.IsVisible = true;
        ExportedPath = null;
        try
        {
            var target = Path.GetFullPath(destination.Text ?? "");
            if (!target.EndsWith(".command", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("请使用 .command 文件名。");
            }

            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new IOException("目标已存在，请选择新的脚本文件名。");
            }

            if (!Directory.Exists(
                Path.GetDirectoryName(target)
            ))
            {
                throw new DirectoryNotFoundException("保存文件夹不存在。");
            }

            status.Text = "正在准备启动文件…";
            await api.ExportAsync(
                "workspace.instances.offlineScript",
                instanceId,
                new
                {
                    playerName = player.Text?.Trim() ?? ""
                },
                target,
                pending.Token
            );
            ExportedPath = target;
            status.Text = "已导出，可在 Finder 中双击启动。";
        }
        catch (OperationCanceledException)
        {
            status.Text = "已取消导出";
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
        }
        finally
        {
            pending.Dispose();
            pending = null;
            export.IsEnabled = browse.IsEnabled = player.IsEnabled = destination.IsEnabled = true;
            cancel.IsVisible = false;
        }
    }
}
