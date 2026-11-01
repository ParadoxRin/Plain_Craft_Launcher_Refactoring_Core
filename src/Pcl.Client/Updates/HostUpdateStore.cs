using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Updates;

namespace Pcl.Client.Updates;

// 信任根由客户端发布者提供，不能从待验证的远端清单下载公钥。
public sealed record HostUpdatePolicy(string FeedUrl, string PublicKeyPem, string Channel = "stable",
    long MinimumSequence = 41000, int DataEpoch = 1, int Protocol = 1,
    string[]? RequiredCapabilities = null, bool ApplyWhenIdle = true, int CheckIntervalSeconds = 21600);

internal sealed record UpdateSelection(string Executable, string Assembly, HostUpdateManifest Manifest);
internal sealed record UpdateState(string? Active = null, string? Previous = null, string? Pending = null,
    string? Trial = null, string? Rejected = null, long HighestSequence = 0, string? HighestHash = null);

/// <summary>独立部署状态；只操作版本目录和一个刷盘后替换的指针，不写游戏/账户数据。</summary>
internal sealed class HostUpdateStore : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal readonly HostUpdatePolicy Policy;
    private readonly string root;
    private readonly FileStream lease;
    private readonly HttpClient http;
    private UpdateState state;
    internal string? Pending => state.Pending;
    internal bool InTrial => state.Trial is not null;
    internal string? Active => state.Active;
    private static readonly Regex SafeName = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    internal static string Runtime => (OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    internal HostUpdateStore(string directory, string dataRoot, HostUpdatePolicy policy, HttpClient? transport = null)
    {
        Policy = policy;
        RequireHttps(policy.FeedUrl);
        if (policy.CheckIntervalSeconds < 60 || policy.MinimumSequence < 0 || policy.DataEpoch != 1 || policy.Protocol != 1)
        {
            throw new InvalidDataException("UPDATE_POLICY_INVALID");
        }
        using var key = RSA.Create();
        key.ImportFromPem(policy.PublicKeyPem);
        if (key.KeySize < 2048)
        {
            throw new InvalidDataException("UPDATE_KEY_TOO_SMALL");
        }
        root = Path.GetFullPath(directory);
        SafeParents(root);
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        SafeParents(Path.Combine(root, ".update.lock"));
        lease = new FileStream(Path.Combine(root, ".update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        http = transport ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        try
        {
            // 同一更新存储仅归一个客户端数据作用域；避免跨客户端回滚或替换。
            var binding = Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot)));
            var bindingFile = Path.Combine(root, "scope");
            SafeParents(bindingFile);
            if (File.Exists(bindingFile) && File.ReadAllText(bindingFile) != binding)
            {
                throw new InvalidDataException("UPDATE_SCOPE_MISMATCH");
            }
            Atomic("scope", System.Text.Encoding.UTF8.GetBytes(binding));
            var stateFile = Path.Combine(root, "state.json");
            SafeParents(stateFile);
            state = File.Exists(stateFile)
                ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllBytes(stateFile), Json) ?? throw new InvalidDataException("UPDATE_STATE_INVALID")
                : new();
            foreach (var id in new[] { state.Active, state.Previous, state.Pending, state.Trial, state.Rejected, state.HighestHash })
            {
                if (id is not null && !Regex.IsMatch(id, "^[a-f0-9]{64}$"))
                {
                    throw new InvalidDataException("UPDATE_STATE_INVALID");
                }
            }
            // 客户端在确认健康前崩溃：下次启动保守回滚，不自动重试同一失败包。
            if (state.Trial is not null)
            {
                Rollback();
            }
        }
        catch
        {
            lease.Dispose();
            if (transport is null)
            {
                http.Dispose();
            }
            throw;
        }
        ownsHttp = transport is null;
    }
    private readonly bool ownsHttp;

    internal async Task<HostUpdateStatus> StageAsync(CancellationToken ct)
    {
        var envelope = await FetchAsync(Policy.FeedUrl, 2 * 1024 * 1024, ct);
        var manifest = Verify(envelope, true);
        var id = Hash(envelope);
        if (manifest.Build.Sequence < Math.Max(Policy.MinimumSequence, state.HighestSequence)
            || manifest.Build.Sequence == state.HighestSequence && state.HighestHash != id)
        {
            throw new InvalidDataException("UPDATE_DOWNGRADE_OR_EQUIVOCATION");
        }
        if (state.Rejected == id)
        {
            return new("rejected", "PREVIOUSLY_FAILED", manifest.Build.Version);
        }
        if (state.Active == id)
        {
            return new("current", "ALREADY_CURRENT", manifest.Build.Version);
        }
        var folder = Path.Combine(root, id);
        SafeParents(folder);
        if (!Directory.Exists(folder))
        {
            // 下载到独立临时目录；任何失败都不改 active/pending。
            var temp = Path.Combine(root, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var zipFile = Path.Combine(temp, "package.zip");
                await DownloadAsync(manifest.PackageUrl, zipFile, manifest.PackageSize, ct);
                if (FileHash(zipFile) != manifest.PackageSha256)
                {
                    throw new InvalidDataException("UPDATE_PACKAGE_HASH");
                }
                var unpacked = Path.Combine(temp, "files");
                Directory.CreateDirectory(unpacked);
                await ExtractAsync(zipFile, unpacked, manifest, ct);
                Directory.Move(unpacked, folder);
                Atomic(id + ".json", envelope);
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }
        VerifyFiles(folder, manifest);
        Atomic(id + ".json", envelope);
        Commit(state with { Pending = id, HighestSequence = manifest.Build.Sequence, HighestHash = id });
        return new("staged", "VERIFIED", manifest.Build.Version);
    }

    // 候选先在无业务数据的 probe 模式验证，再切指针；取消时保留 pending。
    internal async Task BeginAsync(string dotnet, CancellationToken ct)
    {
        if (state.Pending is not { } id)
        {
            return;
        }
        var selection = Select(id, dotnet);
        await ProbeAsync(selection, ct);
        Commit(state with { Previous = state.Active, Active = id, Trial = id, Pending = null });
    }
    internal UpdateSelection? Selection(string dotnet) => state.Active is { } id ? Select(id, dotnet) : null;
    internal void Confirm(HostHello hello)
    {
        if (state.Active is not { } id)
        {
            return;
        }
        CompatibleHello(hello, Verify(ReadEnvelope(id), false));
        if (state.Trial is not null)
        {
            Commit(state with { Trial = null });
        }
    }
    internal void Rollback()
    {
        Commit(state with { Rejected = state.Active, Active = state.Previous, Previous = null, Trial = null, Pending = null });
    }
    internal void RejectPending()
    {
        Commit(state with { Rejected = state.Pending, Pending = null });
    }

    private UpdateSelection Select(string id, string dotnet)
    {
        var manifest = Verify(ReadEnvelope(id), false);
        var folder = Path.Combine(root, id);
        VerifyFiles(folder, manifest);
        var entry = Path.Combine(folder, manifest.EntryPoint);
        return manifest.FrameworkDependent ? new(dotnet, entry, manifest) : new(entry, "", manifest);
    }
    private byte[] ReadEnvelope(string id)
    {
        var path = Path.Combine(root, id + ".json");
        SafeParents(path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 2 * 1024 * 1024 || Hash(bytes) != id)
        {
            throw new InvalidDataException("UPDATE_RECEIPT_HASH");
        }
        return bytes;
    }

    private HostUpdateManifest Verify(byte[] bytes, bool fresh)
    {
        var envelope = JsonSerializer.Deserialize<HostUpdateEnvelope>(bytes, Json) ?? throw new InvalidDataException("UPDATE_ENVELOPE_INVALID");
        var payload = Convert.FromBase64String(envelope.Payload);
        using var key = RSA.Create();
        key.ImportFromPem(Policy.PublicKeyPem);
        if (!key.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
        {
            throw new CryptographicException("UPDATE_SIGNATURE_INVALID");
        }
        var m = JsonSerializer.Deserialize<HostUpdateManifest>(payload, Json) ?? throw new InvalidDataException("UPDATE_MANIFEST_INVALID");
        if (m.Format != 1 || m.Channel != Policy.Channel || m.Build.Runtime != Runtime || m.Build.DataEpoch != Policy.DataEpoch
            || m.Protocol != Policy.Protocol || m.Build.Sequence < Policy.MinimumSequence || !Version.TryParse(m.Build.Version, out _)
            || (Policy.RequiredCapabilities ?? []).Except(m.Capabilities).Any() || m.Files.Length is < 1 or > 10000
            || m.PackageSize is < 1 or > 2147483648L || !ValidHash(m.PackageSha256)
            || fresh && (m.Expires <= DateTimeOffset.UtcNow || m.Expires > DateTimeOffset.UtcNow.AddDays(90)))
        {
            throw new InvalidDataException("UPDATE_INCOMPATIBLE_OR_EXPIRED");
        }
        RequireHttps(m.PackageUrl);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var file in m.Files)
        {
            ValidatePath(file.Path);
            if (!names.Add(file.Path) || file.Size < 0 || !ValidHash(file.Sha256) || (size = checked(size + file.Size)) > 4294967296L)
            {
                throw new InvalidDataException("UPDATE_FILES_INVALID");
            }
        }
        ValidatePath(m.EntryPoint);
        if (!m.Files.Any(x => x.Path == m.EntryPoint && (m.FrameworkDependent || x.Executable))
            || m.FrameworkDependent && !m.EntryPoint.EndsWith(".dll", StringComparison.Ordinal))
        {
            throw new InvalidDataException("UPDATE_ENTRY_INVALID");
        }
        return m;
    }

    private async Task ProbeAsync(UpdateSelection selection, CancellationToken ct)
    {
        var info = new ProcessStartInfo(selection.Executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (selection.Assembly.Length > 0)
        {
            info.ArgumentList.Add(selection.Assembly);
        }
        info.ArgumentList.Add("--update-probe");
        using var process = Process.Start(info) ?? throw new IOException("UPDATE_PROBE_START");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var output = ReadBoundedAsync(process.StandardOutput.BaseStream, 65536, timeout.Token);
        var errors = ReadBoundedAsync(process.StandardError.BaseStream, 65536, timeout.Token);
        try
        {
            // 同时排空输出；超限或超时有界终止，不能让子进程无限堵住管道。
            var exited = process.WaitForExitAsync(timeout.Token);
            var all = Task.WhenAll(output, errors);
            await all.WaitAsync(timeout.Token);
            await exited;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("UPDATE_PROBE_EXIT");
            }
            var hello = JsonSerializer.Deserialize<HostHello>(await output, Json) ?? throw new InvalidDataException("UPDATE_PROBE_INVALID");
            CompatibleHello(hello, selection.Manifest);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }
    private void CompatibleHello(HostHello hello, HostUpdateManifest m)
    {
        if (hello.ProtocolVersion != m.Protocol || hello.Build != m.Build || m.Capabilities.Except(hello.Capabilities).Any())
        {
            throw new InvalidDataException("UPDATE_HEALTH_MISMATCH");
        }
    }

    // GitHub Releases/CDN 会重定向；每一跳都校验 HTTPS，默认传输禁止隐式降级。
    private async Task<HttpResponseMessage> OpenHttpsAsync(string url, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            RequireHttps(url);
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            try
            {
                RequireHttps(response.RequestMessage!.RequestUri!.AbsoluteUri);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("UPDATE_REDIRECT_INVALID");
                    url = new Uri(new Uri(url), location).AbsoluteUri;
                    RequireHttps(url);
                    response.Dispose();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        throw new InvalidDataException("UPDATE_REDIRECT_LIMIT");
    }

    private async Task<byte[]> FetchAsync(string url, int limit, CancellationToken ct)
    {
        using var response = await OpenHttpsAsync(url, ct);
        return await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), limit, ct);
    }
    private async Task DownloadAsync(string url, string file, long size, CancellationToken ct)
    {
        using var response = await OpenHttpsAsync(url, ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > size)
            {
                throw new InvalidDataException("UPDATE_PACKAGE_SIZE");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total != size)
        {
            throw new InvalidDataException("UPDATE_PACKAGE_SIZE");
        }
        output.Flush(true);
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (result.Length + read > limit)
            {
                throw new InvalidDataException("UPDATE_RESPONSE_LIMIT");
            }
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
    private static async Task ExtractAsync(string zipFile, string folder, HostUpdateManifest m, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(zipFile);
        var expected = m.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count > 20000)
        {
            throw new InvalidDataException("UPDATE_ZIP_LIMIT");
        }
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var type = (entry.ExternalAttributes >> 16) & 0xf000;
            var directory = entry.FullName.EndsWith('/');
            ValidatePath(directory ? entry.FullName.TrimEnd('/') : entry.FullName);
            if (type != 0 && type != (directory ? 0x4000 : 0x8000))
            {
                throw new InvalidDataException("UPDATE_LINK_OR_SPECIAL_FILE");
            }
            if (directory)
            {
                continue;
            }
            if (!seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var metadata) || entry.Length != metadata.Size)
            {
                throw new InvalidDataException("UPDATE_ZIP_CONTENTS");
            }
            var target = Path.Combine(folder, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > metadata.Size)
                    {
                        throw new InvalidDataException("UPDATE_ZIP_SIZE");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                output.Flush(true);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    (metadata.Executable ? UnixFileMode.UserExecute : 0));
            }
        }
        VerifyFiles(folder, m);
    }
    private static void VerifyFiles(string folder, HostUpdateManifest m)
    {
        SafeParents(folder);
        var expected = m.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var actual = Walk(folder).ToArray();
        if (actual.Length != expected.Count)
        {
            throw new InvalidDataException("UPDATE_FILE_SET_CHANGED");
        }
        foreach (var file in actual)
        {
            SafeParents(file);
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (!expected.TryGetValue(relative, out var info) || new FileInfo(file).Length != info.Size || FileHash(file) != info.Sha256
                || !OperatingSystem.IsWindows() && info.Executable && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) == 0)
            {
                throw new InvalidDataException("UPDATE_FILE_CHANGED");
            }
        }
    }
    private static IEnumerable<string> Walk(string folder)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            SafeParents(path);
            if (Directory.Exists(path))
            {
                foreach (var child in Walk(path))
                {
                    yield return child;
                }
            }
            else
            {
                yield return path;
            }
        }
    }
    private static void ValidatePath(string path)
    {
        if (path.Length > 240 || path.Split('/').Any(x => !SafeName.IsMatch(x) || x is "." or ".." || x.EndsWith('.')
            || Regex.IsMatch(x.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase)))
        {
            throw new InvalidDataException("UPDATE_PATH_INVALID");
        }
    }
    private static void RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new InvalidDataException("UPDATE_HTTPS_REQUIRED");
        }
    }
    private static void SafeParents(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("UPDATE_LINK_PATH");
                }
            }
        }
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string FileHash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static bool ValidHash(string hash) => Regex.IsMatch(hash, "^[a-f0-9]{64}$");
    private void Commit(UpdateState next)
    {
        // 持久化失败时内存也保留旧状态；不把未提交指针当作已经激活。
        Atomic("state.json", JsonSerializer.SerializeToUtf8Bytes(next, Json));
        state = next;
    }
    private void Atomic(string name, byte[] bytes)
    {
        var file = Path.Combine(root, name);
        SafeParents(file);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, file, true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
    public void Dispose()
    {
        lease.Dispose();
        if (ownsHttp)
        {
            http.Dispose();
        }
    }
}
