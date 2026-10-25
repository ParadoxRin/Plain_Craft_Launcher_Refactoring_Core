using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Operations;
// 在既有安装器上建立恢复接缝，不重复实现下载、哈希校验、Mac natives 或 Java 选择。
public sealed class ExistingVersionInstaller(MinecraftService source) : IOperationInstaller
{
    public async Task<OperationInstalledInstance> InstallAsync(
        string operationId,
        OperationInstallInput request,
        InstallReceipt? previous,
        Action<InstallReceipt> checkpoint,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    )
    {
        using var versionService = source.ForVersion(request.GameVersion);
        var target = new ReceiptTarget(request.Os, request.Architecture, request.OsVersion);
        if (request.Loader is "auto" or "fabric" or "quilt" or "forge" or "neoforge" or "optifine" or "liteloader")
        {
            return await LoaderOperationInstaller.InstallAsync(source, operationId, request, previous, checkpoint, progress, ct);
        }

        if (request.Loader != "vanilla")
        {
            if (previous is not null)
            {
                throw new OperationInstallException("RESUME_UNSUPPORTED");
            }

            return Map(
                await new VersionInstaller(versionService).InstallAsync(request.Loader, progress, ct)
            );
        }

        if (previous is not null)
        {
            if (previous.FormatVersion != 1 || previous.GameVersion != request.GameVersion || previous.Loader != "vanilla" || previous.Target != target || previous.InstanceId != "vanilla-" + request.GameVersion || previous.Files.Count > 100000 || previous.CommitStage is not ("planned" or "verified" or "files-committing" or "files-ready" or "committing" or "committed"))
            {
                throw new InvalidDataException("RECEIPT_IDENTITY_INVALID");
            }

            foreach (var file in previous.Files)
            {
                ValidatePath(file.Path);
                if (file.Sha1.Length != 40 || !file.Sha1.All(Uri.IsHexDigit) || file.Size < 0)
                {
                    throw new InvalidDataException("RECEIPT_FILE_INVALID");
                }
            }
        }

        InstallReceipt? latest = null;
        var installed = await versionService.InstallAsync(
            progress,
            ct,
            (plan, stage) =>
            {
                var files = plan.Files.DistinctBy(
                    x => x.Path
                )
                    .Select(
                        x => new ReceiptFile(
                            Path.GetRelativePath(source.Paths.Root, x.Path),
                            x.Sha1!,
                            x.Size
                        )
                    )
                    .OrderBy(
                        x => x.Path,
                        StringComparer.Ordinal
                    )
                    .ToArray();
                foreach (var file in files)
                {
                    ValidatePath(file.Path);
                }

                var digest = Convert.ToHexString(
                    SHA256.HashData(
                        JsonSerializer.SerializeToUtf8Bytes(files)
                    )
                );
                if (previous is not null && (previous.MetadataSha1 != plan.MetadataSha1 || previous.AssetIndexSha1 != plan.AssetIndexSha1 || previous.PlanDigest != digest || !previous.Files.SequenceEqual(files)))
                {
                    throw new OperationInstallException("RESUME_PLAN_CHANGED");
                }

                latest = new(
                    1,
                    operationId,
                    "vanilla-" + request.GameVersion,
                    request.GameVersion,
                    "vanilla",
                    target,
                    plan.MetadataSha1,
                    plan.AssetIndexSha1,
                    digest,
                    files,
                    stage == "committing"
                        ? "files-committing"
                        : stage == "committed"
                            ? "files-ready"
                            : stage
                );
                checkpoint(latest);
            },
            async (prepared, token) =>
            {
                // Java 自身校验完成之后，才进入游戏安装标识的短提交区间。
                await new JavaService(versionService).EnsureForAsync(prepared, progress, token)
                    .ConfigureAwait(false);
            }
        )
            .ConfigureAwait(false);
        // 游戏标识已经提交；此后不再按迟到取消改写成功状态，收尾仍必须完成。
        checkpoint(
            latest!with
            {
                CommitStage = "committing"
            }
        );
        var settings = new LauncherSettingsStore(source.Paths);
        settings.SavePreferences(
            versionService.VanillaGameDirectory,
            settings.Preferences(versionService.VanillaGameDirectory)with
            {
                Deleted = false,
                Hidden = false
            }
        );
        checkpoint(
            latest!with
            {
                CommitStage = "committed"
            }
        );
        var profile = InstanceRegistry.List(versionService, true)
            .Single(
                x => x.Loader == "vanilla" && x.GameVersion == request.GameVersion && Path.GetFullPath(x.GameDirectory) == Path.GetFullPath(versionService.VanillaGameDirectory)
            );
        return Map(profile);
    }

    private void ValidatePath(string relative)
    {
        if (Path.IsPathFullyQualified(relative) || relative.Split('/', '\\')
            .Any(
                x => x is ".." or "."
            ) || string.IsNullOrWhiteSpace(relative))
        {
            throw new InvalidDataException("RECEIPT_PATH_INVALID");
        }

        var full = AppPaths.SafeChild(source.Paths.Root, relative);
        for (string? path = full; path is not null && path != source.Paths.Root; path = Path.GetDirectoryName(path))
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new InvalidDataException("RECEIPT_PATH_INVALID");
            }
        }
    }

    private static OperationInstalledInstance Map(GameProfile result) => new(result.Id, result.Name, result.GameVersion, result.Loader, result.GameDirectory);
}
