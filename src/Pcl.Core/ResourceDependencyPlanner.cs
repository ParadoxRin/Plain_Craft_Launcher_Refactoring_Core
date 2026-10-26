using System.Security.Cryptography;
using Newtonsoft.Json;

namespace Pcl.Core;
/// <summary>One root-set planning entry for individual installs, batch updates and locked pack files.</summary>
public static class ResourceDependencyPlanner
{
    public static Task<ResourceInstallPlan> ResolveAsync(
        ResourceCatalog catalog,
        DownloadService downloads,
        ResourceRelease release,
        ResourceKind kind,
        GameProfile profile,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    ) => ResolveManyAsync(
        catalog,
        downloads,
        [
            release
        ],
        kind,
        profile,
        progress,
        ct
    );
    /// <summary>Try complete candidates only in a private snapshot; live mods remain untouched until the caller commits.</summary>
    public static async Task<ResourceInstallPlan> ResolveManyAsync(
        ResourceCatalog catalog,
        DownloadService downloads,
        IReadOnlyList<ResourceRelease> roots,
        ResourceKind kind,
        GameProfile profile,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        string? libraryContextDirectory = null
    )
    {
        var directory = Path.GetFullPath(profile.GameDirectory);
        Directory.CreateDirectory(directory);
        var before = Snapshot(directory);
        var staging = ResourceInstaller.SafeTarget(
            directory,
            ".pcl-planning/" + Guid.NewGuid()
                .ToString("N")
        );
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in before.Keys)
            {
                ct.ThrowIfCancellationRequested();
                var target = ResourceInstaller.SafeTarget(staging, file);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(target)!
                );
                File.Copy(
                    ResourceInstaller.SafeTarget(directory, file),
                    target
                );
            }

            Check(
                before,
                Snapshot(staging)
            );
            // This same joint search locks every selected root and backtracks only unpinned dependencies.
            var result = await JointResourcePlanner.ResolveAsync(
                catalog,
                roots,
                kind,
                profile.GameVersion,
                profile.Loader,
                async (candidate, cancellation) =>
                {
                    foreach (var release in candidate.Releases)
                    {
                        var cached = ResourceInstaller.SafeTarget(directory, ".pcl-downloads/" + release.File.Sha1);
                        if (DownloadService.IsValid(
                            new(release.File.Url, cached, release.File.Size, release.File.Sha1)
                        ))
                        {
                            var copy = ResourceInstaller.SafeTarget(staging, ".pcl-downloads/" + release.File.Sha1);
                            Directory.CreateDirectory(
                                Path.GetDirectoryName(copy)!
                            );
                            File.Copy(cached, copy, true);
                        }
                    }

                    await new ResourceInstaller(
                        staging,
                        downloads,
                        profile.Loader,
                        profile.GameVersion,
                        libraryContextDirectory ?? directory
                    ).InstallAsync(candidate, progress, cancellation);
                },
                progress,
                ct
            );
            Check(
                before,
                Snapshot(directory)
            );
            // Cache only verified downloads, never copy candidate mods or modified configuration to live state.
            foreach (var release in result.Releases)
            {
                var cache = ResourceInstaller.SafeTarget(directory, ".pcl-downloads/" + release.File.Sha1);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(cache)!
                );
                File.Copy(
                    ResourceInstaller.SafeTarget(staging, ".pcl-downloads/" + release.File.Sha1),
                    cache,
                    true
                );
            }

            return result;
        }
        finally
        {
            Directory.Delete(staging, true);
        }
    }

    /// <summary>Identify pack files by verified hash, lock their exact versions, and provision missing declared dependencies.</summary>
    public static async Task ResolvePackAsync(
        ResourceCatalog catalog,
        DownloadService downloads,
        GameProfile profile,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        if (profile.Loader is not ("fabric" or "quilt" or "forge" or "neoforge"))
        {
            new LocalResourceService(profile.GameDirectory, profile.Loader, profile.GameVersion).ValidateEnabledResources(ct);
            return;
        }

        var installer = new ResourceInstaller(profile.GameDirectory, downloads, profile.Loader, profile.GameVersion);
        var records = installer.List()
            .ToList();
        var roots = new List<ResourceRelease>();
        // Complete/offline packs use the same planner with file-hash-locked local roots first.
        // Only an actual missing/conflicting set needs remote identity and dependency discovery.
        var localRoots = new List<ResourceRelease>();
        foreach (var item in records.Where(
            x => x.Enabled && x.Kind == ResourceKind.Mod
        ))
        {
            var cache = ResourceInstaller.SafeTarget(profile.GameDirectory, ".pcl-downloads/" + item.Sha1);
            Directory.CreateDirectory(
                Path.GetDirectoryName(cache)!
            );
            File.Copy(
                ResourceInstaller.SafeTarget(profile.GameDirectory, "mods/" + item.FileName),
                cache,
                true
            );
            localRoots.Add(
                new(
                    item.VersionId,
                    item.ProjectId,
                    item.Name,
                    item.Version,
                    "release",
                    item.InstalledAt,
                    [
                        profile.GameVersion
                    ],
                    [
                        profile.Loader
                    ],
                    new(
                        item.FileName,
                        new Uri("https://localhost/locked-pack-file"),
                        item.Size,
                        item.Sha1
                    ),
                    []
                )
            );
        }

        if (localRoots.Count == 0)
        {
            new LocalResourceService(profile.GameDirectory, profile.Loader, profile.GameVersion).ValidateEnabledResources(ct);
            return;
        }

        try
        {
            await ResolveManyAsync(catalog, downloads, localRoots, ResourceKind.Mod, profile, progress, ct);
            return;
        }
        catch (InvalidDataException)
        {
            // Source files remain fixed. Network, cancellation and unsupported semantics are never swallowed.
        }

        foreach (var item in records.Where(
            x => x.Enabled && x.Kind == ResourceKind.Mod
        )
            .ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var release = await catalog.VersionFromHashAsync(item.Sha1, ct);
            if (release is null)
            {
                // Unpublished local jars stay pinned in the snapshot and participate in metadata validation.
                continue;
            }

            if (release.File.Sha1 != item.Sha1 || release.File.Size != item.Size)
            {
                throw new InvalidDataException("整合包文件与远程版本身份不一致：" + item.FileName);
            }

            var root = release with
            {
                File = release.File with
                {
                    Name = item.FileName
                }
            };
            roots.Add(root);
            records[records.IndexOf(item)] = item with
            {
                ProjectId = release.ProjectId,
                VersionId = release.Id,
                Dependencies = release.Dependencies
            };
            var cache = ResourceInstaller.SafeTarget(profile.GameDirectory, ".pcl-downloads/" + item.Sha1);
            Directory.CreateDirectory(
                Path.GetDirectoryName(cache)!
            );
            File.Copy(
                ResourceInstaller.SafeTarget(profile.GameDirectory, "mods/" + item.FileName),
                cache,
                true
            );
        }

        JsonFiles.Write(
            ResourceInstaller.SafeTarget(profile.GameDirectory, ".pcl-resources.json"),
            records
        );
        if (roots.Count > 0)
        {
            var plan = await ResolveManyAsync(catalog, downloads, roots, ResourceKind.Mod, profile, progress, ct);
            await installer.InstallAsync(plan, progress, ct, "pack:" + profile.Id);
        }

        new LocalResourceService(profile.GameDirectory, profile.Loader, profile.GameVersion).ValidateEnabledResources(ct);
    }

    private static Dictionary<string, string> Snapshot(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        long bytes = 0;
        void Add(string relative)
        {
            var file = ResourceInstaller.SafeTarget(directory, relative);
            if (!File.Exists(file))
            {
                return;
            }

            using var input = File.OpenRead(file);
            bytes = checked(bytes + input.Length);
            if (result.Count >= 100_000 || bytes > 8L * 1024 * 1024 * 1024)
            {
                throw new NotSupportedException("依赖规划快照超过 8 GiB / 100000 文件，请缩小实例。");
            }

            result[relative] = Convert.ToHexString(
                SHA256.HashData(input)
            );
        }

        void Walk(string relative)
        {
            var folder = ResourceInstaller.SafeTarget(directory, relative);
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var child = Path.GetRelativePath(directory, entry)
                    .Replace('\\', '/');
                ResourceInstaller.SafeTarget(directory, child);
                if (Directory.Exists(entry))
                {
                    Walk(child);
                }
                else
                {
                    Add(child);
                }
            }
        }

        foreach (var folder in new[]
        {
            "mods",
            "shaderpacks",
            "resourcepacks"
        }

        )
        {
            Walk(folder);
        }

        foreach (var file in new[]
        {
            ".pcl-resources.json",
            "profile.json",
            "version.json",
            QuiltDependencyOverrides.RelativePath,
            FabricDependencyOverrides.RelativePath
        }

        )
        {
            Add(file);
        }

        return result;
    }

    private static void Check(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual
    )
    {
        if (expected.Count != actual.Count || expected.Any(
            x => actual.GetValueOrDefault(x.Key) != x.Value
        ))
        {
            throw new IOException("依赖规划期间文件或覆盖配置发生变化，未提交；请重新规划。");
        }
    }
}
