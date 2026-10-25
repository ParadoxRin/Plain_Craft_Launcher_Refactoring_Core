using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Newtonsoft.Json.Linq;
using Pcl.Core;

namespace Pcl.Desktop;
// 桌面验收：在隔离数据目录驱动实际界面流程，校验结果并写入证据；测试动作不等同人工视觉或游戏验收。
public partial class MainWindow
{
    // [实现] CompletionFeatureSmokeAsync 的实现：准备 var cases = new List<object>()；执行受异常处理保护的操作。
    private async Task CompletionFeatureSmokeAsync()
    {
        var cases = new List<object>();
        try
        {
            // [实现] 在异常保护范围内：准备 var timeout = new CancellationTokenSource( TimeSpan.FromMinutes(35) )；准备 var ct = timeout.Token；准备 var inputIndex = Array.IndexOf(Program.Arguments, "--acceptance-inputs")。
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMinutes(35)
            );
            var ct = timeout.Token;
            var inputIndex = Array.IndexOf(Program.Arguments, "--acceptance-inputs");
            if (inputIndex < 0)
                throw new ArgumentException("Missing acceptance inputs");
            var input = Program.Arguments[inputIndex + 1];
            // [实现] Wait 的实现：在 !done() 条件下循环。
            async Task Wait(Func<bool> done)
            {
                while (!done())
                    await Task.Delay(150, ct);
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
            // [实现] Capture 的实现：准备 var bmp = new RenderTargetBitmap( new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height), new…；执行 bmp.Render(w)；执行 bmp.Save( Path.Combine(paths.Evidence, name + ".png"), PngBitmapEncoderOptions.Default )。
            void Capture(Window w, string name)
            {
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

            // [实现] InWindow 的实现：准备 Exception? error = null；执行 w.Opened += async (_, _) => { try { await Task.Delay(500, ct); await action(); } catch (Excepti…；执行 await w.ShowDialog(this)。
            async Task InWindow(Window w, Func<Task> action)
            {
                Exception? error = null;
                w.Opened += async (_, _) =>
                {
                    // [实现] 回调触发时：执行受异常处理保护的操作。
                    try
                    {
                        // [实现] 在异常保护范围内：执行 await Task.Delay(500, ct)；执行 await action()。
                        await Task.Delay(500, ct);
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

            var converter = new PortableModpackService(service);
            var hmcl = await converter.InstallAsync(
                Path.Combine(input, "hmcl-fabric.zip"),
                new(),
                "accept-hmcl",
                ct: ct
            );
            Console.WriteLine("ACCEPTANCE_HMCL_IMPORTED");
            var multimc = await converter.InstallAsync(
                Path.Combine(input, "multimc-fabric.zip"),
                new(),
                "accept-multimc",
                ct: ct
            );
            Console.WriteLine("ACCEPTANCE_MULTIMC_IMPORTED");
            // [实现] SearchAndInstall 的实现：准备 var w = Bootstrap.WorkspacePages.Search(this, service, profile, _ => { }, Open )；执行 await InWindow( w, async () => { var query = Named<TextBox>(w, "UnifiedQuery"); var list = Name…。
            async Task SearchAndInstall(GameProfile profile, string queryText, string projectId, bool checkLocal)
            {
                var w = Bootstrap.WorkspacePages.Search(
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
                    w,
                    async () =>
                    {
                        // [实现] 回调触发时：准备 var query = Named<TextBox>(w, "UnifiedQuery")；准备 var list = Named<ListBox>(w, "UnifiedResults")；按 checkLocal 分支处理。
                        var query = Named<TextBox>(w, "UnifiedQuery");
                        var list = Named<ListBox>(w, "UnifiedResults");
                        if (checkLocal)
                        {
                            // [实现] 条件成立（checkLocal）时：执行 query.Text = "fabric-api"；执行 Click(w, "搜索")；执行 await Wait( () => !w.Busy )。
                            query.Text = "fabric-api";
                            Click(w, "搜索");
                            await Wait(
                                () => !w.Busy
                            );
                            var merged = list.Items.OfType<ResourceSearchHit>()
                                .Single(
                                    x => x.Identity == "modrinth:P7dR8mSH"
                                );
                            if (!merged.Sources.Contains("本地") || !merged.Sources.Contains("Modrinth"))
                                throw new InvalidOperationException("Live source identity was not merged");
                            Capture(w, "unified-merged");
                            Named<CheckBox>(w, "UnifiedOnline")
                                .IsChecked = false;
                            Click(w, "搜索");
                            await Wait(
                                () => !w.Busy
                            );
                            if (!list.Items.OfType<ResourceSearchHit>()
                                .Any(
                                    x => x.Sources.SequenceEqual(
                                        new[]
                                        {
                                            "本地"
                                        }
                                    )
                                ))
                                throw new InvalidOperationException("Offline search lost installed files");
                            Capture(w, "unified-offline");
                            Named<CheckBox>(w, "UnifiedOnline")
                                .IsChecked = true;
                        }

                        query.Text = queryText;
                        Click(w, "搜索");
                        await Wait(
                            () => !w.Busy
                        );
                        list.SelectedItem = list.Items.OfType<ResourceSearchHit>()
                            .Single(
                                x => x.Modrinth?.Id == projectId
                            );
                        await Wait(
                            () => !w.Busy
                        );
                        if (Named<ComboBox>(w, "UnifiedVersions")
                            .SelectedItem is not ResourceRelease)
                            throw new InvalidOperationException("No compatible release loaded");
                        Click(w, "安装所选版本（含前置）");
                        await Wait(
                            () => !w.Busy
                        );
                        var status = Named<TextBlock>(w, "UnifiedStatus")
                            .Text ?? "";
                        if (!status.StartsWith("已安装并校验"))
                            throw new InvalidOperationException(status);
                        Capture(w, "unified-installed-" + profile.Loader);
                        Console.WriteLine("ACCEPTANCE_RESOURCE_INSTALLED " + queryText);
                    }
                );
            }

            await SearchAndInstall(hmcl, "lithium", "gvQqBUqZ", true);
            var packWindow = Bootstrap.WorkspacePages.Pack(
                this,
                service,
                hmcl,
                _ =>
                {
                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                }
            );
            await packWindow.SelectArchiveAsync(
                Path.Combine(input, "hmcl-fabric.zip")
            );
            await InWindow(
                packWindow,
                async () =>
                {
                    // [实现] 回调触发时：执行 Capture(packWindow, "hmcl-import-preview")；执行 await Task.CompletedTask。
                    Capture(packWindow, "hmcl-import-preview");
                    await Task.CompletedTask;
                }
            );
            using var catalog = new ResourceCatalog();
            var release = ResourceCatalog.ParseRelease(
                JObject.Parse(
                    File.ReadAllText(
                        Path.Combine(input, "adrenaline-release.json")
                    )
                )
            );
            var packs = new ModpackService(service);
            var downloaded = await packs.DownloadReleaseAsync(release, ct: ct);
            var adrenaline = await packs.InstallAsync(
                downloaded,
                "accept-adrenaline",
                ct: ct,
                selectedOptionalFiles: []
            );
            Console.WriteLine("ACCEPTANCE_ADRENALINE_INSTALLED");
            using var forge = new ForgeService(service);
            var neo = await forge.InstallAsync("neoforge", instanceName: "accept-neoforge", ct: ct);
            Console.WriteLine("ACCEPTANCE_NEOFORGE_INSTALLED");
            await SearchAndInstall(neo, "ferritecore", "uXXizFIs", false);
            var warnings = new LocalResourceService(neo.GameDirectory, neo.Loader, neo.GameVersion).ValidateEnabledResources();
            AccountTypeBox.SelectedIndex = 0;
            OfflineNameBox.Text = "PCLMacVerify";
            LoginButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Wait(
                () => operation is null
            );
            foreach (var profile in new[]
            {
                hmcl,
                multimc,
                adrenaline,
                neo
            }

            )
            {
                // [实现] 对 new[] { hmcl, multimc, adrenaline, neo } 中的每一项：执行 RefreshProfiles(profile.Id)；执行 LaunchButton.RaiseEvent( new RoutedEventArgs(Button.ClickEvent) )；执行 await Wait( () => game is not null || operation is null )。
                RefreshProfiles(profile.Id);
                LaunchButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
                await Wait(
                    () => game is not null || operation is null
                );
                var running = game ?? throw new InvalidOperationException("GUI launch failed: " + StatusText.Text);
                Console.WriteLine("ACCEPTANCE_GAME_STARTED " + profile.Id);
                await Task.WhenAny(
                    running.Completion,
                    Task.Delay(
                        TimeSpan.FromSeconds(45),
                        ct
                    )
                );
                var alive = !running.HasExited;
                var log = File.ReadAllText(running.LogFile);
                var graphics = log.Contains("Backend library") || log.Contains("OpenGL");
                var atlas = log.Contains("atlas");
                if (alive)
                {
                    // [实现] 条件成立（alive）时：执行 gameStoppedByUser = true；执行 running.Stop()。
                    gameStoppedByUser = true;
                    await running.StopAsync();
                }

                var exit = await running.Completion;
                await Wait(
                    () => operation is null
                );
                var passed = alive && graphics && atlas;
                cases.Add(
                    new
                    {
                        profile.Id,
                        profile.GameVersion,
                        profile.Loader,
                        profile.GameDirectory,
                        status = passed
                            ? "passed"
                            : "failed",
                        graphics,
                        atlas,
                        alive,
                        observationSeconds = 45,
                        stoppedByTest = alive,
                        exitCode = exit,
                        log = running.LogFile
                    }
                );
                JsonFiles.Write(
                    Path.Combine(paths.Evidence, "completion-runtime-cases.json"),
                    cases
                );
                if (!passed)
                    throw new InvalidOperationException("Runtime failed: " + profile.Id);
            }

            JsonFiles.Write(
                Path.Combine(paths.Evidence, "completion-features-result.json"),
                new
                {
                    status = "passed",
                    officialHmclConversion = true,
                    officialMultiMcPatchConversion = true,
                    liveSourcesMergedByIdentity = true,
                    offlineSearch = true,
                    installViaUnifiedSearch = true,
                    thirdPartyPack = release.Name,
                    thirdPartyPackVersion = release.Id,
                    neoforgeDependencyWarnings = warnings,
                    runtime = cases,
                    worldAcceptance = false,
                    completed = DateTimeOffset.UtcNow
                }
            );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "completion-features-result.json"), new { status …；执行 Console.Error.WriteLine(ex)。
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "completion-features-result.json"),
                new
                {
                    status = "failed",
                    error = ex.ToString(),
                    runtime = cases
                }
            );
            Console.Error.WriteLine(ex);
        }
        finally
        {
            // [实现] 无论成功或失败，退出前：按 game is not null && !game.HasExited 分支处理；执行 Close()。
            if (game is not null && !game.HasExited)
            {
                // [实现] 条件成立（game is not null && !game.HasExited）时：执行 gameStoppedByUser = true；执行 game.Stop()；执行 await game.Completion。
                gameStoppedByUser = true;
                await game.StopAsync();
                await game.Completion;
            }

            Close();
        }
    }
}
