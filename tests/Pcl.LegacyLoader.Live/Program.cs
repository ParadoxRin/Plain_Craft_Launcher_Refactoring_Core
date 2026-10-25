using System.Text.Json;
using Pcl.Core;
using Pcl.Core.Adapters.Operations;
using Pcl.Core.Application.Operations;
using Pcl.Core.Ports.Operations;
using Pcl.Core.Contracts.Installation;

// 两个旧加载器各用真实官方元数据、真实安装器及 Java；取消后关闭服务，再以新操作续做。
var root = Path.GetFullPath(args[0]);
var paths = new AppPaths(root);
using var source = new MinecraftService(paths, gameVersion: "1.12.2");
var target = new InstallationTarget(source.Platform.Os, source.Platform.Architecture, source.Platform.OsVersion);
var results = new List<object>();
foreach (var family in args.Skip(1)
    .DefaultIfEmpty("optifine")
    .ToArray())
{
    try
    {
        string parent;
        DownloadTasks.For(paths)
            .Configure(
                new NetworkOptions
                {
                    ArtificialDelayMs = 1000
                }
            );
        using (var store = new FileOperationStore(root))
        {
            var service = new InstallOperationService(
                store,
                new ExistingVersionInstaller(source),
                target
            );
            parent = (await service.AcceptAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    new("1.12.2", family, target)
                )
            )).OperationId;
            var deadline = DateTime.UtcNow.AddMinutes(6);
            while (true)
            {
                var snapshot = (await service.GetAsync(parent))!;
                if (snapshot.Stage == (family == "optifine"
                    ? "installer-running"
                    : "loader-planned"))
                {
                    break;
                }

                if (snapshot.Terminal || DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException("CHECKPOINT_NOT_REACHED:" + snapshot.ErrorCode);
                }

                await Task.Delay(2);
            }

            await service.CancelAsync(parent);
            if ((await service.WaitAsync(parent)).State != "cancelled")
            {
                throw new Exception("CANCELLATION_NOT_CONFIRMED");
            }

            await service.StopAsync();
        }

        Console.WriteLine("CANCELLED " + family);
        DownloadTasks.For(paths)
            .Configure(
                new NetworkOptions()
            );
        using (var store = new FileOperationStore(root))
        {
            var service = new InstallOperationService(
                store,
                new ExistingVersionInstaller(source),
                target
            );
            var accepted = await service.AcceptAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    new("1.12.2", family, target),
                    parent
                )
            );
            var done = await service.WaitAsync(accepted.OperationId)
                .WaitAsync(
                    TimeSpan.FromMinutes(10)
                );
            if (done.State != "succeeded")
            {
                throw new Exception("RESUME_FAILED:" + done.ErrorCode);
            }

            var receipt = store.ReadAll()
                .Select(
                    x => JsonSerializer.Deserialize<StoredOperation>(x)!
                )
                .Single(
                    x => x.Snapshot.OperationId == done.OperationId
                )
                .Receipt!;
            var profile = InstanceRegistry.List(source, true)
                .Single(
                    x => x.Id == done.Result!.Id
                );
            var installed = InstanceRegistry.Load(source, profile);
            if (installed.Libraries.Any(
                x => !DownloadService.IsValid(x)
            ))
            {
                throw new Exception("INVALID_LIBRARIES");
            }

            var java = await new JavaService(source).EnsureForAsync(installed);
            var session = AccountManager.OfflineSession("LegacyRecovery");
            var launcher = new LaunchService(source);
            var plan = await launcher.PrepareAsync(installed, java, session, gameDirectory: profile.GameDirectory);
            var game = await launcher.StartAsync(plan, session);
            bool rendered = false;
            try
            {
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (!game.HasExited && DateTime.UtcNow < deadline)
                {
                    var log = File.ReadAllText(game.LogFile);
                    if (log.Contains("Created:") && (log.Contains("atlas") || log.Contains("textures")))
                    {
                        rendered = true;
                        break;
                    }

                    await Task.Delay(250);
                }

                if (!rendered)
                {
                    throw new Exception("RENDERER_NOT_INITIALIZED");
                }
            }
            finally
            {
                if (!game.HasExited)
                {
                    await game.StopAsync();
                }

                await game.Completion;
                await game.DisposeAsync();
            }

            results.Add(
                new
                {
                    family,
                    status = "passed",
                    parent,
                    resumed = accepted.OperationId,
                    version = receipt.LoaderPin!.Version,
                    rendered,
                    libraries = installed.Libraries.Count
                }
            );
            await service.StopAsync();
        }

        Console.WriteLine("PASSED " + family);
    }
    catch (Exception ex)
    {
        results.Add(
            new
            {
                family,
                status = "failed",
                error = DiagnosticsService.Redact(ex.Message),
                type = ex.GetType()
                    .Name
            }
        );
        Console.WriteLine(
            "FAILED " + family + " " + DiagnosticsService.Redact(ex.Message)
        );
        Environment.ExitCode = 1;
    }

    File.WriteAllText(
        Path.Combine(
            Path.GetDirectoryName(root)!,
            "legacy-loader-live.json"
        ),
        JsonSerializer.Serialize(
            results,
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
}
