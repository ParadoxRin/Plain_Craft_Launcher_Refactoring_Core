using System.Security.Cryptography;
using System.Text.Json;
using PCLCS;

namespace Pcl.Core.Adapters.Content;

public sealed record ContentRow(int Id, string? Title, string? Modrinth, string? CurseForge, int Popularity);
public sealed record ContentDocument(int Format, string Kind, string Channel, long Sequence,
    string MinHostVersion, string MaxHostVersion, string Provenance, ContentRow[] Entries);
public sealed record ContentEnvelope(string Payload, string Signature);
public sealed record ContentPolicy(string Channel, string PublicKeyPem, string? FeedUrl = null);
public sealed record ContentSelection(IReadOnlyList<WikiEntry> Entries, string Source, long Sequence, string? Rejection);

/// <summary>Signed, platform-independent wiki/mapping data. No code or archive extraction.</summary>
public static class ContentPack
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static byte[] ReadBounded(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("CONTENT_LINK_REJECTED");
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) throw new InvalidDataException("CONTENT_TOO_LARGE");
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (result.Length + count > MaxBytes) throw new InvalidDataException("CONTENT_TOO_LARGE");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    public static ContentDocument Verify(byte[] bytes, ContentPolicy policy, Version hostVersion)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("CONTENT_TOO_LARGE");
        var envelope = JsonSerializer.Deserialize<ContentEnvelope>(bytes, Json)
            ?? throw new InvalidDataException("CONTENT_ENVELOPE_INVALID");
        var payload = Convert.FromBase64String(envelope.Payload);
        using var key = RSA.Create();
        key.ImportFromPem(policy.PublicKeyPem);
        if (key.KeySize < 2048 || !key.VerifyData(payload, Convert.FromBase64String(envelope.Signature),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("CONTENT_SIGNATURE_INVALID");
        var document = JsonSerializer.Deserialize<ContentDocument>(payload, Json)
            ?? throw new InvalidDataException("CONTENT_DOCUMENT_INVALID");
        if (document.Format != 1 || document.Kind != "wiki-project-index" || document.Channel != policy.Channel ||
            document.Sequence <= 0 || string.IsNullOrWhiteSpace(document.Provenance) || document.Provenance.Length > 4096 ||
            !Version.TryParse(document.MinHostVersion, out var min) || !Version.TryParse(document.MaxHostVersion, out var max) ||
            min > max || hostVersion < min || hostVersion > max)
            throw new InvalidDataException("CONTENT_INCOMPATIBLE");
        if (document.Entries is not { Length: > 0 and <= 100000 })
            throw new InvalidDataException("CONTENT_ROWS_INVALID");
        foreach (var row in document.Entries)
        {
            // Preserve multiple projects per wiki ID; the existing domain rules reject ambiguous identity.
            if (row is null || row.Id <= 0 || row.Popularity < 0 || row.Title?.Length > 2048 ||
                !Slug(row.Modrinth) || !Slug(row.CurseForge))
                throw new InvalidDataException("CONTENT_ROW_INVALID");
        }
        return document;
    }

    private static bool Slug(string? value) => value is null or "" ||
        value.Length <= 128 && value.All(c => !char.IsControl(c) && c is not '/' and not '\\');

    public static IReadOnlyList<WikiEntry> Project(ContentDocument document) => document.Entries.Select(row =>
    {
        var entry = new WikiEntry { Id = row.Id, ChineseName = row.Title, Popularity = row.Popularity };
        if (row.Modrinth is { Length: > 0 }) entry.Slugs.Add(ResourcePlatforms.Modrinth, row.Modrinth);
        if (row.CurseForge is { Length: > 0 }) entry.Slugs.Add(ResourcePlatforms.CurseForge, row.CurseForge);
        return entry;
    }).ToArray();

    public static ContentRow[] ExportBuiltIn() => WikiEntry.All.Value.Select(x => new ContentRow(x.Id,
        x.ChineseName, x.Slugs.GetValueOrDefault(ResourcePlatforms.Modrinth),
        x.Slugs.GetValueOrDefault(ResourcePlatforms.CurseForge), x.Popularity)).ToArray();

    public static ContentPolicy ReadPolicy(string path) =>
        JsonSerializer.Deserialize<ContentPolicy>(ReadBounded(path), Json)
        ?? throw new InvalidDataException("CONTENT_POLICY_INVALID");

    private sealed record State(string? Current, string? Previous, long HighestSequence);

    private static State ReadState(string root)
    {
        var path = Path.Combine(root, "state.json");
        if (!File.Exists(path)) return new(null, null, 0);
        var state = JsonSerializer.Deserialize<State>(ReadBounded(path), Json);
        if (state is null || state.HighestSequence <= 0 || state.Current is null)
            throw new InvalidDataException("CONTENT_STATE_INVALID");
        PackagePath(root, state.Current);
        if (state.Previous is not null) PackagePath(root, state.Previous);
        return state;
    }

    private static string PackagePath(string root, string hash)
    {
        if (hash.Length != 64 || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidDataException("CONTENT_STATE_PATH_INVALID");
        return Path.Combine(root, hash + ".json");
    }

    private static ContentDocument ReadPackage(string root, string hash, ContentPolicy policy, Version version)
    {
        var bytes = ReadBounded(PackagePath(root, hash));
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != hash)
            throw new InvalidDataException("CONTENT_HASH_INVALID");
        return Verify(bytes, policy, version);
    }

    public static ContentSelection Load(string root, ContentPolicy policy, Version version)
    {
        var errors = new List<string>();
        try
        {
            CheckDirectory(root, false);
            var state = ReadState(root);
            foreach (var candidate in new[] { state.Current, state.Previous }.Where(x => x is not null).Distinct())
            {
                try
                {
                    var document = ReadPackage(root, candidate!, policy, version);
                    return new(Project(document), document.Provenance, document.Sequence, errors.Count == 0 ? null : string.Join(";", errors));
                }
                catch (Exception ex) when (Expected(ex)) { errors.Add(ex.GetType().Name); }
            }
        }
        catch (Exception ex) when (Expected(ex)) { errors.Add(ex.GetType().Name); }
        return new(WikiEntry.All.Value, McmodCatalog.IndexProvenance, 0, errors.Count == 0 ? null : string.Join(";", errors));
    }

    public static long Install(string root, byte[] bytes, ContentPolicy policy, Version version)
    {
        var document = Verify(bytes, policy, version);
        CheckDirectory(root, true);
        using var held = Lock(root);
        var state = ReadState(root); // A damaged state must not reset anti-replay protection.
        if (document.Sequence <= state.HighestSequence) throw new InvalidDataException("CONTENT_SEQUENCE_NOT_NEWER");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var destination = PackagePath(root, hash);
        if (!File.Exists(destination)) AtomicWrite(destination, bytes, false);
        ReadPackage(root, hash, policy, version);
        // Only a verified predecessor may become the rollback candidate.
        string? previous = null;
        foreach (var candidate in new[] { state.Current, state.Previous }.Where(x => x is not null))
        {
            try { ReadPackage(root, candidate!, policy, version); previous = candidate; break; }
            catch (Exception ex) when (Expected(ex)) { }
        }
        AtomicWrite(Path.Combine(root, "state.json"), JsonSerializer.SerializeToUtf8Bytes(new State(hash, previous, document.Sequence)), true);
        return document.Sequence;
    }

    public static void Rollback(string root, ContentPolicy policy, Version version)
    {
        CheckDirectory(root, false);
        using var held = Lock(root);
        var state = ReadState(root);
        if (state.Previous is null) throw new InvalidDataException("CONTENT_NO_PREVIOUS");
        ReadPackage(root, state.Previous, policy, version);
        AtomicWrite(Path.Combine(root, "state.json"), JsonSerializer.SerializeToUtf8Bytes(
            new State(state.Previous, null, state.HighestSequence)), true);
    }

    // Only hash-addressed packages owned by this store; preserve both active and rollback candidates.
    public static int Cleanup(string root)
    {
        CheckDirectory(root, false);
        if (!Directory.Exists(root)) return 0;
        using var held = Lock(root);
        var state = ReadState(root);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.json"))
        {
            var hash = Path.GetFileNameWithoutExtension(path);
            if (hash.Length != 64 || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                hash == state.Current || hash == state.Previous) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            File.Delete(path); removed++;
        }
        return removed;
    }

    private static FileStream Lock(string root)
    {
        var path = Path.Combine(root, "update.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("CONTENT_LINK_REJECTED");
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void CheckDirectory(string path, bool create)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        for (var current = directory; current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("CONTENT_LINK_REJECTED");
        if (create) directory.Create();
    }

    private static void AtomicWrite(string path, byte[] bytes, bool overwrite)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static bool Expected(Exception ex) => ex is InvalidDataException or IOException or UnauthorizedAccessException or
        JsonException or FormatException or CryptographicException or ArgumentException;
}
