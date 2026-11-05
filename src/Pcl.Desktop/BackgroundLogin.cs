using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace Pcl.Desktop;

public partial class MainWindow
{
    // Presentation lifetime belongs to the main window, never to its selected page.
    // Client/Host retain ownership of authorization and its terminal state.
    private CancellationTokenSource? loginCancellation;
    private Task loginTask = Task.CompletedTask;
    internal Task PendingLoginTask => loginTask;
    private readonly Border loginStatus = new() { IsVisible = false, Padding = new Thickness(12, 8) };
    private readonly Button dismissLoginStatus = new() { Content = "关闭提示", Padding = new Thickness(8, 4), IsVisible = false };

    private void InitializeBackgroundLoginStatus()
    {
        var body = (Control)Content!;
        Content = null;
        var shell = new Grid { RowDefinitions = RowDefinitions.Parse("*,Auto") };
        shell.Children.Add(body);
        var line = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto,Auto"), ColumnSpacing = 10 };
        line.Children.Add(MoveControl(LoginHint));
        LoginHint.MaxWidth = double.PositiveInfinity;
        LoginHint.HorizontalAlignment = HorizontalAlignment.Stretch;
        foreach (var (button, column) in new[] { (CopyCodeButton, 1), (CancelLoginButton, 2), (dismissLoginStatus, 3) })
        {
            MoveControl(button);
            Grid.SetColumn(button, column);
            line.Children.Add(button);
        }
        dismissLoginStatus.Click += (_, _) => loginStatus.IsVisible = false;
        loginStatus.Background = Brushes.Transparent;
        loginStatus.Child = line;
        Grid.SetRow(loginStatus, 1);
        shell.Children.Add(loginStatus);
        Content = shell;
    }

    private void StartBackgroundMicrosoftLogin()
    {
        if (closePending) return;
        if (microsoftLoginPending)
        {
            loginStatus.IsVisible = true;
            return;
        }
        if (game is not null)
        {
            Log("当前账户正在被任务使用；请先结束相关任务，再发起新的登录。");
            return;
        }
        var cancellation = new CancellationTokenSource();
        loginCancellation = cancellation;
        microsoftLoginPending = true;
        loginStatus.IsVisible = LoginHint.IsVisible = CancelLoginButton.IsVisible = true;
        CancelLoginButton.IsEnabled = true;
        dismissLoginStatus.IsVisible = CopyCodeButton.IsVisible = false;
        LoginHint.Text = "正在请求微软授权；可自由切换页面，登录会在后台继续。";
        UpdateButtons();
        loginTask = ObserveMicrosoftLoginAsync(RememberLoginBox.IsChecked == true, cancellation);
    }

    private async Task ObserveMicrosoftLoginAsync(bool remember, CancellationTokenSource cancellation)
    {
        try
        {
            var id = await Bootstrap.WorkspacePages.Login(this, accounts!).LoginAsync(remember, challenge =>
            {
                if (cancellation.IsCancellationRequested || closePending) return;
                loginUserCode = challenge.UserCode;
                CopyCodeButton.IsVisible = true;
                LoginHint.Text = $"设备码：{challenge.UserCode} · 浏览器授权中，切换页面不会取消；关闭网页后可点击“取消登录”。";
                openLoginBrowser(challenge.VerificationUri);
            }, cancellation.Token);
            RefreshAccounts(id);
            LoginHint.Text = accounts!.Accounts.Single(x => x.Id == id).Remembered
                ? "微软账户已登录 · 已记住登录"
                : "微软账户已登录 · 仅本次会话";
        }
        catch (OperationCanceledException)
        {
            LoginHint.Text = "微软登录已取消，可以重新登录。";
        }
        catch (Exception ex)
        {
            LoginHint.Text = "微软登录未完成：" + Pcl.Client.WorkspaceErrors.Describe(ex);
        }
        finally
        {
            loginUserCode = null;
            CopyCodeButton.IsVisible = CancelLoginButton.IsVisible = false;
            dismissLoginStatus.IsVisible = true;
            microsoftLoginPending = false;
            loginCancellation = null;
            cancellation.Dispose();
            UpdateButtons();
        }
    }

    private void CancelLoginClick(object? sender, RoutedEventArgs e)
    {
        if (loginCancellation is null) return;
        CancelLoginButton.IsEnabled = false;
        LoginHint.Text = "正在取消微软登录；页面仍可浏览。";
        loginCancellation.Cancel();
    }

    // Read-only page work has a navigation lifetime and never occupies the main write slot.
    private async Task RunReadAsync(Func<CancellationToken, Task> action)
    {
        if (closePending || navigationLifetime.IsStopping) return;
        using var cancellation = new CancellationTokenSource();
        using var activity = navigationLifetime.Begin(cancellation.Cancel);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { resources.Report(Pcl.Client.WorkspaceErrors.Describe(ex)); }
    }
}
