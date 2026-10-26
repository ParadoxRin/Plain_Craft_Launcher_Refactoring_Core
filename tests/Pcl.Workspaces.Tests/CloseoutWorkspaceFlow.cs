using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

internal static class CloseoutWorkspaceFlow
{
    // 使用真实缓存副本经 Host 复制/删除/恢复实例，并逐文件证明共享目录没有变化。
    internal static async Task Run(string root, string report)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(
            Path.Combine(root, ".closeout-fifth")
        ))
        {
            throw new InvalidOperationException("ISOLATED_ROOT_REQUIRED");
        }

        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }

            checks.Add(label);
            Console.WriteLine(label);
        }

        Dictionary<string, string> Snapshot()
        {
            var result = new Dictionary<string, string>();
            foreach (var relative in new[]
            {
                "minecraft/assets",
                "minecraft/libraries",
                "minecraft/versions",
                "runtime"
            }

            )
            {
                foreach (var file in Directory.EnumerateFiles(
                    Path.Combine(root, relative),
                    "*",
                    SearchOption.AllDirectories
                ))
                {
                    using var input = File.OpenRead(file);
                    result[Path.GetRelativePath(root, file)] = Convert.ToHexString(
                        SHA256.HashData(input)
                    );
                }
            }

            return result;
        }

        await using var session = new LauncherSession(
            new HostClient(
                Path.GetFullPath(".tooling/dotnet/dotnet"),
                Path.GetFullPath("src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll"),
                root,
                reconnectable: true
            )
        );
        try
        {
            var before = Snapshot();
            await session.InitializeAsync();
            var api = new WorkspaceApi(session.Workspaces);
            var copy = await api.CallAsync<GameProfile>(
                "workspace.instances.copy",
                "1.21.1",
                new
                {
                    name = "Shared Cache Acceptance"
                }
            );
            Check(
                copy.Id != "1.21.1" && File.Exists(copy.JsonPath),
                "INST-012-real-instance-copy-registered"
            );
            Check(
                !Directory.Exists(
                    Path.Combine(copy.GameDirectory, "assets")
                ) && !Directory.Exists(
                    Path.Combine(copy.GameDirectory, "runtime")
                ),
                "INST-012-copy-excludes-shared-cache"
            );
            var removed = await api.CallAsync<DeletedInstance>("workspace.instances.trash", copy.Id);
            Check(
                removed.DataMoved && !Directory.Exists(copy.GameDirectory),
                "INST-012-delete-only-moves-selected-instance"
            );
            var restored = await api.CallAsync<GameProfile>(
                "workspace.instances.restoreTrash",
                null,
                new
                {
                    id = removed.Id
                }
            );
            Check(
                File.Exists(restored.JsonPath),
                "INST-012-restore-keeps-instance-metadata"
            );
            var after = Snapshot();
            Check(
                before.Count == after.Count && before.All(
                    x => after.TryGetValue(x.Key, out var hash) && x.Value == hash
                ),
                "INST-012-all-shared-assets-libraries-clients-java-unchanged"
            );
            var encyclopedia = await api.CallAsync<SearchPage>(
                "workspace.search.page",
                "1.21.1",
                new
                {
                    provider = "mcmod",
                    kind = 0,
                    online = false,
                    query = "sodium",
                    page = 0
                }
            );
            Check(
                encyclopedia.Hits.Length > 0 && encyclopedia.Hits.All(
                    x => x.McmodUrl is not null
                ),
                "RES-005-host-encyclopedia-association"
            );
            await session.Client.DisconnectAsync();
            await session.Client.ReconnectAsync();
            var reread = await api.CallAsync<SearchPage>(
                "workspace.search.page",
                "1.21.1",
                new
                {
                    session = encyclopedia.Session,
                    page = 0
                }
            );
            Check(
                reread.Hits.Select(
                    x => x.McmodUrl
                )
                    .SequenceEqual(
                        encyclopedia.Hits.Select(
                            x => x.McmodUrl
                        )
                    ),
                "RES-005-reconnect-keeps-associated-page"
            );
            await session.ShutdownAsync();
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        sharedFiles = before.Count,
                        encyclopediaUrl = encyclopedia.Hits[0].McmodUrl,
                        limitations = "Real cache files and Host transactions; no reference-count garbage collection or live encyclopedia download."
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
            throw;
        }
    }
}
