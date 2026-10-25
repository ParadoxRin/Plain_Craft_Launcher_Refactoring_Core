using Newtonsoft.Json.Linq;
using Pcl.Core.Ports.Operations;
using View = Pcl.Core.Contracts.Workspaces;

namespace Pcl.Core.Adapters.Pages;
public sealed partial class PageExecutionAdapter
{
    // Snapshot reads are Host-owned and safe while the game is running; credentials never leave AccountService.
    public View.DesktopSnapshot DesktopSnapshot(Pcl.Core.Contracts.Accounts.AccountSnapshot accounts)
    {
        using var profiles = new FabricService(source);
        var profileRows = profiles.Profiles();
        View.AppearanceSettings appearance;
        try
        {
            appearance = System.Text.Json.JsonSerializer.Deserialize<View.AppearanceSettings>(
                System.Text.Json.JsonSerializer.Serialize(
                    new AppearanceService(source.Paths).Load()
                )
            )!;
        }
        catch (Exception ex)when (ex is IOException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            appearance = new();
        }

        return new(
            new(
                source.Paths.Root,
                source.Paths.Game,
                source.Paths.Runtime,
                source.Paths.Evidence
            ),
            profileRows.Select(
                p => new View.GameProfile(p.Id, p.Name, p.GameVersion, p.Loader, p.GameDirectory, p.JsonPath)
            )
                .ToArray(),
            appearance,
            accounts,
            new MeloongCore.ConfigEntry<string>("SelectedProfile", "").Get(),
            MinecraftService.Version,
            MicrosoftApplication.BuiltIn.IsConfigured,
            profileRows.Select(
                p =>
                {
                    if (p.Loader != "vanilla")
                        return new View.GameProfile(p.Id, p.Name, p.GameVersion, p.Loader, p.GameDirectory, p.JsonPath);
                    var root = Path.Combine(source.Paths.Root, "instances", "fabric-" + p.GameVersion);
                    var existing = profileRows.FirstOrDefault(
                        x => x.GameDirectory == root
                    );
                    return existing is not null
                        ? new View.GameProfile(
                            existing.Id,
                            existing.Name,
                            existing.GameVersion,
                            existing.Loader,
                            existing.GameDirectory,
                            existing.JsonPath
                        )
                        : new View.GameProfile(
                            "fabric-" + p.GameVersion,
                            p.GameVersion + " · Fabric",
                            p.GameVersion,
                            "fabric",
                            root,
                            Path.Combine(root, "version.json")
                        );
                }
            )
                .ToArray()
        );
    }

