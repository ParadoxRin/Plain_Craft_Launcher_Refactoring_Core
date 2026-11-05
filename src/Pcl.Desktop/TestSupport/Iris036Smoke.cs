using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Pcl.Core.Contracts.Workspaces;

namespace Pcl.Desktop;
public partial class MainWindow
{
    /// <summary>显式隔离测试：真实资源页按钮打开预览，取消不写入，确认通过 Host 卸载并保留共享依赖。</summary>
    private async Task Iris036SmokeAsync()
    {
        var report = Program.Arguments[Array.IndexOf(Program.Arguments, "--remote-report") + 1];
        var checks = new List<string>();
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
                    throw new TimeoutException(resources.IrisTestStatus);
                }

                await Task.Delay(100);
            }
        }

        void Click(Button button) => button.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent)
        );
        Dictionary<string, string> Inventory(string dir) => Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Order()
            .ToDictionary(
                x => Path.GetRelativePath(dir, x),
                x =>
                {
                    using var stream = File.OpenRead(x);
                    return Convert.ToHexString(
                        SHA256.HashData(stream)
                    );
                }
            );
        try
        {
            if (remote is null || !paths.Root.Contains("verification-0.36") || !paths.Root.EndsWith("environment-ui-data"))
            {
                throw new InvalidOperationException("ISOLATED_IRIS_ROOT_REQUIRED");
            }

            var instances = await DesktopApi.CallAsync<InstanceSnapshot>("workspace.instances.list", null);
            var profile = instances.Items.Single(
                x => Path.GetFileName(x.Profile.GameDirectory) == "mac-shaders-1.21.1"
            )
                .Profile;
            RefreshProfiles(profile.Id);
            Check(
                (ProfileBox.SelectedItem as GameProfile)?.Id == profile.Id,
                "exact-isolated-iris-profile-selected"
            );
            // 公开发布的 Sodium Extra 0.6.0，正常安装入口求解共享 Sodium；不是伪造用途记录。
            await DesktopApi.CallAsync<bool>(
                "workspace.browser.install",
                profile.Id,
                new
                {
                    releaseId = "anDafurZ",
                    kind = (int)ResourceKind.Mod
                }
            );
            var plan = await DesktopApi.CallAsync<ShaderEnvironmentPreview>("workspace.browser.irisPreview", profile.Id);
            Check(
                plan.CanApply && plan.Retain.Any(
                    x => x.Contains("sodium-fabric")
                ),
                "real-sodium-extra-install-protects-shared-sodium"
            );
            File.WriteAllText(
                report + ".plan.json",
                JsonSerializer.Serialize(plan)
            );
            var protectedBefore = new Dictionary<string, Dictionary<string, string>>();
            foreach (var dir in new[]
            {
                "config",
                "shaderpacks",
                "saves"
            }

            )
            {
                protectedBefore[dir] = Inventory(
                    Path.Combine(profile.GameDirectory, dir)
                );
            }

            await ShowResourceAsync(ResourceKind.Mod, NavManaged, true);
            var button = resources.GetVisualDescendants()
                .OfType<Button>()
                .Single(
                    x => x.Content?.ToString() == "卸载 Iris 环境"
                );
            Check(button.IsEffectivelyEnabled, "actual-resource-management-uninstall-button-enabled");
            var modsBefore = Inventory(
                Path.Combine(profile.GameDirectory, "mods")
            );
            await Until(
                () => operation is null && button.IsEffectivelyEnabled && !OwnedWindows.OfType<IrisEnvironmentWindow>()
                    .Any()
            );
            Click(button);
            await Until(
                () => OwnedWindows.OfType<IrisEnvironmentWindow>()
                    .Any()
            );
            var dialog = OwnedWindows.OfType<IrisEnvironmentWindow>()
                .Single();
            await Task.Delay(250);
            using (var bitmap = new RenderTargetBitmap(
                new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height),
                new Vector(96, 96)
            ))
            {
                bitmap.Render(dialog);
                bitmap.Save(
                    Path.Combine(
                        Path.GetDirectoryName(report)!,
                        "iris-confirmation.png"
                    ),
                    PngBitmapEncoderOptions.Default
                );
            }

            Check(dialog.Confirm.IsEffectivelyEnabled, "host-plan-allows-confirmation");
            Click(dialog.Cancel);
            await Until(
                () => resources.IrisTestStatus.Contains("已取消")
            );
            Check(
                modsBefore.SequenceEqual(
                    Inventory(
                        Path.Combine(profile.GameDirectory, "mods")
                    )
                ),
                "cancel-leaves-all-mod-bytes-identical"
            );
            await Until(
                () => operation is null && button.IsEffectivelyEnabled && !OwnedWindows.OfType<IrisEnvironmentWindow>()
                    .Any()
            );
            Click(button);
            await Until(
                () => OwnedWindows.OfType<IrisEnvironmentWindow>()
                    .Any()
            );
            Click(
                OwnedWindows.OfType<IrisEnvironmentWindow>()
                    .Single()
                    .Confirm
            );
            await Until(
                () => resources.IrisTestStatus.StartsWith("已卸载 Iris 环境")
            );
            var after = Inventory(
                Path.Combine(profile.GameDirectory, "mods")
            );
            Check(
                plan.Remove.All(
                    x => !File.Exists(
                        Path.Combine(profile.GameDirectory, x)
                    )
                ),
                "confirm-removes-exact-host-planned-components"
            );
            Check(
                after.Any(
                    x => x.Key.Contains("sodium-fabric")
                ) && after.Any(
                    x => x.Key.Contains("sodium-extra")
                ),
                "shared-real-sodium-and-consumer-retained"
            );
            Check(
                after.All(
                    x => modsBefore.GetValueOrDefault(x.Key) == x.Value
                ),
                "retained-mod-bytes-unchanged"
            );
            foreach (var dir in protectedBefore.Keys)
            {
                Check(
                    protectedBefore[dir].SequenceEqual(
                        Inventory(
                            Path.Combine(profile.GameDirectory, dir)
                        )
                    ),
                    "uninstall-preserves-" + dir
                );
            }

            File.WriteAllText(
                report,
                JsonSerializer.Serialize(
                    new
                    {
                        status = "passed",
                        checks,
                        profileId = profile.Id,
                        removed = plan.Remove,
                        retained = plan.Retain
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
            foreach (var dialog in OwnedWindows.OfType<IrisEnvironmentWindow>()
                .ToArray())
            {
                dialog.Close(false);
            }

            Close();
        }
    }
}
