using System.Text.Json;
using System.IO.Compression;
using Pcl.Client;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Operations;

if (args[0] == "--iris036-preview")
{
    await using var probeSession = new LauncherSession(
        new HostClient(
            Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE")!,
            "",
            Path.GetFullPath(args[1]),
            reconnectable: true
        )
    );
    await probeSession.InitializeAsync();
    var api = new WorkspaceApi(probeSession.Workspaces);
    var instances = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
    var profile = instances.Items.Single(
        x => Path.GetFileName(x.Profile.GameDirectory) == "mac-shaders-1.21.1"
    )
        .Profile;
    Console.WriteLine(
        JsonSerializer.Serialize(
            await api.CallAsync<ShaderEnvironmentPreview>("workspace.browser.irisPreview", profile.Id)
        )
    );
    await probeSession.ShutdownAsync();
    return;
}

if (args[0] == "--iris036-uninstall")
{
    await Iris036UninstallFlow.Run(args[1], args[2], args[3] == "exclusive");
    return;
}

if (args[0] == "--iris036-world")
{
    await Iris036Flow.Run(args[1], args[2], args[3]);
    return;
}

if (args[0] == "--copy035-guards")
{
    await Copy035Guards.Run(args[1], args[2], args[3]);
    return;
}

if (args[0] == "--shaders035")
{
    await Shader035Flow.Run(args[1], args[2]);
    return;
}

if (args[0] == "--loaders034")
{
    await Loader034Flow.Run(args[1], args[2]);
    return;
}

if (args[0] == "--packs034")
{
    await CustomPack034Flow.Run(args[1], args[2], args[3]);
    return;
}

if (args[0] == "--main-link")
{
    await MainLinkFlow.Run(args[1], args[2]);
    return;
}

if (args[0] == "--shutdown")
{
    await ShutdownFlow.Run(args[1], args[2]);
    return;
}

if (args[0] == "--browser-accounts")
{
    await BrowserAccountFlow.Run(args[1], args[2]);
    return;
}

if (args[0] == "--discovery")
{
    await DiscoverySettingsFlow.Run(args[1], args[2]);
    return;
}

if (args[0] == "--compat")
{
    await CompatibilityFlow.Run(args[1], args[2], args[3]);
    return;
}

if (args[0] == "--faults")
{
    await FaultFlow.Run(args[1], args[2]);
    return;
}

if (args[0] == "--resources")
{
    await ResourceFlow.Run(args[1], args[2]);
    return;
}

// 每次只在传入的隔离目录运行。所有业务修改穿过真实 Client / 独立 Host，文件读取仅用于验收结果。
var root = Path.GetFullPath(args[0]);
var report = Path.GetFullPath(args[1]);
var checks = new List<string>();
var json = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};
void Check(bool value, string name)
{
    if (!value)
    {
        throw new Exception(name);
    }

    checks.Add(name);
    Console.WriteLine(name);
}

void Zip(string path, params (string Name, string Text)[] entries)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var item in entries)
    {
        using var writer = new StreamWriter(
            zip.CreateEntry(item.Name)
                .Open()
        );
        writer.Write(item.Text);
    }
}