    public View.DownloadSnapshot DownloadSnapshot()
    {
        var tasks = DownloadTasks.For(source.Paths);
        var rows = tasks.Snapshot()
            .Take(300)
            .Select(
                r => new View.DownloadTaskRow(
                    r.Id,
                    new(r.Item.Path, r.Item.Size),
                    r.State,
                    r.Bytes,
                    r.Updated,
                    r.Error is null
                        ? null
                        : DiagnosticsService.Redact(r.Error)
                )
            )
            .ToArray();
        var options = System.Text.Json.JsonSerializer.Deserialize<View.NetworkOptions>(
            System.Text.Json.JsonSerializer.Serialize(tasks.Options)
        )!;
        var history = new InstallOperations(source.Paths).Read()
            .Select(
                r => new View.InstallOperation(
                    r.Id,
                    r.Version,
                    r.Loader,
                    r.State,
                    r.Updated,
                    r.Error is null
                        ? null
                        : DiagnosticsService.Redact(r.Error)
                )
            )
            .ToArray();
        return new(
            rows,
            tasks.IsPaused,
            options,
            history,
            Directory.EnumerateFiles(source.Paths.Evidence, "*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(100)
                .ToArray()
        );
    }

    // Pause must bypass the installation lane: a paused download owns that lane until resumed/cancelled.
    public bool PauseDownloads(bool paused)
    {
        DownloadTasks.For(source.Paths)
            .SetPaused(paused);
        return paused;
    }

    // Remaining shell commands reuse original validation/install/diagnostics in the owned workspace lane.
    private async Task<object?> ExecuteShellAsync(string id, string method, JObject args, CancellationToken ct)
    {
        string Text(string k) => (string? )args[k] ?? "";
        if (method == "workspace.shell.openFolder")
        {
            var profileId = Text("id");
            var folder = profileId.Length == 0
                ? source.Paths.Game
                : InstanceRegistry.List(source, true)
                    .Single(
                        p => p.Id == profileId
                    )
                    .GameDirectory;
            Directory.CreateDirectory(folder);
            return folder;
        }

        if (method == "workspace.shell.downloadRetry")
        {
            var row = DownloadTasks.For(source.Paths)
                .Snapshot()
                .SingleOrDefault(
                    r => r.Id == Text("id")
                ) ?? throw new OperationInstallException("DOWNLOAD_NOT_FOUND");
            ResourceInstaller.SafeTarget(
                source.Paths.Root,
                Path.GetRelativePath(source.Paths.Root, row.Item.Path)
            );
            await source.Downloads.DownloadAsync(row.Item, ct);
            return true;
        }

        if (method == "workspace.shell.diagnostic")
        {
            var text = DiagnosticsService.ReadLog(
                Text("path")
            );
            var found = DiagnosticsService.Analyze(text);
            return found.Count == 0
                ? DiagnosticsService.Redact(
                    text[^Math.Min(text.Length, 6000)..]
                )
                : string.Join(
                    "\n\n",
                    found.Select(
                        x => x.Cause + " · " + x.Confidence + "\n" + x.Advice + "\n" + x.Evidence
                    )
                );
        }

        if (method == "workspace.shell.diagnosticExport")
        {
            var output = WorkspaceOutput(id, "PCL-diagnostics.zip");
            DiagnosticsService.Export(
                Text("path"),
                output
            );
            return RegisterWorkspaceExport(id, output);
        }

        if (method == "workspace.shell.profile")
        {
            var p = InstanceRegistry.List(source, true)
                .SingleOrDefault(
                    p => p.Id == Text("id")
                );
            try
            {
                if (p is null)
                    return new View.ProfileAvailability(false);
                InstanceRegistry.Load(source, p);
                return new View.ProfileAvailability(true);
            }
            catch (Exception ex)when (ex is System.IO.IOException or InvalidDataException or ArgumentException)
            {
                return new View.ProfileAvailability(false);
            }
        }

        if (method == "workspace.shell.selectProfile")
        {
            var selectedId = Text("id");
            if (!InstanceRegistry.List(source, true)
                .Any(
                    p => p.Id == selectedId
                ))
                throw new OperationInstallException("INSTANCE_NOT_FOUND");
            new MeloongCore.ConfigEntry<string>("SelectedProfile", "").Set(selectedId);
            MeloongCore.ConfigUtils.SaveAll();
            return true;
        }

        if (method == "workspace.shell.window")
        {
            var width = (int? )args["width"] ?? 1100;
            var height = (int? )args["height"] ?? 700;
            if (width < 100 || width > 20000 || height < 100 || height > 20000)
                throw new OperationInstallException("INVALID_ARGUMENT");
            JsonFiles.Write(
                Path.Combine(source.Paths.Root, "launcher-window.json"),
                new
                {
                    width,
                    height
                }
            );
            return true;
        }

        if (method == "workspace.shell.gameExit")
            return await DiagnosticsService.GameExitStatusAsync(
                (int? )args["exitCode"] ?? -1,
                (bool? )args["stopped"] ?? false,
                Text("log")
            );
        if (method == "workspace.shell.classifyDrop")
        {
            var path = Text("path");
            if (!File.Exists(path))
                throw new FileNotFoundException();
            var extension = Path.GetExtension(path)
                .ToLowerInvariant();
            if (extension is ".jar" or ".litemod")
                return new View.DropClassification("Mod");
            if (extension is not (".zip" or ".mrpack"))
                throw new OperationInstallException("INVALID_DATA");
            if (extension == ".zip")
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(path);
                if (archive.Entries.Any(
                    e => e.FullName.StartsWith("shaders/", StringComparison.Ordinal)
                ))
                    return new View.DropClassification("Shader");
                if (archive.GetEntry("pack.mcmeta")is not null && archive.Entries.Any(
                    e => e.FullName.StartsWith("assets/", StringComparison.Ordinal)
                ))
                    return new View.DropClassification("ResourcePack");
            }

            return new View.DropClassification(null);
        }

        if (method == "workspace.shell.releaseNotice")
        {
            var options = DownloadTasks.For(source.Paths)
                .Options;
            if (!options.NotifyRelease && !options.NotifySnapshot)
                return new View.ReleaseNotice(
                    false,
                    []
                );
            var versions = await VersionCatalog.ListAsync(source, ct);
            var installed = Directory.Exists(
                Path.Combine(source.Paths.Game, "versions")
            )
                ? Directory.GetDirectories(
                    Path.Combine(source.Paths.Game, "versions")
                )
                    .Select(Path.GetFileName)
                    .ToHashSet()
                : [];
            return new View.ReleaseNotice(
                true,
                versions.Where(
                    v => (options.NotifyRelease && v.Type == "release") || (options.NotifySnapshot && v.Type == "snapshot")
                )
                    .GroupBy(
                        v => v.Type
                    )
                    .Select(
                        g => g.OrderByDescending(
                            v => v.Released
                        )
                            .First()
                    )
                    .Where(
                        v => !installed.Contains(v.Id)
                    )
                    .Select(
                        v => v.Id
                    )
                    .ToArray()
            );
        }

        throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
    }
}
