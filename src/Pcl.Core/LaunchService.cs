using Pcl.Core.Ports.Processes;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Pcl.Legacy;

namespace Pcl.Core;
// 游戏启动：验证授权与启动配置，构造参数并管理进程、输出和退出状态。
public sealed class LaunchService(MinecraftService service)
{
    // [实现] PrepareAsync 的实现：计算并返回 Task.Run( async () => { ct.ThrowIfCancellationRequested(); gameDirectory ??= service.VanillaGam…。
    public Task<LaunchPlan> PrepareAsync(
        InstalledVersion version,
        JavaRuntime java,
        GameSession session,
        string clientId = "",
        string? gameDirectory = null,
        CancellationToken ct = default
    ) => Task.Run(
        async () =>
        {
            // [实现] 回调触发时：执行 ct.ThrowIfCancellationRequested()；执行 gameDirectory ??= service.VanillaGameDirectory；按 OptiFabricService.IsPreset(gameDirectory) 分支处理。
            ct.ThrowIfCancellationRequested();
            gameDirectory ??= service.VanillaGameDirectory;
            ActivationTransaction.RecoverBeforeLaunch(gameDirectory);
            ShaderRemovalTransaction.RecoverBeforeLaunch(gameDirectory);
            IrisEnvironmentService.RecoverBeforeLaunch(gameDirectory);
            if (OptiFabricService.IsPreset(gameDirectory))
                OptiFabricService.ValidatePreset(gameDirectory);
            var profilePath = ResourceInstaller.SafeTarget(gameDirectory, "profile.json");
            if (File.Exists(profilePath))
            {
                // [实现] 条件成立（File.Exists(profilePath)）时：准备 var receipt = JsonFiles.Read(profilePath)；准备 var loader = (string? )receipt["loader"] ?? "vanilla"；执行 new LocalResourceService(gameDirectory, loader, service.GameVersion).ValidateEnabledResources()。
                var receipt = JsonFiles.Read(profilePath);
                var loader = (string? )receipt["loader"] ?? "vanilla";
                new LocalResourceService(gameDirectory, loader, service.GameVersion).ValidateEnabledResources(ct);
            }

            var configuredJava = new LauncherSettingsStore(service.Paths).Effective(gameDirectory)
                .JavaExecutable;
            if (!string.IsNullOrWhiteSpace(configuredJava))
                java = await JavaService.InspectAsync(configuredJava, ct, service.Commands);
            var metadata = JsonFiles.Read(version.JsonPath);
            var client = metadata["downloads"]?["client"];
            if (client is not null && !DownloadService.IsValid(
                new(
                    new Uri("https://localhost/unused"),
                    version.JarPath,
                    (long? )client["size"] ?? -1,
                    (string? )client["sha1"]
                )
            ))
                throw new InvalidDataException("实例客户端已损坏，请修复或重新安装到新实例。");
            foreach (var item in version.Libraries)
            {
                // [实现] 对 version.Libraries 中的每一项：执行 ct.ThrowIfCancellationRequested()；按 !DownloadService.IsValid(item) 分支处理。
                ct.ThrowIfCancellationRequested();
                if (!DownloadService.IsValid(item))
                    throw new InvalidDataException(
                        "库文件缺失或损坏，请修复实例：" + Path.GetFileName(item.Path)
                    );
            }

            IReadOnlyList<NativeBinaryAudit> natives = service.Platform.Os == "osx" && service.Platform.Architecture == "aarch64"
                ? MacNativeAudit.Check(version, java)
                : Array.Empty<NativeBinaryAudit>();
            LegacyLaunchAdapter.PrepareFiles(
                version,
                Path.Combine(service.Paths.Game, "assets"),
                gameDirectory,
                ct
            );
            if (metadata["logging"]?["client"]?["file"] is JObject logging)
            {
                // [实现] 条件成立（metadata["logging"]?["client"]?["file"] is JObject logging）时：准备 var config = AppPaths.SafeChild( Path.Combine(service.Paths.Game, "assets/log_configs"), (strin…；准备 var item = new DownloadItem( new Uri((string)logging["url"]!), config, (long)logging["size"]!, …；按 item.Url.Scheme != "https" || !Regex.IsMatch(item.Sha1 ?? "", "^[0-9a-f]{40}$") 分支处理。
                var config = AppPaths.SafeChild(
                    Path.Combine(service.Paths.Game, "assets/log_configs"),
                    (string)logging["id"]!
                );
                var item = new DownloadItem(
                    new Uri((string)logging["url"]!),
                    config,
                    (long)logging["size"]!,
                    (string)logging["sha1"]!
                );
                if (item.Url.Scheme != "https" || !Regex.IsMatch(item.Sha1 ?? "", "^[0-9a-f]{40}$"))
                    throw new InvalidDataException("日志配置元数据无效。");
                await service.Downloads.DownloadAsync(item, ct);
            }

            var plan = CreatePlan(version, java, session, clientId, gameDirectory);
            ct.ThrowIfCancellationRequested();
            JsonFiles.Write(
                AppPaths.SafeChild(service.Paths.Evidence, version.Id + "-preflight.json"),
                new
                {
                    status = "passed",
                    version.Id,
                    java.Architecture,
                    java.MajorVersion,
                    libraries = version.Libraries.Count,
                    natives,
                    nativeReplacement = metadata["pclMacCompatibility"] ?? new JValue("official_metadata_preserved"),
                    timestamp = DateTimeOffset.UtcNow
                }
            );
            return plan;
        },
        ct
    );
    // [实现] CreatePlan 的实现：执行 gameDirectory ??= service.VanillaGameDirectory；执行 Directory.CreateDirectory(gameDirectory)；按 java.MajorVersion != version.JavaMajor 分支处理。
    public LaunchPlan CreatePlan(
        InstalledVersion version,
        JavaRuntime java,
        GameSession session,
        string clientId = "",
        string? gameDirectory = null
    )
    {
        gameDirectory ??= service.VanillaGameDirectory;
        Directory.CreateDirectory(gameDirectory);
        if (java.MajorVersion != version.JavaMajor)
            throw new InvalidOperationException($"此版本需要 Java {version.JavaMajor}。");
        if (java.Architecture != service.Platform.Architecture)
            throw new InvalidOperationException("Java 架构与目标运行上下文不匹配。");
        var json = LegacyLaunchAdapter.NormalizeMetadata(
            JsonFiles.Read(version.JsonPath)
        );
        var settings = new LauncherSettingsStore(service.Paths).Effective(gameDirectory);
        settings = MacWindowPolicy.ResolveSize(settings, service.Paths);
        settings.Validate();
        // 自动分配保留至少一半可用内存；手动模式与实例覆盖保持原值。
        settings = LaunchRuntimePolicy.ResolveMemory(
            settings,
            GC.GetGCMemoryInfo()
                .TotalAvailableMemoryBytes
        );
        var natives = Path.Combine(
            Path.GetDirectoryName(version.JsonPath)!,
            "natives"
        );
        Directory.CreateDirectory(natives);
        var quickPlay = !string.IsNullOrWhiteSpace(settings.ServerAddress) && json["arguments"]?["game"]?.ToString()
            .Contains("${quickPlayMultiplayer}", StringComparison.Ordinal) == true;
        var quickPlayWorld = !string.IsNullOrWhiteSpace(settings.QuickPlayWorld);
        var customQuickPlayWorld = ArgumentText.Parse(settings.GameArguments)
            .Any(
                argument => argument.Split('=', 2)[0] == "--quickPlaySingleplayer"
            );
        if (quickPlayWorld || customQuickPlayWorld)
        {
            // 本机 Forge 52.1.20 直达路径跳过世界资源初始化；当前产品保留其普通菜单路径。
            var profileFile = ResourceInstaller.SafeTarget(gameDirectory, "profile.json");
            if (File.Exists(profileFile) && (string? )JsonFiles.Read(profileFile)["loader"] == "forge")
            {
                // [实现] 条件成立（File.Exists(profileFile) && (string? )JsonFiles.Read(profileFile)["loader"] == "forge"）时：抛出 new InvalidDataException("当前 Forge 实例请从游戏主菜单进入世界；请清空快速进入世界设置，并移除附加参数中的 --quickPlaySingleplayer。…。
                throw new InvalidDataException("当前 Forge 实例请从游戏主菜单进入世界；请清空快速进入世界设置，并移除附加参数中的 --quickPlaySingleplayer。");
            }
        }

        if (quickPlayWorld)
        {
            // 依据官方版本元数据启用，旧版本明确拒绝；限制到当前实例且拒绝链接和缺失存档。
            if (json["arguments"]?["game"]?.ToString()
                .Contains("${quickPlaySingleplayer}", StringComparison.Ordinal) != true)
            {
                // [实现] 条件成立（json["arguments"]?["game"]?.ToString() .Contains("${quickPlaySingleplayer}", StringComparison.O…）时：抛出 new InvalidDataException("此版本未声明 Quick Play 单人存档支持，请清空快速进入世界后从游戏菜单进入。")。
                throw new InvalidDataException("此版本未声明 Quick Play 单人存档支持，请清空快速进入世界后从游戏菜单进入。");
            }

            var level = ResourceInstaller.SafeTarget(gameDirectory, "saves/" + settings.QuickPlayWorld + "/level.dat");
            if (!File.Exists(level))
            {
                // [实现] 条件成立（!File.Exists(level)）时：抛出 new InvalidDataException("快速进入的世界不存在，请检查当前实例 saves 下的存档文件夹名称。")。
                throw new InvalidDataException("快速进入的世界不存在，请检查当前实例 saves 下的存档文件夹名称。");
            }
        }

        var features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = session.Demo,
            ["has_custom_resolution"] = true,
            ["has_quick_plays_support"] = quickPlay || quickPlayWorld,
            ["is_quick_play_multiplayer"] = quickPlay,
            ["is_quick_play_singleplayer"] = quickPlayWorld
        };
        var replacements = new Dictionary<string, string>
        {
            ["auth_player_name"] = session.Name,
            ["auth_uuid"] = session.Uuid,
            ["auth_access_token"] = session.AccessToken,
            ["auth_xuid"] = session.Xuid ?? "0",
            ["clientid"] = clientId,
            ["user_type"] = session.UserType,
            ["user_properties"] = "{}",
            ["auth_session"] = session.AccessToken,
            ["game_assets"] = LegacyLaunchAdapter.GameAssets(
                version,
                Path.Combine(service.Paths.Game, "assets")
            ),
            ["quickPlayPath"] = Path.Combine(gameDirectory, "quickPlay", "pcl-session.json"),
            ["quickPlayMultiplayer"] = settings.ServerAddress,
            ["quickPlaySingleplayer"] = settings.QuickPlayWorld,
            ["version_name"] = version.Id,
            ["version_type"] = settings.VersionInformation.Length > 0
                ? settings.VersionInformation
                : (string? )json["type"] ?? "release",
            ["game_directory"] = gameDirectory,
            ["assets_root"] = Path.Combine(service.Paths.Game, "assets"),
            ["assets_index_name"] = version.AssetIndex,
            ["natives_directory"] = natives,
            ["classpath_separator"] = service.Platform.Separator,
            ["classpath"] = string.Join(
                service.Platform.Separator,
                version.Libraries.Where(
                    x => !x.ExtractNative
                )
                    .Select(
                        x => x.Path
                    )
                    .Append(version.JarPath)
            ),
            ["library_directory"] = version.LibraryRoot ?? Path.Combine(service.Paths.Game, "libraries"),
            ["launcher_name"] = "PCLMacPort",
            ["launcher_version"] = "0.19.0",
            ["resolution_width"] = settings.Width.ToString(),
            ["resolution_height"] = settings.Height.ToString()
        };
        // [实现] Resolve 的实现：遍历 arguments 处理 argument。
        IEnumerable<string> Resolve(JToken arguments)
        {
            foreach (var argument in arguments)
            {
                // [实现] 对 arguments 中的每一项：按 argument.Type == JTokenType.String 分支处理；按 !MinecraftRules.Check( argument["rules"], service.Platform.Os, java.Architecture, service.Platf… 分支处理；准备 var value = argument["value"]!。
                if (argument.Type == JTokenType.String)
                {
                    // [实现] 条件成立（argument.Type == JTokenType.String）时：执行 yield return Expand( argument.ToString() );；执行 continue;。
                    yield return Expand(
                        argument.ToString()
                    );
                    continue;
                }

                if (!MinecraftRules.Check(
                    argument["rules"],
                    service.Platform.Os,
                    java.Architecture,
                    service.Platform.OsVersion,
                    features
                ))
                    continue;
                var value = argument["value"]!;
                foreach (var item in value is JArray array
                    ? array.Select(
                        x => x.ToString()
                    )
                    : new[]
                    {
                        value.ToString()
                    }

                )
                    yield return Expand(item);
            }
        }

