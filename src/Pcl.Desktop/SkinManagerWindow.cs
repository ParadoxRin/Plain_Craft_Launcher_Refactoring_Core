using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// Only display state and window cancellation live here. Account resolution and texture I/O belong to Host.
public sealed class SkinManagerWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private readonly ListBox list = new()
    {
        Width = 210,
        Margin = new Thickness(0, 0, 18, 0)
    };
    private readonly StackPanel panel = new()
    {
        Spacing = 14
    };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Image preview = new()
    {
        Width = 168,
        Height = 168,
        Stretch = Stretch.Uniform
    };
    private readonly ComboBox skinChoices = new()
    {
        MinWidth = 300
    };
    private readonly ComboBox capeChoices = new()
    {
        MinWidth = 300
    };
    private CancellationTokenSource? operation;
    private Bitmap? bitmap;
    public bool Busy => operation is not null;
    internal bool IsWorking023 => Busy;
    internal string Status023 => status.Text ?? "";
    private LauncherAccount Account => list.SelectedItem as LauncherAccount ?? throw new InvalidOperationException("请选择账户。");

    public SkinManagerWindow(IWorkspaceOperations operations)
    {
        api = new(operations);
        Title = "微软与离线皮肤";
        Width = 860;
        Height = 720;
        MinWidth = 700;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel
        {
            Margin = new Thickness(24)
        };
        var footer = new StackPanel
        {
            Spacing = 10
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
        DockPanel.SetDock(list, Dock.Left);
        root.Children.Add(list);
        root.Children.Add(
            new ScrollViewer
            {
                Content = panel
            }
        );
        Content = root;
        list.SelectionChanged += async (_, _) => await Run(ShowAccount);
        Opened += async (_, _) => await Run(
            async ct =>
            {
                var snapshot = await api.CallAsync<AccountPageSnapshot>("workspace.account.list", null, ct: ct);
                var rows = snapshot.Accounts.Where(
                    x => x.Kind != AccountKind.External
                )
                    .ToArray();
                list.ItemsSource = rows;
                list.SelectedItem = rows.FirstOrDefault(
                    x => x.Id == snapshot.SelectedId
                ) ?? rows.FirstOrDefault();
                await ShowAccount(ct);
            }
        );
        Closing += (_, e) =>
        {
            if (operation is not null)
            {
                operation.Cancel();
                e.Cancel = true;
            }
        };
        Closed += (_, _) => bitmap?.Dispose();
    }

    private async Task Run(Func<CancellationToken, Task> work)
    {
        if (Busy)
        {
            return;
        }

        operation = new();
        panel.IsEnabled = list.IsEnabled = false;
        try
        {
            await work(operation.Token);
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
            panel.IsEnabled = list.IsEnabled = true;
        }
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

    private void ShowImage(byte[]? png)
    {
        preview.Source = null;
        bitmap?.Dispose();
        bitmap = png is null
            ? null
            : new Bitmap(
                new MemoryStream(png)
            );
        preview.Source = bitmap;
    }

    private async Task ShowAccount(CancellationToken ct)
    {
        panel.Children.Clear();
        ShowImage(null);
        if (list.SelectedItem is not LauncherAccount)
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = "请先在首页添加账户。"
                }
            );
            return;
        }

        panel.Children.Add(
            new TextBlock
            {
                Text = Account.Name,
                FontSize = 23
            }
        );
        if (Account.Kind == AccountKind.Offline)
        {
            var saved = await api.CallAsync<SkinState>(
                "workspace.account.skinState",
                null,
                new
                {
                    accountId = Account.Id
                },
                ct
            );
            panel.Children.Add(
                new TextBlock
                {
                    Text = "离线皮肤仅替换本机默认材质。手臂模型调整可能改变离线 UUID；原存档不会改写。",
                    TextWrapping = TextWrapping.Wrap
                }
            );
            panel.Children.Add(preview);
            var presets = new WrapPanel
            {
                ItemSpacing = 8,
                LineSpacing = 8
            };
            var actions = new WrapPanel
            {
                ItemSpacing = 8,
                LineSpacing = 8
            };
            panel.Children.Add(presets);
            foreach (var(label, preset)in new[]
            {
                ("随机默认皮肤", "random"),
                ("Steve 纹理", "steve"),
                ("Alex 纹理", "alex")
            }

            )
            {
                presets.Children.Add(
                    Button(
                        label,
                        async token =>
                        {
                            var result = await api.CallAsync<SkinState>(
                                "workspace.account.skinPreset",
                                null,
                                new
                                {
                                    accountId = Account.Id,
                                    preset
                                },
                                token
                            );
                            ShowImage(result.Png);
                            status.Text = "已保存皮肤与手臂模型，下次离线启动应用。";
                        }
                    )
                );
            }

            var name = new TextBox
            {
                Name = "PublicSkinPlayerName",
                PlaceholderText = "正版玩家名",
                Text = saved.PlayerName
            };
            panel.Children.Add(name);
            panel.Children.Add(actions);
            SkinPreview? fetched = null;
            actions.Children.Add(
                Button(
                    "获取正版玩家皮肤",
                    async token =>
                    {
                        fetched = await api.CallAsync<SkinPreview>(
                            "workspace.account.skinFetch",
                            null,
                            new
                            {
                                accountId = Account.Id,
                                name = name.Text ?? ""
                            },
                            token
                        );
                        ShowImage(fetched.Png);
                        status.Text = $"已获取 {fetched.PlayerName} · {fetched.Model}，点击保存应用。";
                    }
                )
            );
            actions.Children.Add(
                Button(
                    "保存获取的皮肤",
                    async token =>
                    {
                        if (fetched is null)
                        {
                            throw new InvalidOperationException("请先获取玩家皮肤。");
                        }

                        await api.CallAsync<bool>(
                            "workspace.account.skinSaveFetched",
                            null,
                            new
                            {
                                accountId = Account.Id,
                                handle = fetched.Handle
                            },
                            token
                        );
                        status.Text = "已保存，下次启动应用。";
                    }
                )
            );
            actions.Children.Add(
                Button(
                    "刷新正版皮肤",
                    async token =>
                    {
                        fetched = await api.CallAsync<SkinPreview>(
                            "workspace.account.skinRefresh",
                            null,
                            new
                            {
                                accountId = Account.Id
                            },
                            token
                        );
                        ShowImage(fetched.Png);
                        status.Text = "已重新从官方获取并保存；请求失败时保留原皮肤。";
                    }
                )
            );
            actions.Children.Add(
                Button(
                    "保存皮肤到文件",
                    async token =>
                    {
                        var target = await StorageProvider.SaveFilePickerAsync(
                            new()
                            {
                                Title = "保存皮肤 PNG",
                                SuggestedFileName = Account.Name + ".png"
                            }
                        );
                        if (target?.TryGetLocalPath()is string file)
                        {
                            await api.ExportAsync(
                                "workspace.account.skinExport",
                                null,
                                new
                                {
                                    accountId = Account.Id
                                },
                                file,
                                token
                            );
                        }
                    }
                )
            );
            actions.Children.Add(
                Button(
                    "导入本地皮肤",
                    async token =>
                    {
                        var file = await PickPng();
                        if (file is null)
                        {
                            return;
                        }

                        var slim = await ChooseModel();
                        if (slim is null)
                        {
                            return;
                        }

                        var result = await api.CallAsync<SkinState>(
                            "workspace.account.skinImport",
                            null,
                            new
                            {
                                accountId = Account.Id,
                                path = file,
                                model = slim.Value
                                    ? "slim"
                                    : "wide"
                            },
                            token
                        );
                        ShowImage(result.Png);
                        status.Text = "已保存，下次离线启动应用。";
                    }
                )
            );
            actions.Children.Add(
                Button(
                    "恢复默认皮肤",
                    async token =>
                    {
                        await api.CallAsync<bool>(
                            "workspace.account.skinRemove",
                            null,
                            new
                            {
                                accountId = Account.Id
                            },
                            token
                        );
                        ShowImage(null);
                        status.Text = "下次启动移除本地皮肤资源包。";
                    }
                )
            );
            ShowImage(saved.Png);
        }
        else
        {
            panel.Children.Add(
                Button(
                    "读取账户外观",
                    ct => LoadAppearance("get", ct)
                )
            );
            panel.Children.Add(skinChoices);
            panel.Children.Add(
                Button(
                    "使用所选皮肤",
                    ct => LoadAppearance(
                        "skin.use",
                        ct,
                        value: (skinChoices.SelectedItem as SkinInfo)?.Id ?? throw new InvalidOperationException("请选择皮肤。")
                    )
                )
            );
            panel.Children.Add(
                Button(
                    "上传皮肤",
                    async token =>
                    {
                        var file = await PickPng();
                        if (file is null)
                        {
                            return;
                        }

                        var slim = await ChooseModel();
                        if (slim is not null)
                        {
                            await LoadAppearance("skin.upload", token, path: file, slim: slim.Value);
                        }
                    }
                )
            );
            panel.Children.Add(
                Button(
                    "恢复默认皮肤",
                    ct => LoadAppearance("skin.reset", ct)
                )
            );
            panel.Children.Add(capeChoices);
            panel.Children.Add(
                Button(
                    "使用所选披风",
                    ct => LoadAppearance(
                        "cape.use",
                        ct,
                        value: (capeChoices.SelectedItem as CapeInfo)?.Id ?? throw new InvalidOperationException("请选择披风。")
                    )
                )
            );
            panel.Children.Add(
                Button(
                    "隐藏披风",
                    ct => LoadAppearance("cape.hide", ct)
                )
            );
        }

        if (Account.Kind != AccountKind.Offline)
        {
            panel.Children.Add(preview);
        }
    }

    // Host returns only public profile data and a normalized bounded PNG; all authenticated HTTP stays in Host.
    private async Task LoadAppearance(
        string action,
        CancellationToken ct,
        string? value = null,
        string? path = null,
        bool slim = false
    )
    {
        var result = await api.CallAsync<SkinAppearance>(
            "workspace.account.appearance",
            null,
            new
            {
                accountId = Account.Id,
                action,
                value,
                path,
                slim
            },
            ct
        );
        skinChoices.ItemsSource = result.Profile.Skins;
        skinChoices.SelectedItem = result.Profile.Skins.FirstOrDefault(
            x => x.State == "ACTIVE"
        ) ?? result.Profile.Skins.FirstOrDefault();
        capeChoices.ItemsSource = result.Profile.Capes;
        capeChoices.SelectedItem = result.Profile.Capes.FirstOrDefault(
            x => x.State == "ACTIVE"
        ) ?? result.Profile.Capes.FirstOrDefault();
        ShowImage(result.Png);
        status.Text = $"已读取 {result.Profile.Skins.Length} 个皮肤、{result.Profile.Capes.Length} 件披风";
    }

    private async Task<string?> PickPng()
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new()
            {
                Title = "选择皮肤 PNG",
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
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<bool?> ChooseModel()
    {
        var dialog = new WorkspaceWindow
        {
            Title = "选择手臂模型",
            Width = 330,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        bool? choice = null;
        var buttons = new StackPanel
        {
            Margin = new Thickness(24),
            Orientation = Orientation.Horizontal,
            Spacing = 14
        };
        var wide = new Button
        {
            Content = "标准手臂"
        };
        var slim = new Button
        {
            Content = "细手臂"
        };
        buttons.Children.Add(wide);
        buttons.Children.Add(slim);
        dialog.Content = buttons;
        wide.Click += (_, _) =>
        {
            choice = false;
            dialog.Close();
        };
        slim.Click += (_, _) =>
        {
            choice = true;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
        return choice;
    }
}
