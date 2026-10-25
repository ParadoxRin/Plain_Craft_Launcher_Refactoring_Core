using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Pcl.Core;

public static class ComponentMigrationTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string scratch)
    {
        yield return ("component_preview_requires_explicit_mod_disable", () =>
        {
            using var fixture = new Fixture(scratch);
            var manager = new InstanceComponentService(fixture.Service);
            var blocked = manager.Preview(fixture.Source, "vanilla", null, false);
            Require(!blocked.CanApply && blocked.ActiveMods.Count == 1);
            var allowed = manager.Preview(fixture.Source, "vanilla", null, true);
            Require(allowed.CanApply);
            File.WriteAllText(
                Path.Combine(fixture.Source.GameDirectory, "mods", "sample.jar.disabled"),
                "existing disabled"
            );
            Require(
                !manager.Preview(fixture.Source, "vanilla", null, true)
                    .CanApply
            );
            return Task.CompletedTask;
        });
        yield return ("component_copy_removes_loader_preserves_source_and_world", async () =>
        {
            using var fixture = new Fixture(scratch);
            var manager = new InstanceComponentService(fixture.Service, fixture.Install);
            var before = File.ReadAllText(fixture.Source.JsonPath);
            var plan = manager.Preview(fixture.Source, "vanilla", null, true);
            var result = await manager.ApplyAsync(plan);
            Require(
                result.Profile.Loader == "vanilla" && result.DisabledMods == 1 && File.Exists(result.Backup)
            );
            Require(
                File.ReadAllText(fixture.Source.JsonPath) == before && File.Exists(
                    Path.Combine(fixture.Source.GameDirectory, "mods", "sample.jar")
                )
            );
            Require(
                File.ReadAllText(
                    Path.Combine(result.Profile.GameDirectory, "saves", "World", "level.dat")
                ) == "world"
            );
            Require(
                File.ReadAllText(
                    Path.Combine(result.Profile.GameDirectory, "config", "version.json")
                ) == "user config"
            );
            Require(
                !File.Exists(
                    Path.Combine(result.Profile.GameDirectory, "mods", "sample.jar")
                ) && File.ReadAllText(
                    Path.Combine(result.Profile.GameDirectory, "mods", "sample.jar.disabled")
                ) == "mod"
            );
            Require(
                (string? )JsonFiles.Read(result.Receipt)["status"] == "committed"
            );
        });
        yield return ("component_install_failure_rolls_back_pending_copy", async () =>
        {
            using var fixture = new Fixture(scratch);
            string? pending = null;
            var manager = new InstanceComponentService(
                fixture.Service,
                (plan, name, ct) =>
                {
                    pending = Path.Combine(fixture.Service.Paths.Root, "instances", name);
                    Directory.CreateDirectory(pending);
                    File.WriteAllText(
                        Path.Combine(pending, "partial"),
                        "partial"
                    );
                    throw new IOException("installer failed");
                }
            );
            var plan = manager.Preview(fixture.Source, "fabric", "0.18.0", true);
            try
            {
                await manager.ApplyAsync(plan);
                throw new Exception("Expected installer failure");
            }
            catch (IOException e)when (e.Message == "installer failed")
            {
            }

            Require(
                pending is not null && !Directory.Exists(pending)
            );
            Require(
                File.ReadAllText(
                    Path.Combine(fixture.Source.GameDirectory, "mods", "sample.jar")
                ) == "mod"
            );
            var journal = Directory.EnumerateFiles(
                Path.Combine(fixture.Service.Paths.Root, "instance-component-backups"),
                "migration.json",
                SearchOption.AllDirectories
            )
                .Single();
            Require(
                (string? )JsonFiles.Read(journal)["status"] == "rolled_back"
            );
        });
        yield return ("component_source_changes_rejected_before_install", async () =>
        {
            using var fixture = new Fixture(scratch);
            bool started = false;
            var manager = new InstanceComponentService(
                fixture.Service,
                (p, n, ct) =>
                {
                    started = true;
                    return fixture.Install(p, n, ct);
                }
            );
            var plan = manager.Preview(fixture.Source, "vanilla", null, true);
            File.AppendAllText(
                Path.Combine(fixture.Source.GameDirectory, "options.txt"),
                "changed"
            );
            try
            {
                await manager.ApplyAsync(plan);
                throw new Exception("Accepted stale preview");
            }
            catch (InvalidDataException)
            {
            }

            Require(!started);
        });
        yield return ("component_symlink_data_rejected", () =>
        {
            using var fixture = new Fixture(scratch);
            File.CreateSymbolicLink(
                Path.Combine(fixture.Source.GameDirectory, "config", "linked"),
                fixture.Source.JsonPath
            );
            try
            {
                new InstanceComponentService(fixture.Service).Preview(fixture.Source, "vanilla", null, true);
                throw new Exception("Accepted symlink");
            }
            catch (InvalidDataException)
            {
            }

            return Task.CompletedTask;
        });
        yield return ("component_source_changes_during_copy_roll_back", async () =>
        {
            using var fixture = new Fixture(scratch);
            string? pending = null;
            var manager = new InstanceComponentService(
                fixture.Service,
                async (plan, name, ct) =>
                {
                    var profile = await fixture.Install(plan, name, ct);
                    pending = profile.GameDirectory;
                    File.WriteAllText(
                        Path.Combine(fixture.Source.GameDirectory, "options.txt"),
                        "concurrent edit"
                    );
                    return profile;
                }
            );
            var plan = manager.Preview(fixture.Source, "vanilla", null, true);
            try
            {
                await manager.ApplyAsync(plan);
                throw new Exception("Accepted changed source");
            }
            catch (InvalidDataException)
            {
            }

            Require(
                pending is not null && !Directory.Exists(pending)
            );
            Require(
                File.ReadAllText(
                    Path.Combine(fixture.Source.GameDirectory, "options.txt")
                ) == "concurrent edit"
            );
        });
    }

    private static void Require(bool value)
    {
        if (!value)
            throw new Exception("Component migration assertion failed");
    }

    private sealed class Fixture : IDisposable
    {
        public MinecraftService Service { get; }
        public GameProfile Source { get; }

        private readonly JObject metadata;
        public Fixture(string scratch)
        {
            var paths = new AppPaths(
                Path.Combine(
                    scratch,
                    "component-" + Guid.NewGuid()
                        .ToString("N")
                )
            );
            Service = new(
                paths,
                new("linux", "amd64", "test"),
                "1.12.2"
            );
            var folder = paths.VersionFolder("1.12.2");
            Directory.CreateDirectory(folder);
            var client = Path.Combine(folder, "1.12.2.jar");
            File.WriteAllText(client, "client");
            metadata = new JObject
            {
                ["id"] = "1.12.2",
                ["mainClass"] = "net.minecraft.client.main.Main",
                ["downloads"] = new JObject
                {
                    ["client"] = new JObject
                    {
                        ["url"] = "https://example.invalid/client",
                        ["size"] = new FileInfo(client).Length,
                        ["sha1"] = Convert.ToHexString(
                            SHA1.HashData(
                                File.ReadAllBytes(client)
                            )
                        )
                            .ToLowerInvariant()
                    }
                },
                ["assetIndex"] = new JObject
                {
                    ["id"] = "1.12"
                },
                ["libraries"] = new JArray(),
                ["arguments"] = new JObject
                {
                    ["game"] = new JArray(),
                    ["jvm"] = new JArray()
                },
                ["javaVersion"] = new JObject
                {
                    ["majorVersion"] = 8
                }
            };
            JsonFiles.Write(
                Path.Combine(folder, "1.12.2.json"),
                metadata
            );
            JsonFiles.Write(
                Path.Combine(folder, "pcl-mac-install.json"),
                new
                {
                    status = "test"
                }
            );
            var source = Path.Combine(paths.Root, "instances", "source");
            Directory.CreateDirectory(
                Path.Combine(source, "mods")
            );
            Directory.CreateDirectory(
                Path.Combine(source, "saves", "World")
            );
            Directory.CreateDirectory(
                Path.Combine(source, "config")
            );
            File.WriteAllText(
                Path.Combine(source, "mods", "sample.jar"),
                "mod"
            );
            File.WriteAllText(
                Path.Combine(source, "saves", "World", "level.dat"),
                "world"
            );
            File.WriteAllText(
                Path.Combine(source, "config", "version.json"),
                "user config"
            );
            File.WriteAllText(
                Path.Combine(source, "options.txt"),
                "options"
            );
            JsonFiles.Write(
                Path.Combine(source, "version.json"),
                metadata
            );
            JsonFiles.Write(
                Path.Combine(source, "profile.json"),
                new
                {
                    id = "source",
                    loader = "fabric",
                    gameVersion = "1.12.2",
                    loaderVersion = "old"
                }
            );
            Source = new(
                "source",
                "Original",
                "1.12.2",
                "fabric",
                source,
                Path.Combine(source, "version.json")
            );
        }

        public Task<GameProfile> Install(ComponentMigrationPlan plan, string name, CancellationToken ct)
        {
            var folder = Path.Combine(Service.Paths.Root, "instances", name);
            Directory.CreateDirectory(folder);
            JsonFiles.Write(
                Path.Combine(folder, "version.json"),
                metadata
            );
            JsonFiles.Write(
                Path.Combine(folder, "profile.json"),
                new
                {
                    id = name,
                    gameVersion = "1.12.2",
                    loader = plan.TargetLoader,
                    loaderVersion = plan.TargetVersion ?? ""
                }
            );
            return Task.FromResult(
                new GameProfile(
                    name,
                    name,
                    "1.12.2",
                    plan.TargetLoader,
                    folder,
                    Path.Combine(folder, "version.json")
                )
            );
        }

        public void Dispose() => Service.Dispose();
    }
}
