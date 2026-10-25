using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Accounts;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Workspaces;

internal static class Copy035Guards
{
    // 重开 Host 后测试确认偏好与固定路径占用保护，并实际启动 UI 刚创建的副本。
    public static async Task Run(string root, string copyReport, string report)
    {
        root = Path.GetFullPath(root);
        var previous = JsonDocument.Parse(
            File.ReadAllText(copyReport)
        );
        var copyId = previous.RootElement.GetProperty("resultId")
            .GetString()!;
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
        }

        var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE")!;
        await using var session = new LauncherSession(
            new HostClient(native, "", root, reconnectable: true)
        );
        try
        {
            await session.InitializeAsync();
            var service = session.Components("1.21.1");
            var plan = await service.PreviewAsync("vanilla", null, true, default);
            Check(plan.SuppressConfirmation, "confirmation-preference-survives-host-restart");
            Directory.CreateDirectory(plan.TargetDirectory);
            var marker = Path.Combine(plan.TargetDirectory, "user-marker.txt");
            File.WriteAllText(marker, "do not overwrite");
            bool refused = false;
            try
            {
                await service.ApplyAsync(plan.PlanId, null, default);
            }
            catch
            {
                refused = true;
            }

            Check(
                refused && File.ReadAllText(marker) == "do not overwrite",
                "occupied-preview-target-is-preserved-and-install-refused"
            );
            File.Delete(marker);
            Directory.Delete(plan.TargetDirectory);
            var api = new WorkspaceApi(session.Workspaces);
            var account = await api.CallAsync<LauncherAccount>(
                "workspace.account.addOffline",
                null,
                new
                {
                    name = "PCL035_Copy"
                }
            );
            var accepted = await session.Launches.AcceptAsync(
                new GameLaunchCommand(
                    Guid.NewGuid()
                        .ToString("N"),
                    account.Id,
                    copyId
                )
            );
            var result = await session.Launches.WaitAsync(accepted.OperationId)
                .WaitAsync(
                    TimeSpan.FromMinutes(3)
                );
            Check(result.State == "succeeded", "ui-created-copy-launches-through-host");
            var game = result.Result!;
            var details = await session.Client.Call<GameDetails>(
                "game.details",
                new
                {
                    gameProcessId = game.GameProcessId
                }
            );
            try
            {
                var deadline = DateTime.UtcNow.AddMinutes(2);
                var rendered = false;
                while (DateTime.UtcNow < deadline)
                {
                    var text = File.Exists(details.LogFile)
                        ? File.ReadAllText(details.LogFile)
                        : "";
                    rendered = text.Contains("Created:") && text.Contains("atlas");
                    if (rendered || (await session.Launches.GetGameAsync(game.GameProcessId)).State == "exited")
                    {
                        break;
                    }

                    await Task.Delay(200);
                }

                Check(rendered, "ui-created-copy-initializes-real-renderer");
            }
            finally
            {
                await session.Launches.StopGameAsync(game.GameProcessId);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while ((await session.Launches.GetGameAsync(game.GameProcessId)).State != "exited" && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(100);
                }

                Check(
                    (await session.Launches.GetGameAsync(game.GameProcessId)).State == "exited",
                    "copy-test-game-cleaned-up"
                );
            }

            await session.ShutdownAsync();
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        worldAcceptance = "not-executed"
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
                        error = ex.ToString()
                    }
                )
            );
            try
            {
                await session.ShutdownAsync();
            }
            catch
            {
            }

            throw;
        }
    }
}
