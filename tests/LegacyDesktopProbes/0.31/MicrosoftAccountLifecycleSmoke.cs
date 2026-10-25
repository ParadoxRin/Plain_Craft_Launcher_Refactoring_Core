using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Pcl.Core;

namespace Pcl.Desktop;
// [实现] 微软账户生命周期验收：通过实际界面退出并清除凭据，重新授权后验证账户去重、正版权益和冷启动刷新。
public partial class MainWindow
{
    /// <summary>通过产品按钮注销真实账户，核对钥匙串/内存/重启状态，再等待用户重新授权。</summary>
    private async Task MicrosoftAccountLifecycleSmokeAsync()
    {
        using var auth = new MicrosoftAuthentication();
        var destination = Path.Combine(paths.Evidence, "microsoft-lifecycle-0.17.json");
        var stage = "prepare";
        try
        {
            // [实现] 在异常保护范围内：准备 var timeout = new CancellationTokenSource( TimeSpan.FromMinutes(18) )；准备 var ct = timeout.Token；准备 var manager = accounts ?? throw new InvalidOperationException("账户服务尚未初始化。")。
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(18)
            );
            var ct = timeout.Token;
            var manager = accounts ?? throw new InvalidOperationException("账户服务尚未初始化。");
            var account = manager.Accounts.Single(
                item => item.Id == manager.SelectedId && item.Kind == AccountKind.Microsoft && item.Remembered
            );
            var count = manager.Accounts.Count;
            RefreshProfiles(
                fabric.Profiles()
                    .Single(
                        item => item.Loader == "vanilla" && item.GameVersion == "1.21.1"
                    )
                    .Id
            );
            RefreshAccounts(account.Id);
            await RefreshInstalledAsync();
            // 先加载一次真实会话，确保退出动作同时清除内存会话和持久刷新令牌。
            GameLaunchAuthorization.Require(
                await manager.ResolveAsync(account.Id, auth, ct)
            );
            stage = "logout";
            LogoutButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            while (operation is not null)
            {
                // [实现] 循环满足 operation is not null 时：执行 await Task.Delay(100, ct)。
                await Task.Delay(100, ct);
            }

            if (manager.HasMicrosoftCredential(account.Id) || LaunchButton.IsEnabled || new MacAccountSecrets(paths.Root).Get(account.Id)is not null)
            {
                // [实现] 条件成立（manager.HasMicrosoftCredential(account.Id) || LaunchButton.IsEnabled || new MacAccountSecrets(p…）时：抛出 new InvalidOperationException("注销后凭据或启动入口仍然可用。")。
                throw new InvalidOperationException("注销后凭据或启动入口仍然可用。");
            }

            // 分别验证当前内存与重新加载的账户管理器，均不能用保留的账户元数据恢复登录。
            foreach (var probe in new[]
            {
                manager,
                new AccountManager(
                    paths,
                    new MacAccountSecrets(paths.Root)
                )
            }

            )
            {
                // [实现] 对 new[] { manager, new AccountManager( paths, new MacAccountSecrets(paths.Root) ) } 中的每一项：准备 var rejected = false；执行受异常处理保护的操作；按 !rejected 分支处理。
                var rejected = false;
                try
                {
                    // [实现] 在异常保护范围内：执行 await probe.ResolveAsync(account.Id, auth, ct)。
                    await probe.ResolveAsync(account.Id, auth, ct);
                }
                catch (LoginRequiredException)
                {
                    // [实现] 捕获 (LoginRequiredException) 后：执行 rejected = true。
                    rejected = true;
                }

                if (!rejected)
                {
                    // [实现] 条件成立（!rejected）时：抛出 new InvalidOperationException("注销后的账户仍能恢复会话。")。
                    throw new InvalidOperationException("注销后的账户仍能恢复会话。");
                }
            }

            // 即使人为触发禁用按钮的事件，核心也必须拒绝；只观察本次应用的游戏字段。
            LaunchButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            while (operation is not null)
            {
                // [实现] 循环满足 operation is not null 时：执行 await Task.Delay(100, ct)。
                await Task.Delay(100, ct);
            }

            if (game is not null)
            {
                // [实现] 条件成立（game is not null）时：抛出 new InvalidOperationException("注销后的启动事件创建了游戏进程。")。
                throw new InvalidOperationException("注销后的启动事件创建了游戏进程。");
            }

            JsonFiles.Write(
                destination,
                new
                {
                    status = "logged_out_verified",
                    keychainCleared = true,
                    memorySessionCleared = true,
                    coldRestoreRejected = true,
                    launchDisabled = true,
                    forcedLaunchRejected = true
                }
            );
            Console.WriteLine("MICROSOFT_LOGOUT_VERIFIED");
            // 使用实际登录按钮与既有授权范围；设备码只供用户在微软官方页面完成本轮验证。
            stage = "relogin";
            RememberLoginBox.IsChecked = true;
            LoginButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            while (loginUserCode is null && operation is not null)
            {
                // [实现] 循环满足 loginUserCode is null && operation is not null 时：执行 await Task.Delay(100, ct)。
                await Task.Delay(100, ct);
            }

            if (loginUserCode is not null)
            {
                // [实现] 条件成立（loginUserCode is not null）时：执行 Console.WriteLine("MICROSOFT_RELOGIN_DEVICE_CODE " + loginUserCode)。
                Console.WriteLine("MICROSOFT_RELOGIN_DEVICE_CODE " + loginUserCode);
            }

            while (operation is not null)
            {
                // [实现] 循环满足 operation is not null 时：执行 await Task.Delay(200, ct)。
                await Task.Delay(200, ct);
            }

            if (manager.SelectedId != account.Id || manager.Accounts.Count != count || !manager.HasMicrosoftCredential(account.Id) || !LaunchButton.IsEnabled)
            {
                // [实现] 条件成立（manager.SelectedId != account.Id || manager.Accounts.Count != count || !manager.HasMicrosoftCre…）时：抛出 new InvalidOperationException("重新登录没有恢复原账户或生成了重复账户记录。")。
                throw new InvalidOperationException("重新登录没有恢复原账户或生成了重复账户记录。");
            }

            GameLaunchAuthorization.Require(
                await manager.ResolveAsync(account.Id, auth, ct)
            );
            var restarted = new AccountManager(
                paths,
                new MacAccountSecrets(paths.Root)
            );
            GameLaunchAuthorization.Require(
                await restarted.ResolveAsync(account.Id, auth, ct)
            );
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)Bounds.Width, (int)Bounds.Height),
                new Vector(96, 96)
            );
            bitmap.Render(this);
            bitmap.Save(
                Path.Combine(paths.Evidence, "microsoft-relogin-0.17.png"),
                PngBitmapEncoderOptions.Default
            );
            JsonFiles.Write(
                destination,
                new
                {
                    status = "passed",
                    logoutViaButton = true,
                    keychainCleared = true,
                    memorySessionCleared = true,
                    coldRestoreRejected = true,
                    launchDisabled = true,
                    forcedLaunchRejected = true,
                    reloginViaButton = true,
                    sameAccountWithoutDuplicate = true,
                    entitlementRechecked = true,
                    coldRefreshAfterRelogin = true,
                    realMultipleAccounts = "not_tested_second_account_unavailable",
                    timestamp = DateTimeOffset.UtcNow
                }
            );
            Console.WriteLine("MICROSOFT_LIFECYCLE_PASSED");
        }
        catch (Exception ex)
        {
            // 只输出阶段和异常类别，保留先前成功的注销证据，不记录账户名或令牌。
            JsonFiles.Write(
                destination + ".failure.json",
                new
                {
                    status = "failed",
                    stage,
                    errorType = ex.GetType()
                        .Name
                }
            );
            Console.Error.WriteLine(
                "MICROSOFT_LIFECYCLE_FAILED " + stage + " " + ex.GetType()
                    .Name
            );
            operation?.Cancel();
            Environment.ExitCode = 1;
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 Close()。
            Close();
        }
    }
}
