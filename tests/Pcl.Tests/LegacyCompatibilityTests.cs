using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Pcl.Core;

public static class LegacyCompatibilityTests
{
    public static IEnumerable<(string Name, Action Run)> Cases(string scratch)
    {
        yield return ("legacy_arguments_quote_safe_no_shell", () =>
        {
            var parsed = LegacyLaunchAdapter.SplitArguments("--name \"Player One\" --empty '' --token $(touch /tmp/no) 'a\\b'");
            Require(
                parsed.SequenceEqual(
                    new[]
                    {
                        "--name",
                        "Player One",
                        "--empty",
                        "",
                        "--token",
                        "$(touch",
                        "/tmp/no)",
                        "a\\b"
                    }
                )
            );
            try
            {
                LegacyLaunchAdapter.SplitArguments("'unterminated");
                throw new Exception("Accepted unclosed quote");
            }
            catch (InvalidDataException)
            {
            }

            var j = JObject.Parse(
                "{\"minecraftArguments\":\"--username ${auth_player_name} --userProperties ${user_properties}\",\"libraries\":[]}"
            );
            var normalized = LegacyLaunchAdapter.NormalizeMetadata(j);
            Require(
                normalized["arguments"]?["game"]?.Count() == 4 && j["arguments"] is null
            );
        });
        yield return ("lwjgl2_arm64_native_only_replacement", () =>
        {
            var j = JObject.Parse(
                "{\"libraries\":[{\"name\":\"org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209\",\"downloads\":{},\"natives\":{\"osx\":\"natives-osx\"}}]}"
            );
            // 2.9.4 is intentionally not in the fixed source map: unknown binary is not silently replaced.
            Require(
                (string? )LegacyMacLibraries.Adapt(
                    j,
                    new("osx", "aarch64", "14")
                )["libraries"]?[0]?["name"] == "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209"
            );
            j["libraries"]![0]!["name"] = "org.lwjgl.lwjgl:lwjgl-platform:2.9.2-nightly-20140822";
            var adapted = LegacyMacLibraries.Adapt(
                j,
                new("osx", "aarch64", "14")
            );
            var files = MinecraftService.BuildLibraries(
                adapted,
                scratch,
                new("osx", "aarch64", "14")
            );
            Require(
                files.Count == 1 && files[0].ExtractNative && files[0].Sha1 == "042ef2024a9a4054952c496c9ebcdb83345dabe0"
            );
        });
        yield return ("legacy_forge_replaces_complete_game_arguments", () =>
        {
            var parent = JObject.Parse(
                "{\"id\":\"1.12.2\",\"mainClass\":\"Main\",\"arguments\":{\"jvm\":[\"-cp\",\"${classpath}\"],\"game\":[\"--versionType\",\"release\"]},\"libraries\":[]}"
            );
            var child = JObject.Parse(
                "{\"id\":\"forge\",\"mainClass\":\"Launch\",\"minecraftArguments\":\"--versionType Forge --tweakClass FMLTweaker\",\"libraries\":[]}"
            );
            var merged = FabricService.Merge(parent, child);
            Require(
                merged["arguments"]!["game"]!.Select(
                    x => (string)x!
                )
                    .SequenceEqual(
                        new[]
                        {
                            "--versionType",
                            "Forge",
                            "--tweakClass",
                            "FMLTweaker"
                        }
                    )
            );
            Require(
                LegacyLaunchAdapter.RequiresFirstThread(
                    new JObject()
                )
            );
            Require(
                !LegacyLaunchAdapter.RequiresFirstThread(
                    JObject.Parse("{\"libraries\":[{\"name\":\"org.lwjgl.lwjgl:lwjgl:2.9.2\"}]}")
                )
            );
            Require(
                LegacyLaunchAdapter.RequiresFirstThread(
                    JObject.Parse("{\"libraries\":[{\"name\":\"org.lwjgl.lwjgl:lwjgl:2.9.2\"}]}"),
                    appleSilicon: true
                )
            );
        });
        yield return ("optifine_catalog_filters_version_and_preview", () =>
        {
            var releases = OptiFineService.ParseVersions(
                "OptiFine_1.12.2_HD_U_G5.jar OptiFine_1.12.2_HD_U_G5.jar preview_OptiFine_1.12.2_HD_U_G6_pre1.jar OptiFine_1.20.1_HD_U_I6.jar",
                "1.12.2"
            );
            Require(releases.Count == 2 && !releases[0].Preview && releases[1].Preview);
        });
        yield return ("java_runtime_duplicate_bundle_symlink", () =>
        {
            var home = Path.Combine(scratch, "java-bundle", "runtime.jre", "Contents", "Home");
            Directory.CreateDirectory(
                Path.Combine(home, "bin")
            );
            var java = Path.Combine(home, "bin", "java");
            File.WriteAllText(java, "java");
            Directory.CreateSymbolicLink(
                Path.Combine(scratch, "java-bundle", "bin"),
                Path.Combine(home, "bin")
            );
            Require(
                JavaService.FindRuntimeExecutable(
                    Path.Combine(scratch, "java-bundle")
                ) == java
            );
        });
        yield return ("legacy_native_extract_traversal_rejected", () =>
        {
            var root = Path.Combine(scratch, "legacy-native");
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "native.jar");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(
                    zip.CreateEntry("../outside.dylib")
                        .Open()
                );
                w.Write("unsafe");
            }

            var bytes = File.ReadAllBytes(archive);
            var file = new DownloadItem(
                new("https://localhost/native"),
                archive,
                bytes.Length,
                Convert.ToHexString(
                    SHA1.HashData(bytes)
                )
                    .ToLowerInvariant(),
                true
            );
            var v = new InstalledVersion(
                "test",
                Path.Combine(root, "version.json"),
                "unused",
                "test",
                new[]
                {
                    file
                },
                8
            );
            try
            {
                LegacyLaunchAdapter.PrepareFiles(
                    v,
                    Path.Combine(root, "assets"),
                    root
                );
                throw new Exception("Traversal accepted");
            }
            catch (InvalidDataException)
            {
            }

            Require(
                !File.Exists(
                    Path.Combine(root, "outside.dylib")
                )
            );
        });
    }

    private static void Require(bool value)
    {
        if (!value)
            throw new Exception("Legacy compatibility assertion failed");
    }
}
