using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// The window owns input, cancellation and close intent; Host owns EasyTier and its diagnostics.
public sealed class MultiplayerWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private readonly CheckBox host = new()
    {
        Content = "我是房主",
        IsChecked = true,
        Name = "LinkHost"
    };
    private readonly TextBox gamePort = new()
    {
        Text = "25565",
        PlaceholderText = "房主游戏端口",
        Name = "LinkGamePort"
    };
    private readonly TextBox peers = new()
    {
        PlaceholderText = "节点地址，如 tcp://可访问的主机:11010",
        Name = "LinkPeers"
    };
    private readonly TextBox invite = new()
    {
        PlaceholderText = "加入时粘贴邀请码",
        AcceptsReturn = true,
        Height = 80,
        Name = "LinkInvitation"
    };
    private readonly TextBox local = new()
    {
        Text = "25566",
        PlaceholderText = "加入后的本地端口",
        Name = "LinkLocalPort"
    };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Name = "LinkStatus"
    };
    private readonly TextBox log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        Height = 200,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button start = new()
    {
        Content = "连接"
    }, stop = new()
    {
        Content = "断开",
        IsEnabled = false
    }, refresh = new()
    {
        Content = "刷新连接状态"
    };
    private CancellationTokenSource? operation;
    private Task work = Task.CompletedTask;
    private string? id;
    private bool closing, approved;
    public bool Busy => operation is not null;
    internal string? SessionId => id;

    public MultiplayerWindow(IWorkspaceOperations operations, string? invitation = null)
    {
        api = new(operations);
        Title = "联机";
        Width = 640;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        host.IsChecked = invitation is null;
        invite.Text = invitation;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        Content = new ScrollViewer
        {
            Content = panel
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "创建或加入房间",
                FontSize = 24
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "使用 EasyTier 无 TUN 模式。房主先在游戏中开放局域网，再填写端口。双方使用同一节点；邀请码用于 PCL Mac。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        foreach (var control in new Control[]
        {
            host,
            gamePort,
            peers,
            invite,
            local
        }

        )
        {
            panel.Children.Add(control);
        }

        var copy = new Button
        {
            Content = "复制邀请码"
        };
        var cancel = new Button
        {
            Content = "取消任务"
        };
        panel.Children.Add(
            new WrapPanel
            {
                ItemSpacing = 10,
                LineSpacing = 8,
                Children =
                {
                    start,
                    stop,
                    copy,
                    refresh,
                    cancel
                }
            }
        );
        panel.Children.Add(status);
        panel.Children.Add(log);
        start.Click += (_, _) => Begin(
            async ct =>
            {
                var isHost = host.IsChecked == true;
                var secretHandle = isHost
                    ? null
                    : await api.SecretAsync(
                        invite.Text?.Trim() ?? "",
                        ct
                    );
                var row = await api.CallAsync<LinkSnapshot>(
                    "workspace.link.start",
                    null,
                    new
                    {
                        host = isHost,
                        gamePort = Port(gamePort.Text),
                        localPort = Port(local.Text),
                        peers = (peers.Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                        secretHandle
                    },
                    ct
                );
                Apply(row);
                // Sensitive invitation is transient private-channel data, never a normal page-task result.
                if (isHost)
                {
                    invite.Text = await api.LinkInvitationAsync(row.Id, ct);
                }
            }
        );
        stop.Click += (_, _) => Begin(Disconnect);
        refresh.Click += (_, _) => Begin(
            async ct =>
            {
                if (id is not null)
                {
                    Apply(
                        await api.CallAsync<LinkSnapshot>(
                            "workspace.link.status",
                            null,
                            new
                            {
                                id
                            },
                            ct
                        )
                    );
                }
            }
        );
        cancel.Click += (_, _) => operation?.Cancel();
        copy.Click += async (_, _) =>
        {
            if (Clipboard is not null && !string.IsNullOrEmpty(invite.Text))
            {
                await Clipboard.SetTextAsync(invite.Text);
            }
        };
        Closing += async (_, e) =>
        {
            if (approved)
            {
                return;
            }

            e.Cancel = true;
            if (closing)
            {
                return;
            }

            closing = true;
            operation?.Cancel();
            await work;
            try
            {
                await Disconnect(CancellationToken.None);
                invite.Text = "";
                approved = true;
                Close();
            }
            catch (Exception ex)
            {
                status.Text = WorkspaceErrors.Describe(ex);
                closing = false;
            }
        };
    }

    // Input parsing is presentation validation; Host independently validates ranges before executing.
    private static int Port(string? text) => int.TryParse(text, out var port)
        ? port
        : 0;
    private void Begin(Func<CancellationToken, Task> action)
    {
        if (Busy || closing)
        {
            return;
        }

        work = Run(action);
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        operation = new();
        start.IsEnabled = stop.IsEnabled = refresh.IsEnabled = false;
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            status.Text = "已取消";
        }
        catch (Exception ex)
        {
            status.Text = WorkspaceErrors.Describe(ex);
        }
        finally
        {
            operation.Dispose();
            operation = null;
            start.IsEnabled = id is null;
            stop.IsEnabled = id is not null;
            refresh.IsEnabled = true;
        }
    }

    private async Task Disconnect(CancellationToken ct)
    {
        if (id is null)
        {
            return;
        }

        Apply(
            await api.CallAsync<LinkSnapshot>(
                "workspace.link.stop",
                null,
                new
                {
                    id
                },
                ct
            )
        );
    }

    private void Apply(LinkSnapshot row)
    {
        id = row.State == "running"
            ? row.Id
            : null;
        log.Text = string.Join("\n", row.Lines);
        status.Text = id is null
            ? "已断开"
            : row.PeerCount > 0
                ? $"已发现 {row.PeerCount} 个其他成员"
                : row.Host
                    ? $"房间已启动，节点监听端口 {row.ListenPort}。将邀请码交给朋友。"
                    : $"模块已启动。连接建立后使用 127.0.0.1:{row.LocalPort}；可刷新连接状态。";
    }
}
