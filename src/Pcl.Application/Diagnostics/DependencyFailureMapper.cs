using Pcl.Domain;
using Pcl.Core.Contracts.Diagnostics;

namespace Pcl.Core.Application.Diagnostics;
/// <summary>Maps domain evidence into a bounded transport contract, without serializing exception text or stack traces.</summary>
public static class DependencyFailureMapper
{
    public static DependencyDiagnostic? From(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is not FabricResolutionException failure)
            {
                continue;
            }

            var result = failure.Resolution;
            if (result.Status == "limit")
            {
                return new(
                    "DEPENDENCY_SEARCH_LIMIT",
                    "依赖搜索达到预算，尚未判定兼容性；请缩小候选集合。",
                    []
                );
            }

            string Safe(string text, int length = 120) => new(
                text.Where(
                    c => !char.IsControl(c)
                )
                    .Take(length)
                    .ToArray()
            );
            var lines = result.Conflicts.Take(16)
                .Select(
                    x =>
                    {
                        var relation = x.Relation switch
                        {
                            "depends" => "需要",
                            "breaks" => "排斥",
                            "locked" => "已锁定",
                            "parent" => "需要启用父包",
                            "unique" => "不能同时提供同一 ID",
                            _ => "约束"
                        };
                        return Safe(x.Source) + " [" + Safe(x.Origin, 180) + "] → " + relation + " " + Safe(x.Target) + " " + Safe(x.Range) + (x.Candidates.Count == 0
                            ? ""
                            : "；候选：" + string.Join(
                                "、",
                                x.Candidates.Take(4)
                                    .Select(
                                        c => Safe(c, 180)
                                    )
                            ));
                    }
                )
                .ToArray();
            return new("DEPENDENCY_CONFLICT", "Fabric 依赖冲突；顶层模组保持锁定。以下约束无法同时满足：", lines);
        }

        return null;
    }
}
