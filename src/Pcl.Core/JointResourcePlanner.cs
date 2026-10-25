namespace Pcl.Core;
/// <summary>对多个已选根版本联合回溯其声明的远程前置；整套实际JAR必须通过既有加载器校验才返回。</summary>
public static class JointResourcePlanner
{
    public static async Task<ResourceInstallPlan> ResolveAsync(
        ResourceCatalog catalog,
        IReadOnlyList<ResourceRelease> roots,
        ResourceKind kind,
        string gameVersion,
        string loader,
        Func<ResourceInstallPlan, CancellationToken, Task> validateCandidate,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default
    )
    {
        var locked = roots.Select(
            x => x.ProjectId
        )
            .ToHashSet(StringComparer.Ordinal);
        var domains = new Dictionary<string, IReadOnlyList<ResourceRelease>>(StringComparer.Ordinal);
        var pending = new Stack<Dictionary<string, ResourceRelease>>();
        pending.Push(
            new()
        );
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = "";
        var attempts = 0;
        while (pending.TryPop(out var choices))
        {
            ct.ThrowIfCancellationRequested();
            var signature = string.Join(
                "|",
                choices.OrderBy(
                    x => x.Key,
                    StringComparer.Ordinal
                )
                    .Select(
                        x => x.Key + ":" + x.Value.Id
                    )
            );
            if (!seen.Add(signature))
            {
                continue;
            }

            if (++attempts > 256)
            {
                throw new NotSupportedException("联合依赖候选超过256组；尚未判定无解，请缩小版本范围后重试。");
            }

            var visited = new List<ResourceRelease>();
            try
            {
                var plan = await catalog.ResolveManyAsync(roots, kind, gameVersion, loader, ct, choices, visited.Add);
                progress?.Report(
                    new("检查联合依赖组合 " + attempts, 0, 0, 0, 0)
                );
                await validateCandidate(plan, ct);
                return plan;
            }
            catch (InvalidDataException ex)
            {
                // 仅兼容性失败允许换未锁定的前置；取消、网络和文件访问异常仍向调用者传播。
                failures = ex.Message;
            }

            var projects = visited.SelectMany(
                x => x.Dependencies
            )
                .Where(
                    x => x.Type == "required" && x.VersionId is null && x.ProjectId is not null && !locked.Contains(x.ProjectId)
                )
                .Select(
                    x => x.ProjectId!
                )
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (var project in projects.Reverse())
            {
                if (!domains.TryGetValue(project, out var versions))
                {
                    versions = await catalog.VersionsAsync(project, kind, gameVersion, loader, ct);
                    if (versions.Count > 64)
                    {
                        throw new NotSupportedException("联合求解项目候选超过64个，请指定更明确版本：" + project);
                    }

                    domains[project] = versions.OrderBy(
                        x => x.Channel == "release"
                            ? 0
                            : 1
                    )
                        .ToArray();
                }

                foreach (var candidate in domains[project].Reverse())
                {
                    if (visited.Any(
                        x => x.ProjectId == project && x.Id == candidate.Id
                    ))
                    {
                        continue;
                    }

                    var next = new Dictionary<string, ResourceRelease>(choices)
                    {
                        [project] = candidate
                    };
                    if (pending.Count >= 16384)
                    {
                        throw new NotSupportedException("联合依赖待检查组合超过16384组；尚未判定无解。");
                    }

                    pending.Push(next);
                }
            }
        }

        throw new InvalidDataException("已声明的前置候选中没有同时满足全部根版本的组合：" + failures);
    }
}
