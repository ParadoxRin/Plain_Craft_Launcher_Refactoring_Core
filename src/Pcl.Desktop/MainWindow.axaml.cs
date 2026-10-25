using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Installation;

namespace Pcl.Desktop;
// 应用入口与主界面：初始化服务和界面，将命令或用户事件路由到对应核心功能。
public partial class MainWindow : Window
{
    private readonly Pcl.Client.LauncherSession? remote;
    internal Pcl.Client.LauncherSession? RemoteSession => remote;
    internal bool CanUseHost => remote is null || remote.IsAvailable;

    private readonly DesktopPaths paths;
    private readonly Pcl.Client.LauncherSession service;
    private readonly IVersionInstallService installation;
    private readonly InstallationTarget installationTarget;
    private readonly Pcl.Core.Contracts.Launch.IGameLaunchOperations gameLaunch;
    private readonly Func<string, Pcl.Core.Contracts.Launch.IGameSession?> borrowGame;
    private readonly Func<Task, Task<bool>> shutdownScope;
    private readonly Action<Task> systemExit;
    private TaskCompletionSource? uiActionCompletion;
    private bool closePending;
    private bool closeApproved;
    private bool viewReleased;
    private VersionInstallResult? lastInstallationResult;
    internal Task PendingUiAction => uiActionCompletion?.Task ?? Task.CompletedTask;
    private Pcl.Client.WorkspaceApi DesktopApi => new(
        Bootstrap.WorkspacePages.Operations(this, service)
    );
    private IProgress<VersionInstallProgress> DesktopProgress => new Progress<VersionInstallProgress>(
        value => Progress.Report(
            new(value.Stage, value.Completed, value.Total, value.Bytes, value.TotalBytes)
        )
    );

    private readonly ResourceBrowser resources;
    private CancellationTokenSource? operation;
    private AccountViewCache? accounts;
    private bool refreshingAccounts;
    private string? loginUserCode;
    private Pcl.Core.Contracts.Launch.IGameSession? game;
    private Bitmap? instanceArtwork;
    private bool installed;
    private bool gameStoppedByUser;
    // 通过 Contract 注入安装入口，借用组合根的客户端会话；窗口仅拥有自身展示资源。
    public MainWindow(
        DesktopPaths paths,
        Pcl.Client.LauncherSession session,
        AccountViewCache accounts,
        IVersionInstallService installation,
        Pcl.Core.Contracts.Launch.IGameLaunchOperations gameLaunch,
        Func<string, Pcl.Core.Contracts.Launch.IGameSession?> borrowGame,
        InstallationTarget installationTarget,
        Func<Task, Task<bool>> shutdownScope,
        Action<Task> systemExit,
        Pcl.Client.LauncherSession? remote = null
    )
    {
        InitializeComponent();
        this.remote = remote;
        if (remote is not null)
        {
            remote.AccessChanged += () =>
            {
                void ApplyAccess()
                {
                    foreach (var window in OwnedWindows)
                    {
                        if (window is not TaskCenterWindow && window is not IHostWorkspace)
                        {
                            window.IsEnabled = remote.IsAvailable;
                        }
                    }
                }

                if (Dispatcher.UIThread.CheckAccess())
                {
                    ApplyAccess();
                }
                else
                {
                    Dispatcher.UIThread.Post(ApplyAccess);
                }
            };
        }

        // Host 断线时关闭提交入口；停止、取消和复制设备码仍可使用。游戏与任务忙碌状态另由 UpdateButtons 控制。
        AddHandler(
            Button.ClickEvent,
            (_, e) =>
            {
                if (remote is { IsAvailable: false } && e.Source is Button button && button != CancelButton && button != StopGameButton && button != CopyCodeButton)
                {
                    e.Handled = true;
                }
            },
            RoutingStrategies.Tunnel
        );
        // 隔离分组验收窗口不抢占正在手工验收的游戏焦点。
        if (Program.Arguments.Contains("--acceptance-instance-groups"))
        {
            // [实现] 条件成立（Program.Arguments.Contains("--acceptance-instance-groups")）时：执行 ShowActivated = false。
            ShowActivated = false;
        }

        EnableFileDrop();
        this.paths = paths;
        service = session;
        this.installation = installation;
        this.gameLaunch = gameLaunch;
        this.borrowGame = borrowGame;
        this.installationTarget = installationTarget;
        this.shutdownScope = shutdownScope;
        this.systemExit = systemExit;
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
        try
        {
            // [实现] 在异常保护范围内：执行 ApplyAppearance( service.DesktopState.Appearance )。
            ApplyAppearance(service.DesktopState.Appearance);
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 ApplyAppearance( new() )；执行 Log("外观设置加载失败：" + ex.Message)。
            ApplyAppearance(
                new()
            );
            Log("外观设置加载失败：" + ex.Message);
        }

        resources = new(
            RunAsync,
            async ct => Bootstrap.WorkspacePages.Describe(
                await EnsureFabricAsync(ct)
            ),
            () => Bootstrap.WorkspacePages.Describe(
                ActiveFabricProfile()
            ),
            Open,
            () => operation?.Cancel(),
            async (release, optional, ct) => Bootstrap.WorkspacePages.Describe(
                await InstallBrowserPackAsync(release.Id, null, optional, ct)
            ),
            async (file, optional, ct) => Bootstrap.WorkspacePages.Describe(
                await InstallBrowserPackAsync(null, file, optional, ct)
            ),
            () => new Pcl.Client.WorkspaceApi(
                Bootstrap.WorkspacePages.Operations(this, service)
            )
        );
        ResourcePanel.Content = resources;
        MicrosoftLoginInfo.Text = service.DesktopState.MicrosoftConfigured
            ? "在微软网页安全登录"
            : "微软登录准备中";
        this.accounts = accounts;
        // 恢复上次选择的账户类型；首次使用直接提供离线名称输入。
        AccountTypeBox.SelectedIndex = (int)(accounts.Accounts.FirstOrDefault(
            item => item.Id == accounts.SelectedId
        )?.Kind ?? AccountKind.Offline);
        RefreshAccounts();
        RefreshProfiles(service.DesktopState.SelectedProfile);
        InitializeWindowsLayout();
        Opened += async (_, _) =>
        {
            // [实现] 回调触发时：执行 await RefreshInstalledAsync()；执行 Console.WriteLine("DESKTOP_WINDOW_OPENED")；按 Program.Arguments.Contains("--acceptance-visibility") 分支处理。
            await RefreshInstalledAsync();
            Console.WriteLine("DESKTOP_WINDOW_OPENED");
            if (remote?.RecoveredGames.FirstOrDefault()is { } recovered && game is null)
            {
                try
                {
                    StatusText.Text = "已恢复正在运行的游戏会话。";
                    await ObserveGameAsync(
                        remote.BorrowGame(recovered.Details.GameProcessId)!,
                        false
                    );
                }
                catch (Exception ex)
                {
                    Log("恢复游戏会话失败：" + ex.Message);
                    if (!IsVisible)
                    {
                        Show();
                    }
                }

                return;
            }

            if (Program.Arguments.Contains("--license0361-smoke"))
            {
                await License0361SmokeAsync();
                return;
            }
            if (Program.Arguments.Contains("--offline-script-smoke"))
            {
                await OfflineScriptSmokeAsync();
                return;
            }

            if (Program.Arguments.Contains("--shell-boundary-smoke"))
            {
                await ShellBoundarySmokeAsync();
                return;
            }

            if (Program.Arguments.Contains("--main-link-smoke"))
            {
                await MainLinkSmokeAsync();
                return;
            }

            if (Program.Arguments.Contains("--browser-account-smoke"))
            {
                await BrowserAccountSmokeAsync();
                return;
            }

            if (Program.Arguments.Contains("--iris036-smoke"))
            {
                await Iris036SmokeAsync();
                return;
            }

            if (Program.Arguments.Contains("--copy035-smoke"))
            {
                await Copy035SmokeAsync();
                return;
            }

            if (Program.Arguments.Contains("--remote-desktop-smoke"))
            {
                await RemoteDesktopSmokeAsync();
                return;
            }
        };
        Closing += async (_, e) =>
        {
            if (closeApproved)
            {
                return;
            }

            if (e.CloseReason == WindowCloseReason.OSShutdown)
            {
                closeApproved = true;
                systemExit(
                    PrepareForExit()
                );
                return;
            }

            e.Cancel = true;
            await RequestApplicationExitAsync();
        };
        Closed += (_, _) => ReleaseViewResources();
    }

