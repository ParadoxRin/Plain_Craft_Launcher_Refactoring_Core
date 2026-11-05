using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 此探针只观察原生拖放，不调用导入处理器或伪造 DropEvent。
    private async Task DropWorldAcceptanceAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--acceptance-report") + 1];
        var folder = Program.Arguments[Array.IndexOf(Program.Arguments, "--acceptance-files") + 1];
        var checks = new List<string>();
        var drops = 0;
        AddHandler(
            DragDrop.DropEvent,
            (_, e) =>
            {
                var files = e.DataTransfer.TryGetFiles()?.ToArray();
                if (files?.Length == 1 && Path.GetFileName(
                    files[0].TryGetLocalPath()
                ) == "PCL-Acceptance-Resources.zip")
                {
                    drops++;
                }
            },
            handledEventsToo: true
        );
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new InvalidOperationException(name);
            }

            checks.Add(name);
        }

        try
        {
            var api = new WorkspaceApi(service.Workspaces);
            RefreshProfiles("1.21.1");
            Title = "PCL — 拖放验收（隔离副本）";
            File.WriteAllText(report + ".step", "waiting-for-real-drop-and-user-confirmation");
            var deadline = DateTime.UtcNow.AddMinutes(10);
            ManagedResource? imported = null;
            while (DateTime.UtcNow < deadline)
            {
                if (drops > 0 && operation is null)
                {
                    var rows = await api.CallAsync<ManagedResource[]>("workspace.browser.list", "1.21.1");
                    imported = rows.SingleOrDefault(
                        x => x.Kind == ResourceKind.ResourcePack
                    );
                    if (imported is not null)
                    {
                        break;
                    }
                }

                await Task.Delay(500);
            }

            Check(
                drops > 0 && imported is not null,
                "UI-013-real-drop-confirmation-imports-resource-through-host"
            );
            var expected = Convert.ToHexString(
                SHA1.HashData(
                    await File.ReadAllBytesAsync(
                        Path.Combine(folder, "PCL-Acceptance-Resources.zip")
                    )
                )
            )
                .ToLowerInvariant();
            var installedFile = Path.Combine(
                ((GameProfile)ProfileBox.SelectedItem!).GameDirectory,
                "resourcepacks",
                imported!.FileName
            );
            var actual = Convert.ToHexString(
                SHA1.HashData(
                    await File.ReadAllBytesAsync(installedFile)
                )
            )
                .ToLowerInvariant();
            Check(actual == expected, "UI-013-imported-resource-hash-matches-dropped-file");
            await api.CallAsync<bool>(
                "workspace.browser.activate",
                "1.21.1",
                new
                {
                    projectId = imported.ProjectId
                }
            );
            Check(true, "RES-012-host-activates-dropped-resource-pack");
            var worlds = await api.CallAsync<WorldDirectory[]>("workspace.datapacks.worlds", "1.21.1");
            var world = worlds.Single(
                x => x.Name == "PCL_Acceptance"
            );
            await api.CallAsync<string>(
                "workspace.datapacks.install",
                "1.21.1",
                new
                {
                    world = world.Path,
                    source = Path.Combine(folder, "PCL-Acceptance-Data.zip")
                }
            );
            var packs = await api.CallAsync<WorldDataPack[]>(
                "workspace.datapacks.list",
                "1.21.1",
                new
                {
                    world = world.Path
                }
            );
            Check(
                packs.Any(
                    x => x.Name == "PCL-Acceptance-Data.zip" && x.Enabled
                ),
                "RES-013-host-installs-datapack-into-selected-disposable-world"
            );
            File.WriteAllText(report + ".step", "prepared-for-game");
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = error.ToString()
                    }
                )
            );
        }
        finally
        {
            await RequestApplicationExitAsync();
        }
    }
}
