using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Desktop;
public partial class MainWindow
{
    /// <summary>Explicit isolated test: real Host rejection, Client message, task-history window and launch preflight.</summary>
    private async Task Nested038SmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--remote-report") + 1];
        var checks = new List<string>();
        void Check(bool condition, string label)
        {
            if (!condition)
                throw new Exception(label);
            checks.Add(label);
        }

        try
        {
            if (!paths.Root.Contains("verification-0.38") || !paths.Root.EndsWith("diagnostic-ui-data"))
                throw new InvalidOperationException("ISOLATED_NESTED_TEST_ROOT_REQUIRED");
            string? message = null;
            try
            {
                await AccountApi.CallAsync<JsonElement>("workspace.resources.validate", "PCL038-Conflict");
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
            }

            Check(
                message is not null && message.Contains("pcl038_consumer") && message.Contains("pcl038_shared") && message.Contains(">=3"),
                "real-host-client-conflict-explanation"
            );
            Check(
                !message!.Contains(paths.Root),
                "no-host-root-in-client-message"
            );
            var row = (await service.PageTasks.ListAsync()).Last(
                x => x.Command.Method == "workspace.resources.validate"
            );
            Check(
                row.State == "failed" && row.DependencyFailure?.Code == "DEPENDENCY_CONFLICT",
                "structured-page-failure"
            );
            var window = new PageTasksWindow(service.PageTasks);
            var showing = window.ShowDialog(this);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var list = window.GetVisualDescendants()
                .OfType<ListBox>()
                .Single();
            while (list.Items.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            list.SelectedItem = list.Items.Cast<object>()
                .Single(
                    item => item.GetType()
                        .GetProperty("Snapshot")?.GetValue(item)is Pcl.Core.Contracts.Pages.PageSnapshot snapshot && snapshot.OperationId == row.OperationId
                );
            await Task.Delay(200);
            Check(
                window.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Any(
                        text => text.Text?.Contains("pcl038_shared") == true
                    ),
                "task-history-renders-constraint-details"
            );
            using (var bitmap = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                new Vector(96, 96)
            ))
            {
                bitmap.Render(window);
                bitmap.Save(
                    Path.Combine(
                        Path.GetDirectoryName(report)!,
                        "dependency-conflict.png"
                    )
                );
            }

            window.Close();
            await showing;
            var account = await AddOfflineAccountAsync("PCL038_Test", CancellationToken.None);
            var acceptance = await service.Client.Call<OperationAcceptance>(
                "game.launch",
                new
                {
                    idempotencyKey = Guid.NewGuid()
                        .ToString("N"),
                    accountId = account.Id,
                    instanceId = "PCL038-Conflict",
                    guidance = new
                    {
                        culture = "zh-CN",
                        sponsorRead = true,
                        purchaseRead = true,
                        demo = false
                    }
                }
            );
            GameLaunchSnapshot? launch = null;
            deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                launch = await service.Client.Call<GameLaunchSnapshot>(
                    "operation.get",
                    new
                    {
                        operationId = acceptance.OperationId
                    }
                );
                if (launch.State is "failed" or "succeeded" or "cancelled" or "interrupted")
                    break;
                await Task.Delay(100);
            }

            Check(
                launch?.State == "failed" && launch.DependencyFailure?.Code == "DEPENDENCY_CONFLICT" && launch.Result is null,
                "launch-rejected-before-game-with-structured-evidence"
            );
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        operationId = row.OperationId
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
                        errorType = ex.GetType()
                            .Name,
                        message = ex.Message
                    }
                )
            );
        }
        finally
        {
            Close();
        }
    }
}
