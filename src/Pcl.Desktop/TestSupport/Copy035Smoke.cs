using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core.Contracts.Pages;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    // 仅显式测试参数启动；验证真实窗口、RPC、复制结果和偏好，源数据必须位于本轮隔离目录。
    private async Task Copy035SmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--remote-report") + 1];
        var checks = new List<string>();
        ComponentManagerWindow? window = null;
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }

            checks.Add(name);
            File.WriteAllText(
                report + ".progress.json",
                JsonSerializer.Serialize(
                    new
                    {
                        checks
                    }
                )
            );
        }

        async Task Until(Func<bool> condition)
        {
            var limit = DateTime.UtcNow.AddMinutes(4);
            while (!condition())
            {
                if (DateTime.UtcNow > limit)
                {
                    throw new TimeoutException(
                        (window?.TestStatus ?? "") + " | " + window?.OwnedWindows.OfType<ComponentCopyConfirmation>()
                            .FirstOrDefault()?.Feedback.Text
                    );
                }

                await Task.Delay(100);
            }
        }

        void Click(Button button) => button.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent)
        );
        Dictionary<string, string> Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(
                file => !file.EndsWith(".lock")
            )
            .ToDictionary(
                file => Path.GetRelativePath(directory, file),
                file =>
                {
                    using var stream = File.OpenRead(file);
                    return Convert.ToHexString(
                        SHA256.HashData(stream)
                    );
                }
            );
        try
        {
            if (remote is null || !paths.Root.Contains("verification-0.35"))
            {
                throw new InvalidOperationException("ISOLATED_035_ROOT_REQUIRED");
            }

            var instances = await DesktopApi.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
            var source = instances.Items.First(
                x => x.Profile.Id == "1.21.1"
            )
                .Profile;
            var before = Snapshot(source.GameDirectory);
            var service = remote.Components(source.Id);
            await service.SetConfirmationSuppressedAsync(false, default);
            string? resultId = null;
            window = new ComponentManagerWindow(
                service,
                new(source.Id, source.Name, source.GameVersion, source.Loader),
                id => resultId = id
            );
            window.Show(this);
            window.TestLoader.SelectedItem = "vanilla";
            window.TestDisable.IsChecked = true;
            window.TestClosedGame.IsChecked = true;
            Click(window.TestPreview);
            await Until(
                () => !window.TestBusy && window.TestPlan is not null
            );
            var plan = window.TestPlan!;
            Check(
                plan.SourceDirectory == source.GameDirectory && !Directory.Exists(plan.TargetDirectory),
                "host-preview-fixes-destination-without-creating-copy"
            );
            Click(window.TestApply);
            await Until(
                () => window.OwnedWindows.OfType<ComponentCopyConfirmation>()
                    .Any()
            );
            var dialog = window.OwnedWindows.OfType<ComponentCopyConfirmation>()
                .Single();
            await Task.Delay(150);
            using (var bitmap = new RenderTargetBitmap(
                new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height),
                new Vector(96, 96)
            ))
            {
                bitmap.Render(dialog);
                bitmap.Save(
                    Path.Combine(
                        Path.GetDirectoryName(report)!,
                        "copy-confirmation.png"
                    ),
                    PngBitmapEncoderOptions.Default
                );
            }

            var copyButtons = dialog.GetVisualDescendants()
                .OfType<Button>()
                .Where(
                    x => x.Content?.ToString() == "复制路径"
                )
                .ToArray();
            Check(copyButtons.Length == 2, "dialog-provides-both-exact-paths");
            Click(copyButtons[1]);
            await Until(
                () => dialog.Feedback.Text == "路径已复制。"
            );
            Check(
                await dialog.Clipboard!.TryGetTextAsync() == plan.TargetDirectory,
                "target-path-clipboard-is-host-planned-path"
            );
            foreach (var button in dialog.GetVisualDescendants()
                .OfType<Button>()
                .Where(
                    x => x.Content?.ToString()is "打开原实例" or "打开目标父目录"
                )
                .ToArray())
            {
                dialog.Feedback.Text = "";
                Click(button);
                await Until(
                    () => dialog.Feedback.Text?.StartsWith("已打开：") == true
                );
            }

            Check(
                !Directory.Exists(plan.TargetDirectory),
                "finder-open-parent-does-not-create-target"
            );
            Click(dialog.CancelButton);
            await Until(
                () => !window.OwnedWindows.OfType<ComponentCopyConfirmation>()
                    .Any()
            );
            Check(
                resultId is null && !Directory.Exists(plan.TargetDirectory),
                "cancel-starts-no-copy"
            );
            Check(
                before.OrderBy(
                    x => x.Key
                )
                    .SequenceEqual(
                        Snapshot(source.GameDirectory)
                            .OrderBy(
                                x => x.Key
                            )
                    ),
                "cancel-preserves-entire-source-file-hashes"
            );
            Click(window.TestApply);
            await Until(
                () => window.OwnedWindows.OfType<ComponentCopyConfirmation>()
                    .Any()
            );
            dialog = window.OwnedWindows.OfType<ComponentCopyConfirmation>()
                .Single();
            dialog.Suppress.IsChecked = true;
            Click(dialog.ConfirmButton);
            await Until(
                () => !window.TestBusy && resultId is not null
            );
            Check(
                Directory.Exists(plan.TargetDirectory) && File.Exists(
                    Path.Combine(plan.TargetDirectory, "profile.json")
                ),
                "confirmed-copy-published-at-exact-dialog-path"
            );
            Check(
                before.OrderBy(
                    x => x.Key
                )
                    .SequenceEqual(
                        Snapshot(source.GameDirectory)
                            .OrderBy(
                                x => x.Key
                            )
                    ),
                "confirmed-copy-preserves-entire-source-file-hashes"
            );
            var after = await DesktopApi.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
            Check(
                after.Items.Single(
                    x => x.Profile.Id == resultId
                )
                    .OriginDirectory == source.GameDirectory,
                "instance-details-retain-source-provenance"
            );
            var next = await service.PreviewAsync("vanilla", null, true, default);
            Check(next.SuppressConfirmation, "host-persists-dont-show-again-preference");
            var open = window.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == "打开新副本"
                );
            Check(open.IsEnabled, "actual-copy-has-open-action");
            Click(open);
            // 偏好只略过说明；实际提交仍检查源目录变化。
            Click(window.TestPreview);
            await Until(
                () => !window.TestBusy && window.TestPlan is not null
            );
            var stale = window.TestPlan!;
            var marker = Path.Combine(source.GameDirectory, "pcl035-source-change.txt");
            File.WriteAllText(marker, "test-owned-change");
            resultId = null;
            Click(window.TestApply);
            await Until(
                () => !window.TestBusy
            );
            Check(
                resultId is null && !Directory.Exists(stale.TargetDirectory),
                "suppressed-dialog-still-refuses-source-change"
            );
            Check(
                !window.OwnedWindows.OfType<ComponentCopyConfirmation>()
                    .Any(),
                "suppressed-dialog-is-not-opened"
            );
            File.Delete(marker);
            resultId = Path.GetFileName(plan.TargetDirectory);
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        source = source.GameDirectory,
                        target = plan.TargetDirectory,
                        resultId
                    }
                )
            );
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "failed",
                        checks,
                        error = ex.ToString()
                    }
                )
            );
        }
        finally
        {
            window?.Close();
            Close();
        }
    }
}
