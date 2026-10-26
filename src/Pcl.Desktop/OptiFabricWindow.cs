using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Desktop;
// 游戏安装：根据官方版本与加载器元数据规划组件，校验下载文件，写入可复现的实例配置。
public sealed class OptiFabricWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly TextBlock status = new()
    {
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock selected = new()
    {
        Text = "未选择文件",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBox key = new()
    {
        PlaceholderText = "CurseForge API Key（可选，仅本次使用）",
        PasswordChar = '●'
    };
    private string? localFile;
    private CancellationTokenSource? operation;
    // [实现] OptiFabricWindow 构造 的实现：执行 Title = "OptiFabric 兼容实例"；执行 Width = 650；执行 Height = 510。
    public OptiFabricWindow(IWorkspaceOperations operations, Action<GameProfile>? changed = null)
    {
        var api = new WorkspaceApi(operations);
        Title = "OptiFabric 兼容实例";
        Width = 650;
        Height = 510;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "OptiFabric 兼容实例",
                FontSize = 25,
                FontWeight = FontWeight.SemiBold
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "Minecraft 1.20.1 · Fabric 0.16.14\nOptiFabric 1.14.3 · OptiFine I6",
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "创建独立实例。此组合已在 Apple Silicon 启动验证；其他模组与光影需另行检查，不支持 Iris / Sodium 共存。",
                TextWrapping = TextWrapping.Wrap
            }
        );
        var author = new Button
        {
            Content = "打开作者下载页"
        };
        var choose = new Button
        {
            Content = "选择 OptiFabric 1.14.3 文件"
        };
        var install = new Button
        {
            Content = "创建兼容实例"
        };
        var cancel = new Button
        {
            Content = "取消任务",
            IsVisible = false
        };
        panel.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    author,
                    choose
                }
            }
        );
        panel.Children.Add(selected);
        panel.Children.Add(key);
        panel.Children.Add(
            new TextBlock
            {
                Text = "可从作者页下载后选择文件，或使用自己的 CurseForge API Key。OptiFine 将从作者官网下载。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            }
        );
        panel.Children.Add(
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    install,
                    cancel
                }
            }
        );
        panel.Children.Add(status);
        Content = new ScrollViewer
        {
            Content = panel
        };
        author.Click += (_, _) =>
        {
            // [实现] 回调触发时：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：执行 Process.Start( new ProcessStartInfo("https://www.curseforge.com/minecraft/mc-mods/optifabric/files/5025647") { UseShellExecute = true …。
                Process.Start(
                    new ProcessStartInfo("https://www.curseforge.com/minecraft/mc-mods/optifabric/files/5025647")
                    {
                        UseShellExecute = true
                    }
                );
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 status.Text = ex.Message。
                status.Text = ex.Message;
            }
        };
        choose.Click += async (_, _) =>
        {
            // [实现] 回调触发时：执行受异常处理保护的操作。
            if (operation is not null || ViewClosing)
                return;
            operation = new();
            using var viewActivity = ViewLifetime.Begin(operation.Cancel);
            choose.IsEnabled = install.IsEnabled = key.IsEnabled = false;
            cancel.IsVisible = true;
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(
                    new()
                    {
                        Title = "选择 OptiFabric",
                        AllowMultiple = false,
                        FileTypeFilter = [
                            new("Java 存档")
                            {
                                Patterns = [
                                    "*.jar"
                                ]
                            }
                        ]
                    }
                );
                if (files.FirstOrDefault()?.TryGetLocalPath()is not string file)
                    return;
                await api.CallAsync<bool>(
                    "workspace.desktop.optiFabricValidate",
                    null,
                    new
                    {
                        path = file
                    },
                    operation.Token
                );
                localFile = file;
                selected.Text = Path.GetFileName(file) + " · 与已验证文件一致";
            }
            catch (OperationCanceledException)
            {
                status.Text = "已取消";
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 status.Text = ex.Message。
                status.Text = ex.Message;
            }
            finally
            {
                operation.Dispose();
                operation = null;
                choose.IsEnabled = install.IsEnabled = key.IsEnabled = true;
                cancel.IsVisible = false;
            }
        };
        install.Click += async (_, _) =>
        {
            // [实现] 回调触发时：按 operation is not null 分支处理；执行 operation = new()；执行 choose.IsEnabled = install.IsEnabled = key.IsEnabled = false。
            if (operation is not null || ViewClosing)
                return;
            operation = new();
            using var viewActivity = ViewLifetime.Begin(operation.Cancel);
            choose.IsEnabled = install.IsEnabled = key.IsEnabled = false;
            cancel.IsVisible = true;
            try
            {
                var secretHandle = string.IsNullOrWhiteSpace(key.Text)
                    ? null
                    : await api.SecretAsync(key.Text, operation.Token);
                var profile = await api.CallAsync<GameProfile>(
                    "workspace.desktop.optiFabricInstall",
                    null,
                    new
                    {
                        path = localFile,
                        secretHandle
                    },
                    operation.Token,
                    new Progress<VersionInstallProgress>(
                        p => status.Text = p.Stage
                    )
                );
                changed?.Invoke(profile);
                status.Text = "已创建：" + profile.Name;
            }
            catch (OperationCanceledException)
            {
                // [实现] 捕获 (OperationCanceledException) 后：执行 status.Text = "已取消。"。
                status.Text = "已取消。";
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 status.Text = ex.Message。
                status.Text = ex.Message;
            }
            finally
            {
                // [实现] 无论成功或失败，退出前：执行 key.Text = ""；执行 operation.Dispose()；执行 operation = null。
                key.Text = "";
                operation.Dispose();
                operation = null;
                choose.IsEnabled = install.IsEnabled = key.IsEnabled = true;
                cancel.IsVisible = false;
            }
        };
        cancel.Click += (_, _) => operation?.Cancel();
    }
}
