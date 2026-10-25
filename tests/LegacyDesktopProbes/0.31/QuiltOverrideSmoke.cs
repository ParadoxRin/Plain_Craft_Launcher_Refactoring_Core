using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Newtonsoft.Json.Linq;
using Pcl.Core;

namespace Pcl.Desktop;
// [实现] 桌面验收：在隔离数据目录驱动实际界面流程，校验结果并写入证据；测试动作不等同人工视觉或游戏验收。
public partial class MainWindow
{
    /// <summary>真实下载、界面依赖检查、批量预览保护和包往返验收；只操作 --data-root 指定的隔离目录。</summary>
    private async Task QuiltOverrideSmokeAsync()
    {
        try
        {
            // [实现] 在异常保护范围内：准备 var deadline = new CancellationTokenSource( TimeSpan.FromMinutes(10) )；准备 var ct = deadline.Token；执行 static void Check(bool ok, string error) { if (!ok) throw new InvalidOperationException(error);…。
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromMinutes(10)
            );
            var ct = deadline.Token;
            // [实现] Check 的实现：按 !ok 分支处理。
            static void Check(bool ok, string error)
            {
                if (!ok)
                    throw new InvalidOperationException(error);
            }

            // [实现] Named 的实现：计算并返回 w.GetVisualDescendants() .OfType<T>() .Single( x => x.Name == name )。
            static T Named<T>(Window w, string name)
            where T : Control => w.GetVisualDescendants()
                .OfType<T>()
                .Single(
                    x => x.Name == name
                );
            // [实现] Click 的实现：执行 w.GetVisualDescendants() .OfType<Button>() .Single( x => x.Content?.ToString() == title ) .Rais…。
            static void Click(Window w, string title) => w.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == title
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            // [实现] Capture 的实现：执行 await Task.Delay(200, ct)；准备 var image = new RenderTargetBitmap( new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height), n…；执行 image.Render(w)。
            async Task Capture(Window w, string name)
            {
                await Task.Delay(200, ct);
                using var image = new RenderTargetBitmap(
                    new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height),
                    new Vector(96, 96)
                );
                image.Render(w);
                image.Save(
                    Path.Combine(paths.Evidence, name + ".png"),
                    PngBitmapEncoderOptions.Default
                );
            }