    // 视图隐藏保留原游戏策略；真实退出先等待安装及 RunAsync 的 finally，避免关闭后使用已释放服务。
    internal async Task RequestApplicationExitAsync()
    {
        if (game is not null && !game.HasExited)
        {
            Hide();
            return;
        }

        if (closePending)
        {
            return;
        }

        closePending = true;
        IsEnabled = false;
        var pending = PrepareForExit();
        if (await shutdownScope(pending))
        {
            closeApproved = true;
            Close();
        }
    }

    // 系统终止只能尽力取消并保留原安装记录；此方法不伪造已取消或已中断终态。
    internal Task PrepareForExit()
    {
        operation?.Cancel();
        foreach (var ownedWindow in OwnedWindows.ToArray())
        {
            ownedWindow.Close();
        }

        return PendingUiAction;
    }

    // 只释放窗口自己拥有的资源；共享 MinecraftService 与数据锁由组合根单独拥有。
    private void ReleaseViewResources()
    {
        if (viewReleased || !PendingUiAction.IsCompleted)
        {
            return;
        }

        viewReleased = true;
        multiplayer?.Close();
        musicPlayer.Dispose();
        backgroundBitmap?.Dispose();
        instanceArtwork?.Dispose();
        resources.Dispose();
    }

    // [实现] RefreshInstalledAsync 的实现：准备 var selected = ProfileBox.SelectedItem as GameProfile；执行受异常处理保护的操作；执行 UpdateButtons()。
    private async Task RefreshInstalledAsync()
    {
        var selected = ProfileBox.SelectedItem as GameProfile;
        try
        {
            if (selected is null)
                throw new FileNotFoundException();
            installed = (await DesktopApi.CallAsync<ProfileAvailability>(
                "workspace.shell.profile",
                null,
                new
                {
                    id = selected.Id
                }
            )).Installed;
            if (!installed)
                throw new FileNotFoundException();
            InstallState.Text = "已安装";
        }
        catch
        {
            // [实现] 捕获 空 后：执行 installed = false；执行 InstallState.Text = "未安装"。
            installed = false;
            InstallState.Text = "未安装";
        }

        UpdateButtons();
    }

    // [实现] UpdateButtons 的实现：准备 var busy = operation is not null；执行 StopGameButton.IsVisible = game is not null；执行 SkinsButton.IsEnabled = !busy && game is null。
    private void UpdateButtons()
    {
        var busy = operation is not null;
        StopGameButton.IsVisible = game is not null;
        SkinsButton.IsEnabled = !busy && game is null;
        NavMcmod.IsEnabled = NavUnified.IsEnabled = NavInstances.IsEnabled = NavLocal.IsEnabled = NavPackTools.IsEnabled = NavCurseForge.IsEnabled = !busy && game is null;
        InstallButton.IsEnabled = !busy && game is null;
        LoginButton.IsEnabled = !busy && game is null;
        foreach (var control in new Control[]
        {
            AccountTypeBox,
            AccountBox,
            OfflineNameBox,
            RememberLoginBox,
            LogoutButton,
            RemoveAccountButton
        }

        )
            control.IsEnabled = !busy && game is null;
        FabricButton.IsEnabled = !busy && game is null;
        VersionButton.IsEnabled = !busy && game is null;
        MacSetupButton.IsEnabled = !busy && game is null;
        ProfileBox.IsEnabled = !busy && game is null;
        resources.SetBusy(busy || game is not null);
        foreach (var nav in new[]
        {
            NavHome,
            NavMods,
            NavShaders,
            NavPacks,
            NavDataPacks,
            NavModpacks,
            NavManaged
        }

        )
            nav.IsEnabled = !busy && game is null;
        // 离线模式按输入名称启用；微软模式仍检查真实凭据，注销不能退化为离线启动。
        var canLogin = AccountTypeBox.SelectedIndex == 0
            ? IsOfflineNameValid()
            : AccountBox.SelectedItem is LauncherAccount { Kind: AccountKind.Microsoft } selectedAccount && accounts?.HasMicrosoftCredential(selectedAccount.Id) == true;
        // 输入新离线名称时展示实际将启动的身份；不能把尚未保存的名称误当成删除旧账户的目标。
        var offlineName = (OfflineNameBox.Text ?? "").Trim();
        if (AccountTypeBox.SelectedIndex == 0)
        {
            // [实现] 条件成立（AccountTypeBox.SelectedIndex == 0）时：执行 AccountText.Text = offlineName.Length == 0 ? "添加离线账户" : "离线账户 · " + offlineName。
            AccountText.Text = offlineName.Length == 0
                ? "添加离线账户"
                : "离线账户 · " + offlineName;
        }

        RemoveAccountButton.IsEnabled = !busy && game is null && AccountBox.SelectedItem is LauncherAccount removableAccount && (AccountTypeBox.SelectedIndex != 0 || removableAccount.Name == offlineName);
        LogoutButton.IsEnabled = !busy && game is null && AccountBox.SelectedItem is LauncherAccount { Kind: not AccountKind.Offline };
        LaunchButton.IsEnabled = installed && canLogin && !busy && game is null;
        CancelButton.IsVisible = busy && game is null;
    }

