using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
// 资源发现：查询并转换下载平台元数据，按游戏版本和加载器筛选资源，保留来源与可用性信息。
public sealed class CurseForgeWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private readonly GameProfile profile;
    private readonly Action<string> onInstalled;
    private readonly Action<string> open;
    private readonly TextBox key = new()
    {
        PlaceholderText = "自己的 CurseForge API Key（只存本次 Host 内存，30 分钟过期）",
        PasswordChar = '●'
    };
    private readonly ComboBox kind = new()
    {
        ItemsSource = new[]
        {
            "模组",
            "光影",
            "资源包",
            "整合包"
        },
        SelectedIndex = 0,
        MinWidth = 100
    };
    private readonly TextBox query = new()
    {
        PlaceholderText = "项目名称或数字 ID",
        MinWidth = 200
    };
    private readonly ListBox projects = new()
    {
        Height = 250
    };
    private readonly ComboBox versions = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock details = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly List<Button> buttons = new();
    private string? secretHandle;
    private SearchPage? session;
    public bool Busy => cancellation is not null;

    private CancellationTokenSource? cancellation;
    private int offset;
    private int lastCount;
    private ResourceKind Kind => kind.SelectedIndex switch
    {
        1 => ResourceKind.Shader,
        2 => ResourceKind.ResourcePack,
        3 => ResourceKind.Modpack,
        _ => ResourceKind.Mod
    };

    public CurseForgeWindow(
        IWorkspaceOperations operations,
        GameProfile profile,
        Action<string> onInstalled,
        Action<string> open
    )
    {
        api = new(operations);
        this.profile = profile;
        this.onInstalled = onInstalled;
        this.open = open;
        NavigationContext = profile.Id;
        Title = "CurseForge · " + profile.GameVersion;
        Width = 760;
        Height = 730;
        MinWidth = 600;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "CurseForge",
                FontSize = 26
            }
        );
        panel.Children.Add(key);
        panel.Children.Add(
            new TextBlock
            {
                Text = $"Minecraft {profile.GameVersion} · {profile.Loader}。需要合法 API Key；未提供时可在整合包导入页匹配本地文件。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        var search = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        search.Children.Add(kind);
        search.Children.Add(query);
        search.Children.Add(
            Button(
                "搜索",
                async () =>
                {
                    offset = 0;
                    await Search();
                }
            )
        );
        panel.Children.Add(search);
        panel.Children.Add(projects);
        projects.SelectionChanged += async (_, _) =>
        {
            if (cancellation is null)
                await Run(
                    async () =>
                    {
                        if (projects.SelectedItem is not SearchHit selected)
                            return;
                        details.Text = selected.Description;
                        var found = await api.CallAsync<SearchVersions>(
                            "workspace.search.versions",
                            profile.Id,
                            new
                            {
                                selection = selected.Selection,
                                secretHandle = await SecretAsync()
                            },
                            cancellation!.Token
                        );
                        versions.ItemsSource = found.Releases;
                        versions.SelectedIndex = 0;
                    }
                );
        };
        var paging = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        paging.Children.Add(
            Button(
                "上一页",
                async () =>
                {
                    offset = Math.Max(0, offset - 20);
                    await Search();
                }
            )
        );
        paging.Children.Add(
            Button(
                "下一页",
                async () =>
                {
                    if (lastCount < 20)
                    {
                        status.Text = "已到最后一页";
                        return;
                    }

                    offset += 20;
                    await Search();
                }
            )
        );
        panel.Children.Add(paging);
        panel.Children.Add(details);
        panel.Children.Add(versions);
        var actions = new WrapPanel();
        actions.Children.Add(
            Button(
                "项目主页",
                () =>
                {
                    if (projects.SelectedItem is SearchHit project)
                        open(project.WebUrl!);
                    return Task.CompletedTask;
                }
            )
        );
        actions.Children.Add(
            Button(
                "文件下载页",
                () =>
                {
                    if (versions.SelectedItem is SearchRelease release)
                        open(release.Page!);
                    return Task.CompletedTask;
                }
            )
        );
        actions.Children.Add(
            Button(
                "匹配手动下载文件",
                async () =>
                {
                    var release = Selected();
                    var files = await StorageProvider.OpenFilePickerAsync(
                        new()
                        {
                            Title = "匹配 " + release.FileName
                        }
                    );
                    var file = files.FirstOrDefault()?.TryGetLocalPath();
                    if (file is null)
                        return;
                    await api.CallAsync<bool>(
                        "workspace.search.match",
                        profile.Id,
                        new
                        {
                            selection = release.Selection,
                            path = file
                        },
                        cancellation!.Token
                    );
                    status.Text = "已按官方 SHA-1 校验手动文件";
                }
            )
        );
        actions.Children.Add(
            Button("安装（含前置）", Install)
        );
        panel.Children.Add(actions);
        var cancel = new Button
        {
            Content = "取消任务"
        };
        cancel.Click += (_, _) => cancellation?.Cancel();
        panel.Children.Add(cancel);
        panel.Children.Add(status);
        Content = new ScrollViewer
        {
            Content = panel
        };
        ViewLifetime.OnStop(
            () =>
            {
                key.Text = "";
                return Task.CompletedTask;
            }
        );
        key.TextChanged += (_, _) =>
        {
            secretHandle = null;
            session = null;
            projects.ItemsSource = null;
            versions.ItemsSource = null;
        };
        query.TextChanged += (_, _) =>
        {
            session = null;
        };
        kind.SelectionChanged += (_, _) =>
        {
            session = null;
            projects.ItemsSource = null;
            versions.ItemsSource = null;
        };
    }

    private async Task<string?> SecretAsync()
    {
        if (string.IsNullOrWhiteSpace(key.Text))
        {
            throw new InvalidOperationException("请填写自己的 CurseForge API Key；无密钥时可使用整合包本地导入。");
        }

        return secretHandle ??= await api.SecretAsync(key.Text, cancellation!.Token);
    }

    private SearchRelease Selected() => versions.SelectedItem as SearchRelease ?? throw new InvalidOperationException("请选择文件版本。");
    // Dedicated CF page reuses Host search and private selection storage, without sending executable URLs back.
    private async Task Search()
    {
        session = await api.CallAsync<SearchPage>(
            "workspace.search.page",
            profile.Id,
            new
            {
                session = offset == 0
                    ? null
                    : session?.Session,
                page = offset / 20,
                kind = (int)Kind,
                query = query.Text ?? "",
                provider = "curseforge",
                secretHandle = await SecretAsync()
            },
            cancellation!.Token
        );
        projects.ItemsSource = session.Hits;
        versions.ItemsSource = null;
        lastCount = session.HasNext
            ? 20
            : 0;
        status.Text = $"第 {session.Page + 1} 页 · {session.Hits.Length} 项";
    }

    private async Task Install()
    {
        var release = Selected();
        var ct = cancellation!.Token;
        var secret = await SecretAsync();
        var result = await api.CallAsync<SearchInstalled>(
            "workspace.search.install",
            profile.Id,
            new
            {
                selection = release.Selection,
                secretHandle = secret
            },
            ct
        );
        if (result.Pack is { } pack)
        {
            var selected = await PackSelectionWindow.SelectAsync(this, pack.Plan, false, ct);
            var instance = await api.CallAsync<GameProfile>(
                "workspace.packs.install",
                null,
                new
                {
                    handle = pack.Handle,
                    options = new PackImportOptions(SelectedOptionalFiles: selected),
                    secretHandle = secret
                },
                ct
            );
            onInstalled(instance.Id);
            status.Text = "已安装：" + instance.Name;
        }
        else
        {
            status.Text = $"已安装并校验 · {result.Files} 个文件\n" + string.Join("\n", result.Warnings);
        }
    }

    private Button Button(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 0)
        };
        buttons.Add(button);
        button.Click += async (_, _) => await Run(action);
        return button;
    }

    private async Task Run(Func<Task> action)
    {
        if (cancellation is not null || ViewClosing)
            return;
        cancellation = new();
        using var viewActivity = ViewLifetime.Begin(cancellation.Cancel);
        foreach (var button in buttons)
            button.IsEnabled = false;
        projects.IsEnabled = versions.IsEnabled = kind.IsEnabled = key.IsEnabled = query.IsEnabled = false;
        try
        {
            await action();
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
            cancellation.Dispose();
            cancellation = null;
            foreach (var button in buttons)
                button.IsEnabled = true;
            projects.IsEnabled = versions.IsEnabled = kind.IsEnabled = key.IsEnabled = query.IsEnabled = true;
        }
    }
}
