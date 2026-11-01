using System.Text.Json;
using System.Security.Cryptography;
using Pcl.Client;
using Pcl.Core;
using Pcl.Core.Adapters.Operations;
using Pcl.Core.Adapters.Pages;

internal static class UnifiedBrowserFlow
{
    // 混合来源记录通过真实 Host RPC 选取，验证索引偏移、实例绑定和重启后持久句柄。
    internal static async Task Run(string root, string report, string? firstHost = null, string? secondHost = null)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root)
            .Any())
        {
            throw new InvalidOperationException("FRESH_ROOT_REQUIRED");
        }

        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var builds = new List<Pcl.Core.Contracts.Updates.HostBuild?>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }

            checks.Add(name);
        }

        var file = Path.Combine(root, "manual.bin");
        await File.WriteAllTextAsync(file, "public-synthetic-file");
        var sha = Convert.ToHexString(
            SHA1.HashData(
                await File.ReadAllBytesAsync(file)
            )
        )
            .ToLowerInvariant();
        var cf = new CurseResourceRelease(
            123,
            456,
            "CF fixture",
            "fixture.zip",
            sha,
            new FileInfo(file).Length,
            null,
            DateTimeOffset.UtcNow,
            "release",
            [
                "1.21.1"
            ],
            [],
            "https://example.invalid/file"
        );
        var mr = new ResourceRelease(
            "MR",
            "MR-project",
            "MR fixture",
            "1",
            "release",
            DateTimeOffset.UtcNow,
            [
                "1.21.1"
            ],
            [],
            new(
                "other.zip",
                new Uri("https://example.invalid/other"),
                1,
                new string ('a', 40)
            ),
            []
        );
        var id = "op-" + Guid.NewGuid()
            .ToString("N");
        using (var store = new FileOperationStore(root, "page-execution-v1"))
        {
            store.Save(
                id,
                JsonSerializer.SerializeToUtf8Bytes(
                    new PrivatePageRecord(
                        1,
                        new(id),
                        "search-releases",
                        JsonSerializer.SerializeToElement(
                            new ReleaseSelection(
                                "1.21.1",
                                ResourceKind.ResourcePack,
                                [
                                    mr
                                ],
                                [
                                    cf
                                ]
                            )
                        )
                    )
                )
            );
        }

        var other = Path.Combine(root, "instances", "other");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(
            Path.Combine(other, "profile.json"),
            "{\"id\":\"other\",\"gameVersion\":\"1.21.1\",\"loader\":\"fabric\",\"loaderVersion\":\"0.19.5\"}"
        );
        for (var round = 0; round < 2; round++)
        {
            await using var session = new LauncherSession(
                new HostClient(
                    Environment.GetEnvironmentVariable("PCL_DOTNET") ?? Environment.ProcessPath!,
                    Path.GetFullPath((round == 0 ? firstHost : secondHost ?? firstHost)
                        ?? "src/Pcl.Host/bin/Release/net10.0/Pcl.Host.dll"),
                    root,
                    reconnectable: true
                )
            );
            try
            {
                await session.InitializeAsync();
                builds.Add((await session.Client.HelloAsync()).Build);
                var api = new WorkspaceApi(session.Workspaces);
                var accepted = await api.CallAsync<bool>(
                    "workspace.search.match",
                    "1.21.1",
                    new
                    {
                        selection = id + ":1",
                        path = file
                    }
                );
                Check(
                    accepted,
                    round == 0
                        ? "mixed-cf-index-matches-exact-file"
                        : "mixed-selection-survives-host-restart"
                );
                foreach (var bad in new[]
                {
                    (Instance: "1.21.1", Index: 0),
                    (Instance: "1.21.1", Index: 2),
                    (Instance: "other", Index: 1)
                }

                )
                {
                    var rejected = false;
                    try
                    {
                        await api.CallAsync<bool>(
                            "workspace.search.match",
                            bad.Instance,
                            new
                            {
                                selection = id + ":" + bad.Index,
                                path = file
                            }
                        );
                    }
                    catch (Pcl.Core.Contracts.Operations.OperationRejectedException)
                    {
                        rejected = true;
                    }

                    Check(rejected, $"round-{round}-reject-{bad.Instance}-{bad.Index}");
                }
            }
            finally
            {
                await session.ShutdownAsync();
            }
        }

        if (firstHost is not null && secondHost is not null)
        {
            Check(builds[0] is not null && builds[1] is not null
                && builds[0]!.Sequence < builds[1]!.Sequence, "different-host-builds-observed");
        }

        File.WriteAllText(
            report,
            JsonSerializer.Serialize(
                new
                {
                    status = "passed",
                    checks,
                    builds,
                    scope = "Real Host with synthetic mixed-source private selection; no CurseForge network authentication"
                }
            )
        );
    }
}
