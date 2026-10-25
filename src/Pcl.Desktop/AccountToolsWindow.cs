using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 账户管理与正版验证：在账户状态、令牌交换和界面之间传递经过校验的数据，完整游戏启动必须通过权益检查。
public sealed class AccountToolsWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private readonly Func<SkinManagerWindow> skins;
    public bool Busy => operation is not null;

    private readonly Action<string>? changed;
    private readonly ListBox list = new();
    private readonly TextBox server = new()
    {
        PlaceholderText = "认证服务器 HTTPS 地址"
    };
    private readonly TextBox identifier = new()
    {
        PlaceholderText = "账号"
    };
    private readonly TextBox password = new()
    {
        PlaceholderText = "密码",
        PasswordChar = '●'
    };
    private readonly CheckBox remember = new()
    {
        Content = "记住登录（系统钥匙串）",
        IsChecked = true
    };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock serverInfo = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly StackPanel controls = new()
    {
        Spacing = 12
    };
    private readonly Image preview = new()
    {
        Width = 220,
        Height = 220,
        Stretch = Stretch.Uniform
    };
    private ExternalServer? resolved;
    private CancellationTokenSource? operation;
    private Bitmap? bitmap;
    public AccountToolsWindow(
        IWorkspaceOperations operations,
        Func<SkinManagerWindow> skins,
        Action<string>? changed = null
    )
    {
        api = new(operations);
        this.skins = skins;
        this.changed = changed;
        Title = "外置账户与皮肤";
        Width = 900;
        Height = 720;
        MinWidth = 720;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel
        {
            Margin = new Thickness(24)
        };
        var header = new TextBlock
        {
            Text = "外置账户与皮肤",
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 18)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var footer = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0)
        };
        footer.Children.Add(status);
        var cancel = new Button
        {
            Content = "取消任务"
        };
        cancel.Click += (_, _) => operation?.Cancel();
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        list.Width = 255;
        list.Margin = new Thickness(0, 0, 24, 0);
        DockPanel.SetDock(list, Dock.Left);
        root.Children.Add(list);
        root.Children.Add(
            new ScrollViewer
            {
                Content = controls
            }
        );
        Content = root;
        controls.Children.Add(server);
        controls.Children.Add(
            Button(
                "读取服务器",
                async ct =>
                {
                    resolved = await api.CallAsync<ExternalServer>(
                        "workspace.account.discover",
                        null,
                        new
                        {
                            address = server.Text ?? ""
                        },
                        ct
                    );
                    serverInfo.Text = resolved.Name + "\n" + resolved.ApiRoot;
                }
            )
        );
        controls.Children.Add(serverInfo);
        controls.Children.Add(
            Button(
                "微软与离线皮肤",
                _ => skins()
                    .ShowDialog(this)
            )
        );
        controls.Children.Add(identifier);
        controls.Children.Add(password);
        controls.Children.Add(remember);
        controls.Children.Add(
            Button("登录并选择角色", Login)
        );
        var accountActions = new WrapPanel
        {
            ItemSpacing = 10,
            LineSpacing = 10
        };
        accountActions.Children.Add(
            Button(
                "选择账户",
                async ct =>
                {
                    var selected = Selected();
                    await api.CallAsync<AccountPageSnapshot>(
                        "workspace.account.select",
                        null,
                        new
                        {
                            accountId = selected.Id
                        },
                        ct
                    );
                    changed?.Invoke(selected.Id);
                    status.Text = "已选择 " + selected.Name;
                }
            )
        );
        accountActions.Children.Add(
            Button(
                "退出登录",
                async ct =>
                {
                    var selected = Selected();
                    await api.CallAsync<AccountPageSnapshot>(
                        "workspace.account.logout",
                        null,
                        new
                        {
                            accountId = selected.Id
                        },
                        ct
                    );
                    await Refresh(selected.Id, ct);
                    changed?.Invoke(selected.Id);
                }
            )
        );
        accountActions.Children.Add(
            Button(
                "移除账户",
                async ct =>
                {
                    var selected = Selected();
                    var snapshot = await api.CallAsync<AccountPageSnapshot>(
                        "workspace.account.remove",
                        null,
                        new
                        {
                            accountId = selected.Id
                        },
                        ct
                    );
                    await Refresh(ct: ct);
                    changed?.Invoke(snapshot.SelectedId ?? "");
                }
            )
        );
        controls.Children.Add(accountActions);
        controls.Children.Add(
            new TextBlock
            {
                Text = "皮肤与披风",
                FontSize = 19,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 14, 0, 0)
            }
        );
        var skinActions = new WrapPanel
        {
            ItemSpacing = 10,
            LineSpacing = 10
        };
        skinActions.Children.Add(
            Button(
                "查看皮肤",
                async ct =>
                {
                    var textures = await api.CallAsync<ExternalSkin>(
                        "workspace.account.externalSkin",
                        null,
                        new
                        {
                            accountId = Selected()
                                .Id
                        },
                        ct
                    );
                    serverInfo.Text = textures.ServerName + "\n" + textures.ApiRoot + "\n可修改：" + string.Join("、", textures.Uploadable);
                    if (textures.Png is null)
                    {
                        preview.Source = null;
                        status.Text = "当前使用默认皮肤";
                        return;
                    }

                    var bytes = textures.Png;
                    bitmap?.Dispose();
                    bitmap = new Bitmap(
                        new MemoryStream(bytes)
                    );
                    preview.Source = bitmap;
                }
            )
        );
        skinActions.Children.Add(
            Button(
                "上传皮肤",
                ct => Upload("skin", ct)
            )
        );
        skinActions.Children.Add(
            Button(
                "上传披风",
                ct => Upload("cape", ct)
            )
        );
        skinActions.Children.Add(
            Button(
                "重置皮肤",
                async ct =>
                {
                    if (await Confirm("重置皮肤", "将此角色的皮肤恢复为默认。"))
                        await api.CallAsync<bool>(
                            "workspace.account.externalTexture",
                            null,
                            new
                            {
                                accountId = Selected()
                                    .Id,
                                kind = "skin"
                            },
                            ct
                        );
                }
            )
        );
        skinActions.Children.Add(
            Button(
                "清除披风",
                async ct =>
                {
                    if (await Confirm("清除披风", "将删除此角色在该认证服务器上的披风。"))
                        await api.CallAsync<bool>(
                            "workspace.account.externalTexture",
                            null,
                            new
                            {
                                accountId = Selected()
                                    .Id,
                                kind = "cape"
                            },
                            ct
                        );
                }
            )
        );
        controls.Children.Add(skinActions);
        controls.Children.Add(preview);
        server.TextChanged += (_, _) =>
        {
            resolved = null;
            serverInfo.Text = "";
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is LauncherAccount selected)
            {
                server.Text = selected.Server;
                identifier.Text = selected.Identifier;
                remember.IsChecked = selected.Remembered;
                password.Text = "";
                preview.Source = null;
            }
        };
        Closing += (_, e) =>
        {
            if (operation is not null)
            {
                operation.Cancel();
                e.Cancel = true;
                status.Text = "正在取消，请稍后关闭。";
            }
        };
        Closed += (_, _) =>
        {
            password.Text = "";
            bitmap?.Dispose();
        };
        Opened += async (_, _) => await Run(
            ct => Refresh(ct: ct)
        );
    }

    private LauncherAccount Selected() => list.SelectedItem as LauncherAccount ?? throw new InvalidOperationException("请选择外置账户。");
    private async Task Refresh(string? id = null, CancellationToken ct = default)
    {
        var snapshot = await api.CallAsync<AccountPageSnapshot>("workspace.account.list", null, ct: ct);
        var rows = snapshot.Accounts.Where(
            x => x.Kind == AccountKind.External
        )
            .ToArray();
        list.ItemsSource = rows;
        list.SelectedItem = rows.FirstOrDefault(
            x => x.Id == (id ?? snapshot.SelectedId)
        ) ?? rows.FirstOrDefault();
    }

    private Button Button(string title, Func<CancellationToken, Task> action)
    {
        var button = new Button
        {
            Content = title
        };
        button.Click += async (_, _) => await Run(action);
        return button;
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null)
            return;
        operation = new();
        controls.IsEnabled = list.IsEnabled = false;
        status.Text = "正在处理…";
        try
        {
            await action(operation.Token);
            if (status.Text == "正在处理…")
                status.Text = "操作完成";
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
            controls.IsEnabled = list.IsEnabled = true;
        }
    }

    private async Task Login(CancellationToken ct)
    {
        var selectedServer = resolved ?? throw new InvalidOperationException("请先读取并确认认证服务器地址。");
        ExternalChallenge challenge;
        try
        {
            var handle = await api.SecretAsync(password.Text ?? "", ct);
            challenge = await api.CallAsync<ExternalChallenge>(
                "workspace.account.externalLogin",
                null,
                new
                {
                    address = selectedServer.ApiRoot,
                    identifier = identifier.Text?.Trim() ?? "",
                    secretHandle = handle
                },
                ct
            );
        }
        finally
        {
            password.Text = "";
        }

        var selected = challenge.Selected;
        if (selected is null)
        {
            var dialog = new WorkspaceWindow
            {
                Title = "选择角色",
                Width = 380,
                Height = 340,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var choices = new ListBox
            {
                ItemsSource = challenge.Profiles,
                SelectedIndex = 0
            };
            var panel = new DockPanel
            {
                Margin = new Thickness(20)
            };
            var pick = new Button
            {
                Content = "使用此角色"
            };
            DockPanel.SetDock(pick, Dock.Bottom);
            panel.Children.Add(pick);
            panel.Children.Add(choices);
            dialog.Content = panel;
            pick.Click += (_, _) =>
            {
                selected = choices.SelectedItem as ExternalProfile;
                dialog.Close();
            };
            await dialog.ShowDialog(this);
            ct.ThrowIfCancellationRequested();
            if (selected is null)
            {
                status.Text = "未选择角色";
                return;
            }
        }

        var account = await api.CallAsync<LauncherAccount>(
            "workspace.account.externalSelect",
            null,
            new
            {
                handle = challenge.Handle,
                profileId = selected.Id,
                remember = remember.IsChecked == true
            },
            ct
        );
        await Refresh(account.Id, ct);
        changed?.Invoke(account.Id);
        status.Text = account.Remembered
            ? "已登录并记住账户"
            : "已登录，本次会话有效";
    }

    private async Task Upload(string kind, CancellationToken ct)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "选择 PNG " + (kind == "skin"
                    ? "皮肤"
                    : "披风"),
                AllowMultiple = false,
                FileTypeFilter = [
                    new FilePickerFileType("PNG")
                    {
                        Patterns = [
                            "*.png"
                        ]
                    }
                ]
            }
        );
        if (files.FirstOrDefault()?.TryGetLocalPath()is not string file)
            return;
        if (new FileInfo(file).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("图片不能超过 4 MiB。");
        var bytes = await File.ReadAllBytesAsync(file, ct);
        // Decode and re-encode pixels so imported PNG metadata is not forwarded to the server.
        using var image = new Bitmap(
            new MemoryStream(bytes)
        );
        using var clean = new MemoryStream();
        image.Save(clean, PngBitmapEncoderOptions.Default);
        var model = kind == "skin"
            ? await ChooseSkinModel()
            : false;
        if (model is null)
            return;
        var temporary = Path.Combine(
            Path.GetTempPath(),
            "pcl-clean-texture-" + Guid.NewGuid()
                .ToString("N") + ".png"
        );
        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                clean.ToArray(),
                ct
            );
            await api.CallAsync<bool>(
                "workspace.account.externalTexture",
                null,
                new
                {
                    accountId = Selected()
                        .Id,
                    kind,
                    path = temporary,
                    slim = model.Value
                },
                ct
            );
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<bool?> ChooseSkinModel()
    {
        var dialog = new WorkspaceWindow
        {
            Title = "选择皮肤模型",
            Width = 350,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        bool? selected = null;
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "请选择手臂宽度"
            }
        );
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        var standard = new Button
        {
            Content = "标准手臂"
        };
        var slim = new Button
        {
            Content = "细手臂"
        };
        buttons.Children.Add(standard);
        buttons.Children.Add(slim);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        standard.Click += (_, _) =>
        {
            selected = false;
            dialog.Close();
        };
        slim.Click += (_, _) =>
        {
            selected = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        return selected;
    }

    private async Task<bool> Confirm(string title, string text, string accept = "确认")
    {
        var dialog = new WorkspaceWindow
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16
        };
        var result = false;
        panel.Children.Add(
            new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap
            }
        );
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        var yes = new Button
        {
            Content = accept
        };
        var no = new Button
        {
            Content = "取消"
        };
        actions.Children.Add(no);
        actions.Children.Add(yes);
        panel.Children.Add(actions);
        dialog.Content = panel;
        yes.Click += (_, _) =>
        {
            result = true;
            dialog.Close();
        };
        no.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
        return result;
    }
}