var native = Environment.GetEnvironmentVariable("PCL_HOST_TEST_EXE");
await using var session = new LauncherSession(
    new HostClient(
        native ?? Path.GetFullPath(".tooling/dotnet/dotnet"),
        native is null
            ? Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll")
            : "",
        root,
        reconnectable: true
    )
);
try
{
    await session.InitializeAsync();
    var api = new WorkspaceApi(session.Workspaces);
    var initial = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
    Check(
        initial.Items.Any(
            x => x.Profile.Id == "1.21.1"
        ),
        "host-reads-real-installed-instance"
    );
    var copy = await api.CallAsync<GameProfile>(
        "workspace.instances.copy",
        "1.21.1",
        new
        {
            name = "0.28 Host workspace"
        }
    );
    Check(
        Directory.Exists(copy.GameDirectory) && copy.Id != "1.21.1",
        "host-copies-instance-with-source-preserved"
    );
    await api.CallAsync<bool>(
        "workspace.instances.savePreferences",
        copy.Id,
        new
        {
            value = new InstancePreferences(
                Name: "Workspace renamed",
                Description: "host test",
                Favorite: true,
                Hidden: true,
                Group: "验收"
            )
        }
    );
    var snapshot = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
    Check(
        snapshot.Items.Single(
            x => x.Profile.Id == copy.Id
        )
            .Preferences is { Hidden: true, Favorite: true, Group: "验收" },
        "preferences-and-hidden-instance-remain-addressable"
    );
    await api.CallAsync<bool>(
        "workspace.instances.saveSettings",
        null,
        new
        {
            value = new LaunchSettings
            {
                MaximumMemoryMb = 3072
            },
            fields = Array.Empty<string>()
        }
    );
    await api.CallAsync<bool>(
        "workspace.instances.saveSettings",
        copy.Id,
        new
        {
            value = new LaunchSettings
            {
                Width = 1234
            },
            fields = new[]
            {
                "Width"
            }
        }
    );
    snapshot = await api.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
    Check(
        snapshot.Items.Single(
            x => x.Profile.Id == copy.Id
        )
            .Effective is { Width: 1234, MaximumMemoryMb: 3072 },
        "host-preserves-setting-inheritance"
    );
    await api.CallAsync<bool>(
        "workspace.instances.savePreferences",
        copy.Id,
        new
        {
            value = new InstancePreferences(Name: "Workspace renamed")
        }
    );
    var icon = Path.Combine(root, "icon-fixture.png");
    await File.WriteAllBytesAsync(
        icon,
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
    );
    await api.CallAsync<bool>(
        "workspace.instances.saveIcon",
        copy.Id,
        new
        {
            path = icon
        }
    );
    Check(
        (await api.ReadIconAsync(copy.Id))!.SequenceEqual(
            File.ReadAllBytes(icon)
        ),
        "icon-controlled-export-roundtrip"
    );
    await api.CallAsync<bool>("workspace.instances.removeIcon", copy.Id);
    Check(
        await api.ReadIconAsync(copy.Id)is null,
        "host-removes-icon"
    );
    var backup = Path.Combine(root, "workspace-backup.zip");
    await api.ExportAsync(
        "workspace.instances.backup",
        copy.Id,
        new { },
        backup
    );
    var verified = await api.CallAsync<BackupVerification>(
        "workspace.instances.verifyBackup",
        null,
        new
        {
            path = backup
        }
    );
    Check(verified.HasFileHashes && verified.Files > 0, "host-backup-export-and-hash-verification");
    var restored = await api.CallAsync<GameProfile>(
        "workspace.instances.restoreBackup",
        null,
        new
        {
            path = backup
        }
    );
    Check(
        restored.Id != copy.Id && Directory.Exists(restored.GameDirectory),
        "host-restores-backup-as-new-instance"
    );
    var trashed = await api.CallAsync<DeletedInstance>("workspace.instances.trash", restored.Id);
    Check(
        trashed.DataMoved && !Directory.Exists(restored.GameDirectory),
        "host-trash-moves-only-selected-instance"
    );
    var untrashed = await api.CallAsync<GameProfile>(
        "workspace.instances.restoreTrash",
        null,
        new
        {
            id = trashed.Id
        }
    );
    Check(
        Directory.Exists(untrashed.GameDirectory),
        "host-restores-trash"
    );
    var scan = await api.CallAsync<InstanceScan>(
        "workspace.instances.scan",
        null,
        new
        {
            path = copy.GameDirectory
        }
    );
    Check(scan.Items.Length > 0, "host-scans-import-candidates");
    var imported = await api.CallAsync<GameProfile>(
        "workspace.instances.import",
        null,
        new
        {
            selection = scan.Handle + ":0"
        }
    );
    Check(
        imported.Id != copy.Id && Directory.Exists(imported.GameDirectory),
        "opaque-import-selection-copies-real-instance"
    );
    await api.CallAsync<bool>("workspace.instances.repair", imported.Id);
    Check(
        File.Exists(imported.JsonPath),
        "host-repairs-real-official-installation"
    );
    // 原业务资源服务会解析真实 ZIP/JAR 内容、写备份和配置，非替身执行。
    var mods = Path.Combine(copy.GameDirectory, "mods");
    Directory.CreateDirectory(mods);
    var jar = Path.Combine(mods, "workspace-fixture.jar");
    Zip(
        jar,
        ("fabric.mod.json", "{\"schemaVersion\":1,\"id\":\"workspace_fixture\",\"version\":\"1.0.0\",\"name\":\"Workspace Fixture\"}")
    );
    var resources = await api.CallAsync<ResourceScan>("workspace.resources.scan", copy.Id);
    Check(
        resources.Items.Any(
            x => x.Path == jar
        ),
        "host-parses-local-mod-jar"
    );
    await api.CallAsync<bool>(
        "workspace.resources.enable",
        copy.Id,
        new
        {
            paths = new[]
            {
                jar
            },
            enabled = false
        }
    );
    Check(
        File.Exists(jar + ".disabled"),
        "host-disables-local-mod"
    );
    await api.CallAsync<bool>(
        "workspace.resources.enable",
        copy.Id,
        new
        {
            paths = new[]
            {
                jar + ".disabled"
            },
            enabled = true
        }
    );
    Check(
        File.Exists(jar),
        "host-reenables-local-mod"
    );
    await api.CallAsync<string[]>("workspace.resources.validate", copy.Id);
    await api.CallAsync<string[]>("workspace.resources.history", copy.Id);
    var moved = await api.CallAsync<string>(
        "workspace.resources.remove",
        copy.Id,
        new
        {
            paths = new[]
            {
                jar
            }
        }
    );
    Check(
        !File.Exists(jar) && Directory.Exists(moved),
        "host-backs-up-removed-resource"
    );
    var world = Path.Combine(copy.GameDirectory, "saves", "workspace-world");
    Directory.CreateDirectory(world);
    File.WriteAllBytes(
        Path.Combine(world, "level.dat"),
        [
            1
        ]
    );
    var datapack = Path.Combine(root, "workspace-data.zip");
    Zip(
        datapack,
        ("pack.mcmeta", "{\"pack\":{\"pack_format\":48,\"description\":\"workspace test\"}}"),
        ("data/pcl/function/test.mcfunction", "say test")
    );
    var worlds = await api.CallAsync<WorldDirectory[]>("workspace.datapacks.worlds", copy.Id);
    Check(
        worlds.Any(
            x => x.Path == world
        ),
        "host-lists-world-directories"
    );
    await api.CallAsync<string>(
        "workspace.datapacks.install",
        copy.Id,
        new
        {
            world,
            source = datapack
        }
    );
    var packs = await api.CallAsync<WorldDataPack[]>(
        "workspace.datapacks.list",
        copy.Id,
        new
        {
            world
        }
    );
    var data = packs.Single();
    await api.CallAsync<bool>(
        "workspace.datapacks.enable",
        copy.Id,
        new
        {
            world,
            path = data.Path,
            enabled = false
        }
    );
    packs = await api.CallAsync<WorldDataPack[]>(
        "workspace.datapacks.list",
        copy.Id,
        new
        {
            world
        }
    );
    Check(
        !packs.Single()
            .Enabled,
        "host-installs-and-disables-world-datapack"
    );
    await api.CallAsync<string>(
        "workspace.datapacks.remove",
        copy.Id,
        new
        {
            world,
            path = packs.Single()
                .Path
        }
    );
    Check(
        (await api.CallAsync<WorldDataPack[]>(
            "workspace.datapacks.list",
            copy.Id,
            new
            {
                world
            }
        )).Length == 0,
        "host-removes-datapack-to-backup"
    );
    var archive = Path.Combine(root, "workspace-custom.zip");
    Zip(archive, ("config/workspace.txt", "original"));
    var options = new PackImportOptions("1.21.1");
    var preview = await api.CallAsync<PackInspection>(
        "workspace.packs.inspect",
        null,
        new
        {
            path = archive,
            options
        }
    );
    Check(preview.Plan.Overrides.Count == 1, "host-inspects-custom-pack");
    var installed = await api.CallAsync<GameProfile>(
        "workspace.packs.install",
        null,
        new
        {
            handle = preview.Handle,
            options
        }
    );
    Check(
        File.ReadAllText(
            Path.Combine(installed.GameDirectory, "config/workspace.txt")
        ) == "original",
        "host-installs-custom-pack-with-real-game-files"
    );
    File.WriteAllText(
        Path.Combine(installed.GameDirectory, "config/workspace.txt"),
        "user edit"
    );
    var changes = await api.CallAsync<PackChanges>("workspace.packs.changes", installed.Id);
    Check(
        changes.Changed.Contains("config/workspace.txt"),
        "host-detects-user-pack-change"
    );
    var updated = await api.CallAsync<GameProfile>(
        "workspace.packs.update",
        installed.Id,
        new
        {
            handle = preview.Handle,
            options,
            preserve = true
        }
    );
    Check(
        File.ReadAllText(
            Path.Combine(updated.GameDirectory, "config/workspace.txt")
        ) == "user edit" && Directory.Exists(installed.GameDirectory),
        "pack-update-preserves-source-and-user-change"
    );
    var exportOptions = new PackExportOptions(
        "Workspace export",
        IncludedFolders: new[]
        {
            "config"
        }
    );
    await api.CallAsync<bool>(
        "workspace.packs.saveExportOptions",
        updated.Id,
        new
        {
            options = exportOptions
        }
    );
    Check(
        (await api.CallAsync<PackExportOptions>("workspace.packs.exportOptions", updated.Id)).Name == exportOptions.Name,
        "host-persists-pack-export-options"
    );
    var exported = Path.Combine(root, "workspace-export.mrpack");
    await api.ExportAsync(
        "workspace.packs.export",
        updated.Id,
        new
        {
            options = exportOptions
        },
        exported
    );
    using (var zip = ZipFile.OpenRead(exported))
    {
        Check(
            zip.GetEntry("modrinth.index.json")is not null,
            "host-exports-valid-mrpack"
        );
    }

    var repreview = await api.CallAsync<PackInspection>(
        "workspace.packs.inspect",
        null,
        new
        {
            path = exported,
            options
        }
    );
    var reimport = await api.CallAsync<GameProfile>(
        "workspace.packs.install",
        null,
        new
        {
            handle = repreview.Handle,
            options
        }
    );
    Check(
        File.ReadAllText(
            Path.Combine(reimport.GameDirectory, "config/workspace.txt")
        ) == "user edit",
        "host-reimports-exported-mrpack"
    );
    // 改动已预览归档后必须拒绝安装，不能把旧确认套到新输入。
    File.AppendAllText(archive, "changed");
    bool refused = false;
    try
    {
        await api.CallAsync<GameProfile>(
            "workspace.packs.install",
            null,
            new
            {
                handle = preview.Handle,
                options
            }
        );
    }
    catch (OperationRejectedException ex)
    {
        refused = ex.Code == "PACK_PREVIEW_CHANGED";
    }

    Check(refused, "changed-pack-preview-refused");
    var secret = "fixture-only-not-a-real-secret";
    await api.SecretAsync(secret);
    Check(
        !Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(
                x => x.Contains("page-")
            )
            .Any(
                x => File.ReadAllText(x)
                    .Contains(secret)
            ),
        "secret-handle-never-persists-secret"
    );
    await session.ShutdownAsync();
    await File.WriteAllTextAsync(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "passed",
                checks,
                copyId = copy.Id,
                packId = installed.Id
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
}
catch (Exception ex)
{
    await File.WriteAllTextAsync(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "failed",
                checks,
                error = ex.Message
            }
        )
    );
    try
    {
        await session.ShutdownAsync();
    }
    catch
    {
    }

    throw;
}