    // [实现] Log 的实现：执行 Dispatcher.UIThread.Post( () => { var old = LogBox.Text ?? ""; if (old.Length > 24000) old = ol…。
    private void Log(string text) => Dispatcher.UIThread.Post(
        () =>
        {
            // [实现] 回调触发时：准备 var old = LogBox.Text ?? ""；按 old.Length > 24000 分支处理；执行 LogBox.Text = old + Environment.NewLine + text。
            var old = LogBox.Text ?? "";
            if (old.Length > 24000)
                old = old[^18000..];
            LogBox.Text = old + Environment.NewLine + text;
            LogBox.CaretIndex = LogBox.Text.Length;
        }
    );
    private IProgress<VersionInstallProgress> Progress => new Progress<VersionInstallProgress>(
        p => Dispatcher.UIThread.Post(
            () =>
            {
                // [实现] 回调触发时：执行 InstallProgressBar.IsIndeterminate = p.TotalBytes == 0；执行 InstallProgressBar.Value = (p.Total > 0 ? (double)p.Completed/p.Total : 0) * 100；执行 StatusText.Text = p.Total > 0 ? $"{p.Stage} · {p.Completed:N0}/{p.Total:N0} · {p.Bytes / 104857…。
                InstallProgressBar.IsIndeterminate = p.TotalBytes == 0;
                InstallProgressBar.Value = (p.Total > 0
                    ? (double)p.Completed / p.Total
                    : 0) * 100;
                StatusText.Text = p.Total > 0
                    ? $"{p.Stage} · {p.Completed:N0}/{p.Total:N0} · {p.Bytes / 1048576.0:F0}/{p.TotalBytes / 1048576.0:F0} MiB"
                    : p.Stage;
            }
        )
    );

