using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Client;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    private bool closeoutFifthStarted;
    // 独立根中运行真实 Host 和主菜单；只终止本探针创建的游戏，不打开存档。
    private async Task CloseoutFifthAcceptanceAsync()
    {
        // Show() 会再次触发 Opened，恢复窗口不能重新启动同一轮探针。
        if (closeoutFifthStarted)
        {
            return;
        }

        closeoutFifthStarted = true;
        string Arg(string key) => Program.Arguments[Array.IndexOf(Program.Arguments, key) + 1];
        var report = Arg("--acceptance-report");
        var fixtures = Arg("--acceptance-files");
        var mode = Arg("--acceptance-mode");
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }

            checks.Add(label);
            File.AppendAllText(report + ".progress", label + "\n");
        }

        async Task Wait(Func<bool> ready)
        {
            var until = DateTime.UtcNow.AddMinutes(2);
            while (!ready())
            {
                if (DateTime.UtcNow >= until)
                {
                    throw new TimeoutException("fifth acceptance wait");
                }

                await Task.Delay(100);
            }
        }

        try
        {
            if (!File.Exists(
                Path.Combine(paths.Root, ".closeout-fifth")
            ) || accounts!.Accounts.Any(
                x => x.Kind != AccountKind.Offline
            ))
            {
                throw new InvalidOperationException("ISOLATED_OFFLINE_ROOT_REQUIRED");
            }

            UiMotion.Configure(0);
            var api = new WorkspaceApi(service.Workspaces);
            var baseline = new AppearanceSettings
            {
                Background = Path.Combine(fixtures, "background.png"),
                BackgroundOpacity = .6,
                BackgroundTint = false,
                AnimationSpeed = 0,
                LauncherVisibility = mode,
                MusicDirectory = Path.Combine(fixtures, "music"),
                MusicAutoPlay = true,
                MusicVolume = 0,
                PauseMusicInGame = true
            };
            // 实际应用每种背景模式；保留真实窗口渲染图供检查，不将属性检查说成人工验收。
            if (mode == "keep")
            {
                foreach (var fit in AppearanceFeatures.BackgroundFits)
                {
                    ApplyAppearance(
                        baseline with
                        {
                            BackgroundFit = fit
                        }
                    );
                    Check(classicBackground.Background is ImageBrush, "UI-002-brush-" + fit);
                    var brush = (ImageBrush)classicBackground.Background!;
                    if (fit is "tile" or "auto")
                    {
                        Check(brush.TileMode == TileMode.Tile, "UI-002-small-image-tiles-" + fit);
                    }

                    if (fit.Contains("left"))
                    {
                        Check(brush.AlignmentX == AlignmentX.Left, "UI-002-align-" + fit);
                    }

                    if (fit.Contains("right"))
                    {
                        Check(brush.AlignmentX == AlignmentX.Right, "UI-002-align-" + fit);
                    }
                }

                ApplyAppearance(
                    baseline with
                    {
                        BackgroundDirectory = Path.Combine(fixtures, "backgrounds")
                    }
                );
                var first = displayedBackground;
                ApplyAppearance(
                    baseline with
                    {
                        BackgroundDirectory = Path.Combine(fixtures, "backgrounds")
                    }
                );
                Check(first != displayedBackground, "UI-002-folder-avoids-current-file");
                ApplyAppearance(
                    baseline with
                    {
                        BackgroundFit = "tile"
                    }
                );
                await Task.Delay(150);
                using var bitmap = new RenderTargetBitmap(
                    new PixelSize((int)Bounds.Width, (int)Bounds.Height)
                );
                bitmap.Render(this);
                bitmap.Save(report + ".png", PngBitmapEncoderOptions.Default);
                Check(
                    new FileInfo(report + ".png").Length > 1000,
                    "UI-002-real-window-rendered"
                );
            }

            ApplyAppearance(baseline);
            await Wait(
                () => musicPlayer.TracksStarted >= 2 && musicPlayer.IsPlaying
            );
            Check(musicPlayer.LastError is null, "UI-003-real-afplay-playlist-advances");
            var account = await AddOfflineAccountAsync("PCL_Acceptance", default);
            await api.CallAsync<bool>(
                "workspace.instances.saveSettings",
                "1.21.1",
                new
                {
                    value = new
                    {
                        QuickPlayWorld = "",
                        MaximumMemoryMb = 1536
                    },
                    fields = new[]
                    {
                        "QuickPlayWorld",
                        "MaximumMemoryMb"
                    }
                }
            );
            RefreshProfiles("1.21.1");
            var running = RunGameByAccountAsync(account.Id, true, default);
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            Button? continueButton = null;
            await Wait(
                () =>
                {
                    continueButton = lifetime.Windows.SelectMany(
                        x => x.GetVisualDescendants()
                    )
                        .OfType<Button>()
                        .FirstOrDefault(
                            x => x.Name == "GuidanceContinue"
                        );
                    return continueButton is not null || running.IsCompleted;
                }
            );
            if (continueButton is not null)
            {
                continueButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent)
                );
            }

            if (mode == "close")
            {
                // 应用退出后由外部探针读取新 receipt，核对游戏继续运行并定向清理。
                await running;
                return;
            }

            await Wait(
                () => game is not null || running.IsCompleted
            );
            if (game is null)
            {
                await running;
                throw new InvalidOperationException("GAME_NOT_STARTED");
            }

            var owned = game;
            Check(!musicPlayer.IsPlaying, "UI-003-real-game-start-pauses-music");
            Check(
                mode switch
                {
                    "hide-show" or "hide-close" => !IsVisible,
                    "minimize" => WindowState == WindowState.Minimized,
                    _ => IsVisible && WindowState != WindowState.Minimized
                },
                "JAVA-015-actual-window-state-" + mode
            );
            await Wait(
                () => File.Exists(owned.LogFile) && File.ReadAllText(owned.LogFile)
                    .Contains("gui.png-atlas")
            );
            Check(!owned.HasExited, "JAVA-015-real-game-renderer-ready-" + mode);
            // 主菜单主动终止，不把此退出码或过程计作世界保存验收。
            gameStoppedByUser = true;
            await owned.StopAsync();
            await running;
            if (mode != "hide-close")
            {
                await Wait(
                    () => musicPlayer.IsPlaying
                );
                Check(musicPlayer.LastError is null, "UI-003-game-exit-restores-music");
                Check(IsVisible, "JAVA-015-post-game-window-visible-" + mode);
            }

            musicPlayer.Stop();
            var starts = musicPlayer.TracksStarted;
            await Task.Delay(1200);
            Check(
                !musicPlayer.IsPlaying && musicPlayer.TracksStarted == starts,
                "UI-003-stop-does-not-start-next-track"
            );
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        mode,
                        checks
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        mode,
                        checks,
                        error = error.ToString()
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }
                )
            );
        }
        finally
        {
            if (game is not null)
            {
                await game.StopAsync();
            }

            await RequestApplicationExitAsync();
        }
    }
}
