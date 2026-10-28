using System.Text.Json;
using Pcl.Core.Contracts;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Operations;
using Pcl.MinimalClient;

// 明确提供 Host 和数据目录，示例不默认连接用户现有的生产实例。
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("dotnet Pcl.MinimalClient.dll --host /absolute/Pcl.Host --data-root /absolute/isolated-root");
    Console.WriteLine("或 --host-dll /absolute/Pcl.Host.dll --dotnet /absolute/dotnet --data-root /absolute/isolated-root");
    return;
}
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || args[index] is not ("--host" or "--host-dll" or "--dotnet" or "--data-root")
        || !options.TryAdd(args[index], args[index + 1]))
    {
        Console.Error.WriteLine("INVALID_OPTIONS; 使用 --help 查看用法。");
        Environment.ExitCode = 2;
        return;
    }
}
if (!options.TryGetValue("--data-root", out var root)
    || options.ContainsKey("--host") == options.ContainsKey("--host-dll"))
{
    Console.Error.WriteLine("必须指定数据目录及一种 Host 启动方式。");
    Environment.ExitCode = 2;
    return;
}
var executable = options.GetValueOrDefault("--host") ?? options.GetValueOrDefault("--dotnet", "dotnet");
var assembly = options.GetValueOrDefault("--host-dll", "");
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
void Show(object? value) => Console.WriteLine(JsonSerializer.Serialize(value, json));
void Help()
{
    Console.WriteLine("status | accounts | offline <name> <key> | operations | get <operationId>");
    Console.WriteLine("install <version> <loader> <key> | cancel <operationId> | watch <operationId>");
    Console.WriteLine("reconnect | restart | resume <operationId> <new-key>");
    Console.WriteLine("launch <instanceId> <accountId|trial> <key> [detached] | launch-get <operationId>");
    Console.WriteLine("game <gameProcessId> | stop <gameProcessId> | exit | leave");
    Console.WriteLine("Ctrl+C 仅停止当前请求/观察的等待；cancel 才取消安装。超时不代表 Host 已停止操作。");
    Console.WriteLine("reconnect 保留 Host；restart/exit 显式关闭 Host；leave 仅离开客户端。");
}
Console.WriteLine("原作者：龙腾猫跃 · " + ProductIdentity.SponsorUrl);
Console.WriteLine("Plain Craft Launcher Refactoring · 第三方独立二次创作 · 最小客户端示例");
Console.WriteLine("源码：https://github.com/ParadoxRin/Plain_Craft_Launcher_Refactoring_Core");
await using var launcher = new MinimalLauncher(executable, assembly, root);
CancellationTokenSource? command = null;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    command?.Cancel();
};
try
{
    using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await launcher.ConnectAsync(connect.Token);
    Show(launcher.Hello);
    Help();
    while (true)
    {
        Console.Write("pcl> ");
        var line = await Console.In.ReadLineAsync();
        // EOF 与 exit 采用相同的正常退出策略；不会把尚有任务的 Host 默默遗留在后台。
        var parts = (line ?? "exit").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            continue;
        }
        using var current = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        command = current;
        var ct = current.Token;
        try
        {
            switch (parts[0], parts.Length)
            {
                case ("help", 1):
                    Help();
                    break;
                case ("status", 1):
                    await launcher.ConnectAsync(ct);
                    Show(launcher.Hello);
                    break;
                case ("accounts", 1):
                    Show(await launcher.AccountsAsync(ct));
                    break;
                case ("offline", 3):
                    Show(await launcher.AddOfflineAsync(parts[1], parts[2], ct));
                    break;
                case ("operations", 1):
                    Show(await launcher.Client.Installations.ListAsync(ct));
                    break;
                case ("get", 2):
                    Show(await launcher.Client.Installations.GetAsync(parts[1], ct));
                    break;
                case ("install", 4):
                    Console.WriteLine("idempotencyKey=" + parts[3]);
                    Show(await launcher.InstallAsync(parts[1], parts[2], parts[3], ct));
                    break;
                case ("cancel", 2):
                    Show(await launcher.Client.Installations.CancelAsync(parts[1], ct));
                    break;
                case ("watch", 2):
                    await foreach (var row in launcher.WatchAsync(parts[1], ct))
                    {
                        Show(row);
                    }
                    break;
                case ("reconnect", 1):
                    await launcher.ReconnectAsync(ct);
                    Show(launcher.Hello);
                    break;
                case ("restart", 1):
                    await launcher.RestartHostAsync(ct);
                    Show(launcher.Hello);
                    break;
                case ("resume", 3):
                    Console.WriteLine("idempotencyKey=" + parts[2]);
                    Show(await launcher.ResumeAsync(parts[1], parts[2], ct));
                    break;
                case ("launch", 4):
                case ("launch", 5) when parts[4] == "detached":
                    var guidance = await launcher.GuidanceAsync(parts[2], ct);
                    Console.WriteLine("请支持原作者：" + guidance.SponsorUrl);
                    if (guidance.ShowPurchase)
                    {
                        Console.WriteLine("请购买 Minecraft 正版：" + guidance.PurchaseUrl);
                    }
                    var demo = guidance.RequireDemo || parts[2] == "trial";
                    Console.WriteLine(demo ? "本次启动官方试玩模式。" : "本次按所选账户启动。");
                    Console.WriteLine("阅读上述提示后输入 yes 继续，其他输入取消：");
                    if (await Console.In.ReadLineAsync(ct) != "yes")
                    {
                        Console.WriteLine("已取消，未提交启动。");
                        break;
                    }
                    Console.WriteLine("idempotencyKey=" + parts[3]);
                    var accepted = await launcher.LaunchAsync(parts[1], parts[2], parts[3], parts.Length == 5,
                        new("zh-CN", true, true, demo), ct);
                    Show(accepted);
                    Show(await launcher.Client.Launches.WaitAsync(accepted.OperationId, ct));
                    break;
                case ("launch-get", 2):
                    Show(await launcher.Client.Launches.GetAsync(parts[1], ct));
                    break;
                case ("game", 2):
                    Show(await launcher.Client.Launches.GetGameAsync(parts[1], ct));
                    break;
                case ("stop", 2):
                    await launcher.Client.Launches.StopGameAsync(parts[1], ct);
                    break;
                case ("exit", 1):
                    await launcher.Client.ShutdownAsync(ct);
                    return;
                case ("leave", 1):
                    return;
                default:
                    Console.WriteLine("命令或参数不正确，使用 help。");
                    break;
            }
        }
        catch (OperationRejectedException ex)
        {
            Console.Error.WriteLine(ex.Code);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("等待已停止；请用 operations/get 和原幂等键确认 Host 状态。");
        }
        finally
        {
            command = null;
        }
    }
}
catch (Exception ex)
{
    // 不打印原始 Host 日志或潜在含路径/账户资料的异常文本。
    Console.Error.WriteLine(ex is OperationRejectedException rejected ? rejected.Code : ex.GetType().Name);
    if (ex is OperationRejectedException { Code: "ATTACH_FAILED" or "HOST_CONNECT_TIMEOUT" })
    {
        Console.Error.WriteLine("请检查 Host 路径及数据目录权限；数据目录的父路径不能经过符号链接，可先用 pwd -P 确认物理路径。");
    }
    Environment.ExitCode = 1;
}
