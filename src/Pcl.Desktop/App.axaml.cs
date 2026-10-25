using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Desktop.Bootstrap;

namespace Pcl.Desktop;
/// <summary>拥有组合根作用域；窗口借用客户端会话，正常退出先排空，再由根释放一次。</summary>
public sealed class App : Avalonia.Application
{
    private DesktopServices? services;
    private MainWindow? main;
    private Task<bool>? systemCleanup;
    internal const int ShutdownBudgetSeconds = 5;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    // 唯一服务组装发生在 UI 创建之前，数据目录锁覆盖窗口和所有安装收尾。
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new Window
            {
                Title = ProductInfo.Name,
                Width = 440,
                Height = 160,
                Content = new TextBlock
                {
                    Text = "正在连接本地核心…",
                    Margin = new Thickness(24)
                }
            };
            desktop.MainWindow = splash;
            splash.Opened += async (_, _) =>
            {
                try
                {
                    if (Program.Arguments.Contains("--in-process"))
                        throw new InvalidOperationException("--in-process 调试模式已移除，请使用独立 Host 启动。");
                    var index = Array.IndexOf(Program.Arguments, "--data-root");
                    var root = Path.GetFullPath(
                        index >= 0 && index + 1 < Program.Arguments.Length
                            ? Program.Arguments[index + 1]
                            : Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                OperatingSystem.IsMacOS()
                                    ? "Library/Application Support/PCL-MacPort"
                                    : ".pcl-mac-port"
                            )
                    );
                    services = await RemoteComposition.CreateAsync(root);
                    main = new MainWindow(
                        services.Remote.DesktopState.Paths,
                        services.Remote,
                        new AccountViewCache(services.Remote),
                        services.Remote.Installation,
                        services.Remote.Launches,
                        services.Remote.BorrowGame,
                        services.Remote.Target,
                        ShutdownScopeAsync,
                        OnSystemExit,
                        services.Remote
                    );
                    desktop.MainWindow = main;
                    main.Show();
                    splash.Close();
                    desktop.Exit += (_, _) =>
                    {
                        // 异常退出仍有活动任务时，不在回调里释放其依赖；进程退出后由 OS 回收锁。
                        if (systemCleanup?.IsFaulted == true)
                        {
                            _ = systemCleanup.Exception;
                            services.Journal.Record("scope.system-cleanup-failed");
                        }

                        if (!services.Lifetime.Released)
                        {
                            services.Journal.Record("scope.process-exit-with-live-resources");
                        }
                    };
                }
                catch (Exception ex)
                {
                    // 构造失败时尚未运行安装，零预算排空；不引入第二条共享服务 Dispose 路径。
                    if (services is not null)
                    {
                        await services.Lifetime.ShutdownAsync(Task.CompletedTask, TimeSpan.Zero);
                    }

                    desktop.MainWindow = new Window
                    {
                        Title = ProductInfo.Name + " · 无法打开",
                        Width = 540,
                        Height = 220,
                        Content = new TextBlock
                        {
                            Margin = new Thickness(24),
                            Text = Pcl.Client.WorkspaceErrors.Describe(ex),
                            TextWrapping = TextWrapping.Wrap
                        }
                    };
                    desktop.MainWindow.Show();
                    splash.Close();
                    Console.Error.WriteLine(
                        Pcl.Client.WorkspaceErrors.Describe(ex)
                    );
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // OSShutdown 由公开 WindowCloseReason 传入；不阻拦系统终止，也不等待普通退出预算。
    private void OnSystemExit(Task uiQuiescence)
    {
        services!.Journal.Record("scope.system-exit");
        systemCleanup = services.Lifetime.ShutdownAsync(uiQuiescence, TimeSpan.Zero);
    }

    // 五秒仅为退出等待预算，不保证磁盘提交必在五秒完成；超时保留记录和锁直到最终进程退出。
    private async Task<bool> ShutdownScopeAsync(Task uiQuiescence)
    {
        try
        {
            if (await services!.Lifetime.ShutdownAsync(
                uiQuiescence,
                TimeSpan.FromSeconds(ShutdownBudgetSeconds)
            ))
            {
                return true;
            }

            services.Journal.Record("scope.final-process-exit");
        }
        catch (Exception ex)
        {
            services!.Journal.Record("scope.shutdown-failed");
            Console.Error.WriteLine(
                Pcl.Client.WorkspaceErrors.Describe(ex)
            );
        }

        Environment.Exit(2);
        return false;
    }
}