        // [实现] Expand 的实现：计算并返回 Regex.Replace( arg, @"\$\{([^}]+)\}", match => replacements.TryGetValue(match.Groups[1].Value, …。
        string Expand(string arg) => Regex.Replace(
            arg,
            @"\$\{([^}]+)\}",
            match => replacements.TryGetValue(match.Groups[1].Value, out var value)
                ? value
                : throw new InvalidDataException("未识别的启动变量：" + match.Value)
        );
        var args = new List<string>
        {
            $"-Xms{settings.MinimumMemoryMb}M",
            $"-Xmx{settings.MaximumMemoryMb}M"
        };
        // BootstrapLauncher 按文件名排除客户端 JAR；复制/恢复只改变实例 ID，不能继续假设 JAR 同名。
        string BindClientJarExclusion(string argument)
        {
            const string prefix = "-DignoreList=";
            if (!argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                // [实现] 条件成立（!argument.StartsWith(prefix, StringComparison.Ordinal)）时：返回 argument。
                return argument;
            }

            var entries = argument[prefix.Length..].Split(',');
            for (var index = 0; index < entries.Length; index++)
            {
                // 仅替换由当前实例 ID 展开的完整文件名，其余上游排除规则保持原样。
                if (entries[index] == version.Id + ".jar")
                {
                    // [实现] 条件成立（entries[index] == version.Id + ".jar"）时：执行 entries[index] = Path.GetFileName(version.JarPath)。
                    entries[index] = Path.GetFileName(version.JarPath);
                }
            }

            return prefix + string.Join(',', entries);
        }

