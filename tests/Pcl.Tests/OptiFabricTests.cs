using System.IO.Compression;
using Newtonsoft.Json.Linq;
using Pcl.Core;

public static class OptiFabricTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string scratch)
    {
        yield return ("optifabric_conflicts_detect_mod_id_not_filename", () =>
        {
            var root = Folder(scratch);
            Jar(
                Path.Combine(root, "mods", "innocent-name.jar"),
                "iris",
                "1.0"
            );
            try
            {
                OptiFabricService.ValidateConflicts(root);
                throw new Exception("Accepted Iris");
            }
            catch (InvalidDataException e)when (e.Message.Contains("iris"))
            {
            }

            return Task.CompletedTask;
        });
        yield return ("optifabric_disabled_conflicting_mod_is_not_active", () =>
        {
            var root = Folder(scratch);
            Jar(
                Path.Combine(root, "mods", "sodium.jar.disabled"),
                "sodium",
                "1.0"
            );
            OptiFabricService.ValidateConflicts(root);
            return Task.CompletedTask;
        });
        yield return ("optifabric_unknown_artifact_cannot_impersonate_tested_build", () =>
        {
            var root = Folder(scratch);
            var file = Path.Combine(root, "mods", OptiFabricService.OptiFabricFile);
            Jar(file, "optifabric", "1.14.3");
            try
            {
                OptiFabricService.ValidateOptiFabricJar(file);
                throw new Exception("Accepted different artifact");
            }
            catch (InvalidDataException)
            {
            }

            return Task.CompletedTask;
        });
        yield return ("optifabric_symlink_mod_directory_is_rejected", () =>
        {
            var root = Folder(scratch);
            Directory.CreateSymbolicLink(
                Path.Combine(root, "mods", "loop"),
                Path.Combine(root, "mods")
            );
            try
            {
                OptiFabricService.ValidateConflicts(root);
                throw new Exception("Accepted symlink");
            }
            catch (InvalidDataException)
            {
            }

            return Task.CompletedTask;
        });
        yield return ("optifabric_unknown_minecraft_fails_before_network", async () =>
        {
            using var service = new MinecraftService(
                new AppPaths(
                    Folder(scratch)
                ),
                new("osx", "aarch64", "test"),
                "1.21.1"
            );
            try
            {
                await new OptiFabricService(service).InstallAsync();
                throw new Exception("Accepted unknown combination");
            }
            catch (NotSupportedException)
            {
            }
        });
    }

    private static string Folder(string root)
    {
        var path = Path.Combine(
            root,
            "optifabric-test-" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(
            Path.Combine(path, "mods")
        );
        return path;
    }

    private static void Jar(string path, string id, string version)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(
            archive.CreateEntry("fabric.mod.json")
                .Open()
        );
        writer.Write(
            new JObject
            {
                ["schemaVersion"] = 1,
                ["id"] = id,
                ["version"] = version
            }.ToString()
        );
    }
}
