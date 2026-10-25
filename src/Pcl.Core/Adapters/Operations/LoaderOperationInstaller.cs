using System.Security.Cryptography;
using System.Text.Json;
using Pcl.Core.Ports.Operations;

namespace Pcl.Core.Adapters.Operations;
// 恢复的是带固定输入的安装阶段；外部 Java 安装器从干净工作目录重跑，而不是继续其指令位置。
internal static class LoaderOperationInstaller
{
    internal static async Task<OperationInstalledInstance> InstallAsync(
        MinecraftService source,
        string id,
        OperationInstallInput request,
        InstallReceipt? previous,
        Action<InstallReceipt> checkpoint,
        IProgress<InstallProgress> progress,
        CancellationToken ct
    )
    {
        foreach (var folder in new[]
        {
            ".work",
            "instances",
            "cache",
            "minecraft",
            "runtime"
        }

        )
        {
            LoaderRecoveryContext.ValidatePath(
                Path.Combine(source.Paths.Root, folder)
            );
        }

        using var versionService = source.ForVersion(request.GameVersion);
        if (request.Loader == "auto" && previous?.Loader == "vanilla" && previous.LoaderPin is null)
        {
            return await new ExistingVersionInstaller(source).InstallAsync(
                id,
                request with
                {
                    Loader = "vanilla"
                },
                previous,
                checkpoint,
                progress,
                ct
            );
        }

        var pin = previous?.LoaderPin;
        if (previous is not null && (pin is null || previous.GameVersion != request.GameVersion || previous.Target != new ReceiptTarget(request.Os, request.Architecture, request.OsVersion)))
        {
            throw new InvalidDataException("LOADER_RECEIPT_INVALID");
        }

        var family = pin?.Family ?? request.Loader;
        string version;
        using var fabric = new FabricService(
            versionService,
            loader: family == "quilt"
                ? "quilt"
                : "fabric"
        );
        using var forge = new ForgeService(versionService);
        if (pin is not null)
        {
            version = pin.Version;
        }
        else if (family is "auto" or "fabric" or "quilt")
        {
            version = (await fabric.VersionsAsync(ct)).FirstOrDefault(
                x => x.Stable
            )?.Version ?? "";
            if (version == "" && family == "auto")
            {
                return await new ExistingVersionInstaller(source).InstallAsync(
                    id,
                    request with
                    {
                        Loader = "vanilla"
                    },
                    null,
                    checkpoint,
                    progress,
                    ct
                );
            }

            if (version == "")
            {
                throw new NotSupportedException("没有稳定加载器");
            }

            family = family == "quilt"
                ? "quilt"
                : "fabric";
        }
        else if (family is "forge" or "neoforge")
        {
            version = (await forge.VersionsAsync(family, ct)).FirstOrDefault()?.Version ?? throw new NotSupportedException("没有正式加载器");
        }
        else if (family == "optifine")
        {
            using var optifine = new OptiFineService(versionService);
            version = (await optifine.VersionsAsync(ct)).FirstOrDefault(
                x => !x.Preview
            )?.FileName ?? throw new NotSupportedException("无可用 OptiFine");
        }
        else if (family == "liteloader")
        {
            using var lite = new LiteLoaderService(versionService);
            version = (await lite.VersionsAsync(ct)).FirstOrDefault()?.Build ?? throw new NotSupportedException("无可用 LiteLoader");
        }
        else
        {
            throw new OperationInstallException("LOADER_RECOVERY_UNSUPPORTED");
        }

        var owner = pin?.OwnerOperationId ?? id;
        if (!System.Text.RegularExpressions.Regex.IsMatch(owner, "^op-[a-f0-9]{32}$") || family is not ("fabric" or "quilt" or "forge" or "neoforge" or "optifine" or "liteloader"))
        {
            throw new InvalidDataException("LOADER_RECEIPT_INVALID");
        }

        var name = $"{family}-{request.GameVersion}-{version}-{owner[3..11]}";
        ForgeService.ValidateName(name);
        if (pin is not null && pin.InstanceName != name)
        {
            throw new InvalidDataException("LOADER_RECEIPT_INVALID");
        }

        var selected = pin ?? new LoaderRecoveryPin(family, version, name, owner);
        foreach (var file in selected.Files ?? [])
        {
            AppPaths.SafeChild(source.Paths.Root, file.Path);
            if (file.Path.Split('/', '\\')
                .Any(
                    x => x == ".."
                ) || file.Sha1.Length != 40 || !file.Sha1.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("LOADER_RECEIPT_INVALID");
            }
        }

        InstallReceipt? latest = null;
        // 父版本每次从可信根元数据重建并检查；旧 receipt 的父计划不能因换成加载器流程而跳过。
        var vanillaPrevious = previous is null || previous.Files.Count == 0
            ? null
            : previous with
            {
                Loader = "vanilla",
                InstanceId = "vanilla-" + request.GameVersion,
                LoaderPin = null,
                CommitStage = "planned"
            };
        await new ExistingVersionInstaller(source).InstallAsync(
            id,
            request with
            {
                Loader = "vanilla"
            },
            vanillaPrevious,
            receipt =>
            {
                latest = receipt with
                {
                    Loader = request.Loader,
                    InstanceId = name,
                    LoaderPin = selected
                };
                checkpoint(latest);
            },
            progress,
            ct
        );
        latest = latest!with
        {
            CommitStage = "loader-selected",
            LoaderPin = selected
        };
        checkpoint(latest);
        var recovery = new LoaderRecoveryContext(
            (stage, artifact, metadata, files) =>
            {
                if (artifact.Length > 0 && selected.ArtifactHash.Length > 0 && selected.ArtifactHash != artifact)
                {
                    throw new OperationInstallException("RESUME_INSTALLER_CHANGED");
                }

                if (metadata.Length > 0 && selected.MetadataHash.Length > 0 && selected.MetadataHash != metadata)
                {
                    throw new OperationInstallException("RESUME_LOADER_METADATA_CHANGED");
                }

                selected = selected with
                {
                    ArtifactHash = artifact.Length > 0
                        ? artifact
                        : selected.ArtifactHash,
                    MetadataHash = metadata.Length > 0
                        ? metadata
                        : selected.MetadataHash,
                    Stage = stage
                };
                if (files.Count > 0)
                {
                    selected = selected with
                    {
                        Files = files.Select(
                            file =>
                            {
                                var relative = Path.GetRelativePath(source.Paths.Root, file.Path)
                                    .Replace(Path.DirectorySeparatorChar, '/');
                                if (relative.StartsWith(".work/", StringComparison.Ordinal))
                                {
                                    var marker = relative.IndexOf("/libraries/", StringComparison.Ordinal);
                                    if (marker < 0)
                                    {
                                        throw new InvalidDataException("LOADER_STAGE_PATH_INVALID");
                                    }

                                    relative = "instances/" + name + relative[marker..];
                                }

                                AppPaths.SafeChild(source.Paths.Root, relative);
                                return new ReceiptFile(relative, file.Sha1!, file.Size);
                            }
                        )
                            .ToArray()
                    };
                }

                latest = latest!with
                {
                    CommitStage = stage,
                    LoaderPin = selected
                };
                checkpoint(latest);
            }
        );
        GameProfile result;
        if (family is "fabric" or "quilt")
        {
            using var loader = new FabricService(versionService, name, family);
            result = await loader.InstallAsync(version, progress, ct, recovery);
        }
        else if (family is "forge" or "neoforge")
        {
            result = await forge.InstallAsync(family, version, instanceName: name, progress: progress, ct: ct, recovery: recovery);
        }
        else if (family == "optifine")
        {
            using var loader = new OptiFineService(versionService);
            result = await loader.InstallAsync(version, progress, ct, name, recovery);
        }
        else
        {
            using var loader = new LiteLoaderService(versionService);
            result = await loader.InstallAsync(version, progress, ct, name, recovery);
        }

        return new(result.Id, result.Name, result.GameVersion, result.Loader, result.GameDirectory);
    }
}