        args.AddRange(
            Resolve(json["arguments"]!["jvm"]!)
                .Select(BindClientJarExclusion)
        );
        if (json["logging"]?["client"] is JObject logging && logging["file"] is JObject logFile)
        {
            // [实现] 条件成立（json["logging"]?["client"] is JObject logging && logging["file"] is JObject logFile）时：准备 var path = AppPaths.SafeChild( Path.Combine(service.Paths.Game, "assets/log_configs"), (string)…；按 File.Exists(path) 分支处理。
            var path = AppPaths.SafeChild(
                Path.Combine(service.Paths.Game, "assets/log_configs"),
                (string)logFile["id"]!
            );
            if (File.Exists(path))
            {
                // [实现] 条件成立（File.Exists(path)）时：按 !DownloadService.IsValid( new( new Uri((string)logFile["url"]!), path, (long)logFile["size"]!, … 分支处理；执行 args.Add( ((string)logging["argument"]!).Replace("${path}", path) )。
                if (!DownloadService.IsValid(
                    new(
                        new Uri((string)logFile["url"]!),
                        path,
                        (long)logFile["size"]!,
                        (string)logFile["sha1"]!
                    )
                ))
                    throw new InvalidDataException("日志配置校验失败。");
                args.Add(
                    ((string)logging["argument"]!).Replace("${path}", path)
                );
            }
        }

