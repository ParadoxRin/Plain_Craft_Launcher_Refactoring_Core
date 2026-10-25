using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Pcl.Core;

public static class ArtifactDownloadTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string scratch)
    {
        yield return ("download_only_verified_jar_saved_without_installing", async () =>
        {
            var folder = Path.Combine(
                scratch,
                "download-only-" + Guid.NewGuid()
                    .ToString("N")
            );
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "client.jar");
            var bytes = Jar();
            using var download = new DownloadService(
                new Handler(bytes)
            );
            var saved = await ArtifactDownloadService.SaveVerifiedAsync(
                download,
                Item(path, bytes),
                "official_sha1"
            );
            Require(
                File.ReadAllBytes(path)
                    .SequenceEqual(bytes) && saved.ExpectedSha1 == Item(path, bytes)
                    .Sha1 && saved.Sha256.Length == 64
            );
            Require(
                Directory.GetFiles(folder)
                    .Length == 1 && !Directory.GetDirectories(folder)
                    .Any()
            );
        });
        yield return ("download_only_never_overwrites_existing_target", async () =>
        {
            var folder = Path.Combine(
                scratch,
                "download-existing-" + Guid.NewGuid()
                    .ToString("N")
            );
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "server.jar");
            File.WriteAllText(path, "original");
            var handler = new Handler(
                Jar()
            );
            using var download = new DownloadService(handler);
            try
            {
                await ArtifactDownloadService.SaveVerifiedAsync(
                    download,
                    Item(
                        path,
                        Jar()
                    ),
                    "official_sha1"
                );
                throw new Exception("Expected existing target refusal");
            }
            catch (IOException)
            {
            }

            Require(
                File.ReadAllText(path) == "original" && handler.Requests == 0
            );
        });
        yield return ("download_only_rejects_non_jar_and_cleans_partial", async () =>
        {
            var folder = Path.Combine(
                scratch,
                "download-invalid-" + Guid.NewGuid()
                    .ToString("N")
            );
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "installer.jar");
            var bytes = System.Text.Encoding.UTF8.GetBytes("not an archive");
            using var download = new DownloadService(
                new Handler(bytes)
            );
            try
            {
                await ArtifactDownloadService.SaveVerifiedAsync(
                    download,
                    Item(path, bytes),
                    "official_sha1"
                );
                throw new Exception("Accepted invalid jar");
            }
            catch (InvalidDataException)
            {
            }

            Require(
                !Directory.EnumerateFiles(folder)
                    .Any()
            );
        });
        yield return ("quilt_disagreeing_official_checksums_are_not_silently_replaced", () =>
        {
            try
            {
                ArtifactDownloadService.RequireConsistentChecksums("a6ea7a9e08e6f5ca399ea12101fbe56195a445e3", "e4c4e80c91c94d999ffe3468ec2043792b841051");
                throw new Exception("Accepted disagreeing checksums");
            }
            catch (InvalidDataException)
            {
            }

            ArtifactDownloadService.RequireConsistentChecksums(
                new string ('a', 40),
                new string ('A', 40)
            );
            return Task.CompletedTask;
        });
        yield return ("official_notes_render_as_inert_plain_text", () =>
        {
            var plain = ArtifactDownloadService.PlainText(
                "<h1>1.20.1</h1><script>bad()</script><p>Fixed &amp; tested</p><a href='https://example.invalid'>Issue</a>"
            );
            Require(
                plain == "1.20.1\nFixed & tested\nIssue" && !plain.Contains("bad")
            );
            return Task.CompletedTask;
        });
    }

    private static DownloadItem Item(string path, byte[] bytes) => new(
        new Uri("https://piston-data.mojang.com/test.jar"),
        path,
        bytes.Length,
        Convert.ToHexString(
            SHA1.HashData(bytes)
        )
            .ToLowerInvariant()
    );
    private static byte[] Jar()
    {
        using var data = new MemoryStream();
        using (var archive = new ZipArchive(data, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(
                archive.CreateEntry("META-INF/MANIFEST.MF")
                    .Open()
            );
            writer.Write("Manifest-Version: 1.0\n");
        }

        return data.ToArray();
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new Exception("Artifact download assertion failed");
    }

    private sealed class Handler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                }
            );
        }
    }
}
