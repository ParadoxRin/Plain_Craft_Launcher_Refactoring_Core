using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using Pcl.Core.Ports.Operations;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Core.Adapters.Pages;
public sealed partial class PageExecutionAdapter
{
    private sealed record ActiveLink(
        LinkProcess Process,
        LinkRoom Room,
        bool Host,
        int LocalPort,
        int ListenPort,
        ConcurrentQueue<string> Lines
    );
    private readonly ConcurrentDictionary<string, ActiveLink> links = new();
    // Ephemeral invitations contain room credentials: read only over the private RPC, never through receipts or logs.
    public string LinkInvitation(string id) => links.TryGetValue(id, out var link) && !link.Process.Process.HasExited
        ? link.Room.Encode()
        : throw new OperationInstallException("LINK_SESSION_EXPIRED");
    private async Task<object?> ExecuteLinkAsync(string id, string method, JObject args, CancellationToken ct)
    {
        string Text(string key) => (string? )args[key] ?? "";
        if (method == "workspace.link.start")
        {
            if (!links.IsEmpty)
            {
                throw new OperationInstallException("LINK_ALREADY_RUNNING");
            }

            var isHost = (bool? )args["host"] ?? false;
            var room = isHost
                ? LinkRoom.Create(
                    (int? )args["gamePort"] ?? 0,
                    args["peers"]?.ToObject<string[]>() ?? []
                )
                : LinkRoom.Decode(
                    Secret(
                        Text("secretHandle"),
                        consume: true
                    ) ?? throw new OperationInstallException("SECRET_REENTRY_REQUIRED")
                );
            room.Validate();
            var local = (int? )args["localPort"] ?? 25566;
            var listen = (int? )args["listenPort"] ?? (isHost
                ? 11010
                : EasyTierService.FreePort());
            if (local is < 1 or > 65535 || listen is < 1 or > 65535)
            {
                throw new OperationInstallException("LINK_PORT_INVALID");
            }

            var lines = new ConcurrentQueue<string>();
            var process = await new EasyTierService(source).StartAsync(
                room,
                isHost,
                local,
                listen,
                line =>
                {
                    // Engine redacts room secret; bound both number and length of public diagnostics.
                    lines.Enqueue(
                        line[..Math.Min(line.Length, 1000)]
                    );
                    while (lines.Count > 20)
                    {
                        lines.TryDequeue(out _);
                    }
                },
                ct
            );
            if (ct.IsCancellationRequested)
            {
                process.Dispose();
                ct.ThrowIfCancellationRequested();
            }

            links[id] = new(process, room, isHost, local, listen, lines);
            return new View.LinkSnapshot(
                id,
                "running",
                isHost,
                local,
                listen,
                0,
                lines.ToArray()
            );
        }

        var key = Text("id");
        if (!links.TryGetValue(key, out var current))
        {
            return new View.LinkSnapshot(
                key,
                "stopped",
                false,
                0,
                0,
                0,
                []
            );
        }

        if (method == "workspace.link.stop")
        {
            if (links.TryRemove(key, out current))
            {
                current.Process.Dispose();
            }

            return new View.LinkSnapshot(
                key,
                "stopped",
                false,
                0,
                0,
                0,
                []
            );
        }

        if (method != "workspace.link.status")
        {
            throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
        }

        if (current.Process.Process.HasExited)
        {
            links.TryRemove(key, out _);
            current.Process.Dispose();
            return new View.LinkSnapshot(
                key,
                "exited",
                current.Host,
                current.LocalPort,
                current.ListenPort,
                0,
                current.Lines.ToArray()
            );
        }

        var peers = JArray.Parse(
            await current.Process.PeersAsync(ct)
        );
        return new View.LinkSnapshot(
            key,
            "running",
            current.Host,
            current.LocalPort,
            current.ListenPort,
            peers.OfType<JObject>()
                .Count(
                    x => (string? )x["cost"] != "Local"
                ),
            current.Lines.ToArray()
        );
    }
}