        if (service.Platform.Os == "osx")
        {
            // [实现] 条件成立（service.Platform.Os == "osx"）时：按 (json["libraries"] is null || LegacyLaunchAdapter.RequiresFirstThread(json)) && !args.Contains(… 分支处理；执行 args.Add("-Xdock:name=Minecraft · PCL Mac")；准备 var index = Path.Combine(service.Paths.Game, "assets/indexes", version.AssetIndex + ".json")。
            if ((json["libraries"] is null || LegacyLaunchAdapter.RequiresFirstThread(json)) && !args.Contains("-XstartOnFirstThread"))
                args.Add("-XstartOnFirstThread");
            args.Add("-Xdock:name=Minecraft · PCL Mac");
            var index = Path.Combine(service.Paths.Game, "assets/indexes", version.AssetIndex + ".json");
            if (File.Exists(index))
            {
                // [实现] 条件成立（File.Exists(index)）时：准备 var objects = JsonFiles.Read(index)["objects"]；准备 var icon = objects?["icons/minecraft.icns"] ?? objects?["minecraft/icons/minecraft.icns"]；准备 var hash = (string? )icon?["hash"]。
                var objects = JsonFiles.Read(index)["objects"];
                var icon = objects?["icons/minecraft.icns"] ?? objects?["minecraft/icons/minecraft.icns"];
                var hash = (string? )icon?["hash"];
                if (hash is not null && Regex.IsMatch(hash, "^[0-9a-f]{40}$"))
                {
                    // [实现] 条件成立（hash is not null && Regex.IsMatch(hash, "^[0-9a-f]{40}$")）时：准备 var iconPath = Path.Combine(service.Paths.Game, "assets/objects", hash[..2], hash)；按 DownloadService.IsValid( new( new Uri("https://localhost/unused"), iconPath, (long? )icon?["siz… 分支处理。
                    var iconPath = Path.Combine(service.Paths.Game, "assets/objects", hash[..2], hash);
                    if (DownloadService.IsValid(
                        new(
                            new Uri("https://localhost/unused"),
                            iconPath,
                            (long? )icon?["size"] ?? -1,
                            hash
                        )
                    ))
                        args.Add("-Xdock:icon=" + iconPath);
                }
            }
        }

