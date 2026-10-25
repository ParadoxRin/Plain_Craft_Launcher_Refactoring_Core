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
    // [实现] McmodSmokeAsync 的实现：执行受异常处理保护的操作。
    private async Task McmodSmokeAsync()
    {
        try
        {
            // [实现] 在异常保护范围内：准备 var deadline = new CancellationTokenSource( TimeSpan.FromMinutes(15) )；准备 var ct = deadline.Token；执行 async Task Wait(Func<bool> done) { while (!done()) await Task.Delay(150, ct); }。
            using var deadline = new CancellationTokenSource(
                TimeSpan.FromMinutes(15)
            );
            var ct = deadline.Token;
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

            using (var probe = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            }

            )
            {
                // [实现] 本段流程：执行受异常处理保护的操作。
                try
                {
                    // [实现] 在异常保护范围内：准备 var response = await probe.GetAsync("https://www.mcmod.cn/modlist.html", HttpCompletionOption.R…；执行 JsonFiles.Write( Path.Combine(paths.Evidence, "mcmod-network.json"), new { httpStatus = (int)re…。
                    using var response = await probe.GetAsync("https://www.mcmod.cn/modlist.html", HttpCompletionOption.ResponseHeadersRead, ct);
                    JsonFiles.Write(
                        Path.Combine(paths.Evidence, "mcmod-network.json"),
                        new
                        {
                            httpStatus = (int)response.StatusCode,
                            success = response.IsSuccessStatusCode
                        }
                    );
                }
                catch (Exception ex)when (ex is HttpRequestException or TaskCanceledException)
                {
                    // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "mcmod-network.json"), new { success = false, err…。
                    JsonFiles.Write(
                        Path.Combine(paths.Evidence, "mcmod-network.json"),
                        new
                        {
                            success = false,
                            error = ex.GetType()
                                .Name,
                            ex.Message
                        }
                    );
                }
            }

            using var f = new FabricService(service, "accept-mcmod");
            var profile = await f.InstallAsync("0.20.1", ct: ct);
            var target = McmodCatalog.Entries.Single(
                x => x.ModrinthSlugs.Contains("lithium")
            );
            var opened = new List<string>();
            Exception? uiError = null;
            ResourceRelease? installedRelease = null;
            ResourceRelease? importedRelease = null;
            var window = Bootstrap.WorkspacePages.Search(
                this,
                service,
                profile,
                _ =>
                {
                    // [实现] 此分支没有额外语句；控制流继续遵循外围的返回、异常或迭代规则。
                },
                url => opened.Add(url),
                mcmodOnly: true
            );
            window.Opened += async (_, _) =>
            {
                // [实现] 回调触发时：执行受异常处理保护的操作。
                try
                {
                    // [实现] 在异常保护范围内：执行 await Task.Delay(500, ct)；准备 var query = Named<TextBox>(window, "UnifiedQuery")；准备 var results = Named<ListBox>(window, "UnifiedResults")。
                    await Task.Delay(500, ct);
                    var query = Named<TextBox>(window, "UnifiedQuery");
                    var results = Named<ListBox>(window, "UnifiedResults");
                    var online = Named<CheckBox>(window, "UnifiedOnline");
                    var versions = Named<ComboBox>(window, "UnifiedVersions");
                    query.Text = "";
                    online.IsChecked = false;
                    Click(window, "搜索");
                    await Wait(
                        () => !window.Busy
                    );
                    var first = results.Items.OfType<ResourceSearchHit>()
                        .Select(
                            x => x.Identity
                        )
                        .ToArray();
                    Click(window, "下一页");
                    await Wait(
                        () => !window.Busy
                    );
                    if (first.Intersect(
                        results.Items.OfType<ResourceSearchHit>()
                            .Select(
                                x => x.Identity
                            )
                    )
                        .Any())
                        throw new Exception("MC百科 pages overlap");
                    query.Text = "锂";
                    Click(window, "搜索");
                    await Wait(
                        () => !window.Busy
                    );
                    results.SelectedItem = results.Items.OfType<ResourceSearchHit>()
                        .Single(
                            x => x.Mcmod?.Id == target.Id
                        );
                    await Wait(
                        () => !window.Busy
                    );
                    if (versions.ItemCount != 0)
                        throw new Exception("Offline version query");
                    await Capture(window, "mcmod-offline");
                    Click(window, "百科资料");
                    await Wait(
                        () => !window.Busy
                    );
                    Click(window, "百科站内下载");
                    await Wait(
                        () => !window.Busy
                    );
                    if (!opened.Contains(target.WebUrl) || !opened.Contains(target.DownloadPage))
                        throw new Exception("Wrong encyclopedia links");
                    query.Text = target.WebUrl;
                    online.IsChecked = true;
                    Click(window, "搜索");
                    await Wait(
                        () => !window.Busy
                    );
                    results.SelectedItem = results.Items.OfType<ResourceSearchHit>()
                        .Single(
                            x => x.Mcmod?.Id == target.Id
                        );
                    await Wait(
                        () => !window.Busy
                    );
                    installedRelease = versions.SelectedItem as ResourceRelease ?? throw new Exception(
                        "No compatible release: " + Named<TextBlock>(window, "UnifiedStatus")
                            .Text
                    );
                    await Capture(window, "mcmod-versions");
                    Click(window, "安装所选版本（含前置）");
                    await Wait(
                        () => !window.Busy
                    );
                    if (!(Named<TextBlock>(window, "UnifiedStatus")
                        .Text ?? "").StartsWith("已安装并校验"))
                        throw new Exception(
                            Named<TextBlock>(window, "UnifiedStatus")
                                .Text
                        );
                    await Capture(window, "mcmod-installed");
                    // Actual manual-import service path, using a known official downloaded JAR (not a CAPTCHA download).
                    using var catalog = new ResourceCatalog();
                    var ferrite = McmodCatalog.Find(3888) ?? throw new Exception("Missing FerriteCore mapping");
                    importedRelease = (await McmodCatalog.VersionsAsync(ferrite, catalog, "1.21.1", "fabric", ct)).Releases.First();
                    var file = Path.Combine(paths.Root, "manual-download", importedRelease.File.Name);
                    await service.Downloads.DownloadAsync(
                        new(
                            importedRelease.File.Url,
                            file,
                            importedRelease.File.Size,
                            importedRelease.File.Sha1
                        ),
                        ct
                    );
                    await window.ImportFileAsync(file, ct);
                    await Capture(window, "mcmod-imported");
                }
                catch (Exception ex)
                {
                    // [实现] 捕获 (Exception ex) 后：执行 uiError = ex。
                    uiError = ex;
                }
                finally
                {
                    // [实现] 无论成功或失败，退出前：执行 window.Close()。
                    window.Close();
                }
            };
            await window.ShowDialog(this);
            if (uiError is not null)
                throw uiError;
            AccountTypeBox.SelectedIndex = 0;
            OfflineNameBox.Text = "PCLMacVerify";
            LoginButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Wait(
                () => operation is null
            );
            RefreshProfiles(profile.Id);
            LaunchButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent)
            );
            await Wait(
                () => game is not null || operation is null
            );
            var running = game ?? throw new Exception("GUI launch failed: " + StatusText.Text);
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
            if (!alive || !graphics || !atlas)
                throw new Exception("Game startup failed");
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "mcmod-result.json"),
                new
                {
                    status = "passed",
                    source = McmodCatalog.Source,
                    indexEntries = McmodCatalog.Entries.Count,
                    index = McmodCatalog.IndexProvenance,
                    chineseSearch = true,
                    linkSearch = true,
                    offlinePagination = true,
                    selectedWikiId = target.Id,
                    installedRelease,
                    importedRelease,
                    openedLinks = opened,
                    linkOpeningInterceptedForTest = true,
                    manualImportPickerTested = false,
                    mcmodCaptchaDownloadTested = false,
                    downloadHost = "cdn.modrinth.com",
                    runtime = new
                    {
                        profile.Id,
                        profile.GameVersion,
                        profile.Loader,
                        alive,
                        graphics,
                        atlas,
                        observationSeconds = 45,
                        exitCode = exit,
                        stoppedByTest = true,
                        log = running.LogFile
                    },
                    worldAcceptance = false,
                    completed = DateTimeOffset.UtcNow
                }
            );
        }
        catch (Exception ex)
        {
            // [实现] 捕获 (Exception ex) 后：执行 JsonFiles.Write( Path.Combine(paths.Evidence, "mcmod-result.json"), new { status = "failed", er…；执行 Console.Error.WriteLine(ex)。
            JsonFiles.Write(
                Path.Combine(paths.Evidence, "mcmod-result.json"),
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
