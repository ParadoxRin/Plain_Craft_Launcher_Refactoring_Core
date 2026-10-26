using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Desktop;
// 实例管理：读取实例记录和版本配置，执行隔离、备份、恢复与组件变更并校验结果。
public sealed class ComponentManagerWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly IComponentPageService components;
    private readonly ComponentSource profile;
    private readonly Action<string>? changed;
    private readonly ComboBox loader = new()
    {
        ItemsSource = new[]
        {
            "vanilla",
            "fabric",
            "quilt",
            "forge",
            "neoforge",
            "optifine",
            "liteloader"
        },
        MinWidth = 140
    };
    private readonly ComboBox version = new()
    {
        MinWidth = 250,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly CheckBox disable = new()
    {
        Content = "在新副本中禁用现有模组",
        IsChecked = false
    };
    private readonly CheckBox closedGame = new()
    {
        Content = "已关闭此实例的游戏",
        IsChecked = false
    };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly ListBox changes = new()
    {
        Height = 250
    };
    private readonly Button apply = new()
    {
        Content = "创建调整后的副本",
        IsEnabled = false
    };
    private readonly Button preview = new()
    {
        Content = "预览变更"
    };
    private readonly Button refresh = new()
    {
        Content = "读取可用版本"
    };
    private readonly Button cancel = new()
    {
        Content = "取消任务",
        IsVisible = false
    };
    // 隔离目录验收通过真实按钮事件驱动，测试不调用 Host 内部安装方法。
    internal ComboBox TestLoader => loader;
    internal CheckBox TestClosedGame => closedGame;
    internal CheckBox TestDisable => disable;
    internal Button TestPreview => preview;
    internal Button TestApply => apply;
    internal ComponentPreview? TestPlan => plan;
    internal bool TestBusy => operation is not null;
    internal string TestStatus => status.Text ?? "";

    private CancellationTokenSource? operation;
    private ComponentPreview? plan;
    // 源实例只用于显示；内部文件指纹和可执行计划留在适配器中。
    public ComponentManagerWindow(IComponentPageService service, ComponentSource profile, Action<string>? changed = null)
    {
        components = service;
        this.profile = profile;
        this.changed = changed;
        NavigationContext = profile.Id;
        Title = "调整加载器 · 新副本";
        Width = 670;
        Height = 650;
        MinWidth = 560;
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
                Text = "调整加载器",
                FontSize = 25,
                FontWeight = FontWeight.SemiBold
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = profile.Name + "\n" + profile.GameVersion + " · " + profile.Loader,
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "先备份组件，再创建新副本。原实例保留。选择 vanilla 可在副本移除加载器。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        var row = new Grid
        {
            ColumnDefinitions = new("Auto,*"),
            ColumnSpacing = 12
        };
        row.Children.Add(loader);
        Grid.SetColumn(version, 1);
        row.Children.Add(version);
        panel.Children.Add(row);
        panel.Children.Add(refresh);
        panel.Children.Add(disable);
        panel.Children.Add(closedGame);
        panel.Children.Add(
            new TextBlock
            {
                Text = "启用模组须在副本中禁用后重新检查。存档和配置将复制，Java 自动匹配；自定义 JVM 和游戏参数重置。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(preview);
        panel.Children.Add(changes);
        panel.Children.Add(apply);
        panel.Children.Add(cancel);
        panel.Children.Add(status);
        if (service is IPageTaskSource taskSource)
        {
            var tasksButton = new Button
            {
                Content = "后台任务"
            };
            tasksButton.Click += async (_, _) => await new PageTasksWindow(taskSource.Tasks).PresentAsync(this);
            panel.Children.Add(tasksButton);
        }

        Content = new ScrollViewer
        {
            Content = panel
        };
        loader.SelectedItem = profile.Loader;
        loader.SelectionChanged += (_, _) =>
        {
            Invalidate();
            version.ItemsSource = null;
        };
        version.SelectionChanged += (_, _) => Invalidate();
        disable.IsCheckedChanged += (_, _) => Invalidate();
        closedGame.IsCheckedChanged += (_, _) => apply.IsEnabled = operation is null && plan?.CanApply == true && closedGame.IsChecked == true;
        refresh.Click += async (_, _) => await Run(
            async ct =>
            {
                var values = await components.VersionsAsync(
                    loader.SelectedItem?.ToString() ?? "vanilla",
                    ct
                );
                version.ItemsSource = values;
                version.SelectedIndex = values.Count > 0
                    ? 0
                    : -1;
                status.Text = values.Count > 0
                    ? "选择目标版本后预览变更。"
                    : "此 Minecraft 版本没有对应加载器。";
            }
        );
        // 预览返回显示 DTO 与不透明计划 ID，冲突未解除时不开放提交。
        preview.Click += async (_, _) => await Run(
            async ct =>
            {
                var selectedLoader = loader.SelectedItem?.ToString() ?? "vanilla";
                var selectedVersion = version.SelectedItem?.ToString();
                var disableMods = disable.IsChecked == true;
                plan = await components.PreviewAsync(selectedLoader, selectedVersion, disableMods, ct);
                changes.ItemsSource = new[]
                {
                    "目标：" + plan.TargetLoader + " " + (plan.TargetVersion ?? ""),
                    "复制 " + plan.FileCount + " 个数据文件",
                    "原实例：" + plan.SourceDirectory,
                    "新副本：" + plan.TargetDirectory,
                    "组件备份：自动保存到启动器数据目录"
                }.Concat(
                    plan.ActiveMods.Select(
                        x => (plan.DisableMods
                            ? "副本禁用："
                            : "待确认：") + x
                    )
                )
                    .Concat(plan.Conflicts)
                    .ToArray();
                status.Text = plan.CanApply
                    ? "检查变更后创建副本。原实例可作为回退。"
                    : string.Join("\n", plan.Conflicts);
            }
        );
        // 原迁移流程再校验计划、备份和创建副本；成功后只回传新实例 ID。
        apply.Click += async (_, _) =>
        {
            if (plan is null || closedGame.IsChecked != true)
                return;
            // 确认期间禁止再次提交；关闭弹窗等同取消，不向 Host 发送 apply。
            var selectedPlan = plan;
            apply.IsEnabled = false;
            if (!selectedPlan.SuppressConfirmation)
            {
                var confirmation = await new ComponentCopyConfirmation(selectedPlan).PresentAsync<CopyConfirmation?>(this);
                if (confirmation?.Accepted != true)
                {
                    apply.IsEnabled = operation is null && plan?.CanApply == true && closedGame.IsChecked == true;
                    status.Text = "已取消，未开始创建副本。";
                    return;
                }

                if (confirmation.Suppress)
                {
                    await Run(
                        ct => components.SetConfirmationSuppressedAsync(true, ct)
                    );
                }
            }

            await Run(
                async ct =>
                {
                    var result = await components.ApplyAsync(
                        selectedPlan.PlanId,
                        new Progress<VersionInstallProgress>(
                            x => status.Text = x.Stage + (x.Total > 0
                                ? $" · {x.Completed}/{x.Total}"
                                : "")
                        ),
                        ct
                    );
                    changed?.Invoke(result.ProfileId);
                    plan = null;
                    changes.ItemsSource = new[]
                    {
                        result.ProfileName,
                        "禁用模组：" + result.DisabledMods,
                        "备份：" + result.Backup,
                        "记录：" + result.Receipt,
                        "原实例：" + result.SourceDirectory,
                        "新副本：" + result.TargetDirectory
                    };
                    status.Text = "新副本已创建。请先检查模组，再启动游戏。";
                    var openCopy = new Button
                    {
                        Content = "打开新副本"
                    };
                    openCopy.Click += async (_, _) =>
                    {
                        var folder = await StorageProvider.TryGetFolderFromPathAsync(
                            new Uri(result.TargetDirectory)
                        );
                        if (folder is not null)
                        {
                            await Launcher.LaunchFileAsync(folder);
                        }
                    };
                    panel.Children.Add(openCopy);
                }
            );
        };
        cancel.Click += (_, _) => operation?.Cancel();
        // 活动任务必须先完成或显式取消，避免释放窗口后仍回写控件。
    }

    public static Task OpenAsync(
        Window owner,
        IComponentPageService service,
        ComponentSource profile,
        Action<string>? changed = null
    ) => new ComponentManagerWindow(service, profile, changed).PresentAsync(owner);
    private void Invalidate()
    {
        plan = null;
        apply.IsEnabled = false;
        changes.ItemsSource = null;
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null || ViewClosing)
            return;
        operation = new();
        using var viewActivity = ViewLifetime.Begin(operation.Cancel);
        cancel.IsVisible = true;
        loader.IsEnabled = version.IsEnabled = disable.IsEnabled = preview.IsEnabled = refresh.IsEnabled = closedGame.IsEnabled = false;
        apply.IsEnabled = false;
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            status.Text = "已取消，源实例保留。";
        }
        catch (Exception ex)
        {
            status.Text = ex.Message;
        }
        finally
        {
            operation.Dispose();
            operation = null;
            cancel.IsVisible = false;
            loader.IsEnabled = version.IsEnabled = disable.IsEnabled = preview.IsEnabled = refresh.IsEnabled = closedGame.IsEnabled = true;
            apply.IsEnabled = plan?.CanApply == true && closedGame.IsChecked == true;
        }
    }
}
