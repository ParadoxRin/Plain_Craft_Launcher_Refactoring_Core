using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Pcl.Client.SelfUpdate;
using System.Diagnostics;
using System.Text.Json;

namespace Pcl.Desktop;

public partial class MainWindow
{
    private readonly CancellationTokenSource clientUpdateStop = new();
    private Task? clientUpdateCheck;
    private string? clientUpdateRequest;
    private string clientUpdateStatus = "尚未配置客户端更新源。";
    private readonly List<TextBlock> clientUpdateLabels = [];
    private ClientSelfUpdate? ClientUpdater() => File.Exists(Path.Combine(AppContext.BaseDirectory, "client-update-policy.json"))
        ? new(ClientSelfUpdate.CurrentTarget()) : null;

    private void UpdateClientStatus(string message)
    {
        clientUpdateStatus = message;
        foreach (var label in clientUpdateLabels) label.Text = message;
    }
    internal void BeginClientUpdateCheck()
    {
        if (Program.Arguments.Contains("--client-update-test-report"))
        {
            _ = RunClientUpdateAcceptanceAsync();
            return;
        }
        if (Environment.GetEnvironmentVariable("PCL_CLIENT_UPDATE_ACK") is not null)
        {
            UpdateClientStatus("正在确认新版本启动状态…");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                if (ClientUpdater()?.Status.Pending is null)
                { timer.Stop(); UpdateClientStatus("客户端更新完成。"); }
            };
            timer.Start();
            return;
        }
        if (ClientUpdater() is not null) clientUpdateCheck = CheckClientUpdateAsync();
    }
    private async Task CheckClientUpdateAsync()
    {
        if (clientUpdateRequest is not null) { UpdateClientStatus("客户端更新已下载，可以安装并重启。"); return; }
        try
        {
            var updater = ClientUpdater();
            if (updater is null) return;
            UpdateClientStatus(updater.Status.Result switch
            {
                "rolled-back" => "上次更新启动失败，已回退。正在检查新版本…",
                "rejected-before-replacement" => "上次更新未替换文件，请确认应用位于可写目录。正在检查新版本…",
                _ => "正在检查并下载客户端更新…"
            });
            using var http = ClientSelfUpdate.CreateHttpClient();
            clientUpdateRequest = await updater.DownloadAsync(http, Program.Arguments, clientUpdateStop.Token);
            UpdateClientStatus("更新已下载并通过签名校验。点击安装并重启，程序退出后完成替换。");
        }
        catch (OperationCanceledException) { UpdateClientStatus("已取消客户端更新检查。"); }
        catch (Exception ex)
        {
            UpdateClientStatus(ex.Message.Contains("SEQUENCE_NOT_NEWER", StringComparison.Ordinal)
                ? "当前没有可安装的新版本（已回退的失败版本不会重复安装）。"
                : "客户端更新检查失败；当前版本仍可使用。" );
        }
    }
    private void AddClientUpdateControls(StackPanel panel)
    {
        var label = new TextBlock { Text = clientUpdateStatus, TextWrapping = TextWrapping.Wrap };
        clientUpdateLabels.Add(label);
        panel.Children.Add(label);
        var check = new Button { Content = "检查并下载客户端更新" };
        check.Click += async (_, _) =>
        {
            if (clientUpdateCheck is { IsCompleted: false }) return;
            clientUpdateCheck = CheckClientUpdateAsync();
            await clientUpdateCheck;
        };
        panel.Children.Add(check);
        var install = new Button { Content = "安装客户端更新并重启" };
        install.Click += async (_, _) => await InstallClientUpdateAsync();
        panel.Children.Add(install);
    }
    private async Task InstallClientUpdateAsync()
    {
        if (clientUpdateRequest is null) { UpdateClientStatus("请先检查并下载客户端更新。"); return; }
        if (game is not null && !game.HasExited) { UpdateClientStatus("请先结束当前游戏，再重启更新客户端。"); return; }
        try
        {
            ClientUpdater()!.StartHelper(clientUpdateRequest).Dispose();
            clientUpdateRequest = null;
            await RequestApplicationExitAsync();
        }
        catch (Exception) { UpdateClientStatus("无法启动更新器；当前客户端未被替换。"); }
    }
    private async Task StopClientUpdateCheckAsync()
    {
        clientUpdateStop.Cancel();
        if (clientUpdateCheck is not null) await clientUpdateCheck;
    }

    // Explicit local acceptance mode: no production feed, no personal data, no auto-exit of other processes.
    private async Task RunClientUpdateAcceptanceAsync()
    {
        try
        {
            string Arg(string name) => Program.Arguments[Array.IndexOf(Program.Arguments, name) + 1];
            var root = Path.GetFullPath(Arg("--data-root"));
            if (!File.Exists(Path.Combine(root, ".client-update-test"))) throw new IOException("ISOLATED_CLIENT_UPDATE_TEST_REQUIRED");
            var report = Path.GetFullPath(Arg("--client-update-test-report"));
            if (Path.GetDirectoryName(report) != root) throw new IOException("TEST_REPORT_MUST_BE_IN_TEST_ROOT");
            using var self = Process.GetCurrentProcess();
            ClientUpdatePackage.Save(report, new { pid = self.Id, startedTicks = self.StartTime.ToUniversalTime().Ticks,
                sequence = ClientUpdatePackage.ReadRelease(ClientSelfUpdate.CurrentTarget()).Sequence, hostPid = service.Client.HostPid });
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += async (_, _) =>
            {
                if (File.Exists(Path.Combine(root, "stop-client"))) { timer.Stop(); await RequestApplicationExitAsync(); }
            };
            timer.Start();
            if (Program.Arguments.Contains("--client-update-test-feed"))
            {
                var restart = new[] { "--data-root", root, "--client-update-test-report", report };
                clientUpdateRequest = ClientUpdater()!.PrepareLocal(Arg("--client-update-test-feed"), Arg("--client-update-test-archive"), restart);
                await InstallClientUpdateAsync();
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("CLIENT_UPDATE_ACCEPTANCE_FAILED " + ex.Message); }
    }
}
