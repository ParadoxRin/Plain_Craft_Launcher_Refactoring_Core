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
    // [实现] DependencyPackSmokeAsync 的实现：执行受异常处理保护的操作。
    private async Task DependencyPackSmokeAsync()
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
            // [实现] Click 的实现：执行 w.GetVisualDescendants() .OfType<Button>() .Single( x => x.Content?.ToString() == text ) .Raise…。
            static void Click(Window w, string text) => w.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == text
                )
                .RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
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

            // [实现] Show 的实现：准备 Exception? error = null；执行 w.Opened += async (_, _) => { try { await Task.Delay(250, ct); await action(); } catch (Excepti…；执行 await w.ShowDialog(this)。
            async Task Show(Window w, Func<Task> action)
            {
                Exception? error = null;
                w.Opened += async (_, _) =>
                {
                    // [实现] 回调触发时：执行受异常处理保护的操作。
                    try
                    {
                        // [实现] 在异常保护范围内：执行 await Task.Delay(250, ct)；执行 await action()。
                        await Task.Delay(250, ct);
                        await action();
                    }
                    catch (Exception ex)
                    {
                        // [实现] 捕获 (Exception ex) 后：执行 error = ex。
                        error = ex;
                    }
                    finally
                    {
                        // [实现] 无论成功或失败，退出前：执行 w.Close()。
                        w.Close();
                    }
                };
                await w.ShowDialog(this);
                if (error is not null)
                    throw error;
            }

            using var quiltService = service.ForVersion("1.20.1");
            using var loader = new FabricService(quiltService, "accept-quilt-ui", "quilt");
            var quilt = await loader.InstallAsync("0.27.1", ct: ct);
            var search = Bootstrap.WorkspacePages.Search(
                this,
                service,
                quilt,
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
                        "Quilt UI install failed: " + Named<TextBlock>(search, "UnifiedStatus")
                            .Text
                    );
                    await Capture(search, "quilt-search-installed");
                }
            );
            foreach (var profile in new[]
            {
                quilt,
                InstanceRegistry.ByFolder(service, "accept-neoforge")
            }

            )
            {
                // [实现] 对 new[] { quilt, InstanceRegistry.ByFolder(service, "accept-neoforge") } 中的每一项：准备 var window = Bootstrap.WorkspacePages.Local(this,service,profile)；执行 await Show( window, async () => { Click(window, "检查依赖关系"); while (window.Busy) await Task.Delay…。
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
                            status.Text?.StartsWith("已检查当前启用模组") == true,
                            "Dependency GUI failed: " + status.Text
                        );
                        status.BringIntoView();
                        await Capture(window, profile.Loader + "-dependency-check");
                    }
                );
            }

            var packResults = new List<string>();
            foreach (var file in new[]
            {
                "custom-multimc.zip",
                "custom-hmcl.zip"
            }

            )
            {
                // [实现] 对 new[] { "custom-multimc.zip", "custom-hmcl.zip" } 中的每一项：准备 GameProfile? installed = null；准备 var window = new PackManagerWindow( service, quilt, p => installed = p )；执行 await Show( window, async () => { await window.SelectArchiveAsync( Path.Combine(paths.Root, file) ); await…。
                GameProfile? installed = null;
                var window = Bootstrap.WorkspacePages.Pack(
                    this,
                    service,
                    quilt,
                    p => installed = p
                );
                await Show(
                    window,
                    async () =>
                    {
                        // [实现] 回调触发时：执行 await window.SelectArchiveAsync( Path.Combine(paths.Root, file) )；执行 await Capture(window, file + "-preview")；执行 Click(window, "安装为新实例")。
                        await window.SelectArchiveAsync(
                            Path.Combine(paths.Root, file)
                        );
                        await Capture(window, file + "-preview");
                        Click(window, "安装为新实例");
                        while (window.Busy)
                            await Task.Delay(100, ct);
                        Check(
                            installed is not null,
                            "Pack UI install failed: " + Named<TextBlock>(window, "PackStatus")
                                .Text
                        );
                        Check(
                            InstanceRegistry.Load(service, installed!)
                                .Libraries.Any(
                                x => x.Path.Contains("pcl-custom") && DownloadService.IsValid(x)
                            ),
                            "Custom library missing after GUI install"
                        );
                        packResults.Add(installed!.Id);
                        Named<TextBlock>(window, "PackStatus")
                            .BringIntoView();
                        await Capture(window, file + "-installed");
                    }
                );
            }

            JsonFiles.Write(
                Path.Combine(paths.Evidence, "dependency-pack-ui-result.json"),
                new
                {
                    status = "passed",
                    platform = service.Platform,
                    anonymous = true,
                    quilt = quilt.Id,
                    packResults,
                    gameStarted = false,
                    manualVisualAcceptance = false
                }
            );
            Console.WriteLine("ACCEPTANCE_014_PASSED");
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "dependency-pack-ui-result.json"), new { status =…；执行 Console.Error.WriteLine(ex)。
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "dependency-pack-ui-result.json"),
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
