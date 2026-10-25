using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core;

namespace Pcl.Desktop;
// 桌面验收：在隔离数据目录驱动实际界面流程，校验结果并写入证据；测试动作不等同人工视觉或游戏验收。
public partial class MainWindow
{
    // [实现] SearchBackupSmokeAsync 的实现：执行受异常处理保护的操作。
    private async Task SearchBackupSmokeAsync()
    {
        try
        {
            // [实现] 在异常保护范围内：准备 var timeout = new CancellationTokenSource( TimeSpan.FromMinutes(15) )；准备 var ct = timeout.Token；执行 static void Check(bool value, string message) { if (!value) throw new InvalidOperationException…。
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(15)
            );
            var ct = timeout.Token;
            // [实现] Check 的实现：按 !value 分支处理。
            static void Check(bool value, string message)
            {
                if (!value)
                    throw new InvalidOperationException(message);
            }

            // [实现] Named 的实现：计算并返回 w.GetVisualDescendants() .OfType<T>() .Single( x => x.Name == name )。
            static T Named<T>(Window w, string name)
            where T : Control => w.GetVisualDescendants()
                .OfType<T>()
                .Single(
                    x => x.Name == name
                );
            // [实现] Click 的实现：执行 w.GetVisualDescendants() .OfType<Button>() .Single( x => x.Content?.ToString() == text ) .Raise…。
            static void Click(Window w, string text) => w.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == text
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            // [实现] Wait 的实现：在 w.Busy 条件下循环。
            async Task Wait(UnifiedSearchWindow w)
            {
                while (w.Busy)
                    await Task.Delay(100, ct);
            }

            // [实现] Capture 的实现：执行 await Task.Delay(200, ct)；准备 var bmp = new RenderTargetBitmap( new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height), new…；执行 bmp.Render(w)。
            async Task Capture(Window w, string name)
            {
                await Task.Delay(200, ct);
                using var bmp = new RenderTargetBitmap(
                    new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height),
                    new Vector(96, 96)
                );
                bmp.Render(w);
                bmp.Save(
                    Path.Combine(paths.Evidence, name + ".png"),
                    PngBitmapEncoderOptions.Default
                );
            }

            // [实现] InWindow 的实现：准备 Exception? failure = null；执行 w.Opened += async (_, _) => { try { await Task.Delay(300, ct); await action(); } catch (Excepti…；执行 await w.ShowDialog(this)。
            async Task InWindow(Window w, Func<Task> action)
            {
                Exception? failure = null;
                w.Opened += async (_, _) =>
                {
                    // [实现] 回调触发时：执行受异常处理保护的操作。
                    try
                    {
                        // [实现] 在异常保护范围内：执行 await Task.Delay(300, ct)；执行 await action()。
                        await Task.Delay(300, ct);
                        await action();
                    }
                    catch (Exception ex)
                    {
                        // [实现] 捕获 (Exception ex) 后：执行 failure = ex。
                        failure = ex;
                    }
                    finally
                    {
                        // [实现] 无论成功或失败，退出前：执行 w.Close()。
                        w.Close();
                    }
                };
                await w.ShowDialog(this);
                if (failure is not null)
                    throw failure;
            }

            Console.WriteLine("ACCEPTANCE_013_VERIFY_OFFICIAL_GAME_FILES");
            await service.InstallAsync(cancellationToken: ct);
            var manager = new InstanceManager(service);
            var profile = await manager.CopyAsync(
                manager.List()
                    .Single(
                        x => x.Id == "1.21.1"
                    ),
                "0.13 无账号测试",
                ct
            );
            using var catalog = new ResourceCatalog();
            var second = await catalog.SearchAsync(ResourceKind.ResourcePack, "", "1.21.1", "vanilla", 20, ct: ct);
            ResourceRelease? release = null;
            ResourceProject? project = null;
            foreach (var candidate in second.Projects)
            {
                // [实现] 对 second.Projects 中的每一项：准备 var releases = await catalog.VersionsAsync(candidate.Id, ResourceKind.ResourcePack, "1.21.1", c…；执行 release = releases.FirstOrDefault( x => x.File.Size < 8 * 1024 * 1024 && x.Dependencies.Count =…；按 release is not null 分支处理。
                var releases = await catalog.VersionsAsync(candidate.Id, ResourceKind.ResourcePack, "1.21.1", ct: ct);
                release = releases.FirstOrDefault(
                    x => x.File.Size < 8 * 1024 * 1024 && x.Dependencies.Count == 0
                );
                if (release is not null)
                {
                    // [实现] 条件成立（release is not null）时：执行 project = candidate；执行 break;。
                    project = candidate;
                    break;
                }
            }