        args.AddRange(
            LaunchRuntimePolicy.CollectorArguments(settings.GarbageCollector, java.MajorVersion)
        );
        args.AddRange(
            ArgumentText.Parse(settings.JvmArguments)
        );
        args.AddRange(
            MacWindowPolicy.Arguments(settings, service.Paths)
        );
        args.AddRange(
            LwjglUnsafePolicy.Arguments(settings, java.MajorVersion, json)
        );
        args.Add((string)json["mainClass"]!);
        args.AddRange(
            Resolve(json["arguments"]!["game"]!)
        );
        if (!args.Contains("--width"))
        {
            // [实现] 条件成立（!args.Contains("--width")）时：执行 args.Add("--width")；执行 args.Add( settings.Width.ToString() )；执行 args.Add("--height")。
            args.Add("--width");
            args.Add(
                settings.Width.ToString()
            );
            args.Add("--height");
            args.Add(
                settings.Height.ToString()
            );
        }

        if (settings.Fullscreen && !args.Contains("--fullscreen"))
            args.Add("--fullscreen");
        if (!string.IsNullOrWhiteSpace(settings.ServerAddress) && !quickPlay)
        {
            // [实现] 条件成立（!string.IsNullOrWhiteSpace(settings.ServerAddress) && !quickPlay）时：按 !Uri.TryCreate("minecraft://" + settings.ServerAddress, UriKind.Absolute, out var serverUri) 分支处理；执行 args.Add("--server")；执行 args.Add(serverUri.Host)。
            if (!Uri.TryCreate("minecraft://" + settings.ServerAddress, UriKind.Absolute, out var serverUri))
                throw new InvalidDataException("服务器地址无效。");
            args.Add("--server");
            args.Add(serverUri.Host);
            if (serverUri.Port > 0)
            {
                // [实现] 条件成立（serverUri.Port > 0）时：执行 args.Add("--port")；执行 args.Add( serverUri.Port.ToString() )。
                args.Add("--port");
                args.Add(
                    serverUri.Port.ToString()
                );
            }
        }

