namespace Pcl.Core.Contracts.Workspaces;
// Public version labels omit download URLs and implementation models.
public sealed record MinecraftRelease(string Id, string Type, DateTimeOffset Released)
{
    public override string ToString() => Id + (Type == "release"
        ? ""
        : " · " + Type);
}

// Invitation and room passwords are intentionally absent from durable operation results.
public sealed record LinkSnapshot(
    string Id,
    string State,
    bool Host,
    int LocalPort,
    int ListenPort,
    int PeerCount,
    string[] Lines
);
