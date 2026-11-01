using Pcl.Client;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Launch;
using Pcl.Core.Contracts.Operations;

// Compiled call recipes; not executed by the smoke example (no automatic downloads or game launch).
public static class Recipes
{
    public static async Task<OperationAcceptance> InstallAsync(HostClient client, string version, string loader,
        string persistedIdempotencyKey, CancellationToken ct)
    {
        var hello = await client.HelloAsync(ct);
        if (!hello.Capabilities.Contains("version.install.v3") || hello.InstallationTarget is null)
            throw new OperationRejectedException("REQUIRED_CAPABILITY_MISSING");
        return await client.Installations.AcceptAsync(new InstallCommand(persistedIdempotencyKey,
            new VersionInstallRequest(version, loader, hello.InstallationTarget)), ct);
    }

    public static IAsyncEnumerable<OperationSnapshot> WatchInstall(HostClient client, string operationId, CancellationToken ct)
        => ((IVersionInstallEvents)client.Installations).WatchAsync(operationId, ct);

    public static Task<OperationSnapshot> CancelInstallAsync(HostClient client, string operationId, CancellationToken ct)
        => client.Installations.CancelAsync(operationId, ct);

    // Obtain guidance from Host, show it to the user and obtain real consent before calling.
    public static Task<OperationAcceptance> LaunchAsync(HostClient client, string accountId, string instanceId,
        string persistedIdempotencyKey, bool detached, LaunchGuidanceConsent consent, CancellationToken ct)
        => client.Launches.AcceptAsync(new GameLaunchCommand(persistedIdempotencyKey, accountId, instanceId, detached, consent), ct);
}
