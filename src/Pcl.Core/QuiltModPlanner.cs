namespace Pcl.Core;
/// <summary>Searches declared remote project candidates and checks the complete local Quilt graph before commit.</summary>
public sealed class QuiltModPlanner(ResourceCatalog catalog, DownloadService downloads, string cacheRoot, string loaderVersion)
{
    // [实现] 功能：为在线安装找到声明范围内的兼容前置。实现：下载并校验真实候选 JAR，合并当前模组及覆盖配置，全局校验失败后回溯未锁定的项目版本；精确发布锁定不会被放宽。
    public async Task<ResourceInstallPlan> ResolveAsync(
        ResourceRelease root,
        string gameVersion,
        IReadOnlyList<ManagedResource>? installed = null,
        string? gameDirectory = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var inspected = new Dictionary<string, IReadOnlyList<QuiltCandidate>>();
        var domains = new Dictionary<string, IReadOnlyList<ResourceRelease>>();
        var pending = new Stack<Dictionary<string, ResourceRelease>>();
        pending.Push(
            new()
        );
        var seen = new HashSet<string>();
        int states = 0;
        string last = "";
        while (pending.TryPop(out var preferred))
        {
            // [实现] 循环满足 pending.TryPop(out var preferred) 时：执行 ct.ThrowIfCancellationRequested()；准备 var signature = string.Join( '|', preferred.OrderBy( x => x.Key ) .Select( x => x.Key + ":" + x…；按 !seen.Add(signature) 分支处理。
            ct.ThrowIfCancellationRequested();
            var signature = string.Join(
                '|',
                preferred.OrderBy(
                    x => x.Key
                )
                    .Select(
                        x => x.Key + ":" + x.Value.Id
                    )
            );
            if (!seen.Add(signature))
                continue;
            if (++states > 256)
                throw new NotSupportedException("Quilt 远程候选组合超过256组，尚未确定兼容版本；请选择更明确的版本后重试。");
            var visited = new List<ResourceRelease>();
            try
            {
                // [实现] 在异常保护范围内：准备 var plan = await catalog.ResolveAsync(root, ResourceKind.Mod, gameVersion, "quilt", ct, preferr…；准备 var candidates = new List<QuiltCandidate>()；遍历 plan.Releases 处理 release。
                var plan = await catalog.ResolveAsync(root, ResourceKind.Mod, gameVersion, "quilt", ct, preferred, visited.Add);
                var candidates = new List<QuiltCandidate>();
                foreach (var release in plan.Releases)
                {
                    // [实现] 对 plan.Releases 中的每一项：执行 progress?.Report( new("检查 Quilt 前置 · " + release.Name, states, 0, 0, 0) )；按 !inspected.TryGetValue(release.Id, out var read) 分支处理；执行 candidates.AddRange(read)。
                    progress?.Report(
                        new("检查 Quilt 前置 · " + release.Name, states, 0, 0, 0)
                    );
                    if (!inspected.TryGetValue(release.Id, out var read))
                    {
                        // [实现] 条件成立（!inspected.TryGetValue(release.Id, out var read)）时：按 inspected.Count >= 512 分支处理；准备 var path = ResourceInstaller.SafeTarget(cacheRoot, release.File.Sha1)；执行 await downloads.DownloadAsync( new(release.File.Url, path, release.File.Size, release.File.Sha1…。
                        if (inspected.Count >= 512)
                            throw new NotSupportedException("Quilt 下载候选超过512个，未判定兼容。");
                        var path = ResourceInstaller.SafeTarget(cacheRoot, release.File.Sha1);
                        await downloads.DownloadAsync(
                            new(release.File.Url, path, release.File.Size, release.File.Sha1),
                            ct
                        );
                        inspected[release.Id] = read = QuiltDependencies.Read(
                            [
                                path
                            ]
                        );
                    }

                    candidates.AddRange(read);
                }

                var java = 21;
                if (gameDirectory is not null)
                {
                    // [实现] 条件成立（gameDirectory is not null）时：准备 var replaced = (installed ?? []).Where( x => plan.Releases.Any( r => r.ProjectId == x.ProjectId…；执行 candidates.AddRange( QuiltDependencies.Read( QuiltDependencies.InstalledFiles(gameDirectory) .W…；执行 java = VersionCatalog.JavaMajor( JsonFiles.Read( Path.Combine(gameDirectory, "version.json") ) …。
                    var replaced = (installed ?? []).Where(
                        x => plan.Releases.Any(
                            r => r.ProjectId == x.ProjectId
                        )
                    )
                        .Select(
                            x => Path.GetFullPath(
                                Path.Combine(gameDirectory, "mods", x.FileName)
                            )
                        )
                        .ToHashSet(StringComparer.Ordinal);
                    candidates.AddRange(
                        QuiltDependencies.Read(
                            QuiltDependencies.InstalledFiles(gameDirectory)
                                .Where(
                                    x => !replaced.Contains(
                                        Path.GetFullPath(x)
                                    )
                                )
                        )
                    );
                    java = VersionCatalog.JavaMajor(
                        JsonFiles.Read(
                            Path.Combine(gameDirectory, "version.json")
                        )
                    );
                }

                await Task.Run(
                    () => QuiltDependencies.Resolve(candidates, gameVersion, loaderVersion, java, ct, gameDirectory),
                    ct
                );
                return plan;
            }
            catch (InvalidDataException ex)
            {
                // [实现] 捕获 (InvalidDataException ex) 后：执行 last = ex.Message。
                last = ex.Message;
            }

            // Exact version pins stay exact. Search choices are only projects from unpinned required edges.
            var projects = visited.SelectMany(
                x => x.Dependencies
            )
                .Where(
                    x => x.Type == "required" && x.VersionId is null && x.ProjectId is not null && x.ProjectId != root.ProjectId
                )
                .Select(
                    x => x.ProjectId!
                )
                .Distinct()
                .ToArray();
            foreach (var project in projects.Reverse())
            {
                // [实现] 对 projects.Reverse() 中的每一项：按 !domains.TryGetValue(project, out var versions) 分支处理；遍历 domains[project].Reverse() 处理 candidate。
                if (!domains.TryGetValue(project, out var versions))
                {
                    // [实现] 条件成立（!domains.TryGetValue(project, out var versions)）时：执行 versions = await catalog.VersionsAsync(project, ResourceKind.Mod, gameVersion, "quilt", ct)；按 versions.Count > 64 分支处理；执行 domains[project] = versions.OrderBy( x => x.Channel == "release" ? 0 : 1 ) .ToArray()。
                    versions = await catalog.VersionsAsync(project, ResourceKind.Mod, gameVersion, "quilt", ct);
                    if (versions.Count > 64)
                        throw new NotSupportedException("Quilt 项目有超过64个适用候选，请指定依赖版本后重试：" + project);
                    domains[project] = versions.OrderBy(
                        x => x.Channel == "release"
                            ? 0
                            : 1
                    )
                        .ToArray();
                }

                foreach (var candidate in domains[project].Reverse())
                {
                    // [实现] 对 domains[project].Reverse() 中的每一项：按 visited.Any( x => x.ProjectId == project && x.Id == candidate.Id ) 分支处理；准备 var next = new Dictionary<string, ResourceRelease>(preferred) { [project] = candidate }；执行 pending.Push(next)。
                    if (visited.Any(
                        x => x.ProjectId == project && x.Id == candidate.Id
                    ))
                        continue;
                    var next = new Dictionary<string, ResourceRelease>(preferred)
                    {
                        [project] = candidate
                    };
                    pending.Push(next);
                }
            }
        }

        throw new InvalidDataException("Quilt 声明的项目候选中没有兼容组合。若缺失的模组未列入远程前置，请先选择对应项目：" + last);
    }
}
