using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

internal static class ExternalRuntimeFlow
{
    internal static async Task Run(string root, string report, string java)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new InvalidOperationException("FRESH_ROOT_REQUIRED");
        var host = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE") ?? throw new InvalidOperationException("EXPLICIT_HOST_REQUIRED");
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var session = new LauncherSession(new HostClient(host, "", root)))
        {
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            var runtime = await api.CallAsync<JavaRuntime>("workspace.java.inspect", null, new { path = Path.GetFullPath(java) }, timeout.Token);
            Check(runtime.MajorVersion == 21 && runtime.Architecture == "aarch64", "host-runs-real-java21-and-reports-architecture");
            Check(runtime.Executable == Path.GetFullPath(java), "external-runtime-path-preserved-through-contract");
            var missing = Path.Combine(root, "missing-java");
            bool rejected = false;
            try { await api.CallAsync<JavaRuntime>("workspace.java.inspect", null, new { path = missing }, timeout.Token); }
            catch (OperationRejectedException) { rejected = true; }
            Check(rejected, "missing-java-is-structured-rejection");
            var repeated = await api.CallAsync<JavaRuntime>("workspace.java.inspect", null, new { path = java }, timeout.Token);
            Check(repeated.MajorVersion == 21, "runtime-failure-does-not-break-host-connection");
            await session.Client.ShutdownAsync(timeout.Token);
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { status = "passed", checks,
            scope = "Real external Java binary through published Host and client contract; no game launch or download" }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
