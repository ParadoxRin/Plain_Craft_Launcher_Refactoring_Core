using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop.Bootstrap;
// Only assembles a Client and view lifecycle. Host owns all stores, installers and credentials.
internal static class RemoteComposition
{
    internal static async Task<DesktopServices> CreateAsync(string root)
    {
        var host = Environment.GetEnvironmentVariable("PCL_HOST_ASSEMBLY") ?? Path.Combine(AppContext.BaseDirectory, "host", "Pcl.Host.dll");
        var dotnet = Environment.GetEnvironmentVariable("PCL_DOTNET") ?? Environment.ProcessPath ?? "dotnet";
        // framework-dependent .app 的主进程可能是 apphost，使用随包 runtime 或本机 dotnet 启动 Host。
        if (!Path.GetFileName(dotnet)
            .StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "dotnet", "dotnet");
            dotnet = File.Exists(bundled)
                ? bundled
                : "dotnet";
        }

        var nativeHost = Path.Combine(AppContext.BaseDirectory, "host", "Pcl.Host");
        var standalone = Environment.GetEnvironmentVariable("PCL_HOST_ASSEMBLY")is null && File.Exists(nativeHost) && File.Exists(
            Path.Combine(AppContext.BaseDirectory, "host", "libhostfxr.dylib")
        );
        var remote = new Pcl.Client.LauncherSession(
            new HostClient(
                standalone
                    ? nativeHost
                    : dotnet,
                standalone
                    ? ""
                    : host,
                root,
                reconnectable: true
            )
        );
        try
        {
            // 客户端部署方显式提供可信更新源；没有策略文件时保持随包 Host 和原登录行为。
            var policyFile = Path.Combine(AppContext.BaseDirectory, "host-update-policy.json");
            if (File.Exists(policyFile))
            {
                var policy = System.Text.Json.JsonSerializer.Deserialize<Pcl.Client.Updates.HostUpdatePolicy>(File.ReadAllText(policyFile),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("UPDATE_POLICY_INVALID");
                var scope = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory + "\n" + Path.GetFullPath(root))));
                remote.Client.ConfigureUpdates(policy, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PlainCraftLauncherRefactoring", "host-updates", scope));
            }
            await remote.InitializeAsync();
            var journal = new LifecycleJournal();
            return new(
                remote,
                new RemoteLifetime(remote, journal.Record),
                journal
            );
        }
        catch
        {
            await remote.DisposeAsync();
            throw;
        }
    }
}

internal sealed record DesktopServices(LauncherSession Remote, RemoteLifetime Lifetime, LifecycleJournal Journal);
internal sealed class LifecycleJournal
{
    private readonly string file = Path.Combine(
        Path.GetTempPath(),
        "pcl-desktop-lifecycle",
        Guid.NewGuid()
            .ToString("N") + ".jsonl"
    );
    internal void Record(string name)
    {
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(file)!
            );
            File.AppendAllText(
                file,
                System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        time = DateTimeOffset.UtcNow,
                        name
                    }
                ) + "\n"
            );
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class RemoteLifetime(LauncherSession remote, Action<string> observe)
{
    private Task? shutdown;
    private readonly SemaphoreSlim gate = new(1, 1);
    public bool Released { get; private set; }

    public async Task<bool> ShutdownAsync(Task uiQuiescence, TimeSpan budget)
    {
        await gate.WaitAsync();
        try
        {
            shutdown ??= StopAsync(uiQuiescence);
        }
        finally
        {
            gate.Release();
        }

        try
        {
            await shutdown.WaitAsync(budget);
            return true;
        }
        catch (TimeoutException)
        {
            observe("scope.timeout");
            return false;
        }
    }

    private async Task StopAsync(Task uiQuiescence)
    {
        await uiQuiescence;
        await remote.ShutdownAsync();
        await remote.DisposeAsync();
        Released = true;
        observe("scope.drained");
    }
}
