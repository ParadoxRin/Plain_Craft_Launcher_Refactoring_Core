using System.Text.Json;
using Pcl.Core.Adapters.Content;

namespace Pcl.Host;

// A Host-owned background operation. The protocol never accepts a URL, path or trust key from UI.
internal sealed class ContentUpdates : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly string root = Environment.GetEnvironmentVariable("PCL_CONTENT_ROOT") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlainCraftLauncherRefactoring", "content", "wiki-project-index");
    private readonly string policyPath = Environment.GetEnvironmentVariable("PCL_CONTENT_POLICY") ?? Path.Combine(AppContext.BaseDirectory, "content-update-policy.json");
    private readonly Version version = Version.Parse(HostBuildInfo.Build.Version);
    private readonly long active = ActiveContentIndex.Status.Sequence;
    private readonly Func<HttpMessageHandler> transport;
    private readonly bool configured;
    private Task? pending;
    private string result = "idle";
    private long installed;
    private bool disposed;
    internal ContentUpdates(Func<HttpMessageHandler>? transport = null)
    {
        this.transport = transport ?? (() => new HttpClientHandler { AllowAutoRedirect = false });
        try
        {
            if (File.Exists(policyPath))
            {
                var policy = ContentPack.ReadPolicy(policyPath);
                configured = policy.FeedUrl is { Length: > 0 };
                installed = ContentPack.Load(root, policy, version).Sequence;
            }
        }
        catch (Exception) { result = "invalid-policy"; }
    }
    internal object Status()
    {
        lock (gate) return new { configured,
            busy = pending is { IsCompleted: false }, result, activeSequence = active, installedSequence = installed,
            activation = "next-host-start" };
    }
    internal object Begin()
    {
        lock (gate)
        {
            if (disposed) throw new InvalidOperationException("CONTENT_UPDATE_CLOSED");
            if (pending is { IsCompleted: false }) return Status();
            result = "checking";
            pending = Task.Run(UpdateAsync);
            return Status();
        }
    }
    private async Task UpdateAsync()
    {
        try
        {
            if (!File.Exists(policyPath)) { Set("not-configured"); return; }
            var policy = ContentPack.ReadPolicy(policyPath);
            if (string.IsNullOrEmpty(policy.FeedUrl)) { Set("not-configured"); return; }
            var uri = new Uri(policy.FeedUrl, UriKind.Absolute);
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) throw new InvalidDataException("CONTENT_HTTPS_REQUIRED");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var client = new HttpClient(transport());
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > ContentPack.MaxBytes) throw new InvalidDataException("CONTENT_TOO_LARGE");
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (bytes.Length + count > ContentPack.MaxBytes) throw new InvalidDataException("CONTENT_TOO_LARGE");
                bytes.Write(buffer, 0, count);
            }
            timeout.Token.ThrowIfCancellationRequested();
            var sequence = ContentPack.Install(root, bytes.ToArray(), policy, version);
            lock (gate) { installed = sequence; result = "installed"; }
            try { ContentPack.Cleanup(root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (OperationCanceledException) { Set("cancelled"); }
        catch (InvalidDataException ex) when (ex.Message == "CONTENT_SEQUENCE_NOT_NEWER") { Set("up-to-date"); }
        catch (Exception) { Set("failed"); } // Never expose publisher URLs, keys, or local paths through RPC.
    }
    private void Set(string value) { lock (gate) result = value; }
    public void Dispose()
    {
        Task? task;
        lock (gate) { disposed = true; stop.Cancel(); task = pending; }
        task?.GetAwaiter().GetResult();
        stop.Dispose();
    }
}
