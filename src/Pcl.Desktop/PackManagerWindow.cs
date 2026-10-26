using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pcl.Core.Contracts.Workspaces;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Installation;
using Pcl.Client;

namespace Pcl.Desktop;
// 整合包兼容：检查清单及归档路径，转换受支持组件，下载校验内容并安装到隔离实例。
public sealed class PackManagerWindow : WorkspaceWindow, IHostWorkspace
{
    private readonly WorkspaceApi api;
    private string? inspectedHandle;
    private readonly GameProfile profile;
    private readonly Action<string> installed;
    private readonly TextBox archive = new()
    {
        IsReadOnly = true,
        PlaceholderText = "选择整合包"
    };
    private readonly TextBox minecraft = new()
    {
        PlaceholderText = "Minecraft 版本（普通 ZIP 必填）"
    };
    private readonly ComboBox loader = new()
    {
        ItemsSource = new[]
        {
            "vanilla",
            "fabric",
            "quilt",
            "forge",
            "neoforge"
        },
        SelectedIndex = 0
    };
    private readonly TextBox loaderVersion = new()
    {
        PlaceholderText = "加载器版本"
    };
    private readonly TextBox apiKey = new()
    {
        PlaceholderText = "CurseForge API Key（仅本次使用）",
        PasswordChar = '●'
    };
    private readonly CheckBox optional = new()
    {
        Content = "全选可选文件"
    };
    private readonly StackPanel optionalRows = new()
    {
        Spacing = 6
    };
    private readonly Dictionary<string, CheckBox> optionalChoices = new(StringComparer.Ordinal);
    private string? inspectedDigest;
    private readonly TextBlock status = new()
    {
        Name = "PackStatus",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly ListBox missing = new()
    {
        Height = 110
    };
    private readonly Dictionary<string, string> manual = new();
    private readonly List<Button> actions = new();
    private CancellationTokenSource? cancellation;
    public bool Busy => cancellation is not null;

    public async Task SelectArchiveAsync(string path)
    {
        archive.Text = Path.GetFullPath(path);
        manual.Clear();
        optionalChoices.Clear();
        inspectedDigest = null;
        inspectedHandle = null;
        if (Busy || ViewClosing)
        {
            throw new InvalidOperationException("当前任务尚未结束。");
        }

        cancellation = new();
        using var viewActivity = ViewLifetime.Begin(cancellation.Cancel);
        foreach (var button in actions)
        {
            button.IsEnabled = false;
        }

        try
        {
            await InspectAsync();
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            foreach (var button in actions)
            {
                button.IsEnabled = true;
            }
        }
    }

    public PackManagerWindow(IWorkspaceOperations operations, GameProfile profile, Action<string> installed)
    {
        api = new WorkspaceApi(operations);
        this.profile = profile;
        this.installed = installed;
        NavigationContext = profile.Id;
        Title = "整合包 · 导入与导出";
        Width = 620;
        Height = 720;
        MinWidth = 540;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10
        };
        panel.Children.Add(
            new TextBlock
            {
                Text = "导入整合包",
                FontSize = 24
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "支持 Modrinth、CurseForge、MultiMC、HMCL、MCBBS 与普通 ZIP",
                TextWrapping = TextWrapping.Wrap
            }
        );
        panel.Children.Add(archive);
        panel.Children.Add(
            Button(
                "选择文件",
                async () =>
                {
                    var files = await StorageProvider.OpenFilePickerAsync(
                        new()
                        {
                            Title = "选择整合包",
                            FileTypeFilter = [
                                new("整合包")
                                {
                                    Patterns = [
                                        "*.mrpack",
                                        "*.zip"
                                    ]
                                }
                            ]
                        }
                    );
                    var path = files.FirstOrDefault()?.TryGetLocalPath();
                    if (path is not null)
                    {
                        archive.Text = path;
                        manual.Clear();
                        optionalChoices.Clear();
                        inspectedDigest = null;
                        inspectedHandle = null;
                        await InspectAsync();
                    }
                }
            )
        );
        var row = new Grid
        {
            ColumnDefinitions = new("2*,*,*")
        };
        row.Children.Add(minecraft);
        Grid.SetColumn(loader, 1);
        row.Children.Add(loader);
        Grid.SetColumn(loaderVersion, 2);
        row.Children.Add(loaderVersion);
        panel.Children.Add(row);
        panel.Children.Add(optional);
        panel.Children.Add(
            new ScrollViewer
            {
                Content = optionalRows,
                MaxHeight = 180
            }
        );
        optional.IsCheckedChanged += (_, _) =>
        {
            foreach (var choice in optionalChoices.Values)
                choice.IsChecked = optional.IsChecked == true;
        };
        panel.Children.Add(apiKey);
        panel.Children.Add(
            Button("检查内容", InspectAsync)
        );
        panel.Children.Add(missing);
        panel.Children.Add(
            Button(
                "为选中项目匹配本地 JAR",
                async () =>
                {
                    if (missing.SelectedItem is not CursePackFile file)
                        throw new InvalidOperationException("先选择待匹配的文件。");
                    var selected = await StorageProvider.OpenFilePickerAsync(
                        new()
                        {
                            Title = $"匹配 {file.ProjectId}/{file.FileId}（请核对官方文件页）",
                            FileTypeFilter = [
                                new("模组")
                                {
                                    Patterns = [
                                        "*.jar"
                                    ]
                                }
                            ]
                        }
                    );
                    var path = selected.FirstOrDefault()?.TryGetLocalPath();
                    if (path is not null)
                    {
                        manual[file.Key] = path;
                        status.Text = $"已手动匹配 {file.Key}。未使用 API 核对远程身份。";
                    }
                }
            )
        );
        panel.Children.Add(
            Button(
                "安装为新实例",
                async () =>
                {
                    var path = archive.Text ?? throw new InvalidOperationException("请选择整合包。");
                    RequirePreview();
                    var secret = string.IsNullOrWhiteSpace(apiKey.Text)
                        ? null
                        : await api.SecretAsync(apiKey.Text, cancellation!.Token);
                    var result = await api.CallAsync<GameProfile>(
                        "workspace.packs.install",
                        null,
                        new
                        {
                            handle = inspectedHandle,
                            options = Options(),
                            secretHandle = secret
                        },
                        cancellation!.Token,
                        Progress()
                    );
                    installed(result.Id);
                    status.Text = "已安装：" + result.Name;
                }
            )
        );
        panel.Children.Add(
            Button(
                "更新当前整合包（保留原实例）",
                async () =>
                {
                    RequirePreview();
                    var difference = await api.CallAsync<PackChanges>("workspace.packs.changes", profile.Id, ct: cancellation!.Token);
                    var changes = difference.Changed;
                    var deletions = difference.Deleted;
                    var preserve = new CheckBox
                    {
                        Content = "保留用户新增、修改和删除的文件",
                        IsChecked = true
                    };
                    var confirm = new Button
                    {
                        Content = "安装更新副本"
                    };
                    var dialog = new WorkspaceWindow
                    {
                        Title = "确认整合包更新",
                        Width = 520,
                        Height = 430,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(24),
                            Spacing = 12,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "更新会创建新实例。保留的旧模组和配置可能与新版冲突；原实例可继续使用。",
                                    TextWrapping = TextWrapping.Wrap
                                },
                                new ListBox
                                {
                                    Height = 230,
                                    ItemsSource = changes.Select(
                                        x => "保留修改：" + x
                                    )
                                        .Concat(
                                            deletions.Select(
                                                x => "保持删除：" + x
                                            )
                                        )
                                        .ToArray()
                                },
                                preserve,
                                confirm
                            }
                        }
                    };
                    confirm.Click += (_, _) => dialog.Close(preserve.IsChecked == true);
                    var choice = await dialog.PresentAsync<bool?>(this);
                    if (choice is null)
                        return;
                    var secret = string.IsNullOrWhiteSpace(apiKey.Text)
                        ? null
                        : await api.SecretAsync(apiKey.Text, cancellation!.Token);
                    var result = await api.CallAsync<GameProfile>(
                        "workspace.packs.update",
                        profile.Id,
                        new
                        {
                            handle = inspectedHandle,
                            options = Options(),
                            preserve = choice.Value,
                            secretHandle = secret
                        },
                        cancellation!.Token,
                        Progress()
                    );
                    installed(result.Id);
                    status.Text = "已创建更新副本：" + result.Name;
                }
            )
        );
        panel.Children.Add(
            new Separator()
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "导出当前实例",
                FontSize = 20
            }
        );
        var exportName = new TextBox
        {
            Text = profile.Name,
            PlaceholderText = "整合包名称"
        };
        var exportVersion = new TextBox
        {
            Text = "1.0.0",
            PlaceholderText = "整合包版本"
        };
        panel.Children.Add(exportName);
        panel.Children.Add(exportVersion);
        var saves = new CheckBox
        {
            Content = "包含存档",
            IsChecked = false
        };
        var options = new CheckBox
        {
            Content = "包含游戏选项",
            IsChecked = false
        };
        panel.Children.Add(saves);
        panel.Children.Add(options);
        var folders = new[]
        {
            "mods",
            "config",
            "resourcepacks",
            "shaderpacks",
            "datapacks",
            "scripts",
            "kubejs",
            "defaultconfigs",
            "resources"
        }.Select(
            x => new CheckBox
            {
                Content = x,
                IsChecked = true
            }
        )
            .ToArray();
        var folderPanel = new WrapPanel();
        foreach (var folder in folders)
        {
            folder.Margin = new Thickness(0, 0, 16, 6);
            folderPanel.Children.Add(folder);
        }

        panel.Children.Add(folderPanel);
        PackExportOptions ExportOptions() => new(
            exportName.Text ?? "",
            exportVersion.Text ?? "",
            saves.IsChecked == true,
            options.IsChecked == true,
            folders.Where(
                x => x.IsChecked == true
            )
                .Select(
                    x => (string)x.Content!
                )
                .ToArray()
        );
        panel.Children.Add(
            Button(
                "保存导出配置",
                async () =>
                {
                    await api.CallAsync<bool>(
                        "workspace.packs.saveExportOptions",
                        profile.Id,
                        new
                        {
                            options = ExportOptions()
                        },
                        cancellation!.Token
                    );
                    status.Text = "已保存导出配置";
                }
            )
        );
        panel.Children.Add(
            Button(
                "读取导出配置",
                async () =>
                {
                    var saved = await api.CallAsync<PackExportOptions>("workspace.packs.exportOptions", profile.Id, ct: cancellation!.Token);
                    exportName.Text = saved.Name;
                    exportVersion.Text = saved.Version;
                    saves.IsChecked = saved.IncludeSaves;
                    options.IsChecked = saved.IncludeOptions;
                    foreach (var box in folders)
                        box.IsChecked = saved.IncludedFolders is null || saved.IncludedFolders.Contains((string)box.Content!);
                    status.Text = "已读取导出配置";
                }
            )
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = "默认排除账号、日志和服务器列表。包内资源会随包复制；分享前请检查配置中的私密内容及资源分发许可。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            }
        );
        panel.Children.Add(
            Button(
                "导出 .mrpack",
                async () =>
                {
                    var destination = await StorageProvider.SaveFilePickerAsync(
                        new()
                        {
                            Title = "导出整合包",
                            SuggestedFileName = profile.Id + ".mrpack",
                            DefaultExtension = ".mrpack",
                            FileTypeChoices = [
                                new("Modrinth 整合包")
                                {
                                    Patterns = [
                                        "*.mrpack"
                                    ]
                                }
                            ]
                        }
                    );
                    var path = destination?.TryGetLocalPath();
                    if (path is null)
                        return;
                    await api.ExportAsync(
                        "workspace.packs.export",
                        profile.Id,
                        new
                        {
                            options = ExportOptions()
                        },
                        path,
                        cancellation!.Token
                    );
                    status.Text = "已导出：" + path;
                }
            )
        );
        var cancel = new Button
        {
            Content = "取消任务"
        };
        cancel.Click += (_, _) => cancellation?.Cancel();
        panel.Children.Add(cancel);
        if (operations is IPageTaskSource)
        {
            panel.Children.Add(
                Button(
                    "后台任务",
                    () => new PageTasksWindow(operations.Tasks).PresentAsync(this)
                )
            );
        }

        panel.Children.Add(status);
        Content = new ScrollViewer
        {
            Content = panel
        };
    }

    private PackImportOptions Options() => new(
        minecraft.Text,
        loader.SelectedItem?.ToString() ?? "vanilla",
        loaderVersion.Text ?? "",
        optional.IsChecked == true,
        manual,
        inspectedDigest is null
            ? null
            : optionalChoices.Where(
                x => x.Value.IsChecked == true
            )
                .Select(
                    x => x.Key
                )
                .ToArray()
    );
    private async Task InspectAsync()
    {
        var prior = optionalChoices.Where(
            x => x.Value.IsChecked == true
        )
            .Select(
                x => x.Key
            )
            .ToHashSet();
        var hadPreview = inspectedDigest is not null;
        var inspection = await api.CallAsync<PackInspection>(
            "workspace.packs.inspect",
            null,
            new
            {
                path = archive.Text ?? "",
                options = Options()with
                {
                    IncludeOptional = true,
                    SelectedOptionalFiles = null
                }
            },
            cancellation?.Token ?? CancellationToken.None
        );
        var plan = inspection.Plan;
        inspectedHandle = inspection.Handle;
        optionalRows.Children.Clear();
        optionalChoices.Clear();
        foreach (var item in plan.OptionalFiles)
        {
            var choice = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = item.Name,
                    TextWrapping = TextWrapping.Wrap
                },
                Tag = item.Id,
                IsChecked = hadPreview
                    ? prior.Contains(item.Id)
                    : optional.IsChecked == true
            };
            optionalChoices[item.Id] = choice;
            optionalRows.Children.Add(choice);
        }

        optional.IsVisible = plan.OptionalFiles.Count > 0;
        inspectedDigest = inspection.Digest;
        minecraft.Text = plan.Minecraft;
        loader.SelectedItem = plan.Loader;
        loaderVersion.Text = plan.LoaderVersion;
        void RefreshManualList()
        {
            missing.ItemsSource = plan.CurseFiles.Where(
                x => x.Required || (optionalChoices.TryGetValue("curse:" + x.Key, out var box) && box.IsChecked == true)
            )
                .ToArray();
            missing.SelectedIndex = 0;
        }

        foreach (var choice in optionalChoices.Values)
            choice.IsCheckedChanged += (_, _) => RefreshManualList();
        RefreshManualList();
        status.Text = $"{plan.Format} · {plan.Name}\nMinecraft {plan.Minecraft} / {plan.Loader} {plan.LoaderVersion}\n{plan.Overrides.Count} 个附带文件，{plan.CurseFiles.Count} 个 CurseForge 文件\n" + string.Join('\n', plan.Warnings);
    }

    // 这里只检查是否已选择预览；文件指纹与选项校验由 Host 重新执行。
    private void RequirePreview()
    {
        if (inspectedHandle is null)
        {
            throw new InvalidOperationException("请先检查整合包内容并选择可选文件。");
        }
    }

    private IProgress<VersionInstallProgress> Progress() => new Progress<VersionInstallProgress>(
        x => status.Text = $"{x.Stage}  {x.Completed}/{x.Total}"
    );
    private Button Button(string title, Func<Task> action)
    {
        var button = new Button
        {
            Content = title,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        actions.Add(button);
        button.Click += async (_, _) =>
        {
            if (cancellation is not null || ViewClosing)
                return;
            cancellation = new();
            using var viewActivity = ViewLifetime.Begin(cancellation.Cancel);
            foreach (var b in actions)
                b.IsEnabled = false;
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                status.Text = "已取消";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                foreach (var b in actions)
                    b.IsEnabled = true;
            }
        };
        return button;
    }
}
