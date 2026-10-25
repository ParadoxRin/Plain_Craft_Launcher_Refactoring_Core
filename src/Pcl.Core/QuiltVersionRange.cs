using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Pcl.Core;
/// <summary>Quilt version expressions; bare semantic versions and caret use the next major, including 0.x.</summary>
public static class QuiltVersionRange
{
    // [实现] 数据约定：Interval 汇集 Low、LowClosed、High、HighClosed，供该模块传递状态。
    private sealed record Interval(string? Low, bool LowClosed, string? High, bool HighClosed);
    // [实现] Semantic 的实现：计算并返回 QuiltVersion.Semantic(text)。
    private static bool Semantic(string text) => QuiltVersion.Semantic(text);
    // [实现] Compare 的实现：计算并返回 QuiltVersion.Compare(a, b)。
    public static int Compare(string a, string b) => QuiltVersion.Compare(a, b);
    // [实现] 功能：判断版本是否属于 Quilt 条件。实现：解析成规范化区间，对任一区间满足上下界即返回真。
    public static bool Matches(string version, JToken? expression) => Parse(expression, 0)
        .Any(
            x => Contains(x, version)
        );
    /// <summary>将表达式化为区间，再按官方版本对象的原始端点文本判断覆盖条件相等。</summary>
    public static bool Equivalent(JToken? left, JToken? right)
    {
        var a = Normalize(
            Parse(left, 0)
        );
        var b = Normalize(
            Parse(right, 0)
        );
        // [实现] Same 的实现：计算并返回 string.Equals(x, y, StringComparison.Ordinal)。
        bool Same(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);
        return a.Count == b.Count && a.Zip(b)
            .All(
                x => Same(x.First.Low, x.Second.Low) && Same(x.First.High, x.Second.High) && x.First.LowClosed == x.Second.LowClosed && x.First.HighClosed == x.Second.HighClosed
            );
    }

    // [实现] Contains 的实现：计算并返回 (x.Low is null || (x.LowClosed ? Compare(v, x.Low) >= 0 : Compare(v, x.Low) > 0)) && (x.High is…。
    private static bool Contains(Interval x, string v) => (x.Low is null || (x.LowClosed
        ? Compare(v, x.Low) >= 0
        : Compare(v, x.Low) > 0)) && (x.High is null || (x.HighClosed
        ? Compare(v, x.High) <= 0
        : Compare(v, x.High) < 0));
    // [实现] 功能：展开 Quilt 版本条件。实现：字符串生成单区间，旧数组与 any 求并集，all 求交集，同时限制层数、大小和非法无限范围。
    private static List<Interval> Parse(JToken? value, int depth)
    {
        if (depth > 32)
            throw new InvalidDataException("Quilt 版本表达式过深。");
        if (value is null)
            return[
                new(null, false, null, false)
            ];
        if (value.Type == JTokenType.String)
            return[
                Single((string)value!)
            ];
        if (value is JArray old)
        {
            // [实现] 条件成立（value is JArray old）时：按 old.Count == 0 || old.Count > 256 || old.Any( x => x.Type != JTokenType.String ) 分支处理；返回 Normalize( old.SelectMany( x => Parse(x, depth + 1) ) )。
            if (old.Count == 0 || old.Count > 256 || old.Any(
                x => x.Type != JTokenType.String
            ))
                throw new InvalidDataException("Quilt 旧版本数组必须包含字符串。");
            return Normalize(
                old.SelectMany(
                    x => Parse(x, depth + 1)
                )
            );
        }

        if (value is not JObject obj || obj.Count != 1 || obj.Properties()
            .Single()
            .Name is not ("any" or "all") || obj.Properties()
            .Single()
            .Value is not JArray parts || parts.Count is 0 or > 256 || parts.Any(
            x => x is not JObject && x.Type != JTokenType.String
        ))
            throw new InvalidDataException("Quilt 版本范围需要非空 any/all 数组。");
        List<Interval> result = [];
        foreach (var part in parts)
        {
            // [实现] 对 parts 中的每一项：准备 var next = Parse(part, depth + 1)；执行 result = result.Count == 0 ? next : obj["any"] is not null ? Normalize( result.Concat(next) ) :…；按 result.Count == 0 && obj["all"] is not null 分支处理。
            var next = Parse(part, depth + 1);
            result = result.Count == 0
                ? next
                : obj["any"] is not null
                    ? Normalize(
                        result.Concat(next)
                    )
                    : Intersect(result, next);
            if (result.Count == 0 && obj["all"] is not null)
                throw new InvalidDataException("Quilt all 版本范围没有交集。");
        }

        result = Normalize(result);
        if (result.Count == 0 || result.Count == 1 && result[0].Low is null && result[0].High is null)
            throw new InvalidDataException("Quilt any/all 必须限制版本范围。");
        return result;
    }

