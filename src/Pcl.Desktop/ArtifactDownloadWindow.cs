using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Core.Contracts.Pages;

namespace Pcl.Desktop;
// 下载与网络管理：按来源与文件校验信息取得内容，跟踪进度、取消和失败，保留可恢复缓存。
public sealed class ArtifactDownloadWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly TextBox version;
    private readonly ComboBox kind = new()
    {
        ItemsSource = new[]
        {
            "客户端 JAR",
            "服务端 JAR",
            "Forge 安装器",
            "NeoForge 安装器",
            "Fabric 安装器",
            "Quilt 安装器",
            "OptiFine 安装器",
            "LiteLoader 加载器 JAR"
        },
        SelectedIndex = 0,
        MinWidth = 190
    };
    private readonly ListBox files = new()
    {
        MinHeight = 150,
        Height = 190
    };
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBox notes = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 100
    };
    private CancellationTokenSource? operation;
    private string listedVersion = "";
    // 只注入页面契约；读取列表、校验下载和取消由适配器执行，窗口持有展示生命周期。
    public ArtifactDownloadWindow(IArtifactPageService service, string gameVersion)
    {
        Title = "下载文件";
        Width = 720;
        Height = 680;
        MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        version = new()
        {
            Text = gameVersion,
            PlaceholderText = "Minecraft 版本",
            Width = 150
        };
        var refresh = new Button
        {
            Content = "读取文件列表"
        };
        var save = new Button
        {
            Content = "另存为…"
        };
        var changelog = new Button
        {
            Content = "官方更新日志"
        };
        var cancel = new Button
        {
            Content = "取消任务",
            IsVisible = false
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "下载文件",
                FontSize = 26,
                FontWeight = FontWeight.SemiBold
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "下载到指定位置，不安装、不运行。Fabric 和 Quilt 是通用安装器；LiteLoader 为加载器文件。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    version,
                    kind,
                    refresh
                }
            }
        );
        panel.Children.Add(files);
        panel.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    save,
                    changelog,
                    cancel
                }
            }
        );
        panel.Children.Add(status);
        panel.Children.Add(notes);
        if (service is IPageTaskSource taskSource)
        {
            var tasksButton = new Button
            {
                Content = "后台任务"
            };
            tasksButton.Click += async (_, _) => await new PageTasksWindow(taskSource.Tasks).ShowDialog(this);
            panel.Children.Add(tasksButton);
        }

        Content = new ScrollViewer
        {
            Content = panel
        };
        version.TextChanged += (_, _) => files.ItemsSource = null;
        kind.SelectionChanged += (_, _) => files.ItemsSource = null;
        async Task Run(Func<CancellationToken, Task> work)
        {
            if (operation is not null)
                return;
            operation = new();
            cancel.IsVisible = true;
            version.IsEnabled = kind.IsEnabled = refresh.IsEnabled = save.IsEnabled = changelog.IsEnabled = false;
            try
            {
                status.Text = "正在读取…";
                await work(operation.Token);
            }
            catch (OperationCanceledException)
            {
                status.Text = "已取消。";
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
                version.IsEnabled = kind.IsEnabled = refresh.IsEnabled = save.IsEnabled = changelog.IsEnabled = true;
            }
        }

        // 目录返回不透明文件选择 ID，页面不保存或拼接下载 URL。
        refresh.Click += async (_, _) => await Run(
            async ct =>
            {
                listedVersion = version.Text?.Trim() ?? "";
                var list = await service.ListAsync(
                    listedVersion,
                    new[]
                    {
                        "client",
                        "server",
                        "forge",
                        "neoforge",
                        "fabric",
                        "quilt",
                        "optifine",
                        "liteloader"
                    }[kind.SelectedIndex],
                    ct
                );
                files.ItemsSource = list;
                files.SelectedIndex = list.Count > 0
                    ? 0
                    : -1;
                status.Text = list.Count > 0
                    ? "选择文件后另存为。"
                    : "官方目录没有对应文件。";
            }
        );
        // 保存位置由系统选择器交给用户；契约根据之前的选择执行原校验下载。
        save.Click += async (_, _) =>
        {
            if (files.SelectedItem is not ArtifactChoice selected)
            {
                status.Text = "请先读取并选择文件。";
                return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(
                new()
                {
                    Title = "保存下载文件",
                    SuggestedFileName = selected.FileName,
                    FileTypeChoices = [
                        new("Java 存档")
                        {
                            Patterns = [
                                "*.jar"
                            ]
                        }
                    ]
                }
            );
            if (file?.TryGetLocalPath()is not string path)
                return;
            await Run(
                async ct =>
                {
                    status.Text = "正在下载并校验…";
                    var saved = await service.SaveAsync(selected.SelectionId, path, ct);
                    status.Text = "已保存：" + saved.Path + "\n" + (saved.Verification == "author_https_and_local_sha256_only"
                        ? "作者来源；没有已知校验值，已记录本地 SHA-256。"
                        : saved.Verification == "pcl2_known_sha1"
                            ? "已通过 PCL2 已知校验值检查。"
                            : "已通过官方 SHA-1 检查。");
                }
            );
        };
        // 更新日志仍由原官方内容服务读取，页面只负责显示纯文本。
        changelog.Click += async (_, _) => await Run(
            async ct =>
            {
                var note = await service.NotesAsync(
                    version.Text?.Trim() ?? "",
                    ct
                );
                notes.Text = note is null
                    ? "官方目录没有此版本的更新日志。"
                    : note.Title + "\n\n" + note.Text;
                status.Text = note is null
                    ? "没有对应记录。"
                    : "来源：Minecraft 官方启动器内容服务";
            }
        );
        cancel.Click += (_, _) => operation?.Cancel();
        // 活动任务必须先完成或显式取消，避免释放窗口后仍回写控件。
        Closing += (_, e) =>
        {
            if (operation is not null)
            {
                e.Cancel = true;
                status.Text = "请先取消或等待当前任务。";
            }
        };
    }

    public static Task OpenAsync(Window owner, IArtifactPageService service, string gameVersion) => new ArtifactDownloadWindow(service, gameVersion).ShowDialog(owner);
}
