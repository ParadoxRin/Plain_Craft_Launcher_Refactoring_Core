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
    private Task? contentUpdateCheck;
    private string contentUpdateStatus = "正在读取数据更新状态…";
    private readonly List<TextBlock> contentUpdateLabels = [];
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
        contentUpdateCheck = ContentUpdateLoopAsync();
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
            try { updater.CleanupCache(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
        var contentLabel = new TextBlock { Text = contentUpdateStatus, TextWrapping = TextWrapping.Wrap };
        contentUpdateLabels.Add(contentLabel); panel.Children.Add(contentLabel);
        var content = new Button { Content = "检查并安装百科与映射数据更新" };
        content.Click += async (_, _) =>
        {
            try { await CheckContentUpdateAsync(true); }
            catch (OperationCanceledException) { }
            catch (Exception) { SetContentStatus("数据更新失败，已有数据仍可使用。"); }
        };
        panel.Children.Add(content);
        var clean = new Button { Content = "清理旧客户端更新缓存" };
        clean.Click += (_, _) =>
        {
            try { var count = ClientUpdater()?.CleanupCache() ?? 0; UpdateClientStatus($"已清理 {count} 项旧缓存；保留当前更新和最近回退备份。"); }
            catch (Exception) { UpdateClientStatus("更新正在进行或缓存不可访问，稍后再试。"); }
        };
        panel.Children.Add(clean);
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
        if (contentUpdateCheck is not null) await contentUpdateCheck;
        await contentCheckGate.WaitAsync();
        contentCheckGate.Release();
    }

    private void SetContentStatus(string value)
    {
        contentUpdateStatus = value;
        foreach (var label in contentUpdateLabels) label.Text = value;
    }
    private readonly SemaphoreSlim contentCheckGate = new(1);
    private async Task CheckContentUpdateAsync(bool begin)
    {
        var ct = clientUpdateStop.Token;
        if (!await contentCheckGate.WaitAsync(0, ct)) return;
        try
        {
            var hello = await service.Client.HelloAsync(ct);
            if (!hello.Capabilities.Contains("content.update.v1")) { SetContentStatus("当前 Host 不支持数据自动更新。"); return; }
            var state = await service.Client.Call<JsonElement>("content.update.status", new { }, ct);
            if (!state.GetProperty("configured").GetBoolean()) { SetContentStatus("尚未配置百科与映射数据更新源。"); return; }
            if (begin) state = await service.Client.Call<JsonElement>("content.update.begin", new { }, ct);
            while (state.GetProperty("busy").GetBoolean())
            {
                SetContentStatus("正在检查并安装签名数据包…");
                await Task.Delay(500, ct);
                state = await service.Client.Call<JsonElement>("content.update.status", new { }, ct);
            }
            var installed = state.GetProperty("installedSequence").GetInt64();
            var active = state.GetProperty("activeSequence").GetInt64();
            SetContentStatus(state.GetProperty("result").GetString() switch
            {
                "failed" or "invalid-policy" => "数据更新失败，已有数据仍可使用。",
                "cancelled" => "已取消数据更新。",
                _ when installed != active => $"数据包 {installed} 已安装，下次 Host 启动生效（当前 {active}）。",
                _ => $"数据已是当前可用版本（序号 {active}）。"
            });
        }
        finally { contentCheckGate.Release(); }
    }
    private async Task ContentUpdateLoopAsync()
    {
        try
        {
            while (!clientUpdateStop.IsCancellationRequested)
            {
                try { await CheckContentUpdateAsync(true); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { SetContentStatus("数据更新检查失败，已有数据仍可使用。"); }
                await Task.Delay(TimeSpan.FromHours(6), clientUpdateStop.Token);
            }
        }
        catch (OperationCanceledException) { }
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