    // [实现] 功能：解析单条版本表达式。实现：区分比较符、通配、裸语义版本、caret、tilde 与原始字符串，并按 Quilt 规则计算上界。
    private static Interval Single(string s)
    {
        if (s.Length is 0 or > 512)
            throw new InvalidDataException("无效 Quilt 版本范围。");
        if (s == "*")
            return new(null, false, null, false);
        foreach (var op in new[]
        {
            ">=",
            "<=",
            ">",
            "<",
            "="
        }

        )
            if (s.StartsWith(op, StringComparison.Ordinal))
            {
                // [实现] 条件成立（s.StartsWith(op, StringComparison.Ordinal)）时：准备 var v = s[op.Length..]；按 s.Length == 1 && s[0] is '>' or '<' 分支处理；返回 op switch { ">=" => new(v, true, null, false), ">" => new(v, false, null, false), "<=" => new(n…。
                var v = s[op.Length..];
                if (s.Length == 1 && s[0] is '>' or '<')
                    throw new InvalidDataException("无效 Quilt 比较版本。");
                return op switch
                {
                    ">=" => new(v, true, null, false),
                    ">" => new(v, false, null, false),
                    "<=" => new(null, false, v, true),
                    "<" => new(null, false, v, false),
                    _ => new(v, true, v, true)
                };
            }

        string lower = s, upper;
        bool wildcard = s.EndsWith(".x", StringComparison.Ordinal);
        var modifier = s[0];
        if (modifier is '~' or '^')
            lower = s[1..];
        else if (wildcard)
            lower = s[..^2];
        if (!Semantic(lower))
        {
            // [实现] 条件成立（!Semantic(lower)）时：按 modifier is '~' or '^' || wildcard 分支处理；返回 new(s, true, s, true)。
            if (modifier is '~' or '^' || wildcard)
                throw new InvalidDataException("此 Quilt 范围需要语义版本。");
            return new(s, true, s, true);
        }

        var p = lower.Split('+')[0].Split('-')[0].Split('.')
            .Select(int.Parse)
            .ToArray();
        try
        {
            // [实现] 在异常保护范围内：执行 checked { if (wildcard) { p[^1]++; upper = string.Join('.', p); } else if (modifier == '~') { u…。
            checked
            {
                // [实现] 本段流程：按 wildcard 分支处理。
                if (wildcard)
                {
                    // [实现] 条件成立（wildcard）时：执行 p[^1]++；执行 upper = string.Join('.', p)。
                    p[^1]++;
                    upper = string.Join('.', p);
                }
                else if (modifier == '~')
                {
                    // [实现] 条件成立（modifier == '~'）时：执行 upper = p[0] + "." + (p.Length > 1 ? p[1] + 1 : 1)。
                    upper = p[0] + "." + (p.Length > 1
                        ? p[1] + 1
                        : 1);
                }
                else
                    upper = (p[0] + 1).ToString();
            }
        }
        catch (OverflowException)
        {
            // [实现] 捕获 (OverflowException) 后：抛出 new InvalidDataException("Quilt 版本范围溢出。")。
            throw new InvalidDataException("Quilt 版本范围溢出。");
        }

        return new(lower, true, upper, false);
    }

