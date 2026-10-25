using System.IO.Compression;
using System.Security.Cryptography;
using Pcl.Core;

internal static class LocalSkinAcceptance
{
    // 只向本轮带标记的克隆目录写入测试皮肤与资源包；读本地真实客户端材质。
    public static int Run(string root, string output)
    {
        var paths = new AppPaths(root);
        if (!File.Exists(
            Path.Combine(root, ".visibility-acceptance")
        ))
        {
            throw new InvalidOperationException("Isolation marker required");
        }

        var accounts = new AccountManager(
            paths,
            new MemoryAccountSecrets()
        );
        if (accounts.Accounts.Any(
            x => x.Kind != AccountKind.Offline
        ))
        {
            throw new InvalidOperationException("Offline fixture required");
        }

        var account = accounts.AddOffline("PCL_Test");
        using var service = new MinecraftService(paths);
        var version = service.LoadInstalled();
        var skins = new OfflineSkinService(paths);
        var results = new List<object>();
        foreach (var preset in new[]
        {
            "alex",
            "random",
            "steve"
        }

        )
        {
            var bytes = LocalSkinPresets.Read(paths, preset);
            skins.Save(account, bytes);
            var saved = new OfflineSkinService(paths).Get(account)!;
            if (saved.Sha256 != Convert.ToHexString(
                SHA256.HashData(bytes)
            )
                .ToLowerInvariant())
            {
                throw new Exception("Persistence mismatch");
            }

            skins.Prepare(account, version, service.VanillaGameDirectory);
            using var pack = ZipFile.OpenRead(
                Path.Combine(service.VanillaGameDirectory, "resourcepacks", OfflineSkinService.PackName)
            );
            var textures = pack.Entries.Where(
                x => x.FullName.EndsWith(".png")
            )
                .ToArray();
            foreach (var entry in textures)
            {
                using var input = entry.Open();
                using var image = new MemoryStream();
                input.CopyTo(image);
                if (!image.ToArray()
                    .SequenceEqual(bytes))
                {
                    throw new Exception("Resource pack texture mismatch");
                }
            }

            if (textures.Length == 0)
            {
                throw new Exception("No replaced texture");
            }

            results.Add(
                new
                {
                    preset,
                    saved.Sha256,
                    replacedTextures = textures.Length,
                    pngBytes = bytes.Length,
                    persisted = true
                }
            );
        }

        JsonFiles.Write(
            output,
            new
            {
                status = "passed",
                results,
                limitations = "Texture substitution; original offline UUID and arm model unchanged; no multiplayer skin service"
            }
        );
        return 0;
    }
}
