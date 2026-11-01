using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

if (args.Length is < 2 or > 3)
{
    Console.WriteLine("Pcl.DeveloperQuickstart <absolute-Host-executable> <fresh-absolute-data-root> [absolute-java21]");
    return 2;
}
if (!Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]) || Directory.Exists(args[1]))
    throw new ArgumentException("Use absolute Host path and a fresh isolated data root.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
await using var session = new LauncherSession(new HostClient(args[0], "", args[1], reconnectable: true));
try
{
    await session.InitializeAsync();
    var hello = await session.Client.HelloAsync(timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(hello));
    if (hello.Capabilities.Contains("content.update.v1"))
        Console.WriteLine(await session.Client.Call<JsonElement>("content.update.status", new { }, timeout.Token));
    if (args.Length == 3)
    {
        var java = await new WorkspaceApi(session.Workspaces).CallAsync<JavaRuntime>(
            "workspace.java.inspect", null, new { path = Path.GetFullPath(args[2]) }, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(java));
    }
    Console.WriteLine("QUICKSTART_CONNECTED");
    return 0;
}
catch (OperationRejectedException ex)
{
    Console.Error.WriteLine(ex.Code + ": verify Host compatibility and use physical absolute paths (pwd -P on macOS).");
    return 2;
}
finally
{
    // This sample owns its isolated Host. A real client must use its own task/game exit policy.
    if (session.Client.IsConnected) await session.Client.ShutdownAsync(timeout.Token);
}