    // [实现] 功能：计算区间集合交集。实现：逐对取较大下界和较小上界，组合端点闭合性后丢弃空交集，再规范化。
    private static List<Interval> Intersect(List<Interval> a, List<Interval> b)
    {
        var r = new List<Interval>();
        foreach (var x in a)
            foreach (var y in b)
            {
                // [实现] 对 b 中的每一项：准备 string? low；准备 bool lc；准备 string? high。
                string? low;
                bool lc;
                string? high;
                bool hc;
                if (x.Low is null)
                {
                    // [实现] 条件成立（x.Low is null）时：执行 low = y.Low；执行 lc = y.LowClosed。
                    low = y.Low;
                    lc = y.LowClosed;
                }
                else if (y.Low is null)
                {
                    // [实现] 条件成立（y.Low is null）时：执行 low = x.Low；执行 lc = x.LowClosed。
                    low = x.Low;
                    lc = x.LowClosed;
                }
                else
                {
                    // [实现] 前一条件不成立时：准备 int c = Compare(x.Low, y.Low)；执行 low = c >= 0 ? x.Low : y.Low；执行 lc = c == 0 ? x.LowClosed && y.LowClosed : c > 0 ? x.LowClosed : y.LowClosed。
                    int c = Compare(x.Low, y.Low);
                    low = c >= 0
                        ? x.Low
                        : y.Low;
                    lc = c == 0
                        ? x.LowClosed && y.LowClosed
                        : c > 0
                            ? x.LowClosed
                            : y.LowClosed;
                }

                if (x.High is null)
                {
                    // [实现] 条件成立（x.High is null）时：执行 high = y.High；执行 hc = y.HighClosed。
                    high = y.High;
                    hc = y.HighClosed;
                }
                else if (y.High is null)
                {
                    // [实现] 条件成立（y.High is null）时：执行 high = x.High；执行 hc = x.HighClosed。
                    high = x.High;
                    hc = x.HighClosed;
                }
                else
                {
                    // [实现] 前一条件不成立时：准备 int c = Compare(x.High, y.High)；执行 high = c <= 0 ? x.High : y.High；执行 hc = c == 0 ? x.HighClosed && y.HighClosed : c < 0 ? x.HighClosed : y.HighClosed。
                    int c = Compare(x.High, y.High);
                    high = c <= 0
                        ? x.High
                        : y.High;
                    hc = c == 0
                        ? x.HighClosed && y.HighClosed
                        : c < 0
                            ? x.HighClosed
                            : y.HighClosed;
                }

                if (low is null || high is null || Compare(low, high) < 0 || Compare(low, high) == 0 && lc && hc)
                    r.Add(
                        new(low, lc, high, hc)
                    );
                if (r.Count > 4096)
                    throw new InvalidDataException("Quilt 版本区间过多。");
            }

        return Normalize(r);
    }

    // [实现] 功能：合并可连续表示的区间。实现：按下界排序，仅在重叠或至少一侧闭合的相接端点合并，保留开闭边界。
    private static List<Interval> Normalize(IEnumerable<Interval> source)
    {
        var sorted = source.OrderBy(
            x => x.Low,
            Comparer<string?>.Create(
                (a, b) => a is null
                    ? b is null
                        ? 0
                        : -1
                    : b is null
                        ? 1
                        : Compare(a, b)
            )
        )
            .ThenByDescending(
                x => x.LowClosed
            );
        var r = new List<Interval>();
        foreach (var x in sorted)
        {
            // [实现] 对 sorted 中的每一项：按 r.Count == 0 分支处理；准备 var last = r[^1]；按 last.High is null 分支处理。
            if (r.Count == 0)
            {
                // [实现] 条件成立（r.Count == 0）时：执行 r.Add(x)；执行 continue;。
                r.Add(x);
                continue;
            }

            var last = r[^1];
            if (last.High is null)
                continue;
            if (x.Low is null || Compare(x.Low, last.High) < 0 || Compare(x.Low, last.High) == 0 && (x.LowClosed || last.HighClosed))
            {
                // [实现] 条件成立（x.Low is null || Compare(x.Low, last.High) < 0 || Compare(x.Low, last.High) == 0 && (x.LowClose…）时：按 x.High is null || Compare(x.High, last.High) > 0 分支处理。
                if (x.High is null || Compare(x.High, last.High) > 0)
                    r[^1] = last with
                    {
                        High = x.High,
                        HighClosed = x.HighClosed
                    };
                else if (Compare(x.High, last.High) == 0)
                    r[^1] = last with
                    {
                        HighClosed = last.HighClosed || x.HighClosed
                    };
            }
            else
                r.Add(x);
            if (r.Count > 4096)
                throw new InvalidDataException("Quilt 版本区间过多。");
        }

        return r;
    }
}
