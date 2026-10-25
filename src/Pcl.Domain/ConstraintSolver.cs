namespace Pcl.Core;
/// <summary>Bounded weighted Boolean solver. Exhaustion is reported as unsupported, never as compatible.</summary>
internal sealed class ConstraintSolver
{
    private readonly List<int[]> clauses = [];
    private readonly List<int> costs = [
        0
    ];
    private int nodes;
    // [实现] 功能：分配布尔候选及选择代价。实现：变量索引从 1 开始，记录权重并限制总量，避免不受控的输入规模。
    public int Variable(int cost = 0)
    {
        if (costs.Count > 6000)
            throw new NotSupportedException("依赖变量超过求解上限。");
        costs.Add(cost);
        return costs.Count - 1;
    }

    // [实现] 功能：追加一个析取规则。实现：去除重复文字和恒真子句，对规则数设置上限；空子句保留为不可满足条件。
    public void Add(params int[] literals)
    {
        var row = literals.Distinct()
            .ToArray();
        if (row.Any(
            x => row.Contains(-x)
        ))
            return;
        if (clauses.Count >= 60000)
            throw new NotSupportedException("依赖规则超过求解上限。");
        clauses.Add(row);
    }

    // [实现] 功能：构造逻辑 OR。实现：创建辅助变量，通过双向蕴含子句保证辅助变量与输入析取等价。
    public int Any(IEnumerable<int> input)
    {
        var a = input.Distinct()
            .ToArray();
        if (a.Length == 1)
            return a[0];
        var v = Variable();
        Add(
            a.Append(-v)
                .ToArray()
        );
        foreach (var x in a)
            Add(v, -x);
        return v;
    }

    // [实现] 功能：构造逻辑 AND。实现：按德摩根律对输入取反后复用 OR 编码。
    public int All(IEnumerable<int> input) => -Any(
        input.Select(
            x => -x
        )
    );
    // [实现] 功能：求代价最小的可满足选择。实现：单元传播固定必然值，以权重下界剪枝，再尝试两种赋值；取消、节点数和耗时限制防止界面无限等待。
    public bool[]? Solve(CancellationToken ct = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long best = long.MaxValue;
        sbyte[]? answer = null;
        // [实现] 功能：探索当前布尔赋值。实现：先传播约束并排除冲突，再计算代价下界，最后选择未赋值变量递归搜索；只有完整可满足解才更新答案。
        void Search(sbyte[] values)
        {
            ct.ThrowIfCancellationRequested();
            if (++nodes > 250000 || watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new NotSupportedException("依赖组合超过求解预算；未判定兼容，请减少冲突候选后重试。");
            bool change;
            do
            {
                // [实现] 本段流程：执行 change = false；遍历 clauses 处理 row。
                change = false;
                foreach (var row in clauses)
                {
                    // [实现] 对 clauses 中的每一项：准备 bool satisfied = false；准备 int unassigned = 0, last = 0；遍历 row 处理 literal。
                    bool satisfied = false;
                    int unassigned = 0, last = 0;
                    foreach (var literal in row)
                    {
                        // [实现] 对 row 中的每一项：准备 var value = values[Math.Abs(literal)]；按 value == 0 分支处理。
                        var value = values[Math.Abs(literal)];
                        if (value == 0)
                        {
                            // [实现] 条件成立（value == 0）时：执行 unassigned++；执行 last = literal。
                            unassigned++;
                            last = literal;
                        }
                        else if (value == (literal > 0
                            ? 1
                            : -1))
                        {
                            // [实现] 条件成立（value == (literal > 0 ? 1 : -1)）时：执行 satisfied = true；执行 break;。
                            satisfied = true;
                            break;
                        }
                    }

                    if (satisfied)
                        continue;
                    if (unassigned == 0)
                        return;
                    if (unassigned == 1)
                    {
                        // [实现] 条件成立（unassigned == 1）时：准备 var id = Math.Abs(last)；准备 var wanted = (sbyte)(last > 0 ? 1 : -1)；按 values[id] != 0 && values[id] != wanted 分支处理。
                        var id = Math.Abs(last);
                        var wanted = (sbyte)(last > 0
                            ? 1
                            : -1);
                        if (values[id] != 0 && values[id] != wanted)
                            return;
                        if (values[id] == 0)
                        {
                            // [实现] 条件成立（values[id] == 0）时：执行 values[id] = wanted；执行 change = true。
                            values[id] = wanted;
                            change = true;
                        }
                    }
                }
            }
            while (change);
            long lower = 0;
            for (int i = 1; i < values.Length; i++)
                if (values[i] > 0)
                    lower += costs[i];
                else if (values[i] == 0)
                    lower += Math.Min(0, costs[i]);
            if (lower >= best)
                return;
            int next = 0;
            foreach (var row in clauses)
            {
                // [实现] 对 clauses 中的每一项：按 row.Any( x => values[Math.Abs(x)] == (x > 0 ? 1 : -1) ) 分支处理；执行 next = row.Where( x => values[Math.Abs(x)] == 0 ) .Select(Math.Abs) .OrderByDescending( x => Ma…；按 next != 0 分支处理。
                if (row.Any(
                    x => values[Math.Abs(x)] == (x > 0
                        ? 1
                        : -1)
                ))
                    continue;
                next = row.Where(
                    x => values[Math.Abs(x)] == 0
                )
                    .Select(Math.Abs)
                    .OrderByDescending(
                        x => Math.Abs(costs[x])
                    )
                    .FirstOrDefault();
                if (next != 0)
                    break;
            }

            if (next == 0)
            {
                // [实现] 条件成立（next == 0）时：在 i < values.Length 条件下迭代；执行 answer = values；执行 best = lower。
                for (int i = 1; i < values.Length; i++)
                    if (values[i] == 0)
                        values[i] = (sbyte)(costs[i] < 0
                            ? 1
                            : -1);
                answer = values;
                best = lower;
                return;
            }

            var preferred = (sbyte)(costs[next] < 0
                ? 1
                : -1);
            var branch = (sbyte[])values.Clone();
            branch[next] = preferred;
            Search(branch);
            values[next] = (sbyte)-preferred;
            Search(values);
        }

        Search(new sbyte[costs.Count]);
        return answer?.Select(
            x => x > 0
        )
            .ToArray();
    }
}
