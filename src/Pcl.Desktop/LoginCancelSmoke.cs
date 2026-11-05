using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // Real authorization challenge + UI; no credentials, screenshots, or browser navigation.
    private async Task LoginCancelSmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--remote-report") + 1];
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException(label);
            checks.Add(label);
        }
        async Task Until(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!ready())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("LOGIN_UI_TIMEOUT");
                await Task.Delay(50);
            }
        }
        bool Visible(Control control) => control.GetVisualAncestors().Contains(this) && control.IsEffectivelyVisible;
        async Task LeaveHome()
        {
            if (WindowsLayout) await NavigateClassicAsync("matrix");
            else await ShowResourceAsync(ResourceKind.ResourcePack, NavPacks);
        }
        var browserRequests = 0;
        try
        {
            if (!paths.Root.EndsWith("login-cancel-data") || !File.Exists(Path.Combine(paths.Root, ".login-cancel-test")))
                throw new InvalidOperationException("ISOLATED_LOGIN_TEST_ROOT_REQUIRED");
            Check(accounts!.Accounts.Length == 0, "isolated-empty-account-index");
            openLoginBrowser = _ => browserRequests++;
            Width = MinWidth;
            AccountTypeBox.SelectedIndex = 1;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                HomeClick(null, new RoutedEventArgs());
                Task? unrelatedWork = null;
                if (attempt == 2)
                {
                    unrelatedWork = RunAsync(ct => Task.Delay(Timeout.Infinite, ct));
                    Check(LoginButton.IsEnabled, "non-account-operation-allows-login-entry");
                }
                LoginButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var pending = PendingLoginTask;
                Check(attempt == 2 ? unrelatedWork is { IsCompleted: false } : operation is null,
                    "login-independent-from-main-operation-" + attempt);
                Check(Visible(CancelLoginButton) && CancelLoginButton.IsEffectivelyEnabled, "visible-global-cancel-during-request-" + attempt);
                if (attempt > 0)
                {
                    await Until(() => loginUserCode is not null || pending.IsCompleted);
                    Check(loginUserCode is not null, "real-microsoft-challenge-" + attempt);
                    await LeaveHome();
                    Check(!pending.IsCompleted && microsoftLoginPending, "navigation-keeps-login-running-" + attempt);
                    using (var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        var snapshot = await DesktopApi.CallAsync<JsonElement>("workspace.settings.read", null, new { }, readTimeout.Token);
                        Check(snapshot.ValueKind == JsonValueKind.Object && !pending.IsCompleted,
                            "real-host-read-completes-while-login-remains-active-" + attempt);
                    }
                    Check(Visible(CancelLoginButton) && Visible(CopyCodeButton), "global-actions-visible-away-from-home-" + attempt);
                    await Task.Delay(100);
                    Check(dismissLoginStatus.Bounds.Right <= ((Control)dismissLoginStatus.Parent!).Bounds.Width + 1, "global-actions-fit-minimum-width-" + attempt);
                }
                var opened = browserRequests;
                if (attempt == 2)
                {
                    await PrepareForExit().WaitAsync(TimeSpan.FromSeconds(30));
                    Check(PendingLoginTask.IsCompleted, "application-exit-drains-background-login");
                    Check(unrelatedWork!.IsCompleted, "application-exit-drains-unrelated-operation");
                }
                else
                {
                    CancelLoginButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await pending.WaitAsync(TimeSpan.FromSeconds(30));
                }
                Check(browserRequests == opened, "no-browser-open-after-cancel-" + attempt);
                Check(!microsoftLoginPending && LoginButton.IsEnabled && AccountTypeBox.IsEnabled, "account-controls-restored-" + attempt);
                Check(!CopyCodeButton.IsVisible && !CancelLoginButton.IsVisible && loginUserCode is null, "prompt-cleared-" + attempt);
                Check(LoginHint.Text!.Contains("已取消"), "cancelled-global-status-" + attempt);
                if (attempt == 1)
                {
                    Check(WindowsLayout ? classicRoute == "matrix" : ResourcePanel.IsVisible, "terminal-login-does-not-change-current-page");
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var work = RunAsync(ct => release.Task.WaitAsync(ct));
                    await LeaveHome();
                    Check(!work.IsCompleted && NavHome.IsEnabled && NavPacks.IsEnabled, "main-operation-allows-navigation");
                    release.SetResult();
                    await work;
                }
            }
            Check(accounts!.Accounts.Length == 0, "cancel-never-adds-account");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = ex.GetType().Name, code = ex is InvalidOperationException ? ex.Message : ex is Pcl.Core.Contracts.Operations.OperationRejectedException rejected ? rejected.Code : null }));
            Environment.ExitCode = 1;
        }
        finally
        {
            openLoginBrowser = Open;
            Close();
        }
    }
}