    // 跟踪界面操作及 finally 的完成，供退出协调等待；取消只请求停止，收尾完成后才释放依赖。
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (operation is not null || closePending)
            return;
        operation = new();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uiActionCompletion = completion;
        InstallProgressBar.IsVisible = true;
        UpdateButtons();
        try
        {
            // [实现] 在异常保护范围内：执行 await action(operation.Token)。
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            // [实现] 捕获 (OperationCanceledException) 后：执行 StatusText.Text = "已取消"；执行 Log("任务已取消。")；执行 resources.Report("已取消")。
            StatusText.Text = "已取消";
            Log("任务已取消。");
            resources.Report("已取消");
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 Console.Error.WriteLine( ex.GetType() .Name + ": " + ex.Message )；执行 StatusText.Text = ex.Message；执行 Log( ex.GetType() .Name + ": " + ex.Message )。
            Console.Error.WriteLine(
                ex.GetType()
                    .Name + ": " + ex.Message
            );
            StatusText.Text = ex.Message;
            Log(
                ex.GetType()
                    .Name + ": " + ex.Message
            );
            resources.Report(ex.Message);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：执行 InstallProgressBar.IsIndeterminate = false；执行 InstallProgressBar.IsVisible = false；执行 RefreshProfiles()。
                InstallProgressBar.IsIndeterminate = false;
                InstallProgressBar.IsVisible = false;
                RefreshProfiles();
                await RefreshInstalledAsync();
            }
            finally
            {
                // [实现] 无论成功或失败，退出前：执行 operation.Dispose()；执行 operation = null；执行 UpdateButtons()。
                try
                {
                    operation.Dispose();
                    operation = null;
                    UpdateButtons();
                }
                finally
                {
                    completion.TrySetResult();
                }
            }
        }
    }

    // 新安装经 Contract 应用入口；已安装实例的修复保持原独立路径。
    private async void InstallClick(object? sender, RoutedEventArgs e) => await RunAsync(
        async c =>
        {
            // [实现] 回调触发时：执行 Log("正在准备安装…")；按 installed && ProfileBox.SelectedItem is GameProfile selected 分支处理；执行 RefreshProfiles()。
            Log("正在准备安装…");
            if (installed && ProfileBox.SelectedItem is GameProfile selected)
                await DesktopApi.CallAsync<bool>("workspace.instances.repair", selected.Id, ct: c);
            else
                await InstallThroughApplicationAsync(SelectedGameVersion, "vanilla", c);
            RefreshProfiles();
            Log("安装完成，校验通过。");
        }
    );
    // 展示层只提交 Contract 请求；转换已有进度显示，不解释核心异常或组装安装实现。
    private async Task<InstalledInstance> InstallThroughApplicationAsync(string version, string loader, CancellationToken ct)
    {
        var progress = new Progress<VersionInstallProgress>(
            value => Progress.Report(
                new(value.Stage, value.Completed, value.Total, value.Bytes, value.TotalBytes)
            )
        );
        var result = await installation.InstallAsync(
            new(version, loader, installationTarget),
            progress,
            ct
        );
        lastInstallationResult = result;
        if (result.Instance is not null)
        {
            return result.Instance;
        }

        if (result.Error?.Code == "CANCELLED")
        {
            throw new OperationCanceledException(ct);
        }

        throw new InvalidOperationException(
            result.Error?.Code switch
            {
                "INVALID_ARGUMENT" => "安装参数无效。",
                "UNSUPPORTED_TARGET" => "当前安装服务不支持这个目标平台。",
                "UNSUPPORTED" => "此版本或加载器尚不支持。",
                "INSTALL_BUSY" => "已有安装任务正在运行。",
                "HOST_WRITER_ACTIVE" => "正在完成另一个任务，请稍后重试。",
                "APPLICATION_STOPPING" => "启动器正在退出，无法开始安装。",
                "ACCESS_DENIED" => "没有安装目录的访问权限。",
                "NETWORK_ERROR" => "安装网络请求失败，可在任务记录中查看原因。",
                "TIMEOUT" => "安装请求超时，可在任务记录中查看原因。",
                "INVALID_DATA" => "安装数据或依赖校验失败，可在任务记录中查看原因。",
                "IO_ERROR" => "安装文件操作失败，可在任务记录中查看原因。",
                _ => "安装失败，可在任务记录中查看原因。"
            }
        );
    }

    // [实现] IsOfflineNameValid 的实现：执行受异常处理保护的操作。
    private bool IsOfflineNameValid()
    {
        var name = (OfflineNameBox.Text ?? "").Trim();
        return name.Length is >= 1 and <= 16 && !name.Any(char.IsControl) && !name.Contains('"');
    }

    // [实现] OfflineNameChanged 的实现：按 accounts is not null 分支处理。
    private void OfflineNameChanged(object? sender, TextChangedEventArgs e)
    {
        if (accounts is not null)
            UpdateButtons();
    }

    // [实现] AccountModeChanged 的实现：按 accounts is not null 分支处理。
    private void AccountModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (accounts is not null)
            RefreshAccounts();
    }

    // [实现] RefreshAccounts 的实现：按 accounts is null 分支处理；执行 refreshingAccounts = true；准备 var microsoft = AccountTypeBox.SelectedIndex == 1。
    private void RefreshAccounts(string? selectedId = null)
    {
        if (accounts is null)
            return;
        refreshingAccounts = true;
        var microsoft = AccountTypeBox.SelectedIndex == 1;
        var list = accounts.Accounts.Where(
            x => (int)x.Kind == AccountTypeBox.SelectedIndex
        )
            .ToArray();
        AccountBox.ItemsSource = list;
        AccountBox.SelectedItem = list.FirstOrDefault(
            x => x.Id == (selectedId ?? accounts.SelectedId)
        ) ?? list.FirstOrDefault();
        OfflineNameBox.IsVisible = AccountTypeBox.SelectedIndex == 0;
        MicrosoftLoginInfo.IsVisible = RememberLoginBox.IsVisible = microsoft;
        LogoutButton.IsVisible = AccountTypeBox.SelectedIndex != 0;
        LoginButton.Content = microsoft
            ? "登录微软账号"
            : AccountTypeBox.SelectedIndex == 2
                ? "管理外置账户"
                : "使用此名称";
        refreshingAccounts = false;
        AccountSelected(null, null!);
        UpdateButtons();
    }

    // [实现] AccountSelected 的实现：按 accounts is null || refreshingAccounts 分支处理；按 AccountBox.SelectedItem is LauncherAccount account 分支处理；执行 UpdateButtons()。
    private async void AccountSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (accounts is null || refreshingAccounts)
            return;
        if (AccountBox.SelectedItem is LauncherAccount account)
        {
            // [实现] 条件成立（AccountBox.SelectedItem is LauncherAccount account）时：执行 accounts.Select(account.Id)；执行 AccountText.Text = (account.Kind == AccountKind.Offline ? "离线账户 · " : account.Kind == AccountKi…；按 account.Kind == AccountKind.Offline 分支处理。
            AccountText.Text = (account.Kind == AccountKind.Offline
                ? "离线账户 · "
                : account.Kind == AccountKind.External
                    ? "外置账户 · "
                    : "微软账户 · ") + account.Name;
            if (account.Kind == AccountKind.Offline)
                OfflineNameBox.Text = account.Name;
            else if (account.Kind == AccountKind.Microsoft)
            {
                // [实现] 条件成立（account.Kind == AccountKind.Microsoft）时：执行 RememberLoginBox.IsChecked = account.Remembered；按 !accounts.HasMicrosoftCredential(account.Id) 分支处理。
                RememberLoginBox.IsChecked = account.Remembered;
                if (!accounts.HasMicrosoftCredential(account.Id))
                {
                    // [实现] 条件成立（!accounts.HasMicrosoftCredential(account.Id)）时：执行 AccountText.Text += " · 需重新登录"。
                    AccountText.Text += " · 需重新登录";
                }
            }
        }
        else
        {
            // [实现] 前一条件不成立时：执行 AccountText.Text = AccountTypeBox.SelectedIndex == 0 ? "添加离线账户" : AccountTypeBox.SelectedIndex …；按 AccountTypeBox.SelectedIndex == 0 分支处理。
            AccountText.Text = AccountTypeBox.SelectedIndex == 0
                ? "添加离线账户"
                : AccountTypeBox.SelectedIndex == 2
                    ? "添加外置账户"
                    : "添加微软账户";
            if (AccountTypeBox.SelectedIndex == 0)
                OfflineNameBox.Text = "";
        }

        UpdateButtons();
        if (e is not null && AccountBox.SelectedItem is LauncherAccount chosen && chosen.Id != accounts.SelectedId)
        {
            await RunAsync(
                ct => SelectAccountAsync(chosen.Id, ct)
            );
        }
    }

    // The legacy view index is reloaded when Client returns its lease; it never owns the write.
    private Pcl.Client.WorkspaceApi AccountApi => new(
        Bootstrap.WorkspacePages.AccountOperations(this, accounts!)
    );

    private async Task SelectAccountAsync(string id, CancellationToken ct)
    {
        await AccountApi.CallAsync<Pcl.Core.Contracts.Workspaces.AccountPageSnapshot>(
            "workspace.account.select",
            null,
            new
            {
                accountId = id
            },
            ct
        );
    }

    private async Task<LauncherAccount> AddOfflineAccountAsync(string name, CancellationToken ct)
    {
        var created = await AccountApi.CallAsync<Pcl.Core.Contracts.Workspaces.LauncherAccount>(
            "workspace.account.addOffline",
            null,
            new
            {
                name
            },
            ct
        );
        return accounts!.Accounts.Single(
            x => x.Id == created.Id
        );
    }

    // [实现] LoginClick 的实现：执行 await RunAsync( async c => { if (accounts is null) return; if (AccountTypeBox.SelectedIndex == …。
    private async void LoginClick(object? sender, RoutedEventArgs e) => await RunAsync(
        async c =>
        {
            // [实现] 回调触发时：按 accounts is null 分支处理；按 AccountTypeBox.SelectedIndex == 2 分支处理；按 AccountTypeBox.SelectedIndex == 0 分支处理。
            if (accounts is null)
                return;
            if (AccountTypeBox.SelectedIndex == 2)
            {
                // [实现] 条件成立（AccountTypeBox.SelectedIndex == 2）时：执行 await Bootstrap.WorkspacePages.Accounts(this, accounts, id => RefreshAccounts(id) ).ShowDialog(this)；结束当前过程。
                await Bootstrap.WorkspacePages.Accounts(
                    this,
                    accounts,
                    id => RefreshAccounts(id)
                )
                    .ShowDialog(this);
                return;
            }

            if (AccountTypeBox.SelectedIndex == 0)
            {
                // [实现] 条件成立（AccountTypeBox.SelectedIndex == 0）时：准备 var account = accounts.AddOffline(OfflineNameBox.Text ?? "")；执行 RefreshAccounts(account.Id)；执行 StatusText.Text = "已选择离线账户"。
                var account = await AddOfflineAccountAsync(OfflineNameBox.Text ?? "", c);
                RefreshAccounts(account.Id);
                StatusText.Text = "已选择离线账户";
                return;
            }

            try
            {
                var accountId = await Bootstrap.WorkspacePages.Login(this, accounts)
                    .LoginAsync(
                        RememberLoginBox.IsChecked == true,
                        challenge =>
                        {
                            loginUserCode = challenge.UserCode;
                            CopyCodeButton.IsVisible = LoginHint.IsVisible = true;
                            LoginHint.Text = $"设备码：{challenge.UserCode} · 请在浏览器完成授权";
                            Open(challenge.VerificationUri);
                        },
                        c
                    );
                RefreshAccounts(accountId);
                StatusText.Text = "微软账户已登录";
                LoginHint.Text = accounts.Accounts.Single(
                    x => x.Id == accountId
                )
                    .Remembered
                    ? "已登录 · 已记住登录"
                    : "已登录 · 仅本次会话";
            }
            finally
            {
                loginUserCode = null;
                CopyCodeButton.IsVisible = false;
                if (LoginHint.Text?.StartsWith("设备码：") == true)
                {
                    LoginHint.Text = "授权已结束，可以重新登录";
                }
            }
        }
    );
    // [实现] CopyLoginCodeClick 的实现：按 loginUserCode is not null && Clipboard is not null 分支处理。
    private async void CopyLoginCodeClick(object? sender, RoutedEventArgs e)
    {
        if (loginUserCode is not null && Clipboard is not null)
            await Clipboard.SetTextAsync(loginUserCode);
    }

    // [实现] LogoutClick 的实现：执行 await RunAsync( async ct => { if (AccountBox.SelectedItem is LauncherAccount account) { if (acc…。
    private async void LogoutClick(object? sender, RoutedEventArgs e) => await RunAsync(
        async ct =>
        {
            // [实现] 回调触发时：按 AccountBox.SelectedItem is LauncherAccount account 分支处理；执行 RefreshAccounts()；执行 LoginHint.IsVisible = true。
            if (AccountBox.SelectedItem is LauncherAccount account)
            {
                await AccountApi.CallAsync<Pcl.Core.Contracts.Workspaces.AccountPageSnapshot>(
                    "workspace.account.logout",
                    null,
                    new
                    {
                        accountId = account.Id
                    },
                    ct
                );
            }

            RefreshAccounts();
            LoginHint.IsVisible = true;
            LoginHint.Text = "已退出 · 下次需重新登录";
        }
    );
    // [实现] RemoveAccountClick 的实现：执行 await RunAsync( _ => { if (AccountBox.SelectedItem is LauncherAccount account) accounts!.Remove…。
    private async void RemoveAccountClick(object? sender, RoutedEventArgs e) => await RunAsync(
        async ct =>
        {
            if (AccountBox.SelectedItem is LauncherAccount account)
            {
                await AccountApi.CallAsync<Pcl.Core.Contracts.Workspaces.AccountPageSnapshot>(
                    "workspace.account.remove",
                    null,
                    new
                    {
                        accountId = account.Id
                    },
                    ct
                );
            }

            RefreshAccounts();
        }
    );
    // [实现] LaunchClick 的实现：执行 await RunAsync( async c => { if (accounts is null) return; // 在离线模式下允许输入名称后直接启动，并将该名称加入可切换的历史账户…。
    private async void LaunchClick(object? sender, RoutedEventArgs e) => await RunAsync(
        async c =>
        {
            // [实现] 回调触发时：按 accounts is null 分支处理；准备 var account = AccountTypeBox.SelectedIndex == 0 ? accounts.AddOffline(OfflineNameBox.Text ?? ""…；按 account.Kind == AccountKind.External 分支处理。
            if (accounts is null)
                return;
            // 在离线模式下允许输入名称后直接启动，并将该名称加入可切换的历史账户。
            var account = AccountTypeBox.SelectedIndex == 0
                ? await AddOfflineAccountAsync(OfflineNameBox.Text ?? "", c)
                : AccountBox.SelectedItem as LauncherAccount ?? throw new InvalidOperationException("请先登录并选择微软账户。");
            if (account.Kind == AccountKind.External)
            {
                // [实现] 条件成立（account.Kind == AccountKind.External）时：抛出 new InvalidOperationException("此外置账户暂不支持启动，请选择离线或微软账户。")。
                throw new InvalidOperationException("此外置账户暂不支持启动，请选择离线或微软账户。");
            }

            await SelectAccountAsync(account.Id, c);
            RefreshAccounts(account.Id);
            await RunGameByAccountAsync(account.Id, account.Kind == AccountKind.Offline, c);
        }
    );
    private async Task RunGameByAccountAsync(string accountId, bool offlineLaunch, CancellationToken c)
    {
        var selected = ProfileBox.SelectedItem as GameProfile ?? throw new InvalidOperationException("请先选择一个实例。");
        // v3 启动入口只提交账户、实例和可见性策略；准备、授权、Java 与参数由应用执行链完成。
        await DesktopApi.CallAsync<bool>(
            "workspace.shell.window",
            null,
            new
            {
                width = (int)Bounds.Width,
                height = (int)Bounds.Height
            },
            c
        );
        var consent = await ConfirmLaunchGuidanceAsync(accountId, c);
        if (consent is null)
        {
            return;
        }
        var accepted = await gameLaunch.AcceptAsync(
            new(
                Guid.NewGuid()
                    .ToString("N"),
                accountId,
                selected.Id,
                appearance.LauncherVisibility == "close",
                consent
            ),
            c
        );
        var launching = gameLaunch.WaitAsync(accepted.OperationId);
        bool cancelSent = false;
        while (!launching.IsCompleted)
        {
            if (c.IsCancellationRequested && !cancelSent)
            {
                await gameLaunch.CancelAsync(accepted.OperationId);
                cancelSent = true;
            }

            await Task.WhenAny(
                launching,
                Task.Delay(100)
            );
        }

        var launched = await launching;
        if (launched.Result is null)
        {
            if (launched.ErrorCode == "CANCELLED")
            {
                throw new OperationCanceledException(c);
            }

            throw new InvalidOperationException(launched.ErrorCode ?? "LAUNCH_FAILED");
        }

        if (accountId != "trial")
        {
            RefreshAccounts(accountId);
        }

        if (AppearanceFeatures.ShouldPlayMusic(appearance, true))
        {
            // [实现] 条件成立（AppearanceFeatures.ShouldPlayMusic(appearance, true)）时：执行 musicPlayer.Play(appearance)。
            musicPlayer.Play(appearance);
        }
        else
        {
            // [实现] 前一条件不成立时：执行 musicPlayer.Stop()。
            musicPlayer.Stop();
        }

        if (appearance.LauncherVisibility == "close")
        {
            // [实现] 条件成立（appearance.LauncherVisibility == "close"）时：执行 launcher.StartDetached(plan, profile)；执行 Dispatcher.UIThread.Post(Close)；结束当前过程。
            // 已由受理操作创建独立进程；此处只处理窗口退出。
            Dispatcher.UIThread.Post(Close);
            return;
        }

        var session = borrowGame(launched.Result.GameProcessId) ?? throw new InvalidOperationException("游戏会话不属于当前 Host");
        await ObserveGameAsync(session, offlineLaunch);
    }

    // 新启动和重连接入共用展示生命周期；只借用 Host 的逻辑会话，不重新启动游戏。
    private async Task ObserveGameAsync(Pcl.Core.Contracts.Launch.IGameSession session, bool offlineLaunch)
    {
        gameStoppedByUser = false;
        game = session;
        game.Output += Log;
        if (appearance.LauncherVisibility is "hide-close" or "hide-show" || (appearance.LauncherVisibility == "legacy" && appearance.HideLauncherInGame))
        {
            // [实现] 条件成立（appearance.LauncherVisibility is "hide-close" or "hide-show" || (appearance.LauncherVisibility …）时：执行 Hide()。
            Hide();
        }
        else if (appearance.LauncherVisibility == "minimize" || (appearance.LauncherVisibility == "legacy" && appearance.MinimizeLauncherInGame))
        {
            // [实现] 条件成立（appearance.LauncherVisibility == "minimize" || (appearance.LauncherVisibility == "legacy" && ap…）时：执行 WindowState = WindowState.Minimized。
            WindowState = WindowState.Minimized;
        }

        StatusText.Text = offlineLaunch
            ? "离线游戏运行中。"
            : "游戏运行中。";
        UpdateButtons();
        int exit;
        var gameLogFile = session.LogFile;
        try
        {
            exit = await session.Completion;
        }
        catch
        {
            if (!IsVisible)
            {
                Show();
                Activate();
            }

            throw;
        }
        finally
        {
            session.Output -= Log;
            game = null;
            // 已观察的退出异常不应遮住原始错误，但必须移除客户端会话的唯一借用。
            try
            {
                await session.DisposeAsync();
            }
            catch when (session.Completion.IsFaulted)
            {
            }
        }

        if (AppearanceFeatures.ShouldPlayMusic(appearance, false))
        {
            // [实现] 条件成立（AppearanceFeatures.ShouldPlayMusic(appearance, false)）时：执行受异常处理保护的操作。
            try
            {
                // [实现] 在异常保护范围内：执行 musicPlayer.Play(appearance)。
                musicPlayer.Play(appearance);
            }
            catch (Exception ex)
            {
                // [实现] 捕获 (Exception ex) 后：执行 Log(ex.Message)。
                Log(ex.Message);
            }
        }
        else
        {
            // [实现] 前一条件不成立时：执行 musicPlayer.Stop()。
            musicPlayer.Stop();
        }

        if (appearance.LauncherVisibility == "hide-close")
        {
            // [实现] 条件成立（appearance.LauncherVisibility == "hide-close"）时：执行 Dispatcher.UIThread.Post(Close)；结束当前过程。
            Dispatcher.UIThread.Post(Close);
            return;
        }

        StatusText.Text = await DesktopApi.CallAsync<string>(
            "workspace.shell.gameExit",
            null,
            new
            {
                exitCode = exit,
                stopped = gameStoppedByUser,
                log = gameLogFile
            }
        );
        Log(StatusText.Text);
        if (!IsVisible)
        {
            // [实现] 条件成立（!IsVisible）时：执行 Show()；执行 Activate()。
            Show();
            Activate();
        }
    }

    // [实现] SkinsClick 的实现：按 accounts is not null 分支处理。
    private async void SkinsClick(object? sender, RoutedEventArgs e)
    {
        if (accounts is not null)
            await Bootstrap.WorkspacePages.Skins(this, accounts)
                .ShowDialog(this);
    }

    // [实现] StopGameClick 的实现：按 game is null 分支处理；准备 var dialog = new WorkspaceWindow { Title = "结束游戏", Width = 420, SizeToContent = SizeToContent.H…；准备 var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16 }。
    private async void StopGameClick(object? sender, RoutedEventArgs e)
    {
        if (game is null)
            return;
        var dialog = new WorkspaceWindow
        {
            Title = "结束游戏",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "强制结束可能丢失未保存的进度。建议先在游戏中保存并退出。",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            }
        );
        var confirm = new Button
        {
            Content = "仍要结束"
        };
        confirm.Click += (_, _) => dialog.Close(true);
        panel.Children.Add(confirm);
        var cancel = new Button
        {
            Content = "取消"
        };
        cancel.Click += (_, _) => dialog.Close(false);
        panel.Children.Add(cancel);
        dialog.Content = panel;
        if (await dialog.ShowDialog<bool>(this) && game is not null)
        {
            // [实现] 条件成立（await dialog.ShowDialog<bool>(this) && game is not null）时：执行 gameStoppedByUser = true；执行 game.Stop()。
            gameStoppedByUser = true;
            await game.StopAsync();
        }
    }

    // [实现] CancelClick 的实现：执行 operation?.Cancel()。
    private void CancelClick(object? sender, RoutedEventArgs e) => operation?.Cancel();
    // [实现] RefreshProfiles 的实现：准备 var previous = selectId ?? (ProfileBox.SelectedItem as GameProfile)?.Id；准备 var profiles = service.DesktopState.Profiles；执行 ProfileBox.ItemsSource = profiles。
    private void RefreshProfiles(string? selectId = null)
    {
        var previous = selectId ?? (ProfileBox.SelectedItem as GameProfile)?.Id;
        var profiles = service.DesktopState.Profiles;
        ProfileBox.ItemsSource = profiles;
        ProfileBox.SelectedItem = profiles.FirstOrDefault(
            x => x.Id == previous
        ) ?? profiles.FirstOrDefault();
        FabricButton.Content = "加载器";
    }

    private int profileDisplayRevision;
    // 选择通过 Host 持久化；异步图标结果只可更新仍然选中的版本。
    private async void ProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not GameProfile profile)
            return;
        var revision = ++profileDisplayRevision;
        // Persist only a user selection; list refreshes must not enqueue a competing writer.
        if (IsVisible && service.DesktopState.SelectedProfile != profile.Id && operation is null && game is null)
        {
            try
            {
                await DesktopApi.CallAsync<bool>(
                    "workspace.shell.selectProfile",
                    null,
                    new
                    {
                        id = profile.Id
                    }
                );
            }
            catch (Exception ex)
            {
                Log(
                    Pcl.Client.WorkspaceErrors.Describe(ex)
                );
            }
        }

        if (revision != profileDisplayRevision)
            return;
        GameVersionText.Text = profile.GameVersion;
        instanceArtwork?.Dispose();
        instanceArtwork = null;
        GameArtwork.Source = null;
        GameArtwork.IsVisible = false;
        try
        {
            // [实现] 在异常保护范围内：按 await DesktopApi.ReadIconAsync(profile.Id)is byte[] png 分支处理。
            if (await DesktopApi.ReadIconAsync(profile.Id)is byte[] png && revision == profileDisplayRevision)
            {
                // [实现] 条件成立（await DesktopApi.ReadIconAsync(profile.Id)is byte[] png）时：准备 var stream = new MemoryStream(png)；执行 instanceArtwork = new Bitmap(stream)；执行 GameArtwork.Source = instanceArtwork。
                using var stream = new MemoryStream(png);
                instanceArtwork = new Bitmap(stream);
                GameArtwork.Source = instanceArtwork;
                GameArtwork.IsVisible = true;
            }
        }
        catch
        {
            // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
        }

        await RefreshInstalledAsync();
    }

    private string SelectedGameVersion => (ProfileBox.SelectedItem as GameProfile)?.GameVersion ?? service.DesktopState.DefaultVersion;

    private GameProfile ActiveFabricProfile()
    {
        if (ProfileBox.SelectedItem is GameProfile { Loader: not "vanilla" } selected)
            return selected;
        return service.DesktopState.ResourceTargets.First(
            x => x.GameVersion == SelectedGameVersion && x.Loader == "fabric"
        );
    }

    // 只传递当前选择，版本/Java/加载器准备由 Host 保持原流程并记入任务收据。
    private async Task<GameProfile> EnsureFabricAsync(CancellationToken ct)
    {
        var api = new Pcl.Client.WorkspaceApi(
            Bootstrap.WorkspacePages.Operations(this, service)
        );
        var result = await api.CallAsync<Pcl.Core.Contracts.Workspaces.GameProfile>(
            "workspace.browser.ensureFabric",
            null,
            new
            {
                gameVersion = SelectedGameVersion,
                selectedId = (ProfileBox.SelectedItem as GameProfile)?.Id
            },
            ct
        );
        RefreshProfiles(result.Id);
        return result;
    }

    // [实现] FabricClick 的实现：准备 var types = new ComboBox { ItemsSource = new[] { "Fabric", "Forge", "NeoForge", "Quilt", "OptiF…；准备 var dialog = new WorkspaceWindow { Title = "安装加载器 · " + SelectedGameVersion, Width = 400, SizeT…；准备 var content = new StackPanel { Margin = new Thickness(24), Spacing = 16 }。
    private async void FabricClick(object? sender, RoutedEventArgs e)
    {
        var types = new ComboBox
        {
            ItemsSource = new[]
            {
                "Fabric",
                "Forge",
                "NeoForge",
                "Quilt",
                "OptiFine",
                "LiteLoader"
            },
            SelectedIndex = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        var dialog = new WorkspaceWindow
        {
            Title = "安装加载器 · " + SelectedGameVersion,
            Width = 400,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16
        };
        content.Children.Add(types);
        var confirm = new Button
        {
            Content = "选择版本"
        };
        confirm.Click += (_, _) => dialog.Close(types.SelectedIndex);
        content.Children.Add(confirm);
        dialog.Content = content;
        var choice = await dialog.ShowDialog<int?>(this);
        if (choice is null)
            return;
        await RunAsync(
            async ct =>
            {
                // UI gathers choices; the Contract request delegates installation and lifetime to Host.
                var loader = choice switch
                {
                    1 => "forge",
                    2 => "neoforge",
                    3 => "quilt",
                    4 => "optifine",
                    5 => "liteloader",
                    _ => "fabric"
                };
                var versions = await DesktopApi.CallAsync<string[]>(
                    "workspace.desktop.loaderVersions",
                    null,
                    new
                    {
                        gameVersion = SelectedGameVersion,
                        loader
                    },
                    ct
                );
                if (versions.Length == 0)
                    throw new NotSupportedException("此游戏版本没有对应的 " + types.SelectedItem + " 发布版本。");
                var combo = new ComboBox
                {
                    ItemsSource = versions,
                    SelectedIndex = 0,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
                };
                var picker = new WorkspaceWindow
                {
                    Title = types.SelectedItem + " · " + SelectedGameVersion,
                    Width = 400,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                var panel = new StackPanel
                {
                    Margin = new Thickness(24),
                    Spacing = 16
                };
                panel.Children.Add(combo);
                var install = new Button
                {
                    Content = "安装"
                };
                install.Click += (_, _) =>
                {
                    // [实现] 回调触发时：按 combo.SelectedItem is string version 分支处理。
                    if (combo.SelectedItem is string version)
                        picker.Close(version);
                };
                panel.Children.Add(install);
                picker.Content = panel;
                var selected = await picker.ShowDialog<string?>(this);
                if (selected is null)
                    return;
                var profile = await DesktopApi.CallAsync<Pcl.Core.Contracts.Workspaces.GameProfile>(
                    "workspace.desktop.loaderInstall",
                    null,
                    new
                    {
                        gameVersion = SelectedGameVersion,
                        loader,
                        version = selected
                    },
                    ct,
                    DesktopProgress
                );
                RefreshProfiles(profile.Id);
                StatusText.Text = types.SelectedItem + " 已安装";
            }
        );
    }

    // [实现] MacSetupClick 的实现：准备 var dialog = new WorkspaceWindow { Title = "Mac 配置", Width = 450, SizeToContent = SizeToContent…；准备 var content = new StackPanel { Margin = new Thickness(24), Spacing = 16 }；执行 content.Children.Add( new TextBlock { Text = "Apple Silicon 配置", FontSize = 20 } )。
    private async void MacSetupClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new WorkspaceWindow
        {
            Title = "Mac 配置",
            Width = 450,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16
        };
        content.Children.Add(
            new TextBlock
            {
                Text = "Apple Silicon 配置",
                FontSize = 20
            }
        );
        content.Children.Add(
            new TextBlock
            {
                Text = "创建独立 1.21.1 实例。\n基础：原生 Java 21 + Fabric API\n光影：固定 Iris / Sodium + Complementary 中档\n\nIris 不为 Apple Silicon 提供官方驱动支持。光影属于实验兼容配置，世界内效果需验证。",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            }
        );
        var basic = new Button
        {
            Content = "安装 Mac 基础"
        };
        basic.Click += (_, _) => dialog.Close("basic");
        content.Children.Add(basic);
        var shader = new Button
        {
            Content = "安装 Mac 光影（实验）"
        };
        shader.Click += (_, _) => dialog.Close("shaders");
        content.Children.Add(shader);
        var opti = new Button
        {
            Content = "1.20.1 OptiFabric / OptiFine"
        };
        opti.Click += (_, _) => dialog.Close("optifabric");
        content.Children.Add(opti);
        dialog.Content = content;
        var choice = await dialog.ShowDialog<string?>(this);
        if (choice is null)
            return;
        if (choice == "optifabric")
        {
            // UI gathers choices; the Contract request delegates installation and lifetime to Host.
            await Bootstrap.WorkspacePages.OptiFabric(
                this,
                service,
                p => RefreshProfiles(p.Id)
            )
                .ShowDialog(this);
            return;
        }

        await RunAsync(
            async ct =>
            {
                // UI gathers choices; the Contract request delegates installation and lifetime to Host.
                var result = await DesktopApi.CallAsync<Pcl.Core.Contracts.Workspaces.GameProfile>(
                    "workspace.desktop.macSetup",
                    null,
                    new
                    {
                        shaders = choice == "shaders"
                    },
                    ct,
                    DesktopProgress
                );
                RefreshProfiles(result.Id);
                StatusText.Text = choice == "shaders"
                    ? "Mac 光影配置已安装 · 实验兼容"
                    : "Mac 基础配置已安装";
            }
        );
    }

    // [实现] SelectNav 的实现：遍历 new[] { NavHome, NavMods, NavShaders, NavPacks, NavDataPacks, NavModpacks, NavManaged } 处理 nav。
    private void SelectNav(Button selected)
    {
        foreach (var nav in new[]
        {
            NavHome,
            NavMods,
            NavShaders,
            NavPacks,
            NavDataPacks,
            NavModpacks,
            NavManaged
        }

        )
            nav.Background = nav == selected
                ? (Avalonia.Media.IBrush)Resources["PclSelection"]!
                : Avalonia.Media.Brushes.Transparent;
    }

    // [实现] HomeClick 的实现：按 WindowsLayout 分支处理；执行 SelectNav(NavHome)；执行 HomePanel.IsVisible = true。
    private void HomeClick(object? sender, RoutedEventArgs e)
    {
        if (WindowsLayout)
        {
            // [实现] 条件成立（WindowsLayout）时：执行 ShowClassicHome()；结束当前过程。
            ShowClassicHome();
            return;
        }

        SelectNav(NavHome);
        HomePanel.IsVisible = true;
        ResourcePanel.IsVisible = false;
    }

    // [实现] ShowResourceAsync 的实现：执行 SelectNav(nav)；执行 HomePanel.IsVisible = false；执行 ResourcePanel.IsVisible = true。
    private async Task ShowResourceAsync(ResourceKind kind, Button nav, bool managed = false)
    {
        SelectNav(nav);
        HomePanel.IsVisible = false;
        ResourcePanel.IsVisible = true;
        await resources.ShowAsync((Pcl.Core.Contracts.Workspaces.ResourceKind)kind, managed);
    }

    // [实现] ModsClick 的实现：执行 await ShowResourceAsync(ResourceKind.Mod, NavMods)。
    private async void ModsClick(object? sender, RoutedEventArgs e) => await ShowResourceAsync(ResourceKind.Mod, NavMods);
    // [实现] ShadersClick 的实现：执行 await ShowResourceAsync(ResourceKind.Shader, NavShaders)。
    private async void ShadersClick(object? sender, RoutedEventArgs e) => await ShowResourceAsync(ResourceKind.Shader, NavShaders);
    // [实现] PacksClick 的实现：执行 await ShowResourceAsync(ResourceKind.ResourcePack, NavPacks)。
    private async void PacksClick(object? sender, RoutedEventArgs e) => await ShowResourceAsync(ResourceKind.ResourcePack, NavPacks);
    // [实现] DataPacksClick 的实现：执行 await ShowResourceAsync(ResourceKind.DataPack, NavDataPacks)。
    private async void DataPacksClick(object? sender, RoutedEventArgs e) => await ShowResourceAsync(ResourceKind.DataPack, NavDataPacks);
    // [实现] ModpacksClick 的实现：执行 await ShowResourceAsync(ResourceKind.Modpack, NavModpacks)。
    private async void ModpacksClick(object? sender, RoutedEventArgs e) => await ShowResourceAsync(ResourceKind.Modpack, NavModpacks);
    // [实现] ManagedClick 的实现：执行 await ShowResourceAsync(ResourceKind.Mod, NavManaged, true)。
    private async void ManagedClick(object? sender, RoutedEventArgs e) => await ShowResourceAsync(ResourceKind.Mod, NavManaged, true);
    // [实现] OpenGameClick 的实现：准备 var folder = (ProfileBox.SelectedItem as GameProfile)?.GameDirectory ?? paths.Game；执行 Directory.CreateDirectory(folder)；执行 Open(folder)。
    private async void OpenGameClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await DesktopApi.CallAsync<string>(
                "workspace.shell.openFolder",
                null,
                new
                {
                    id = (ProfileBox.SelectedItem as GameProfile)?.Id
                }
            );
            Open(folder);
        }
        catch (Exception ex)
        {
            StatusText.Text = Pcl.Client.WorkspaceErrors.Describe(ex);
        }
    }

    // [实现] OpenLogsClick 的实现：执行 Open(paths.Evidence)。
    private void OpenLogsClick(object? sender, RoutedEventArgs e) => Open(paths.Evidence);
    // [实现] InstancesClick 的实现：执行 await Bootstrap.WorkspacePages.OpenInstancesAsync( this, service, (ProfileBox.SelectedItem as GameProfile)?.Id,…。
    private async void InstancesClick(object? sender, RoutedEventArgs e) => await Bootstrap.WorkspacePages.OpenInstancesAsync(
        this,
        service,
        (ProfileBox.SelectedItem as GameProfile)?.Id,
        id => RefreshProfiles(id)
    );
    // [实现] PackToolsClick 的实现：按 ProfileBox.SelectedItem is GameProfile selected 分支处理。
    private async void PackToolsClick(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is GameProfile selected)
            await Bootstrap.WorkspacePages.Pack(
                this,
                service,
                selected,
                p => RefreshProfiles(p.Id)
            )
                .ShowDialog(this);
    }

    // [实现] LocalResourcesClick 的实现：按 ProfileBox.SelectedItem is GameProfile selected 分支处理。
    private async void LocalResourcesClick(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is GameProfile selected)
            await Bootstrap.WorkspacePages.Local(this, service, selected)
                .ShowDialog(this);
    }

    // [实现] DownloadFilesClick 的实现：执行 await ArtifactDownloadWindow.OpenAsync(this, Bootstrap.PageComposition.Artifacts(service,remote), SelectedGameVersion)。
    private async void DownloadFilesClick(object? sender, RoutedEventArgs e) => await ArtifactDownloadWindow.OpenAsync(
        this,
        Bootstrap.PageComposition.Artifacts(service, remote),
        SelectedGameVersion
    );
    // [实现] TaskCenterClick 的实现：执行 await new TaskCenterWindow( service, RunAsync, Open, () => operation?.Cancel() ).ShowDialog(this)。
    private async void TaskCenterClick(object? sender, RoutedEventArgs e) => await new TaskCenterWindow(
        service,
        RunAsync,
        Open,
        () => operation?.Cancel(),
        installation.Operations
    ).ShowDialog(this);
    // [实现] McmodSearchClick 的实现：按 ProfileBox.SelectedItem is GameProfile profile 分支处理。
    private async void McmodSearchClick(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is GameProfile profile)
            await Bootstrap.WorkspacePages.Search(
                this,
                service,
                profile,
                p => RefreshProfiles(p.Id),
                Open,
                mcmodOnly: true
            )
                .ShowDialog(this);
    }

    // [实现] UnifiedSearchClick 的实现：按 ProfileBox.SelectedItem is GameProfile profile 分支处理。
    private async void UnifiedSearchClick(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is GameProfile profile)
            await Bootstrap.WorkspacePages.Search(
                this,
                service,
                profile,
                p => RefreshProfiles(p.Id),
                Open
            )
                .ShowDialog(this);
    }

    // [实现] CurseForgeClick 的实现：按 ProfileBox.SelectedItem is GameProfile profile 分支处理。
    private async void CurseForgeClick(object? sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is GameProfile profile)
            await Bootstrap.WorkspacePages.Curse(
                this,
                service,
                profile,
                p => RefreshProfiles(p.Id),
                Open
            )
                .ShowDialog(this);
    }

    // [实现] SettingsClick 的实现：执行 await Bootstrap.WorkspacePages.Settings(this, service, ApplyAppearance, Open).ShowDialog(this)。
    private async void SettingsClick(object? sender, RoutedEventArgs e) => await Bootstrap.WorkspacePages.Settings(this, service, ApplyAppearance, Open)
        .ShowDialog(this);
    private MultiplayerWindow? multiplayer;
    // [实现] MultiplayerClick 的实现：按 multiplayer is null 分支处理。
    private void MultiplayerClick(object? sender, RoutedEventArgs e)
    {
        if (multiplayer is null)
        {
            // [实现] 条件成立（multiplayer is null）时：执行 multiplayer = new(service)；执行 multiplayer.Closed += (_, _) => multiplayer = null；执行 multiplayer.Show(this)。
            multiplayer = Bootstrap.WorkspacePages.Multiplayer(this, service);
            multiplayer.Closed += (_, _) => multiplayer = null;
            multiplayer.Show(this);
        }
        else
            multiplayer.Activate();
    }

    // Both the classic page and this dialog use the same attribution-first view.
    private async void AboutClick(object? sender, RoutedEventArgs e)
    {
        await new WorkspaceWindow
        {
            Title = "关于 · " + ProductInfo.Name,
            Width = 720,
            Height = 660,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = BuildAboutContent() }
        }.ShowDialog(this);
    }

    // [实现] Open 的实现：按 OperatingSystem.IsMacOS() 分支处理。
    private static void Open(string target)
    {
        if (OperatingSystem.IsMacOS())
        {
            // [实现] 条件成立（OperatingSystem.IsMacOS()）时：准备 var start = new ProcessStartInfo("/usr/bin/open")；执行 start.ArgumentList.Add(target)；执行 Process.Start(start)。
            var start = new ProcessStartInfo("/usr/bin/open");
            start.ArgumentList.Add(target);
            Process.Start(start);
        }
        else
            Process.Start(
                new ProcessStartInfo(target)
                {
                    UseShellExecute = true
                }
            );
    }
}
