using System.Text;
using Newtonsoft.Json;
using Pcl.Core;
using Pcl.Cli;

// 远程命令由私有 Host 独占数据目录；父 CLI 不构造第二套本地可写服务。
if (args.FirstOrDefault()is "host-install" or "host-launch")
{
    return await RemoteCli.RunAsync(args);
}

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
MeloongCore.Main.Init("PCLMacPort");
// [实现] Option 的实现：准备 var i = Array.IndexOf(args, name)；返回 i >= 0 && i + 1 < args.Length ? args[i + 1] : null。
string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length
        ? args[i + 1]
        : null;
}

var paths = new AppPaths(
    Option("--data-root")
);
// CLI 的旧安装/资源/诊断入口也遵守同一数据目录 lease，不能绕过独立 Host 的写入权。
FileStream rootLease;
try
{
    rootLease = new FileStream(
        Path.Combine(paths.Root, ".launcher.lock"),
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None
    );
}
catch (IOException)
{
    Console.Error.WriteLine("DATA_ROOT_BUSY");
    return 2;
}

using var managedRootLease = rootLease;
MeloongCore.ConfigProviders.Default = new MeloongCore.JsonConfigProvider(
    Path.Combine(paths.Root, "settings.json")
);
using var service = new MinecraftService(
    paths,
    gameVersion: Option("--game-version")
);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // [实现] 回调触发时：执行 e.Cancel = true；执行 cancellation.Cancel()。
    e.Cancel = true;
    cancellation.Cancel();
};
var lastPrint = DateTime.MinValue;
var lastStage = "";
var progress = new Progress<InstallProgress>(
    p =>
    {
        // [实现] 回调触发时：按 p.Stage != lastStage || DateTime.UtcNow - lastPrint > TimeSpan.FromSeconds(5) || p.Completed ==… 分支处理。
        if (p.Stage != lastStage || DateTime.UtcNow - lastPrint > TimeSpan.FromSeconds(5) || p.Completed == p.Total)
        {
            // [实现] 条件成立（p.Stage != lastStage || DateTime.UtcNow - lastPrint > TimeSpan.FromSeconds(5) || p.Completed ==…）时：执行 Console.WriteLine( $"{p.Stage}: {p.Completed}/{p.Total} {p.Bytes / 1048576.0:F1}/{p.TotalBytes …；执行 lastStage = p.Stage；执行 lastPrint = DateTime.UtcNow。
            Console.WriteLine(
                $"{p.Stage}: {p.Completed}/{p.Total} {p.Bytes / 1048576.0:F1}/{p.TotalBytes / 1048576.0:F1} MiB"
            );
            lastStage = p.Stage;
            lastPrint = DateTime.UtcNow;
        }
    }
);
try
{
    // [实现] 在异常保护范围内：准备 var command = args.FirstOrDefault() ?? "help"；按 command == "network-probe" 分支处理；按 command == "link-smoke" 分支处理。
    var command = args.FirstOrDefault() ?? "help";
    if (command == "network-probe")
    {
        // [实现] 条件成立（command == "network-probe"）时：准备 var probe = new NetworkDiagnosticsService( DownloadTasks.For(paths) .Options )；准备 var targets = Option("--url")is string url ? new[] { new NetworkProbeTarget("自定义源", url) } : Ne…；准备 var results = new List<NetworkProbeResult>()。
        using var probe = new NetworkDiagnosticsService(
            DownloadTasks.For(paths)
                .Options
        );
        var targets = Option("--url")is string url
            ? new[]
            {
                new NetworkProbeTarget("自定义源", url)
            }

            : NetworkDiagnosticsService.ConfiguredTargets(paths);
        var results = new List<NetworkProbeResult>();
        foreach (var target in targets)
            results.Add(
                await probe.ProbeAsync(target, cancellation.Token)
            );
        JsonFiles.Write(
            Path.Combine(paths.Evidence, "network-probe.json"),
            new
            {
                timestamp = DateTimeOffset.UtcNow,
                sampleByteLimit = NetworkDiagnosticsService.MaximumSampleBytes,
                timeoutSeconds = 10,
                fullDownloadValidation = false,
                results
            }
        );
        Console.WriteLine(
            JsonConvert.SerializeObject(results, Formatting.Indented)
        );
        return results.All(
            x => x.Success
        )
            ? 0
            : 2;
    }

    if (command == "link-smoke")
    {
        // [实现] 条件成立（command == "link-smoke"）时：执行 await LinkSmoke.RunAsync(service, cancellation.Token)；执行 Console.WriteLine("LINK TRANSPORT PASSED")；返回 0。
        await LinkSmoke.RunAsync(service, cancellation.Token);
        Console.WriteLine("LINK TRANSPORT PASSED");
        return 0;
    }

    if (command.StartsWith("account-"))
    {
        // [实现] 条件成立（command.StartsWith("account-")）时：准备 var manager = new AccountManager( paths, Pcl.Core.Adapters.Credentials.PlatformAccountSecrets.Create(paths.Root) )；按 command == "account-login" 分支处理；返回 0。
        var manager = new AccountManager(
            paths,
            Pcl.Core.Adapters.Credentials.PlatformAccountSecrets.Create(paths.Root)
        );
        if (command == "account-login")
        {
            // [实现] 条件成立（command == "account-login"）时：准备 var auth = new MicrosoftAuthentication()；准备 var clientId = MicrosoftApplication.BuiltIn.RequireClientId()；准备 var challenge = await auth.BeginAsync(clientId, cancellation.Token)。
            using var auth = new MicrosoftAuthentication();
            var clientId = MicrosoftApplication.BuiltIn.RequireClientId();
            var challenge = await auth.BeginAsync(clientId, cancellation.Token);
            Console.WriteLine($"请在浏览器打开 {challenge.VerificationUri} 并输入 {challenge.UserCode}。");
            var login = await auth.CompleteLoginAsync(clientId, challenge, cancellation.Token);
            GameLaunchAuthorization.Require(login.Session);
            // 先保存已完成的登录证据及明确请求记住的凭据；刷新失败不应丢失首次正版验证结果。
            if (args.Contains("--remember"))
            {
                // [实现] 条件成立（args.Contains("--remember")）时：执行 manager.SaveMicrosoft(login, true)。
                manager.SaveMicrosoft(login, true);
            }

            JsonFiles.Write(
                Path.Combine(paths.Evidence, "microsoft-initial-login-0.18.json"),
                new
                {
                    status = "passed",
                    clientId,
                    deviceCodeLogin = true,
                    javaOwnership = true,
                    playerProfile = true,
                    timestamp = DateTimeOffset.UtcNow
                }
            );
            var refreshed = await auth.RefreshAsync(clientId, login.RefreshToken ?? "", cancellation.Token);
            GameLaunchAuthorization.Require(refreshed.Session);
            if (refreshed.Session.Uuid != login.Session.Uuid)
                throw new LoginRequiredException("刷新后的账户身份不一致。");
            if (args.Contains("--remember"))
                manager.SaveMicrosoft(refreshed, true);
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "microsoft-login-result.json"),
                new
                {
                    status = "passed",
                    clientId,
                    deviceCodeLogin = true,
                    javaOwnership = true,
                    playerProfile = true,
                    refresh = true,
                    remembered = args.Contains("--remember"),
                    gameLaunch = "not_tested",
                    timestamp = DateTimeOffset.UtcNow
                }
            );
            Console.WriteLine("MICROSOFT LOGIN AND REFRESH PASSED");
        }
        else if (command == "account-verify")
        {
            // 新进程从钥匙串读取已选择的账户，重新交换令牌并检验权益；不将令牌写入终端或证据。
            using var auth = new MicrosoftAuthentication();
            var session = await manager.ResolveAsync(
                manager.SelectedId ?? throw new LoginRequiredException("请先登录微软账户。"),
                auth,
                cancellation.Token
            );
            GameLaunchAuthorization.Require(session);
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "microsoft-cold-refresh-0.18.json"),
                new
                {
                    status = "passed",
                    coldProcess = true,
                    javaOwnership = true,
                    timestamp = DateTimeOffset.UtcNow
                }
            );
            Console.WriteLine("MICROSOFT COLD REFRESH PASSED");
        }
        else if (command == "account-add-offline")
            Console.WriteLine(
                JsonConvert.SerializeObject(
                    manager.AddOffline(
                        Option("--name") ?? ""
                    ),
                    Formatting.Indented
                )
            );
        else if (command == "account-list")
            Console.WriteLine(
                JsonConvert.SerializeObject(manager.Accounts, Formatting.Indented)
            );
        else if (command == "account-remove")
            manager.Remove(
                Option("--account") ?? ""
            );
        else if (command == "account-logout")
            manager.Logout(
                Option("--account") ?? ""
            );
        else
            throw new ArgumentException("未知账户命令。");
        return 0;
    }

    if (command == "keychain-self-test")
    {
        // Mac 专用自检继续直接验证历史钥匙串适配器；普通账户入口使用宿主平台选择。
        var secrets = new Pcl.Core.Adapters.Mac.KeychainAccountSecrets(paths.Root);
        var id = "self-test-" + Guid.NewGuid()
            .ToString("N");
        try
        {
            // [实现] 在异常保护范围内：执行 secrets.Set(id, "test-value-1")；按 secrets.Get(id) != "test-value-1" 分支处理；执行 secrets.Set(id, "test-value-2")。
            secrets.Set(id, "test-value-1");
            if (secrets.Get(id) != "test-value-1")
                throw new IOException("钥匙串读取验证失败。");
            secrets.Set(id, "test-value-2");
            if (secrets.Get(id) != "test-value-2")
                throw new IOException("钥匙串更新验证失败。");
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 secrets.Delete(id)。
            secrets.Delete(id);
        }

        if (secrets.Get(id)is not null)
            throw new IOException("钥匙串清理验证失败。");
        JsonFiles.Write(
            Path.Combine(paths.Evidence, "keychain-self-test.json"),
            new
            {
                status = "passed",
                writeReadUpdateDelete = true,
                usesRealAccount = false
            }
        );
        Console.WriteLine("KEYCHAIN SELF TEST PASSED");
        return 0;
    }

    if (command == "version-list")
    {
        // [实现] 条件成立（command == "version-list"）时：执行 Console.WriteLine( JsonConvert.SerializeObject( (await VersionCatalog.ListAsync(service, cancel…；返回 0。
        Console.WriteLine(
            JsonConvert.SerializeObject(
                (await VersionCatalog.ListAsync(service, cancellation.Token)).Where(
                    x => args.Contains("--snapshots") || x.Type == "release"
                ),
                Formatting.Indented
            )
        );
        return 0;
    }

    if (command == "java-check")
    {
        // [实现] 条件成立（command == "java-check"）时：准备 var java = Option("--java")is string executable ? await JavaService.InspectAsync(executable, ca…；执行 JsonFiles.Write( Path.Combine(paths.Evidence, "java-check-result.json"), java )；执行 Console.WriteLine( JsonConvert.SerializeObject(java, Formatting.Indented) )。
        var java = Option("--java")is string executable
            ? await JavaService.InspectAsync(executable, cancellation.Token)
            : await new JavaService(service).EnsureAsync(
                int.TryParse(
                    Option("--major"),
                    out var major
                )
                    ? major
                    : 21,
                progress,
                cancellation.Token
            );
        JsonFiles.Write(
            Path.Combine(paths.Evidence, "java-check-result.json"),
            java
        );
        Console.WriteLine(
            JsonConvert.SerializeObject(java, Formatting.Indented)
        );
        return 0;
    }

    if (command is "pack-install" or "pack-inspect")
    {
        // [实现] 条件成立（command is "pack-install" or "pack-inspect"）时：准备 var file = Option("--file") ?? throw new ArgumentException("需要 --file")；按 command == "pack-inspect" 分支处理；准备 var selection = args.Select( (value, index) => (value, index) ) .Where( x => x.value == "--opti…。
        var file = Option("--file") ?? throw new ArgumentException("需要 --file");
        if (command == "pack-inspect")
        {
            // [实现] 条件成立（command == "pack-inspect"）时：执行 Console.WriteLine( JsonConvert.SerializeObject( ModpackService.Inspect( file, args.Contains("--…；返回 0。
            Console.WriteLine(
                JsonConvert.SerializeObject(
                    ModpackService.Inspect(
                        file,
                        args.Contains("--include-optional")
                    ),
                    Formatting.Indented
                )
            );
            return 0;
        }

        var selection = args.Select(
            (value, index) => (value, index)
        )
            .Where(
                x => x.value == "--optional-file" && x.index + 1 < args.Length
            )
            .Select(
                x => args[x.index + 1]
            )
            .ToArray();
        var result = await new ModpackService(service).InstallAsync(
            file,
            Option("--instance"),
            args.Contains("--include-optional"),
            progress,
            cancellation.Token,
            selection.Length > 0
                ? selection
                : args.Contains("--no-optional")
                    ? []
                    : null
        );
        Console.WriteLine(
            JsonConvert.SerializeObject(result, Formatting.Indented)
        );
        return 0;
    }

    if (command is "loader-install" or "loader-list")
    {
        // [实现] 条件成立（command is "loader-install" or "loader-list"）时：准备 var forge = new ForgeService(service)；准备 var kind = Option("--loader") ?? "neoforge"；按 command == "loader-list" 分支处理。
        using var forge = new ForgeService(service);
        var kind = Option("--loader") ?? "neoforge";
        if (command == "loader-list")
        {
            // [实现] 条件成立（command == "loader-list"）时：执行 Console.WriteLine( JsonConvert.SerializeObject( await forge.VersionsAsync(kind, cancellation.To…；返回 0。
            Console.WriteLine(
                JsonConvert.SerializeObject(
                    await forge.VersionsAsync(kind, cancellation.Token),
                    Formatting.Indented
                )
            );
            return 0;
        }

        var profile = await forge.InstallAsync(
            kind,
            Option("--loader-version"),
            Option("--instance"),
            progress: progress,
            ct: cancellation.Token
        );
        Console.WriteLine(
            JsonConvert.SerializeObject(profile, Formatting.Indented)
        );
        return 0;
    }

    if (command == "mac-setup")
    {
        // [实现] 条件成立（command == "mac-setup"）时：准备 var result = await new MacSetupService(service).InstallAsync( args.Contains("--shaders"), progr…；执行 Console.WriteLine( JsonConvert.SerializeObject(result, Formatting.Indented) )；返回 0。
        var result = await new MacSetupService(service).InstallAsync(
            args.Contains("--shaders"),
            progress,
            cancellation.Token
        );
        Console.WriteLine(
            JsonConvert.SerializeObject(result, Formatting.Indented)
        );
        return 0;
    }

    if (command == "mac-shader-inspect")
    {
        // [实现] 条件成立（command == "mac-shader-inspect"）时：准备 var report = MacShaderCompatibility.Inspect( Option("--file") ?? throw new ArgumentException("需…；执行 Console.WriteLine( JsonConvert.SerializeObject(report, Formatting.Indented) )；返回 report.CanActivate ? 0 : 2。
        var report = MacShaderCompatibility.Inspect(
            Option("--file") ?? throw new ArgumentException("需要 --file")
        );
        Console.WriteLine(
            JsonConvert.SerializeObject(report, Formatting.Indented)
        );
        return report.CanActivate
            ? 0
            : 2;
    }

    if (command.StartsWith("resource-") || command is "fabric-install" or "shader-setup")
    {
        // [实现] 条件成立（command.StartsWith("resource-") || command is "fabric-install" or "shader-setup"）时：准备 var fabric = new FabricService(service)；准备 var catalog = new ResourceCatalog(paths: service.Paths)；准备 var kind = (Option("--kind") ?? "mod") switch { "shader" => ResourceKind.Shader, "resourcepack"…。
        using var fabric = new FabricService(service);
        using var catalog = new ResourceCatalog(paths: service.Paths);
        var kind = (Option("--kind") ?? "mod") switch
        {
            "shader" => ResourceKind.Shader,
            "resourcepack" => ResourceKind.ResourcePack,
            "modpack" => ResourceKind.Modpack,
            _ => ResourceKind.Mod
        };
        var ct = cancellation.Token;
        if (command == "resource-search")
        {
            // [实现] 条件成立（command == "resource-search"）时：执行 Console.WriteLine( JsonConvert.SerializeObject( await catalog.SearchAsync( kind, Option("--quer…；返回 0。
            Console.WriteLine(
                JsonConvert.SerializeObject(
                    await catalog.SearchAsync(
                        kind,
                        Option("--query") ?? "",
                        service.GameVersion,
                        Option("--loader") ?? "fabric",
                        ct: ct
                    ),
                    Formatting.Indented
                )
            );
            return 0;
        }

        if (command == "fabric-install")
        {
            // [实现] 条件成立（command == "fabric-install"）时：准备 var profile = await fabric.InstallAsync( Option("--loader-version"), progress, ct )；执行 JsonFiles.Write( Path.Combine(paths.Evidence, "fabric-install-result.json"), new { status = "pa…；执行 Console.WriteLine(profile.Name)。
            var profile = await fabric.InstallAsync(
                Option("--loader-version"),
                progress,
                ct
            );
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "fabric-install-result.json"),
                new
                {
                    status = "passed",
                    profile
                }
            );
            Console.WriteLine(profile.Name);
            return 0;
        }

        var resourceProfile = Option("--profile");
        if (kind == ResourceKind.Modpack && command == "resource-install")
        {
            // [实现] 条件成立（kind == ResourceKind.Modpack && command == "resource-install"）时：准备 var id = Option("--project") ?? throw new ArgumentException("需要 --project")；准备 var project = await catalog.ProjectAsync(id, ct)；按 project.Kind != kind 分支处理。
            var id = Option("--project") ?? throw new ArgumentException("需要 --project");
            var project = await catalog.ProjectAsync(id, ct);
            if (project.Kind != kind)
                throw new InvalidDataException("不是整合包项目。");
            var versions = await catalog.VersionsAsync(id, kind, service.GameVersion, ct: ct);
            var release = Option("--version-id")is string vid
                ? await catalog.VersionAsync(vid, ct)
                : versions.FirstOrDefault(
                    x => x.Channel == "release"
                ) ?? versions.First();
            if (release.ProjectId != project.Id)
                throw new InvalidDataException("版本项目不匹配。");
            var result = await new ModpackService(service).InstallReleaseAsync(
                release,
                args.Contains("--include-optional"),
                progress,
                ct
            );
            Console.WriteLine(
                JsonConvert.SerializeObject(result, Formatting.Indented)
            );
            return 0;
        }

        var target = resourceProfile is "mac-base" or "mac-shaders"
            ? fabric.Profiles()
                .Single(
                    x => Path.GetFileName(x.GameDirectory) == resourceProfile + "-1.21.1"
                )
            : resourceProfile is not null && resourceProfile != "fabric"
                ? InstanceRegistry.ByFolder(service, resourceProfile)
                : fabric.Profiles()
                    .FirstOrDefault(
                        x => x.GameDirectory == fabric.ProfileRoot
                    ) ?? await fabric.InstallAsync(progress: progress, ct: ct);
        var store = new ResourceInstaller(target.GameDirectory, service.Downloads, target.Loader, target.GameVersion);
        if (command == "resource-activate")
        {
            // [实现] 条件成立（command == "resource-activate"）时：执行 store.Activate( Option("--project") ?? throw new ArgumentException("需要项目 ID") )；返回 0。
            store.Activate(
                Option("--project") ?? throw new ArgumentException("需要项目 ID")
            );
            return 0;
        }

        if (command is "resource-install" or "shader-setup")
        {
            // [实现] 条件成立（command is "resource-install" or "shader-setup"）时：准备 var id = command == "shader-setup" ? "iris" : Option("--project") ?? throw new ArgumentExceptio…；按 command == "shader-setup" 分支处理；准备 var versions = await catalog.VersionsAsync(id, kind, target.GameVersion, loader: target.Loader,…。
            var id = command == "shader-setup"
                ? "iris"
                : Option("--project") ?? throw new ArgumentException("需要 --project");
            if (command == "shader-setup")
            {
                // [实现] 条件成立（command == "shader-setup"）时：按 target.Loader != "fabric" 分支处理；执行 kind = ResourceKind.Mod。
                if (target.Loader != "fabric")
                    throw new NotSupportedException("Iris 方案需要 Fabric 实例。");
                kind = ResourceKind.Mod;
            }

            var versions = await catalog.VersionsAsync(id, kind, target.GameVersion, loader: target.Loader, ct: ct);
            var release = Option("--version-id")is string versionId
                ? await catalog.VersionAsync(versionId, ct)
                : versions.FirstOrDefault(
                    x => x.Channel == "release"
                ) ?? versions.First();
            var project = await catalog.ProjectAsync(id, ct);
            if (project.Id != release.ProjectId || project.Kind != kind)
                throw new InvalidDataException("项目与版本或资源类型不匹配。");
            var plan = await ResourceDependencyPlanner.ResolveAsync(catalog, service.Downloads, release, kind, target, progress, ct);
            plan = plan with
            {
                Releases = plan.Releases.Select(
                    x => x.ProjectId == project.Id
                        ? x with
                        {
                            Name = project.Title
                        }
                        : x
                )
                    .ToArray()
            };
            Console.WriteLine($"PLAN {plan.Releases.Count} files / {plan.Bytes} bytes");
            var installed = await store.InstallAsync(plan, progress, ct);
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "resource-install-" + project.Id + ".json"),
                new
                {
                    status = "passed",
                    project,
                    plan,
                    installed,
                    target.GameDirectory
                }
            );
            Console.WriteLine(
                JsonConvert.SerializeObject(installed, Formatting.Indented)
            );
            return 0;
        }

        if (command == "resource-list")
        {
            // [实现] 条件成立（command == "resource-list"）时：执行 Console.WriteLine( JsonConvert.SerializeObject( store.List(), Formatting.Indented ) )；返回 0。
            Console.WriteLine(
                JsonConvert.SerializeObject(
                    store.List(),
                    Formatting.Indented
                )
            );
            return 0;
        }

        throw new ArgumentException("未知资源命令。");
    }

    if (command == "version-install")
    {
        // [实现] 条件成立（command == "version-install"）时：准备 var result = await new VersionInstaller(service).InstallAsync( Option("--loader") ?? "auto", pr…；执行 Console.WriteLine( JsonConvert.SerializeObject(result, Formatting.Indented) )；返回 0。
        var result = await new VersionInstaller(service).InstallAsync(
            Option("--loader") ?? "auto",
            progress,
            cancellation.Token
        );
        Console.WriteLine(
            JsonConvert.SerializeObject(result, Formatting.Indented)
        );
        return 0;
    }

    if (command == "install")
    {
        // [实现] 条件成立（command == "install"）时：准备 var version = await service.InstallAsync(progress, cancellation.Token)；准备 var java = await new JavaService(service).EnsureForAsync(version, progress, cancellation.Token)；执行 JsonFiles.Write( Path.Combine(paths.Evidence, "install-result.json"), new { status = "passed", …。
        var version = await service.InstallAsync(progress, cancellation.Token);
        var java = await new JavaService(service).EnsureForAsync(version, progress, cancellation.Token);
        JsonFiles.Write(
            Path.Combine(paths.Evidence, "install-result.json"),
            new
            {
                status = "passed",
                version.Id,
                java,
                platform = service.Platform,
                timestamp = DateTimeOffset.UtcNow
            }
        );
        Console.WriteLine("INSTALL PASSED " + paths.Root);
        return 0;
    }

    // 实例管理复用桌面服务与校验事务，备份/恢复不要求微软凭据，也不写入账户数据。
    if (command == "instance-copy")
    {
        // 复制使用同一实例管理事务，生成独立 ID 并排除账户与共享缓存。
        var folder = Option("--profile") ?? throw new ArgumentException("请用 --profile 指定实例目录名称。");
        var name = Option("--name") ?? throw new ArgumentException("请用 --name 指定副本名称。");
        var manager = new InstanceManager(service);
        var copied = await manager.CopyAsync(
            InstanceRegistry.ByFolder(service, folder),
            name,
            cancellation.Token
        );
        JsonFiles.Write(
            Path.Combine(paths.Evidence, "last-instance-copy.json"),
            new
            {
                status = "passed",
                profileId = copied.Id,
                gameVersion = copied.GameVersion,
                loader = copied.Loader,
                gameDirectory = copied.GameDirectory
            }
        );
        Console.WriteLine("INSTANCE_COPIED " + copied.Id);
        return 0;
    }

    if (command is "instance-backup" or "instance-restore")
    {
        // [实现] 条件成立（command is "instance-backup" or "instance-restore"）时：准备 var file = Option("--file") ?? throw new ArgumentException("请用 --file 指定备份 ZIP 路径。")；准备 var manager = new InstanceManager(service)；按 command == "instance-backup" 分支处理。
        var file = Option("--file") ?? throw new ArgumentException("请用 --file 指定备份 ZIP 路径。");
        var manager = new InstanceManager(service);
        if (command == "instance-backup")
        {
            // [实现] 条件成立（command == "instance-backup"）时：准备 var folder = Option("--profile") ?? throw new ArgumentException("请用 --profile 指定实例目录名称。")；准备 var profile = InstanceRegistry.ByFolder(service, folder)；执行 await manager.BackupAsync(profile, file, cancellation.Token)。
            var folder = Option("--profile") ?? throw new ArgumentException("请用 --profile 指定实例目录名称。");
            var profile = InstanceRegistry.ByFolder(service, folder);
            await manager.BackupAsync(profile, file, cancellation.Token);
            Console.WriteLine(
                "INSTANCE_BACKUP_VERIFIED " + Path.GetFullPath(file)
            );
        }
        else
        {
            // [实现] 前一条件不成立时：准备 var restored = await manager.RestoreBackupAsync(file, progress, cancellation.Token)；执行 JsonFiles.Write( Path.Combine(paths.Evidence, "last-instance-restore.json"), new { status = "pa…；执行 Console.WriteLine("INSTANCE_RESTORED " + restored.Id)。
            var restored = await manager.RestoreBackupAsync(file, progress, cancellation.Token);
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "last-instance-restore.json"),
                new
                {
                    status = "passed",
                    profileId = restored.Id,
                    gameVersion = restored.GameVersion,
                    loader = restored.Loader,
                    gameDirectory = restored.GameDirectory,
                    timestamp = DateTimeOffset.UtcNow
                }
            );
            Console.WriteLine("INSTANCE_RESTORED " + restored.Id);
        }

        return 0;
    }

    if (command is "plan" or "launch" or "native-audit")
    {
        // [实现] 条件成立（command is "plan" or "launch" or "native-audit"）时：准备 var fabric = new FabricService(service)；准备 var selectedName = Option("--profile")；准备 var selected = selectedName == "fabric" ? fabric.FabricProfile() : selectedName is "mac-base" o…。
        using var fabric = new FabricService(service);
        var selectedName = Option("--profile");
        var selected = selectedName == "fabric"
            ? fabric.FabricProfile()
            : selectedName is "mac-base" or "mac-shaders"
                ? fabric.Profiles()
                    .Single(
                        x => Path.GetFileName(x.GameDirectory) == selectedName + "-1.21.1"
                    )
                : selectedName is not null && selectedName != "vanilla"
                    ? InstanceRegistry.ByFolder(service, selectedName)
                    : fabric.Profiles()
                        .Single(
                            x => x.Loader == "vanilla" && x.GameVersion == service.GameVersion
                        );
        if (command == "launch")
        {
            if (args.Contains("--demo"))
            {
                throw new LoginRequiredException("此版本启动游戏需要微软正版验证。");
            }

            var manager = new AccountManager(
                paths,
                Pcl.Core.Adapters.Credentials.PlatformAccountSecrets.Create(paths.Root)
            );
            var requestedAccount = Option("--account");
            // 已有外置认证诊断路径保留；主流离线/微软启动与桌面、Host 共用统一受理器。
            if (requestedAccount is null || manager.Accounts.FirstOrDefault(
                x => x.Id == requestedAccount
            )?.Kind != AccountKind.External)
            {
                return await UnifiedCliLaunch.RunAsync(
                    service,
                    manager,
                    selected,
                    Option("--offline-name"),
                    requestedAccount,
                    Option("--java"),
                    Option("--idempotency-key") ?? Guid.NewGuid()
                        .ToString("N"),
                    args.Contains("--remember"),
                    args.Contains("--detached"),
                    int.TryParse(
                        Option("--smoke-seconds"),
                        out var smokeSeconds
                    )
                        ? smokeSeconds
                        : 0,
                    cancellation.Token
                );
            }
        }

        var version = fabric.LoadInstalled(selected);
        var java = Option("--java")is string exe
            ? await JavaService.InspectAsync(exe, cancellation.Token)
            : await new LauncherSettingsStore(paths).ResolveJavaAsync(service, version, selected.GameDirectory, progress, cancellation.Token);
        if (command == "native-audit")
        {
            // [实现] 条件成立（command == "native-audit"）时：准备 var report = MacNativeAudit.Check(version, java)；执行 JsonFiles.Write( Path.Combine(paths.Evidence, selected.Id + "-native-audit.json"), report )；执行 Console.WriteLine("NATIVE AUDIT PASSED " + selected.Id)。
            var report = MacNativeAudit.Check(version, java);
            JsonFiles.Write(
                Path.Combine(paths.Evidence, selected.Id + "-native-audit.json"),
                report
            );
            Console.WriteLine("NATIVE AUDIT PASSED " + selected.Id);
            return 0;
        }

        GameSession session;
        AccountManager? launchAccounts = null;
        LauncherAccount? launchAccount = null;
        var clientId = MicrosoftApplication.BuiltIn.ClientId;
        if (args.Contains("--demo"))
            throw new LoginRequiredException("此版本启动游戏需要微软正版验证。");
        else if (Option("--offline-name")is string offlineName)
        {
            // [实现] 条件成立（Option("--offline-name")is string offlineName）时：准备 var manager = new AccountManager( paths, Pcl.Core.Adapters.Credentials.PlatformAccountSecrets.Create(paths.Root) )；准备 var account = manager.AddOffline(offlineName)；执行 clientId = ""。
            var manager = new AccountManager(
                paths,
                Pcl.Core.Adapters.Credentials.PlatformAccountSecrets.Create(paths.Root)
            );
            var account = manager.AddOffline(offlineName);
            clientId = "";
            launchAccounts = manager;
            launchAccount = account;
            using var auth = new MicrosoftAuthentication();
            session = await manager.ResolveAsync(account.Id, auth, cancellation.Token);
        }
        else if (Option("--account")is string accountId)
        {
            // [实现] 条件成立（Option("--account")is string accountId）时：准备 var manager = new AccountManager( paths, Pcl.Core.Adapters.Credentials.PlatformAccountSecrets.Create(paths.Root) )；准备 var auth = new MicrosoftAuthentication()；执行 session = await manager.ResolveAsync(accountId, auth, cancellation.Token)。
            var manager = new AccountManager(
                paths,
                Pcl.Core.Adapters.Credentials.PlatformAccountSecrets.Create(paths.Root)
            );
            using var auth = new MicrosoftAuthentication();
            session = await manager.ResolveAsync(accountId, auth, cancellation.Token);
            launchAccounts = manager;
            launchAccount = manager.Accounts.Single(
                x => x.Id == accountId
            );
            clientId = launchAccount.ClientId ?? "";
        }
        else
        {
            // [实现] 前一条件不成立时：准备 var auth = new MicrosoftAuthentication()；准备 var challenge = await auth.BeginAsync(clientId, cancellation.Token)；执行 Console.WriteLine($"请在浏览器打开 {challenge.VerificationUri} 并输入 {challenge.UserCode}。")。
            using var auth = new MicrosoftAuthentication();
            var challenge = await auth.BeginAsync(clientId, cancellation.Token);
            Console.WriteLine($"请在浏览器打开 {challenge.VerificationUri} 并输入 {challenge.UserCode}。");
            var login = await auth.CompleteLoginAsync(clientId, challenge, cancellation.Token);
            session = login.Session;
            var manager = new AccountManager(
                paths,
                Pcl.Core.Adapters.Credentials.PlatformAccountSecrets.Create(paths.Root)
            );
            launchAccounts = manager;
            launchAccount = manager.SaveMicrosoft(
                login,
                args.Contains("--remember")
            );
        }

        GameLaunchAuthorization.Require(session);
        new OfflineSkinService(paths).Prepare(
            launchAccount,
            version,
            selected.GameDirectory,
            selected.GameVersion,
            cancellation.Token
        );
        var launcher = new LaunchService(service);
        var plan = await launcher.PrepareAsync(version, java, session, clientId, selected.GameDirectory, cancellation.Token);
        if (launchAccount?.Kind == AccountKind.External)
            plan = ExternalAuthentication.AddJvmArguments(
                plan,
                await launchAccounts!.ExternalLaunchArgumentsAsync(launchAccount.Id, service, cancellation.Token)
            );
        JsonFiles.Write(
            Path.Combine(paths.Evidence, "launch-plan.json"),
            new
            {
                plan.Java,
                Arguments = LaunchService.Redact(plan.Arguments),
                plan.WorkingDirectory,
                session.Demo,
                platform = service.Platform
            }
        );
        if (command == "plan")
        {
            // [实现] 条件成立（command == "plan"）时：执行 Console.WriteLine("PLAN PASSED")；返回 0。
            Console.WriteLine("PLAN PASSED");
            return 0;
        }

        var logLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        new AppearanceService(paths).PrepareGameLanguage(selected.GameDirectory, selected.GameVersion);
        using var hooks = new LaunchHookRunner(service.Commands);
        await hooks.RunAsync(
            LaunchHookSettings.Load(paths),
            selected.GameDirectory,
            Console.WriteLine,
            cancellation.Token
        );
        var game = await launcher.StartAsync(
            plan,
            session,
            line =>
            {
                // [实现] 回调触发时：执行 logLines.Enqueue(line)；执行 Console.WriteLine(line)。
                logLines.Enqueue(line);
                Console.WriteLine(line);
            }
        );
        Console.WriteLine($"GAME PID {game.Pid}; LOG {game.LogFile}");
        var smoke = int.TryParse(
            Option("--smoke-seconds"),
            out var seconds
        )
            ? seconds
            : 0;
        if (smoke > 0)
        {
            // [实现] 条件成立（smoke > 0）时：准备 var finished = await Task.WhenAny( game.Completion, Task.Delay( TimeSpan.FromSeconds(smoke), ca…；准备 var alive = !game.HasExited；准备 var lines = logLines.ToArray()。
            var finished = await Task.WhenAny(
                game.Completion,
                Task.Delay(
                    TimeSpan.FromSeconds(smoke),
                    cancellation.Token
                )
            );
            var alive = !game.HasExited;
            var lines = logLines.ToArray();
            var rendered = lines.Any(
                x => x.Contains("OpenGL", StringComparison.OrdinalIgnoreCase) || x.Contains("Backend library", StringComparison.OrdinalIgnoreCase) || x.Contains("LWJGL Version:", StringComparison.OrdinalIgnoreCase)
            );
            var resources = lines.Any(
                x => x.Contains("Created:") && x.Contains("atlas")
            );
            if (alive)
                await game.StopAsync();
            var exit = await game.Completion;
            var startupPassed = rendered && resources && (alive || exit == 0);
            var result = new
            {
                profileId = selected.Id,
                gameVersion = selected.GameVersion,
                loader = selected.Loader,
                javaMajor = java.MajorVersion,
                javaArchitecture = java.Architecture,
                status = startupPassed
                    ? (alive
                        ? "render_initialization_observed"
                        : "render_initialized_and_clean_exit")
                    : "needs_review",
                session.Demo,
                aliveAfterSeconds = smoke,
                wasAlive = alive,
                graphicsLogObserved = rendered,
                atlasCreationObserved = resources,
                stoppedBySmokeTest = alive,
                exitCode = exit,
                log = game.LogFile,
                timestamp = DateTimeOffset.UtcNow,
                visualInspection = "not_performed",
                interactiveWorldPlay = "not_tested",
                accountKind = session.Demo
                    ? "demo"
                    : launchAccount?.Kind == AccountKind.External
                        ? "external"
                        : session.UserType == "msa"
                            ? "microsoft"
                            : "offline",
                accountLogin = session.UserType == "msa" || launchAccount?.Kind == AccountKind.External
                    ? "passed"
                    : "not_applicable"
            };
            if (!args.Contains("--isolated-evidence"))
                JsonFiles.Write(
                    Path.Combine(paths.Evidence, "launch-result.json"),
                    result
                );
            JsonFiles.Write(
                AppPaths.SafeChild(paths.Evidence, "launch-result-" + selected.Id + ".json"),
                result
            );
            return startupPassed
                ? 0
                : 2;
        }

        return await game.Completion;
    }

    Console.WriteLine(
        "PCL Mac Port CLI\nhost-install --data-root <folder> --game-version <version> --loader <loader> [--resume-of <operationId>]\nhost-launch --data-root <folder> --account <id> --profile <instanceId> [--detached] [--idempotency-key <key>]\ninstance-copy --profile <folder> --name <name> | instance-backup --profile <folder> --file <zip> | instance-restore --file <zip>\naccount-add-offline --name <name> | account-list | account-remove --account <id>\njava-check [--java <executable>] | install | account-login [--remember] | plan --account <id> | launch --offline-name <name> | launch --account <id> [--smoke-seconds 90] [--idempotency-key <key>] [--detached] | launch\nloader-list | loader-install --loader forge|neoforge [--loader-version <v>]\npack-inspect | pack-install --file <mrpack> [--instance <name>] [--include-optional | --no-optional | --optional-file <path> (repeatable)]\nfabric-install | shader-setup | resource-search --kind mod|shader|resourcepack|modpack --query <text> | resource-install --kind <type> --project <slug> | resource-list\nmac-setup [--shaders] | mac-shader-inspect --file <zip>\nversion-list [--snapshots] | version-install --game-version <version> [--loader auto|vanilla|fabric|forge|neoforge]\nOptional: --game-version <version>, --data-root <folder>, --java <executable>, --profile <instance-folder>"
    );
    return 0;
}
catch (Exception ex)
{
    // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "last-error.json"), new { status = "failed", erro…；执行 Console.Error.WriteLine(ex)；返回 1。
    JsonFiles.Write(
        Path.Combine(paths.Evidence, "last-error.json"),
        new
        {
            status = "failed",
            error = ex.GetType()
                .FullName,
            ex.Message,
            timestamp = DateTimeOffset.UtcNow
        }
    );
    Console.Error.WriteLine(ex);
    return 1;
}