        args.AddRange(
            ArgumentText.Parse(settings.GameArguments)
        );
        if (session.Demo && !args.Contains("--demo"))
            args.Add("--demo");
        return new(java.Executable, args, gameDirectory);
    }

    // 兼容旧同步入口；生产 UI/CLI 使用异步入口，进程的拥有权与原生细节均位于 Port 后。
    public int StartDetached(LaunchPlan plan, GameSession session) => StartDetachedAsync(plan, session)
        .GetAwaiter()
        .GetResult()
        .Pid;
    public async Task<GameProcessHandle> StartDetachedAsync(LaunchPlan plan, GameSession session, CancellationToken ct = default)
    {
        GameLaunchAuthorization.Require(session);
        RuntimeTrace.Write("launch.authorized");
        var log = Path.Combine(
            service.Paths.Evidence,
            "detached-game-" + Guid.NewGuid()
                .ToString("N") + ".log"
        );
        var handle = await service.GameProcesses.StartAsync(
            Spec(plan, session),
            new(log, true),
            ct
        )
            .ConfigureAwait(false);
        try
        {
            JsonFiles.Write(
                Path.Combine(service.Paths.Evidence, "detached-launch.json"),
                new
                {
                    pid = handle.Pid,
                    gameProcessId = handle.Id,
                    log
                }
            );
        }
        catch (Exception ex)when (ex is IOException or UnauthorizedAccessException)
        {
            // 游戏已经创建，诊断收据失败不应丢掉逻辑句柄或诱发重复启动；操作记录仍负责保存结果。
            RuntimeTrace.Write("launch.detached-diagnostic-write-failed");
        }

        return handle;
    }

    public RunningGame Start(LaunchPlan plan, GameSession session, Action<string>? onOutput = null) => StartAsync(plan, session, onOutput)
        .GetAwaiter()
        .GetResult();
    public async Task<RunningGame> StartAsync(
        LaunchPlan plan,
        GameSession session,
        Action<string>? onOutput = null,
        CancellationToken ct = default
    )
    {
        GameLaunchAuthorization.Require(session);
        RuntimeTrace.Write("launch.authorized");
        var file = Path.Combine(
            service.Paths.Evidence,
            "game-" + Guid.NewGuid()
                .ToString("N") + ".log"
        );
        var handle = await service.GameProcesses.StartAsync(
            Spec(plan, session),
            new(file),
            ct
        )
            .ConfigureAwait(false);
        RuntimeTrace.Write("launch.started", handle.Pid);
        return new(service.GameProcesses, handle, file, onOutput);
    }

    private static ProcessSpec Spec(LaunchPlan plan, GameSession session) => new(plan.Java, plan.Arguments, plan.WorkingDirectory)
    {
        SensitiveValues = [
            session.AccessToken
        ]
    };
    // [实现] Redact 的实现：计算并返回 args.Select( (value, index) => index > 0 && args[index - 1] is "--accessToken" or "--session" o…。
    public static IReadOnlyList<string> Redact(IReadOnlyList<string> args) => args.Select(
        (value, index) => index > 0 && args[index - 1] is "--accessToken" or "--session" or "--clientToken"
            ? "<redacted>"
            : Regex.Replace(
                value,
                @"^(--(?:accessToken|session|clientToken)=).+$",
                "$1<redacted>",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            )
    )
        .ToArray();
}

/// <summary>兼容核心调用门面，只暴露逻辑身份、退出任务和日志；不再向 UI 递交 Process 对象。</summary>
public sealed class RunningGame : Pcl.Core.Contracts.Launch.IGameSession
{
    private readonly IGameProcessHost host;
    private readonly GameProcessHandle handle;
    public int Pid => handle.Pid;
    public string Id => handle.Id;
    public string LogFile { get; }

    public event Action<string>? Output;
    public Task<int> Completion { get; }
    public bool HasExited => Completion.IsCompleted || !host.IsRunning(handle);

    public RunningGame(IGameProcessHost host, GameProcessHandle handle, string file, Action<string>? onOutput)
    {
        this.host = host;
        this.handle = handle;
        LogFile = file;
        Completion = ObserveAsync(onOutput);
    }

    // 输出观察失败不能杀死游戏或漏掉退出码；日志仍由 Adapter 持久化。
    private async Task<int> ObserveAsync(Action<string>? output)
    {
        var exited = host.WaitForExitAsync(handle);
        await foreach (var line in host.ReadAsync(handle)
            .ConfigureAwait(false))
        {
            try
            {
                output?.Invoke(line.Text);
                Output?.Invoke(line.Text);
            }
            catch (Exception)
            {
            }
        }

        var code = await exited.ConfigureAwait(false);
        RuntimeTrace.Write("launch.exit", code);
        return code;
    }

    public Task StopAsync(CancellationToken ct = default) => host.TerminateAsync(handle, true, ct);
    public void Stop() => StopAsync()
        .GetAwaiter()
        .GetResult();
    public async ValueTask DisposeAsync()
    {
        await Completion.ConfigureAwait(false);
        await host.ReleaseAsync(handle)
            .ConfigureAwait(false);
    }
}
