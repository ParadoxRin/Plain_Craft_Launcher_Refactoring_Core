using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // Real UI + Host device authorization; never sign in, persist a code, or open a browser.
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
        var browserRequests = 0;
        try
        {
            if (!paths.Root.EndsWith("login-cancel-data") || !File.Exists(Path.Combine(paths.Root, ".login-cancel-test")))
                throw new InvalidOperationException("ISOLATED_LOGIN_TEST_ROOT_REQUIRED");
            Check(accounts!.Accounts.Count() == 0, "isolated-empty-account-index");
            openLoginBrowser = _ => browserRequests++;
            Width = MinWidth;
            AccountTypeBox.SelectedIndex = 1;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                LoginButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                Check(CancelLoginButton.GetVisualAncestors().Contains(this) && CancelLoginButton.IsEffectivelyVisible && CancelLoginButton.IsEffectivelyEnabled, "cancel-available-during-request-" + attempt);
                if (attempt != 0)
                {
                    await Until(() => loginUserCode is not null || PendingUiAction.IsCompleted);
                    Check(loginUserCode is not null, "real-microsoft-challenge-" + attempt);
                    await Task.Delay(100);
                    Check(CopyCodeButton.Bounds.Right <= ((Avalonia.Controls.Control)CopyCodeButton.Parent!).Bounds.Width + 1,
                        "login-actions-fit-minimum-window-width-" + attempt);
                    Check(LoginHint.Text!.Contains("关闭网页"), "browser-abandonment-guidance-" + attempt);
                }
                var opened = browserRequests;
                if (attempt == 2 && WindowsLayout)
                {
                    await NavigateClassicAsync("matrix");
                    Check(classicRoute == "matrix" && classicPage.IsEffectivelyVisible, "navigation-cancels-login-and-opens-target");
                    await NavigateClassicAsync("launch");
                }
                else
                    CancelLoginButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                await PendingUiAction.WaitAsync(TimeSpan.FromSeconds(30));
                Check(browserRequests == opened, "no-browser-open-after-cancel-" + attempt);
                Check(operation is null && LoginButton.IsEffectivelyEnabled && AccountTypeBox.IsEffectivelyEnabled && VersionButton.IsEffectivelyEnabled,
                    "controls-restored-after-host-terminal-" + attempt);
                Check(!CancelLoginButton.IsVisible && !CopyCodeButton.IsVisible && loginUserCode is null, "prompt-cleared-" + attempt);
                Check(StatusText.Text == "已取消", "cancelled-status-" + attempt);
            }
            Check(accounts!.Accounts.Count() == 0, "cancel-never-adds-account");
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new { status = "failed", checks, error = ex.GetType().Name, code = ex is InvalidOperationException ? ex.Message : null }));
            Environment.ExitCode = 1;
        }
        finally
        {
            openLoginBrowser = Open;
            Close();
        }
    }
}
