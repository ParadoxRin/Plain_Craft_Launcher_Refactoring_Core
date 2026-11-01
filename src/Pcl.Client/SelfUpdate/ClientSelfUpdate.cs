using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pcl.Client.SelfUpdate;

public sealed record ClientUpdateRequest(string Target, string Envelope, string Archive, int ParentPid,
    long ParentStartedTicks, string[] Arguments, bool Recover = false);
public sealed record ClientUpdateTransaction(string Backup, string OldHash, string NewHash, long OldSequence, long NewSequence,
    int CandidatePid = 0, long CandidateStartedTicks = 0);
public sealed record ClientRecoveryRunner(string Directory, string Hash);
public sealed record ClientUpdateState(long HighestSequence, string Result, ClientUpdateTransaction? Pending = null,
    string? PreviousDirectory = null);
public sealed record ClientUpdateReady(string Nonce, int Pid, long StartedTicks, long Sequence);

public sealed class ClientSelfUpdate
{
    public string Target { get; }
    public string Store { get; }
    public ClientUpdatePolicy Policy { get; }
    public ClientRelease Installed { get; }
    private string StatePath => Path.Combine(Store, "state.json");
    public ClientSelfUpdate(string target)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.OSArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("CLIENT_UPDATE_REQUIRES_MACOS_ARM64");
        Target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        ClientUpdatePackage.RejectLinks(Target);
        if (!Directory.Exists(Target) || Directory.GetParent(Target) is null) throw new IOException("CLIENT_UPDATE_TARGET_MISSING");
        Policy = ClientUpdatePackage.ReadPolicy(Target);
        Installed = ClientUpdatePackage.ReadRelease(Target);
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Target)));
        var storeRoot = Environment.GetEnvironmentVariable("PCL_CLIENT_UPDATE_ROOT") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlainCraftLauncherRefactoring", "client-updates");
        if (!Path.IsPathFullyQualified(storeRoot)) throw new InvalidDataException("CLIENT_UPDATE_STORE_MUST_BE_ABSOLUTE");
        Store = Path.Combine(Path.GetFullPath(storeRoot), scope);
        if (Store.StartsWith(Target + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Target.StartsWith(Store + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("CLIENT_UPDATE_STORE_OVERLAPS_TARGET");
        ClientUpdatePackage.RejectLinks(Store);
        Directory.CreateDirectory(Store);
        File.SetUnixFileMode(Store, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public ClientUpdateState Status
    {
        get
        {
            if (!File.Exists(StatePath)) return new(Installed.Sequence, "idle");
            var state = ClientUpdatePackage.Read<ClientUpdateState>(StatePath);
            if (state.HighestSequence <= 0 || string.IsNullOrWhiteSpace(state.Result))
                throw new InvalidDataException("CLIENT_UPDATE_STATE_INVALID");
            return state;
        }
    }
    public FileStream Lock()
    {
        var file = Path.Combine(Store, "update.lock");
        ClientUpdatePackage.RejectLinks(file);
        return new(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public FileStream AcquireRuntimeLease()
    {
        if (Status.Pending is not null && Environment.GetEnvironmentVariable("PCL_CLIENT_UPDATE_ACK") is null)
            throw new IOException("CLIENT_UPDATE_IN_PROGRESS");
        var path = Path.Combine(Store, "runtime.lock");
        ClientUpdatePackage.RejectLinks(path);
        if (!File.Exists(path)) using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private async Task<FileStream> RuntimeExclusiveAsync(TimeSpan budget, CancellationToken ct)
    {
        var path = Path.Combine(Store, "runtime.lock");
        ClientUpdatePackage.RejectLinks(path);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (watch.Elapsed < budget) { await Task.Delay(100, ct); }
        }
    }
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task FetchAsync(HttpClient http, string url, string destination, long limit, CancellationToken ct)
    {
        using var response = await http.GetAsync(ClientUpdatePackage.Https(url), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit)
            throw new IOException("CLIENT_UPDATE_DOWNLOAD_FAILED");
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("CLIENT_UPDATE_SIZE_LIMIT");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        output.Flush(true);
    }
    public async Task<string> DownloadAsync(HttpClient http, string[] restartArguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var held = Lock();
        if (Status.Pending is not null) throw new InvalidOperationException("CLIENT_UPDATE_RECOVERY_REQUIRED");
        var work = Path.Combine(Store, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var envelope = Path.Combine(work, "feed.json");
            await FetchAsync(http, Policy.FeedUrl, envelope, 256 * 1024, timeout.Token);
            var manifest = ValidateCandidate(envelope);
            var archive = Path.Combine(work, "client.zip");
            await FetchAsync(http, manifest.PackageUrl, archive, manifest.Size, timeout.Token);
            ClientUpdatePackage.VerifyArchive(archive, manifest);
            return CreateRequest(envelope, archive, restartArguments);
        }
        catch { Directory.Delete(work, true); throw; }
    }
    public string PrepareLocal(string envelope, string archive, string[] restartArguments)
    {
        using var held = Lock();
        if (Status.Pending is not null) throw new InvalidOperationException("CLIENT_UPDATE_RECOVERY_REQUIRED");
        var manifest = ValidateCandidate(envelope);
        ClientUpdatePackage.VerifyArchive(archive, manifest);
        var work = Path.Combine(Store, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        File.Copy(envelope, Path.Combine(work, "feed.json"));
        File.Copy(archive, Path.Combine(work, "client.zip"));
        return CreateRequest(Path.Combine(work, "feed.json"), Path.Combine(work, "client.zip"), restartArguments);
    }
    private string CreateRequest(string envelope, string archive, string[] args, bool recover = false)
    {
        using var self = Process.GetCurrentProcess();
        var request = new ClientUpdateRequest(Target, Path.GetFullPath(envelope), Path.GetFullPath(archive), self.Id,
            self.StartTime.ToUniversalTime().Ticks, args, recover);
        var path = Path.Combine(Store, "request-" + Guid.NewGuid().ToString("N") + ".json");
        ClientUpdatePackage.Save(path, request);
        return path;
    }
    private ClientUpdateManifest ValidateCandidate(string envelope)
    {
        var manifest = ClientUpdatePackage.Verify(ClientUpdatePackage.ReadBytes(envelope, 256 * 1024), Policy);
        if (manifest.Release.Sequence <= Math.Max(Installed.Sequence, Status.HighestSequence))
            throw new InvalidDataException("CLIENT_UPDATE_SEQUENCE_NOT_NEWER");
        if ((manifest.Kind == "macos-app") != Target.EndsWith(".app", StringComparison.Ordinal))
            throw new InvalidDataException("CLIENT_UPDATE_DEPLOYMENT_KIND_MISMATCH");
        return manifest;
    }

    /// <summary>Copy the installed helper outside the replaceable directory before spawning.</summary>
    public Process StartHelper(string request)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var source = Path.Combine(ClientUpdatePackage.BaseDirectory(Target), "updater");
        var destination = Path.Combine(Store, "helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        foreach (var path in ClientUpdatePackage.Walk(source))
        {
            var output = Path.Combine(destination, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(path, output);
            File.SetUnixFileMode(output, File.GetUnixFileMode(path));
        }
        ClientUpdatePackage.Save(Path.Combine(Store, "recovery-helper.json"), new ClientRecoveryRunner(destination, ClientUpdatePackage.TreeHash(destination)));
        return SpawnHelper(destination, request);
    }
    private static Process SpawnHelper(string directory, string request)
    {
        var start = new ProcessStartInfo(Path.Combine(directory, "Pcl.ClientUpdater")) { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(request));
        // Do not pass a stale update acknowledgement into recovery or another helper.
        start.Environment.Remove("PCL_CLIENT_UPDATE_ACK");
        start.Environment.Remove("PCL_CLIENT_UPDATE_NONCE");
        return Process.Start(start) ?? throw new IOException("CLIENT_UPDATE_HELPER_START_FAILED");
    }
    public bool RecoverBeforeStartup(string[] args)
    {
        FileStream held;
        try { held = Lock(); } catch (IOException) { return false; } // The live updater owns probation.
        using (held)
        {
            if (Status.Pending is null) return false;
            var request = CreateRequest(StatePath, StatePath, args, true);
            var runner = ClientUpdatePackage.Read<ClientRecoveryRunner>(Path.Combine(Store, "recovery-helper.json"));
            if (Path.GetDirectoryName(runner.Directory) != Store || !Path.GetFileName(runner.Directory).StartsWith("helper-", StringComparison.Ordinal) ||
                ClientUpdatePackage.TreeHash(runner.Directory) != runner.Hash)
                throw new InvalidDataException("CLIENT_UPDATE_RECOVERY_HELPER_INVALID");
            SpawnHelper(runner.Directory, request).Dispose();
            return true;
        }
    }

    public static void AcknowledgeReady()
    {
        var path = Environment.GetEnvironmentVariable("PCL_CLIENT_UPDATE_ACK");
        var nonce = Environment.GetEnvironmentVariable("PCL_CLIENT_UPDATE_NONCE");
        if (path is null || nonce is null) return;
        var target = CurrentTarget();
        var updater = new ClientSelfUpdate(target);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != updater.Store || nonce.Length != 64)
            throw new InvalidDataException("CLIENT_UPDATE_ACK_PATH_INVALID");
        using var process = Process.GetCurrentProcess();
        ClientUpdatePackage.Save(path, new ClientUpdateReady(nonce, process.Id, process.StartTime.ToUniversalTime().Ticks,
            ClientUpdatePackage.ReadRelease(target).Sequence));
    }
    public static string CurrentTarget()
    {
        var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        return directory.EndsWith(".app/Contents/MacOS", StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(directory, "../..")) : directory;
    }

    public async Task ApplyAsync(ClientUpdateRequest request, TimeSpan exitTimeout, TimeSpan readyTimeout, CancellationToken ct)
    {
        if (Path.GetFullPath(request.Target) != Target || request.Arguments.Length > 64)
            throw new InvalidDataException("CLIENT_UPDATE_REQUEST_INVALID");
        using var held = Lock();
        // No timeout or cancellation path is allowed to kill the old client.
        await WaitForParentAsync(request.ParentPid, request.ParentStartedTicks, exitTimeout, ct);
        if (request.Recover || Status.Pending is not null)
        {
            var pending = Status.Pending;
            if (pending is { CandidatePid: > 0 })
            {
                try
                {
                    using var owned = Process.GetProcessById(pending.CandidatePid);
                    if (!owned.HasExited && owned.StartTime.ToUniversalTime().Ticks == pending.CandidateStartedTicks)
                    { owned.Kill(true); await owned.WaitForExitAsync(ct); }
                }
                catch (ArgumentException) { }
            }
            using (await RuntimeExclusiveAsync(exitTimeout, ct)) Recover();
            StartClient(request.Arguments, null, null).Dispose();
            return;
        }
        var stage = Path.Combine(Path.GetDirectoryName(Target)!, "." + Path.GetFileName(Target) + ".pcl-update-" + Guid.NewGuid().ToString("N"));
        using var extraction = CancellationTokenSource.CreateLinkedTokenSource(ct);
        extraction.CancelAfter(TimeSpan.FromMinutes(5));
        Process? candidate = null;
        var runtimeLease = await RuntimeExclusiveAsync(exitTimeout, ct);
        try
        {
            var manifest = ValidateCandidate(request.Envelope);
            await ClientUpdatePackage.ExtractAsync(request.Archive, manifest, stage, Policy, extraction.Token);
            var replacement = manifest.Kind == "macos-app" ? Path.Combine(stage, manifest.ArchiveRoot) : stage;
            await PreserveHostConfigurationAsync(replacement, extraction.Token);
            var pending = new ClientUpdateTransaction(replacement, ClientUpdatePackage.TreeHash(Target), ClientUpdatePackage.TreeHash(replacement),
                Installed.Sequence, manifest.Release.Sequence);
            ClientUpdatePackage.Save(StatePath, new ClientUpdateState(manifest.Release.Sequence, "starting", pending, Status.PreviousDirectory));
            Swap(Target, replacement); // macOS atomic exchange: the original target path never disappears.
            runtimeLease.Dispose();
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var ack = Path.Combine(Store, "ready-" + nonce + ".json");
            candidate = StartClient(request.Arguments, ack, nonce);
            var birth = candidate.StartTime.ToUniversalTime().Ticks;
            ClientUpdatePackage.Save(StatePath, Status with { Pending = pending with { CandidatePid = candidate.Id, CandidateStartedTicks = birth } });
            var watch = Stopwatch.StartNew();
            var ready = false;
            while (watch.Elapsed < readyTimeout)
            {
                ct.ThrowIfCancellationRequested();
                if (candidate.HasExited) throw new IOException("CLIENT_UPDATE_STARTUP_EXITED");
                if (File.Exists(ack))
                {
                    var signal = ClientUpdatePackage.Read<ClientUpdateReady>(ack);
                    if (signal != new ClientUpdateReady(nonce, candidate.Id, birth, manifest.Release.Sequence))
                        throw new InvalidDataException("CLIENT_UPDATE_ACK_INVALID");
                    ready = true;
                    break;
                }
                await Task.Delay(100, ct);
            }
            if (!ready) throw new TimeoutException("CLIENT_UPDATE_STARTUP_TIMEOUT");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            if (candidate.HasExited) throw new IOException("CLIENT_UPDATE_STARTUP_EXITED");
            ClientUpdatePackage.Save(StatePath, new ClientUpdateState(manifest.Release.Sequence, "updated", null, replacement));
            File.Delete(ack);
        }
        catch
        {
            runtimeLease.Dispose();
            if (candidate is not null)
            {
                try { if (!candidate.HasExited) candidate.Kill(true); await candidate.WaitForExitAsync(CancellationToken.None); }
                catch (InvalidOperationException) { }
            }
            if (Status.Pending is not null)
            {
                using (await RuntimeExclusiveAsync(exitTimeout, CancellationToken.None)) Recover();
            }
            else ClientUpdatePackage.Save(StatePath, Status with { Result = "rejected-before-replacement" });
            // Restart the known previous client at the same path with the same data-root arguments.
            StartClient(request.Arguments, null, null).Dispose();
            throw;
        }
        finally { runtimeLease.Dispose(); candidate?.Dispose(); }
    }
    private Process StartClient(string[] arguments, string? ack, string? nonce)
    {
        var start = new ProcessStartInfo(Path.Combine(ClientUpdatePackage.BaseDirectory(Target), "PCL-MacPort"))
        { UseShellExecute = false, WorkingDirectory = ClientUpdatePackage.BaseDirectory(Target) };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment.Remove("PCL_CLIENT_UPDATE_ACK");
        start.Environment.Remove("PCL_CLIENT_UPDATE_NONCE");
        if (ack is not null) { start.Environment["PCL_CLIENT_UPDATE_ACK"] = ack; start.Environment["PCL_CLIENT_UPDATE_NONCE"] = nonce!; }
        return Process.Start(start) ?? throw new IOException("CLIENT_UPDATE_CLIENT_START_FAILED");
    }
    private async Task PreserveHostConfigurationAsync(string replacement, CancellationToken ct)
    {
        var previous = ClientUpdatePackage.BaseDirectory(Target);
        var next = ClientUpdatePackage.BaseDirectory(replacement);
        foreach (var relative in new[] { "host-update-policy.json", "host/content-update-policy.json" })
        {
            var oldFile = Path.Combine(previous, relative);
            var newFile = Path.Combine(next, relative);
            if (File.Exists(oldFile) && (!File.Exists(newFile) || !System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(ClientUpdatePackage.ReadBytes(oldFile, 1024 * 1024)),
                    System.Text.Json.Nodes.JsonNode.Parse(ClientUpdatePackage.ReadBytes(newFile, 1024 * 1024)))))
                throw new InvalidDataException("CLIENT_UPDATE_HOST_POLICY_CHANGED");
        }
        var oldHost = Path.Combine(previous, "host/Pcl.Host");
        if (!File.Exists(oldHost)) return; // Standalone clients keep their externally configured Host.
        var newHost = Path.Combine(next, "host/Pcl.Host");
        if (!File.Exists(newHost)) throw new InvalidDataException("CLIENT_UPDATE_BUNDLED_HOST_REMOVED");
        var oldProbe = JsonSerializer.Deserialize<Pcl.Core.Contracts.Host.HostHello>(await ClientUpdatePackage.RunAsync(oldHost, ["--update-probe"], ct), ClientUpdatePackage.Json)!;
        var newProbe = JsonSerializer.Deserialize<Pcl.Core.Contracts.Host.HostHello>(await ClientUpdatePackage.RunAsync(newHost, ["--update-probe"], ct), ClientUpdatePackage.Json)!;
        if (newProbe.ProtocolVersion != 1 || newProbe.Build?.DataEpoch != 1 || oldProbe.Build is null ||
            newProbe.Build.Sequence < oldProbe.Build.Sequence || !newProbe.Capabilities.Contains("desktop.contract-only.v1"))
            throw new InvalidDataException("CLIENT_UPDATE_HOST_INCOMPATIBLE_OR_OLDER");
    }
    private void Recover()
    {
        var state = Status;
        var pending = state.Pending ?? throw new InvalidOperationException("CLIENT_UPDATE_NO_PENDING_TRANSACTION");
        var parent = Path.GetDirectoryName(Target)!;
        var prefix = Path.Combine(parent, "." + Path.GetFileName(Target) + ".pcl-update-");
        if (!Path.GetFullPath(pending.Backup).StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("CLIENT_UPDATE_RECOVERY_PATH_INVALID");
        var targetHash = ClientUpdatePackage.TreeHash(Target);
        if (targetHash == pending.NewHash && ClientUpdatePackage.TreeHash(pending.Backup) == pending.OldHash)
            Swap(Target, pending.Backup);
        else if (targetHash != pending.OldHash)
            throw new InvalidDataException("CLIENT_UPDATE_RECOVERY_CONTENT_CHANGED");
        ClientUpdatePackage.Save(StatePath, state with { Result = "rolled-back", Pending = null });
    }
    private static async Task WaitForParentAsync(int pid, long birth, TimeSpan budget, CancellationToken ct)
    {
        Process process;
        try { process = Process.GetProcessById(pid); } catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != birth) return;
            await process.WaitForExitAsync(ct).WaitAsync(budget, ct);
        }
    }
    [DllImport("libSystem.B.dylib", SetLastError = true, EntryPoint = "renameatx_np")]
    private static extern int RenameSwap(int fromFd, string from, int toFd, string to, uint flags);
    private static void Swap(string first, string second)
    {
        if (RenameSwap(-2, first, -2, second, 2) != 0)
            throw new IOException("CLIENT_UPDATE_ATOMIC_SWAP_FAILED: " + Marshal.GetLastPInvokeError());
    }
}
