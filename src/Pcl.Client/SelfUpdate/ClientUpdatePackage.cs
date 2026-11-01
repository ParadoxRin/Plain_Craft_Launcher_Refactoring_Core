using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pcl.Client.SelfUpdate;

public sealed record ClientRelease(string Version, long Sequence, string Runtime);
public sealed record ClientUpdatePolicy(string FeedUrl, string PublicKeyPem, string Channel);
public sealed record ClientUpdateManifest(int Format, string Channel, ClientRelease Release, string Kind,
    string ArchiveRoot, string PackageUrl, long Size, string Sha256, DateTimeOffset ExpiresUtc);
public sealed record ClientUpdateEnvelope(string Payload, string Signature);

/// <summary>Client deployment only; no Core reference and no user-data migration.</summary>
public static class ClientUpdatePackage
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string BaseDirectory(string target) => target.EndsWith(".app", StringComparison.Ordinal)
        ? Path.Combine(target, "Contents", "MacOS") : target;
    public static ClientRelease ReadRelease(string target) => Read<ClientRelease>(Path.Combine(BaseDirectory(target), "client-release.json"));
    public static ClientUpdatePolicy ReadPolicy(string target) => Read<ClientUpdatePolicy>(Path.Combine(BaseDirectory(target), "client-update-policy.json"));
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(ReadBytes(path, 1024 * 1024), Json)
        ?? throw new InvalidDataException("CLIENT_UPDATE_JSON_INVALID");
    public static byte[] ReadBytes(string path, long limit)
    {
        RejectLinks(path);
        using var file = File.OpenRead(path);
        if (file.Length > limit) throw new InvalidDataException("CLIENT_UPDATE_SIZE_LIMIT");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = file.Read(buffer)) > 0)
        {
            if (bytes.Length + count > limit) throw new InvalidDataException("CLIENT_UPDATE_SIZE_LIMIT");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }

    public static Uri Https(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("CLIENT_UPDATE_HTTPS_REQUIRED");
        return uri;
    }

    public static ClientUpdateManifest Verify(byte[] bytes, ClientUpdatePolicy policy)
    {
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("CLIENT_UPDATE_FEED_TOO_LARGE");
        var envelope = JsonSerializer.Deserialize<ClientUpdateEnvelope>(bytes, Json) ?? throw new InvalidDataException("CLIENT_UPDATE_ENVELOPE_INVALID");
        var payload = Convert.FromBase64String(envelope.Payload);
        using var key = RSA.Create();
        key.ImportFromPem(policy.PublicKeyPem);
        if (key.KeySize < 2048 || !key.VerifyData(payload, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("CLIENT_UPDATE_SIGNATURE_INVALID");
        var manifest = JsonSerializer.Deserialize<ClientUpdateManifest>(payload, Json) ?? throw new InvalidDataException("CLIENT_UPDATE_MANIFEST_INVALID");
        if (manifest.Format != 1 || manifest.Channel != policy.Channel || manifest.Release is null ||
            manifest.Release.Sequence <= 0 || !Version.TryParse(manifest.Release.Version, out _) ||
            manifest.Release.Runtime != RuntimeInformation.RuntimeIdentifier || manifest.Release.Runtime != "osx-arm64" ||
            manifest.Kind is not ("macos-app" or "client-directory") || manifest.Size is <= 0 or > MaxPackageBytes ||
            manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit) || manifest.ExpiresUtc <= DateTimeOffset.UtcNow ||
            (manifest.Kind == "macos-app" && (manifest.ArchiveRoot.Length > 160 || !manifest.ArchiveRoot.EndsWith(".app", StringComparison.Ordinal) ||
                manifest.ArchiveRoot.IndexOfAny(['/', '\\', ':']) >= 0 || manifest.ArchiveRoot.StartsWith('.'))) ||
            (manifest.Kind == "client-directory" && manifest.ArchiveRoot != ""))
            throw new InvalidDataException("CLIENT_UPDATE_INCOMPATIBLE");
        Https(manifest.PackageUrl);
        return manifest;
    }

    public static void VerifyArchive(string archive, ClientUpdateManifest manifest)
    {
        RejectLinks(archive);
        using var input = File.OpenRead(archive);
        if (input.Length != manifest.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CLIENT_UPDATE_PACKAGE_HASH_INVALID");
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("CLIENT_UPDATE_TOO_MANY_FILES");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var segments = name.TrimEnd('/').Split('/');
            var mode = ((uint)entry.ExternalAttributes >> 16) & 0xf000;
            if (name.Length > 1024 || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl) ||
                segments.Any(x => x is "" or "." or "..") || !paths.Add(name.TrimEnd('/').Normalize(NormalizationForm.FormC)) ||
                mode is not (0 or 0x8000 or 0x4000) || entry.Length < 0 || entry.Length > MaxPackageBytes ||
                (expanded += entry.Length) > 2L * 1024 * 1024 * 1024)
                throw new InvalidDataException("CLIENT_UPDATE_UNSAFE_ARCHIVE");
            if (manifest.Kind == "macos-app" && segments[0] != manifest.ArchiveRoot &&
                !(segments[0] == "__MACOSX" && (segments.Length == 1 && name.EndsWith('/') || segments.Length >= 2 &&
                  (segments[1] == manifest.ArchiveRoot || segments[1] == "._" + manifest.ArchiveRoot))))
                throw new InvalidDataException("CLIENT_UPDATE_ARCHIVE_ROOT_INVALID");
        }
    }

    public static async Task ExtractAsync(string archive, ClientUpdateManifest manifest, string stage, ClientUpdatePolicy policy, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        VerifyArchive(archive, manifest);
        if (Directory.Exists(stage)) throw new IOException("CLIENT_UPDATE_STAGE_EXISTS");
        RejectLinks(Path.GetDirectoryName(stage)!);
        Directory.CreateDirectory(stage);
        if (manifest.Kind == "macos-app")
            await RunAsync("/usr/bin/ditto", ["-x", "-k", archive, stage], ct);
        else
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                var output = Path.Combine(stage, entry.FullName);
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                entry.ExtractToFile(output);
                File.SetUnixFileMode(output, (UnixFileMode)(((uint)entry.ExternalAttributes >> 16) & 0x1ff));
            }
        }
        var candidate = manifest.Kind == "macos-app" ? Path.Combine(stage, manifest.ArchiveRoot) : stage;
        TreeHash(candidate); // Reject extracted links before looking inside the candidate.
        if (ReadRelease(candidate) != manifest.Release || ReadPolicy(candidate) != policy ||
            !File.Exists(Path.Combine(BaseDirectory(candidate), "PCL-MacPort")) ||
            !File.Exists(Path.Combine(BaseDirectory(candidate), "PCL-MacPort.dll")) ||
            !File.Exists(Path.Combine(BaseDirectory(candidate), "updater", "Pcl.ClientUpdater")))
            throw new InvalidDataException("CLIENT_UPDATE_CANDIDATE_IDENTITY_INVALID");
        if (manifest.Kind == "macos-app")
        {
            await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", candidate], ct);
            var plist = Path.Combine(candidate, "Contents/Info.plist");
            foreach (var (field, expected) in new[] { ("CFBundleIdentifier", "local.pcl.macport.preview"),
                         ("CFBundleExecutable", "PCL-MacPort"), ("CFBundleVersion", manifest.Release.Sequence.ToString()),
                         ("CFBundleShortVersionString", manifest.Release.Version) })
                if ((await RunAsync("/usr/bin/plutil", ["-extract", field, "raw", "-o", "-", plist], ct)).Trim() != expected)
                    throw new InvalidDataException("CLIENT_UPDATE_BUNDLE_IDENTITY_INVALID");
        }
    }

    public static IEnumerable<string> Walk(string directory)
    {
        RejectLinks(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            RejectLinks(path);
            if (Directory.Exists(path)) foreach (var child in Walk(path)) yield return child;
            else yield return path;
        }
    }
    public static string TreeHash(string directory)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Walk(directory))
        {
            var name = Path.GetRelativePath(directory, path);
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
            using var input = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(input));
            hash.AppendData(Encoding.UTF8.GetBytes(((int)File.GetUnixFileMode(path) & 0x1ff).ToString() + "\n"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("CLIENT_UPDATE_LINK_REJECTED");
    }
    public static void Save<T>(string path, T value)
    {
        RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value, Json); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<string> RunAsync(string executable, string[] args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("CLIENT_UPDATE_PROCESS_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException("CLIENT_UPDATE_PROCESS_FAILED: " + Path.GetFileName(executable) + " " + error[..Math.Min(error.Length, 512)]);
        return output;
    }
}
