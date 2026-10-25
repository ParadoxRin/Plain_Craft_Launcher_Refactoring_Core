using Pcl.Client;
using Pcl.Core;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Launch;

namespace Pcl.Cli;
// 独立 Host 的正式 CLI 接线；账户创建/首次登录沿用已有命令，Host 启动后只通过受控契约修改数据。
internal static class RemoteCli
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length
                ? args[index + 1]
                : null;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        var ct = cancellation.Token;
        var root = new AppPaths(
            Option("--data-root")
        );
        var hostAssembly = Option("--host-assembly") ?? Path.GetFullPath("src/Pcl.Host/bin/Release/net10.0/Pcl.Host.dll");
        var runtime = Option("--dotnet") ?? (Environment.ProcessPath is { } process && Path.GetFileNameWithoutExtension(process) == "dotnet"
            ? process
            : "dotnet");
        try
        {
            await using var client = new HostClient(runtime, hostAssembly, root.Root);
            var hello = await client.HelloAsync(ct);
            var key = Option("--idempotency-key") ?? Guid.NewGuid()
                .ToString("N");
            if (args[0] == "host-install")
            {
                var target = hello.InstallationTarget ?? throw new InvalidOperationException("HOST_TARGET_UNAVAILABLE");
                var accepted = await client.Installations.AcceptAsync(
                    new(
                        key,
                        new VersionInstallRequest(
                            Option("--game-version") ?? "1.21.1",
                            Option("--loader") ?? "vanilla",
                            target
                        ),
                        Option("--resume-of")
                    ),
                    ct
                );
                Console.WriteLine("INSTALL OPERATION " + accepted.OperationId);
                var result = await client.Installations.WaitAsync(accepted.OperationId, ct);
                Console.WriteLine(
                    result.State + (result.ErrorCode is null
                        ? ""
                        : " " + result.ErrorCode)
                );
                await client.ShutdownAsync();
                return result.State == "succeeded"
                    ? 0
                    : 2;
            }

            var account = Option("--account") ?? throw new ArgumentException("请先用 account-add-offline/account-login 创建账户，再传 --account <id>。");
            var instance = Option("--profile") ?? "1.21.1";
            bool detached = args.Contains("--detached");
            // The Host owns policy; CLI obtains explicit consent before accepting a launch.
            var culture = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (string.IsNullOrWhiteSpace(culture))
            {
                culture = "en-US";
            }
            var guidance = await client.Call<LaunchGuidance>("game.guidance", new LaunchGuidanceRequest(account, culture), ct);
            var consent = await UnifiedCliLaunch.ConfirmGuidanceAsync(guidance, culture, ct);
            if (consent is null)
            {
                await client.ShutdownAsync();
                return 1;
            }
            var launch = await client.Launches.AcceptAsync(
                new(key, account, instance, detached, consent),
                ct
            );
            Console.WriteLine("LAUNCH OPERATION " + launch.OperationId);
            var done = await client.Launches.WaitAsync(launch.OperationId, ct);
            if (done.Result is null)
            {
                Console.WriteLine(done.ErrorCode ?? "LAUNCH_FAILED");
                await client.ShutdownAsync();
                return 2;
            }

            if (launch.Replayed)
            {
                Console.WriteLine("已返回原启动结果；没有重新创建游戏。");
                await client.ShutdownAsync();
                return 0;
            }

            Console.WriteLine("GAME PROCESS " + done.Result.GameProcessId);
            if (detached)
            {
                await client.ShutdownAsync();
                return 0;
            }

            var smoke = int.TryParse(
                Option("--smoke-seconds"),
                out var seconds
            )
                ? seconds
                : 0;
            var until = smoke > 0
                ? DateTime.UtcNow.AddSeconds(smoke)
                : DateTime.MaxValue;
            while (DateTime.UtcNow < until)
            {
                var game = await client.Launches.GetGameAsync(done.Result.GameProcessId, ct);
                if (game.State == "exited")
                {
                    await client.ShutdownAsync();
                    return game.ExitCode ?? 0;
                }

                await Task.Delay(250, ct);
            }

            await client.Launches.StopGameAsync(done.Result.GameProcessId);
            await client.ShutdownAsync();
            return 0;
        }
        catch (Pcl.Core.Contracts.Operations.OperationRejectedException ex)
        {
            Console.Error.WriteLine(ex.Code);
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("等待已停止；已受理操作继续由 Host 管理。");
            return 2;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("HOST_START_OR_TRANSPORT_FAILED");
            return 2;
        }
    }
}
