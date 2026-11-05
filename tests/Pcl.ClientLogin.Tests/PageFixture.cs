using System.Text.Json;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Operations;
using Pcl.Core.Contracts.Sessions;
using Pcl.Core.Contracts.Host;

internal static class PageFixture
{
    internal static async Task RunAsync(string root)
    {
        var wire = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
        var pages = new Dictionary<string, PageSnapshot>();
        var subscriptions = new Dictionary<string, string>();
        bool writeCancelled = false, refreshAfterCancel = false;
        int refreshes = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var req = JsonDocument.Parse(line).RootElement;
            var method = req.GetProperty("method").GetString();
            var args = req.GetProperty("params");
            object result;
            string? cancelledId = null;
            switch (method)
            {
                case "host.hello": result = new HostHello(1, "fixture-host", [], 524288);break;
                case "page.begin":
                    var command = args.Deserialize<PageCommand>(wire)!;
                    var id = command.Method == "workspace.instances.backup" ? "write" : "read";
                    pages.Add(id, new(id, id == "write" ? "running" : "succeeded", "fixture", 1, DateTimeOffset.UtcNow, command,
                        Result: JsonSerializer.SerializeToElement(new { ok = true })));
                    result = new OperationAcceptance(id, pages[id].State, false);
                    break;
                case "event.subscribe":
                    var subject = args.GetProperty("subjectId").GetString()!;
                    var subscription = args.GetProperty("subscriptionId").GetString()!;
                    subscriptions[subject] = subscription;
                    result = new EventSubscription(subscription, "fixture-host", "page", subject);
                    break;
                case "event.unsubscribe": result = new { removed = true };break;
                case "page.get": result = pages[args.GetProperty("operationId").GetString()!];break;
                case "page.cancel":
                    cancelledId = args.GetProperty("operationId").GetString()!;
                    result = pages[cancelledId] with { State = "cancelling" };
                    break;
                case "desktop.snapshot":
                    refreshes++;refreshAfterCancel |= writeCancelled;
                    result = new { accounts = new { accounts = Array.Empty<object>(), selectedId = (string?)null } };
                    break;
                case "host.shutdown": result = new { stopped = true };break;
                default: throw new InvalidOperationException("Unexpected fixture request: " + method);
            }
            File.WriteAllText(Path.Combine(root, "pages-fixture.json"), JsonSerializer.Serialize(new
            { beginCount = pages.Count, uniqueKeys = pages.Values.Select(p => p.Command.IdempotencyKey).Distinct().Count(), refreshes, refreshAfterCancel }));
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { protocolVersion = 1, id = req.GetProperty("id").GetString(), result }, wire));
            await Console.Out.FlushAsync();
            if (cancelledId is not null)
            {
                await Task.Delay(100);
                pages[cancelledId] = pages[cancelledId] with { State = "cancelled", ErrorCode = "CANCELLED", Sequence = 2 };
                writeCancelled = true;
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    protocolVersion = 1,
                    method = "event.changed",
                    @params = new HostEvent(subscriptions[cancelledId], "fixture-host", "page", cancelledId, 2, JsonSerializer.SerializeToElement(pages[cancelledId], wire))
                }, wire));
                await Console.Out.FlushAsync();
            }
            if (method == "host.shutdown") break;
        }
    }
}
