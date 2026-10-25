using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Workspaces;

internal static class FaultFlow
{
    // 故意终止的仅是本测试新建 Host；用真实磁盘收据验证取消、重连和不确定提交的拒绝重放。
    internal static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        report = Path.GetFullPath(report);
        var checks = new List<string>();
        var wire = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        void Check(bool okay, string name)
        {
            if (!okay)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            Console.WriteLine(name);
        }

        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
        await using var client = new HostClient(
            native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
            native is null
                ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
                : "",
            root,
            reconnectable: true
        );
        try
        {
            var paths = new Pcl.Core.AppPaths(root);
            var source = Path.Combine(paths.Game, "config", "workspace-crash-fixture");
            Directory.CreateDirectory(source);
            for (var i = 0; i < 10000; i++)
            {
                File.WriteAllText(
                    Path.Combine(source, i + ".txt"),
                    "source must survive"
                );
            }

            File.WriteAllBytes(
                Path.Combine(source, "large.bin"),
                RandomNumberGenerator.GetBytes(32 * 1024 * 1024)
            );
            await client.HelloAsync();
            var pages = new HostPageOperations(client);
            var command = new PageCommand("backup-crash", "workspace.instances.backup", InstanceId: "1.21.1", PayloadJson: "{}");
            var accepted = await pages.AcceptAsync(command);
            Check(
                (await pages.AcceptAsync(command)).OperationId == accepted.OperationId,
                "workspace-duplicate-admission-has-one-operation"
            );
            var cancelled = await pages.AcceptAsync(
                new(
                    "cancel-queued",
                    "workspace.instances.copy",
                    InstanceId: "1.21.1",
                    PayloadJson: "{\"name\":\"never copied\"}"
                )
            );
            await pages.CancelAsync(cancelled.OperationId);
            Check(
                (await pages.WaitAsync(cancelled.OperationId)).State == "cancelled",
                "queued-workspace-cancel-does-not-execute"
            );
            command = command with
            {
                IdempotencyKey = "actual-crash"
            };
            accepted = await pages.AcceptAsync(command);
            var tracking = "op-" + Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes("workspace:" + accepted.OperationId)
                )
            )
                .ToLowerInvariant()[..32];
            var checkpoint = Path.Combine(root, "page-execution-v1", tracking + ".json");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(checkpoint) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5);
            }

            Check(
                File.Exists(checkpoint),
                "workspace-start-receipt-exists-before-write"
            );
            using (var process = Process.GetProcessById(client.HostPid))
            {
                process.Kill();
                await process.WaitForExitAsync();
            }

            await client.DisconnectAsync();
            await client.ReconnectAsync();
            pages = new(client);
            var interrupted = await pages.GetAsync(accepted.OperationId);
            Check(interrupted.State == "interrupted", "host-crash-restores-interrupted-workspace-state");
            var retry = await pages.AcceptAsync(
                command with
                {
                    IdempotencyKey = "resume-crash",
                    ResumeOfOperationId = accepted.OperationId
                }
            );
            var terminal = await pages.WaitAsync(retry.OperationId);
            Check(
                terminal.State == "failed" && terminal.ErrorCode == "WORKSPACE_REVIEW_REQUIRED",
                "uncertain-write-refuses-blind-resume"
            );
            Check(
                Directory.GetFiles(source)
                    .Length == 10001 && File.ReadAllText(
                    Path.Combine(source, "0.txt")
                ) == "source must survive",
                "crash-and-retry-preserve-source-files"
            );
            var resumed = await pages.AcceptAsync(
                new(
                    "resume-queued",
                    "workspace.instances.copy",
                    InstanceId: "1.21.1",
                    PayloadJson: "{\"name\":\"never copied\"}",
                    ResumeOfOperationId: cancelled.OperationId
                )
            );
            var complete = await pages.WaitAsync(resumed.OperationId);
            Check(complete.State == "succeeded", "cancel-before-execution-can-safely-resume");
            var copied = complete.Result!.Value.Deserialize<GameProfile>(wire)!;
            Check(
                Directory.Exists(copied.GameDirectory),
                "safe-resume-creates-real-copy"
            );
            bool secretRejected = false;
            try
            {
                await pages.AcceptAsync(
                    new(
                        "bad-secret",
                        "workspace.packs.inspect",
                        PayloadJson: "{\"nested\":{\"accessToken\":\"fixture\"}}"
                    )
                );
            }
            catch (OperationRejectedException ex)
            {
                secretRejected = ex.Code == "SECRET_IN_PERSISTED_COMMAND";
            }

            Check(secretRejected, "nested-secret-rejected-before-receipt-persistence");
            Check(
                !Directory.EnumerateFiles(
                    Path.Combine(root, "page-operations-v1"),
                    "*.json"
                )
                    .Any(
                        x => File.ReadAllText(x)
                            .Contains("bad-secret")
                    ),
                "rejected-secret-command-leaves-no-receipt"
            );
            await client.ShutdownAsync();
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = ex.Message
                    }
                )
            );
            try
            {
                await client.ShutdownAsync();
            }
            catch
            {
            }

            throw;
        }
    }
}
