using Newtonsoft.Json.Linq;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Pages;
public sealed partial class PageExecutionAdapter
{
    // 复用原设置白名单、原子文件写入与导入备份；先验证整个请求，再写发生变化的配置段。
    private object SettingsSnapshot() => new
    {
        Appearance = new AppearanceService(source.Paths).Load(),
        Launch = new LauncherSettingsStore(source.Paths).Global,
        Network = NetworkOptions.Load(source.Paths),
        Hook = LaunchHookSettings.Load(source.Paths),
        Presets = new HomePresets(source.Paths).List()
            .Select(
                x => new
                {
                    x.Id,
                    x.Name
                }
            )
            .ToArray()
    };
    // 跨序列化器的 1 与 1.0 表示相同配置，按配置类型规范化后再作并发比较。
    private static JToken NormalizeSetting(string name, JToken value) => name switch
    {
        "Appearance" => JToken.FromObject(
            value.ToObject<AppearanceSettings>()!
        ),
        "Launch" => JToken.FromObject(
            value.ToObject<LaunchSettings>()!
        ),
        "Network" => JToken.FromObject(
            value.ToObject<NetworkOptions>()!
        ),
        "Hook" => JToken.FromObject(
            value.ToObject<LaunchHookSettings>()!
        ),
        _ => throw new OperationInstallException("SETTINGS_FIELD_INVALID")
    };
    private async Task<object> ExecuteSettingsAsync(string id, JObject args, string method, CancellationToken ct)
    {
        var appearance = new AppearanceService(source.Paths);
        switch (method)
        {
            case "workspace.settings.networkTargets":
                return NetworkDiagnosticsService.ConfiguredTargets(source.Paths);
            case "workspace.settings.probe":
                using (var probe = new NetworkDiagnosticsService(
                    NetworkOptions.Load(source.Paths)
                ))
                {
                    var target = args["target"]?.ToObject<NetworkProbeTarget>() ?? throw new OperationInstallException("SETTINGS_INPUT_INVALID");
                    return await probe.ProbeAsync(target, ct);
                }

            case "workspace.settings.cachePreview":
                var candidates = new TemporaryDownloadCache(source.Paths).Preview(ct: ct)
                    .ToArray();
                Save(id, "cache-preview", candidates);
                return new
                {
                    Handle = id,
                    Items = candidates
                };
            case "workspace.settings.cacheTrash":
                return new TemporaryDownloadCache(source.Paths).ListTrash();
            case "workspace.settings.cacheMove":
                var preview = Read<TemporaryCacheFile[]>((string? )args["handle"] ?? "", "cache-preview");
                var selected = args["paths"]?.ToObject<string[]>() ?? [];
                if (selected.Length == 0 || selected.Any(
                    p => !preview.Any(
                        x => x.RelativePath == p
                    )
                ))
                {
                    throw new OperationInstallException("WORKSPACE_SELECTION_INVALID");
                }

                return new TemporaryDownloadCache(source.Paths).MoveToTrash(
                    preview.Where(
                        x => selected.Contains(x.RelativePath)
                    )
                        .ToArray(),
                    ct
                );
            case "workspace.settings.cacheRestore":
                return new TemporaryDownloadCache(source.Paths).Restore((string? )args["id"] ?? "", ct);
            case "workspace.settings.read":
                return SettingsSnapshot();
            case "workspace.settings.save":
                var current = JObject.FromObject(
                    SettingsSnapshot()
                );
                var changes = args["changes"] as JObject ?? throw new OperationInstallException("SETTINGS_INPUT_INVALID");
                var expected = args["expected"] as JObject;
                foreach (var entry in changes.Properties())
                {
                    if (entry.Name is not ("Appearance" or "Launch" or "Network" or "Hook"))
                    {
                        throw new OperationInstallException("SETTINGS_FIELD_INVALID");
                    }

                    // 防止两个设置窗口基于旧快照覆盖彼此已经保存的修改。
                    if (expected?[entry.Name] is { } old && !JToken.DeepEquals(
                        NormalizeSetting(entry.Name, old),
                        current[entry.Name]
                    ))
                    {
                        throw new OperationInstallException("SETTINGS_CHANGED_RELOAD");
                    }
                }

                var a = changes["Appearance"]?.ToObject<AppearanceSettings>();
                var l = changes["Launch"]?.ToObject<LaunchSettings>();
                var n = changes["Network"]?.ToObject<NetworkOptions>();
                var h = changes["Hook"]?.ToObject<LaunchHookSettings>();
                a?.Validate();
                l?.Validate();
                n?.Validate();
                h?.Validate();
                if (a is not null)
                {
                    foreach (var path in new[]
                    {
                        a.Background,
                        a.Music,
                        a.TitleImage
                    }

                    )
                    {
                        if (path.Length > 0 && !File.Exists(path))
                        {
                            throw new FileNotFoundException("本地媒体文件不存在。", path);
                        }
                    }

                    foreach (var path in new[]
                    {
                        a.BackgroundDirectory,
                        a.MusicDirectory
                    }

                    )
                    {
                        if (path.Length > 0 && !Directory.Exists(path))
                        {
                            throw new DirectoryNotFoundException("媒体文件夹不存在。");
                        }
                    }
                }

                ct.ThrowIfCancellationRequested();
                // 提交区间不运行网络和用户程序；中途故障由工作区收据拒绝盲目重放。
                if (l is not null)
                {
                    new LauncherSettingsStore(source.Paths).SaveGlobal(l);
                }

                if (n is not null)
                {
                    DownloadTasks.For(source.Paths)
                        .Configure(n);
                }

                if (h is not null)
                {
                    h.Save(source.Paths);
                }

                if (a is not null)
                {
                    appearance.Save(a);
                }

                return SettingsSnapshot();
            case "workspace.settings.export":
                var output = WorkspaceOutput(id, "PCL-Mac-settings.json");
                appearance.Export(output);
                return RegisterWorkspaceExport(id, output);
            case "workspace.settings.import":
                appearance.Import((string? )args["path"] ?? "");
                return SettingsSnapshot();
            case "workspace.settings.backups":
                var directory = Path.Combine(source.Paths.Root, "settings-backups");
                Directory.CreateDirectory(directory);
                return directory;
            case "workspace.settings.home":
                return await appearance.ReadHomeAsync(ct);
            case "workspace.settings.example":
                var example = Path.Combine(
                    source.Paths.Root,
                    "home-example-" + Id(id) + ".json"
                );
                JsonFiles.Write(
                    example,
                    new HomeDocument(
                        "我的主页",
                        "编辑正文，并通过 links 添加网页或启动器导航。",
                        [
                            new HomeLink("下载游戏", "downloads", "navigate")
                        ]
                    )
                );
                return example;
            case "workspace.settings.savePreset":
                var page = await appearance.ReadHomeAsync(ct);
                var presetEntry = new HomePresets(source.Paths).Save(page.Title, page);
                appearance.Save(
                    appearance.Load()with
                    {
                        HomeMode = "preset",
                        HomePreset = presetEntry.Id
                    }
                );
                return SettingsSnapshot();
            case "workspace.settings.removePreset":
                var value = appearance.Load();
                if (!value.HomePreset.StartsWith("local-", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("内置预设不删除，请先保存自定义预设。");
                }

                new HomePresets(source.Paths).Remove(value.HomePreset);
                appearance.Save(
                    value with
                    {
                        HomePreset = "welcome"
                    }
                );
                return SettingsSnapshot();
            default:
                throw new OperationInstallException("WORKSPACE_METHOD_UNKNOWN");
        }
    }
}