            if (release is null || project is null)
                throw new InvalidOperationException("No bounded real download fixture found");
            await new ResourceInstaller(profile.GameDirectory, service.Downloads, profile.Loader, profile.GameVersion).InstallAsync(
                new(
                    ResourceKind.ResourcePack,
                    release.ProjectId,
                    [
                        release
                    ]
                ),
                ct: ct
            );
            Console.WriteLine("ACCEPTANCE_013_REAL_RESOURCE_DOWNLOADED " + release.Id);
            string[] firstIds = [], secondIds = [];
            bool crossPage = false, backEnriched = false, filterReset = false;
            var search = Bootstrap.WorkspacePages.Search(
                this,
                service,
                profile,
                _ =>
                {
                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                },
                Open
            );
            await InWindow(
                search,
                async () =>
                {
                    // [实现] 回调触发时：执行 Named<ComboBox>(search, "UnifiedKind") .SelectedIndex = 2；准备 var list = Named<ListBox>(search, "UnifiedResults")；执行 Click(search, "搜索")。
                    Named<ComboBox>(search, "UnifiedKind")
                        .SelectedIndex = 2;
                    var list = Named<ListBox>(search, "UnifiedResults");
                    Click(search, "搜索");
                    await Wait(search);
                    firstIds = list.Items.OfType<ResourceSearchHit>()
                        .Select(
                            x => x.Identity
                        )
                        .ToArray();
                    Check(
                        firstIds.Contains("modrinth:" + project.Id),
                        "Installed local resource absent"
                    );
                    Click(search, "下一页");
                    await Wait(search);
                    secondIds = list.Items.OfType<ResourceSearchHit>()
                        .Select(
                            x => x.Identity
                        )
                        .ToArray();
                    Check(
                        !firstIds.Intersect(secondIds)
                            .Any(),
                        "Live pagination repeated identity"
                    );
                    crossPage = Named<TextBlock>(search, "UnifiedStatus")
                        .Text?.Contains("跨页重复") == true;
                    Check(crossPage, "Live second-page fixture moved or dedup missing");
                    await Capture(search, "search-second-page");
                    Click(search, "上一页");
                    await Wait(search);
                    var merged = list.Items.OfType<ResourceSearchHit>()
                        .Single(
                            x => x.Identity == "modrinth:" + project.Id
                        );
                    backEnriched = merged.Sources.Contains("本地") && merged.Sources.Contains("Modrinth");
                    Check(backEnriched, "Remote data not merged into cached earlier page");
                    await Capture(search, "search-cached-first-page");
                    Named<TextBox>(search, "UnifiedQuery")
                        .Text = project.Slug;
                    filterReset = list.ItemCount == 0;
                    Check(filterReset, "Query edits retained stale results");
                    Click(search, "搜索");
                    await Wait(search);
                    list.SelectedItem = list.Items.OfType<ResourceSearchHit>()
                        .Single(
                            x => x.Identity == "modrinth:" + project.Id
                        );
                    await Wait(search);
                    Check(
                        Named<ComboBox>(search, "UnifiedVersions")
                            .SelectedItem is ResourceRelease,
                        "Version selection failed"
                    );
                    Click(search, "安装所选版本（含前置）");
                    await Wait(search);
                    Check(
                        Named<TextBlock>(search, "UnifiedStatus")
                            .Text?.StartsWith("已安装并校验") == true,
                        "UI resource install failed"
                    );
                    await Capture(search, "search-installed");
                    Named<CheckBox>(search, "UnifiedOnline")
                        .IsChecked = false;
                    Check(list.ItemCount == 0, "Online filter retained stale page");
                    Named<TextBox>(search, "UnifiedQuery")
                        .Text = "";
                    Click(search, "搜索");
                    await Wait(search);
                    Check(
                        list.Items.OfType<ResourceSearchHit>()
                            .Any(
                                x => x.Sources.SequenceEqual(
                                    new[]
                                    {
                                        "本地"
                                    }
                                )
                            ),
                        "Offline management unavailable"
                    );
                    await Capture(search, "search-offline");
                }
            );
            // A save fixture verifies byte preservation; this does not claim gameplay or world acceptance.
            var save = Path.Combine(profile.GameDirectory, "saves/校验世界/level.dat");
            Directory.CreateDirectory(
                Path.GetDirectoryName(save)!
            );
            File.WriteAllText(save, "0.13 backup byte-preservation fixture");
            manager.Settings.SavePreferences(
                profile.GameDirectory,
                new("0.13 备份", "原文件保留", true)
            );
            manager.Settings.SavePatch(
                profile.GameDirectory,
                new()
                {
                    Width = 1440
                }
            );
            var backup = Path.Combine(
                paths.Evidence,
                "verified-instance-" + Guid.NewGuid()
                    .ToString("N") + ".zip"
            );
            await manager.BackupAsync(profile, backup, ct);
            var verified = await InstanceBackupArchive.VerifyAsync(backup, ct);
            var workspace = Bootstrap.WorkspacePages.Instances(this, service, profile.Id);
            await InWindow(
                workspace,
                async () =>
                {
                    // [实现] 回调触发时：执行 await workspace.VerifyBackupFileAsync(backup)；执行 Check( Named<TextBlock>(workspace, "InstanceStatus") .Text?.Contains("SHA-256") == true, "Backu…；执行 await Capture(workspace, "backup-verification")。
                    await workspace.VerifyBackupFileAsync(backup);
                    Check(
                        Named<TextBlock>(workspace, "InstanceStatus")
                            .Text?.Contains("SHA-256") == true,
                        "Backup verification UI result missing"
                    );
                    await Capture(workspace, "backup-verification");
                }
            );
            var restored = await manager.RestoreBackupAsync(backup, ct: ct);
            var restoredSave = Path.Combine(restored.GameDirectory, "saves/校验世界/level.dat");
            Check(
                File.ReadAllBytes(save)
                    .SequenceEqual(
                        File.ReadAllBytes(restoredSave)
                    ),
                "Restored save changed"
            );
            var restoredStore = new ResourceInstaller(restored.GameDirectory, service.Downloads, restored.Loader, restored.GameVersion);
            var restoredResource = restoredStore.List()
                .Single(
                    x => x.ProjectId == release.ProjectId
                );
            var resourcePath = Path.Combine(restored.GameDirectory, "resourcepacks", restoredResource.FileName);
            Check(
                DownloadService.IsValid(
                    new(release.File.Url, resourcePath, restoredResource.Size, restoredResource.Sha1)
                ),
                "Restored real resource damaged"
            );
            Check(
                manager.Settings.Preferences(restored.GameDirectory)
                    .Favorite && manager.Settings.Effective(restored.GameDirectory)
                    .Width == 1440,
                "Preferences/patch not restored"
            );
            Check(
                File.Exists(save),
                "Original instance changed"
            );
            var restoredWindow = Bootstrap.WorkspacePages.Instances(this, service, restored.Id);
            await InWindow(
                restoredWindow,
                async () =>
                {
                    // [实现] 回调触发时：执行 await Capture(restoredWindow, "restored-instance")；执行 await Task.CompletedTask。
                    await Capture(restoredWindow, "restored-instance");
                    await Task.CompletedTask;
                }
            );
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "search-backup-result.json"),
                new
                {
                    status = "passed",
                    completed = DateTimeOffset.UtcNow,
                    platform = service.Platform,
                    anonymous = true,
                    crossPageDedup = crossPage,
                    cachedBackEnriched = backEnriched,
                    filterReset,
                    firstIds,
                    secondIds,
                    resource = new
                    {
                        project.Id,
                        project.Slug,
                        releaseId = release.Id,
                        release.File
                    },
                    backup = verified,
                    restoredInstance = restored.Id,
                    saveFixtureBytesPreserved = true,
                    realResourceHashPreserved = true,
                    settingsRestored = true,
                    originalPreserved = true,
                    gameLaunchAttempted = false,
                    worldAcceptance = false
                }
            );
            Console.WriteLine("ACCEPTANCE_013_PASSED");
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "search-backup-result.json"), new { status = "fai…；执行 Console.Error.WriteLine(ex)。
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "search-backup-result.json"),
                new
                {
                    status = "failed",
                    error = ex.ToString()
                }
            );
            Console.Error.WriteLine(ex);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：执行 Close()。
            Close();
        }
    }
}