            // [实现] Show 的实现：准备 Exception? failure = null；执行 w.Opened += async (_, _) => { try { await Task.Delay(200, ct); await action(); } catch (Excepti…；执行 await w.ShowDialog(this)。
            async Task Show(Window w, Func<Task> action)
            {
                Exception? failure = null;
                w.Opened += async (_, _) =>
                {
                    // [实现] 回调触发时：执行受异常处理保护的操作。
                    try
                    {
                        // [实现] 在异常保护范围内：执行 await Task.Delay(200, ct)；执行 await action()。
                        await Task.Delay(200, ct);
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

            // 使用固定官方加载器版本及真实 Modrinth 发布，客户端文件可复用经过哈希校验的缓存。
            using var quiltService = service.ForVersion("1.20.1");
            using var loader = new FabricService(quiltService, "accept-overrides", "quilt");
            var profile = await loader.InstallAsync("0.27.1", ct: ct);
            // [实现] Override 的实现：执行 JsonFiles.Write( Path.Combine(profile.GameDirectory, QuiltDependencyOverrides.RelativePath), ne…。
            void Override(int java) => JsonFiles.Write(
                Path.Combine(profile.GameDirectory, QuiltDependencyOverrides.RelativePath),
                new
                {
                    schema_version = 1,
                    overrides = new[]
                    {
                        new
                        {
                            id = "quilted_fabric_api",
                            depends = new
                            {
                                add = new
                                {
                                    id = "java",
                                    versions = ">=" + java
                                }
                            }
                        }
                    }
                }
            );
            Override(17);
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
            await Show(
                search,
                async () =>
                {
                    // [实现] 回调触发时：执行 Named<TextBox>(search, "UnifiedQuery") .Text = "QFAPI"；执行 Click(search, "搜索")；在 search.Busy 条件下循环。
                    Named<TextBox>(search, "UnifiedQuery")
                        .Text = "QFAPI";
                    Click(search, "搜索");
                    while (search.Busy)
                        await Task.Delay(100, ct);
                    var list = Named<ListBox>(search, "UnifiedResults");
                    list.SelectedItem = list.Items.OfType<ResourceSearchHit>()
                        .Single(
                            x => x.Modrinth?.Id == "qvIfYCYJ"
                        );
                    while (search.Busy)
                        await Task.Delay(100, ct);
                    var versions = Named<ComboBox>(search, "UnifiedVersions");
                    versions.SelectedItem = versions.Items.OfType<ResourceRelease>()
                        .Single(
                            x => x.Id == "s8dfz9Xa"
                        );
                    Click(search, "安装所选版本（含前置）");
                    while (search.Busy)
                        await Task.Delay(100, ct);
                    Check(
                        Named<TextBlock>(search, "UnifiedStatus")
                            .Text?.StartsWith("已安装并校验") == true,
                        "Live QFAPI installation failed"
                    );
                    await Capture(search, "override-install");
                }
            );
            // 同一界面分别显示有效覆盖和无解覆盖，防止依赖检查按钮吞掉配置错误。
            var window = Bootstrap.WorkspacePages.Local(this, service, profile);
            await Show(
                window,
                async () =>
                {
                    // [实现] 回调触发时：执行 Click(window, "检查依赖关系")；在 window.Busy 条件下循环；准备 var status = Named<TextBlock>(window, "LocalDependencyStatus")。
                    Click(window, "检查依赖关系");
                    while (window.Busy)
                        await Task.Delay(100, ct);
                    var status = Named<TextBlock>(window, "LocalDependencyStatus");
                    Check(
                        status.Text?.Contains("已应用 Quilt 依赖覆盖") == true,
                        "Override warning missing in GUI"
                    );
                    status.BringIntoView();
                    await Capture(window, "override-valid");
                    Override(99);
                    Click(window, "检查依赖关系");
                    while (window.Busy)
                        await Task.Delay(100, ct);
                    Check(
                        status.Text?.Contains("无法同时满足") == true,
                        "Impossible override accepted by GUI"
                    );
                    await Capture(window, "override-conflict");
                    Override(17);
                    Click(window, "检查依赖关系");
                    while (window.Busy)
                        await Task.Delay(100, ct);
                    Check(
                        status.Text?.Contains("已应用 Quilt 依赖覆盖") == true,
                        "Restored override rejected"
                    );
                }
            );
            // 使用相同真实发布复现批量预览/提交通路；预览后改配置必须被拦截，重新预览才能提交。
            using var catalog = new ResourceCatalog();
            var release = (await catalog.VersionsAsync("qvIfYCYJ", ResourceKind.Mod, "1.20.1", "quilt", ct)).Single(
                x => x.Id == "s8dfz9Xa"
            );
            var local = new LocalResourceService(profile.GameDirectory, "quilt", "1.20.1");
            var update = new LocalResourceUpdate(
                local.Scan()
                    .Single(
                        x => x.ModIds.Contains("quilted_fabric_api")
                    ),
                release
            );
            var updater = new ResourceBatchUpdater(profile.GameDirectory, "quilt", "1.20.1");
            using (var prepared = await updater.PrepareAsync(
                [
                    update
                ],
                catalog,
                service.Downloads,
                ct: ct
            ))
            {
                // [实现] 本段流程：执行 Override(18)；准备 bool rejected = false；执行受异常处理保护的操作。
                Override(18);
                bool rejected = false;
                try
                {
                    // [实现] 在异常保护范围内：执行 updater.Apply(prepared, ct)。
                    updater.Apply(prepared, ct);
                }
                catch (IOException)
                {
                    // [实现] 捕获 (IOException) 后：执行 rejected = true。
                    rejected = true;
                }

                Check(rejected, "Changed override was overwritten or ignored");
                Override(17);
            }

            using (var prepared = await updater.PrepareAsync(
                [
                    update
                ],
                catalog,
                service.Downloads,
                ct: ct
            ))
                updater.Apply(prepared, ct);
            Check(
                local.ValidateEnabledResources()
                    .Any(
                        x => x.Contains("已应用")
                    ),
                "Batch lost override context"
            );
            // 导出后的 config 随包重新安装，证明覆盖规则在包往返中保留，且安装时确实参与校验。
            var archive = Path.Combine(paths.Root, "override-roundtrip.mrpack");
            await PortableModpackService.ExportAsync(
                profile,
                archive,
                new("Quilt override acceptance"),
                ct
            );
            GameProfile? imported = null;
            var packs = Bootstrap.WorkspacePages.Pack(
                this,
                service,
                profile,
                p => imported = p
            );
            await Show(
                packs,
                async () =>
                {
                    // [实现] 回调触发时：执行 await packs.SelectArchiveAsync(archive)；执行 Click(packs, "安装为新实例")；在 packs.Busy 条件下循环。
                    await packs.SelectArchiveAsync(archive);
                    Click(packs, "安装为新实例");
                    while (packs.Busy)
                        await Task.Delay(100, ct);
                    Check(
                        imported is not null,
                        "Pack import failed: " + Named<TextBlock>(packs, "PackStatus")
                            .Text
                    );
                    Check(
                        File.ReadAllText(
                            Path.Combine(imported!.GameDirectory, QuiltDependencyOverrides.RelativePath)
                        ) == File.ReadAllText(
                            Path.Combine(profile.GameDirectory, QuiltDependencyOverrides.RelativePath)
                        ),
                        "Override changed in pack roundtrip"
                    );
                    Check(
                        new LocalResourceService(imported.GameDirectory, "quilt", "1.20.1").ValidateEnabledResources()
                            .Any(
                                x => x.Contains("已应用")
                            ),
                        "Imported override unused"
                    );
                    Named<TextBlock>(packs, "PackStatus")
                        .BringIntoView();
                    await Capture(packs, "override-pack-imported");
                }
            );
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "quilt-overrides-ui-result.json"),
                new
                {
                    status = "passed",
                    anonymous = true,
                    realRelease = release.Id,
                    sourceInstance = profile.Id,
                    importedInstance = imported!.Id,
                    batchContextChangeRejected = true,
                    batchApplied = true,
                    gameStarted = false,
                    manualVisualAcceptance = false
                }
            );
            Console.WriteLine("ACCEPTANCE_015_PASSED");
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "quilt-overrides-ui-result.json"), new { status =…；执行 Console.Error.WriteLine(ex)。
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "quilt-overrides-ui-result.json"),
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
