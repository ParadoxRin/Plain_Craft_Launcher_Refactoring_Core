using Pcl.Desktop.Updates;

if (args.Length != 1) return 2;
try
{
    var request = ClientUpdatePackage.Read<ClientUpdateRequest>(args[0]);
    var updater = new ClientSelfUpdate(request.Target);
    await updater.ApplyAsync(request, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(60), CancellationToken.None);
    return 0;
}
catch (Exception ex)
{
    // Requests contain no credentials; avoid logging full environment or arguments.
    Console.Error.WriteLine("CLIENT_UPDATE_FAILED " + ex.GetType().Name + ": " + ex.Message);
    return 1;
}
