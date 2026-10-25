using System.Text;
using System.Text.RegularExpressions;

namespace Pcl.Core;
// Quilt 依赖兼容：读取候选与版本条件，建立或检查约束，仅提交能同时满足当前实例条件的组合。
internal static class QuiltVersion
{
    // [实现] Semantic 的实现：准备 var parts = value.Split('+', 2)[0].Split('-', 2)；返回 Regex.IsMatch(parts[0], @"^[0-9]+(?:\.[0-9]+)*$") && parts[0].Split('.') .All( x => int.TryPars…。
    public static bool Semantic(string value)
    {
        var parts = value.Split('+', 2)[0].Split('-', 2);
        return Regex.IsMatch(parts[0], @"^[0-9]+(?:\.[0-9]+)*$") && parts[0].Split('.')
            .All(
                x => int.TryParse(x, out _)
            ) && (parts.Length == 1 || Regex.IsMatch(parts[1], @"^(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"));
    }

    // [实现] Compare 的实现：按 !Semantic(left) || !Semantic(right) 分支处理；准备 var a = left.Split('+', 2)[0].Split('-', 2)；准备 var b = right.Split('+', 2)[0].Split('-', 2)。
    public static int Compare(string left, string right)
    {
        if (!Semantic(left) || !Semantic(right))
            return RawCompare(left, right);
        var a = left.Split('+', 2)[0].Split('-', 2);
        var b = right.Split('+', 2)[0].Split('-', 2);
        var an = a[0].Split('.')
            .Select(int.Parse)
            .ToArray();
        var bn = b[0].Split('.')
            .Select(int.Parse)
            .ToArray();
        for (int i = 0; i < Math.Max(an.Length, bn.Length); i++)
        {
            // [实现] 每次迭代（i < Math.Max(an.Length, bn.Length)）：准备 int c = (i < an.Length ? an[i] : 0).CompareTo( i < bn.Length ? bn[i] : 0 )；按 c != 0 分支处理。
            int c = (i < an.Length
                ? an[i]
                : 0).CompareTo(
                i < bn.Length
                    ? bn[i]
                    : 0
            );
            if (c != 0)
                return c;
        }

        if (a.Length != b.Length)
            return a.Length == 1
                ? 1
                : -1;
        if (a.Length == 1)
            return 0;
        var ap = a[1].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var bp = b[1].Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            // [实现] 每次迭代（i < Math.Min(ap.Length, bp.Length)）：准备 bool ad = Regex.IsMatch(ap[i], @"^(0|[1-9][0-9]*)$"), bd = Regex.IsMatch(bp[i], @"^(0|[1-9][0-9…；按 ad != bd 分支处理；按 ad && ap[i].Length != bp[i].Length 分支处理。
            bool ad = Regex.IsMatch(ap[i], @"^(0|[1-9][0-9]*)$"), bd = Regex.IsMatch(bp[i], @"^(0|[1-9][0-9]*)$");
            if (ad != bd)
                return ad
                    ? -1
                    : 1;
            if (ad && ap[i].Length != bp[i].Length)
                return ap[i].Length.CompareTo(bp[i].Length);
            int c = string.CompareOrdinal(ap[i], bp[i]);
            if (c != 0)
                return c;
        }

        return ap.Length.CompareTo(bp.Length);
    }

    // FlexVer reference behavior, CC0: exaskye/FlexVer. Used only for non-semantic versions.
    private sealed record Part(int[] Points, bool Numeric, bool Prerelease);
    // [实现] Parts 的实现：准备 var result = new List<Part>()；按 value.Length == 0 分支处理；准备 var buffer = new List<int>()。
    private static List<Part> Parts(string value)
    {
        var result = new List<Part>();
        if (value.Length == 0)
            return result;
        var buffer = new List<int>();
        bool numeric = value[0] is >= '0' and <= '9';
        // [实现] Finish 的实现：执行 result.Add( new( buffer.ToArray(), numeric, !numeric && buffer.Count > 1 && buffer[0] == '-' ) …；执行 buffer.Clear()。
        void Finish()
        {
            result.Add(
                new(
                    buffer.ToArray(),
                    numeric,
                    !numeric && buffer.Count > 1 && buffer[0] == '-'
                )
            );
            buffer.Clear();
        }

        foreach (var rune in value.EnumerateRunes())
        {
            // [实现] 对 value.EnumerateRunes() 中的每一项：准备 int cp = rune.Value；按 cp == '+' 分支处理；准备 bool next = cp is >= '0' and <= '9'。
            int cp = rune.Value;
            if (cp == '+')
                break;
            bool next = cp is >= '0' and <= '9';
            if (next != numeric || cp == '-' && buffer.Count > 0 && buffer[0] != '-')
            {
                // [实现] 条件成立（next != numeric || cp == '-' && buffer.Count > 0 && buffer[0] != '-'）时：执行 Finish()；执行 numeric = next。
                Finish();
                numeric = next;
            }

            buffer.Add(cp);
        }

        Finish();
        return result;
    }

    // [实现] RawCompare 的实现：准备 var ap = Parts(a)；准备 var bp = Parts(b)；在 i < Math.Max(ap.Count, bp.Count) 条件下迭代。
    private static int RawCompare(string a, string b)
    {
        var ap = Parts(a);
        var bp = Parts(b);
        for (int i = 0; i < Math.Max(ap.Count, bp.Count); i++)
        {
            // [实现] 每次迭代（i < Math.Max(ap.Count, bp.Count)）：按 i >= ap.Count 分支处理；按 i >= bp.Count 分支处理；准备 var av = ap[i].Points。
            if (i >= ap.Count)
                return bp[i].Prerelease
                    ? 1
                    : -1;
            if (i >= bp.Count)
                return ap[i].Prerelease
                    ? -1
                    : 1;
            var av = ap[i].Points;
            var bv = bp[i].Points;
            if (ap[i].Numeric && bp[i].Numeric)
            {
                // [实现] 条件成立（ap[i].Numeric && bp[i].Numeric）时：执行 av = av.SkipWhile( (x, j) => x == '0' && j < av.Length - 1 ) .ToArray()；执行 bv = bv.SkipWhile( (x, j) => x == '0' && j < bv.Length - 1 ) .ToArray()；按 av.Length != bv.Length 分支处理。
                av = av.SkipWhile(
                    (x, j) => x == '0' && j < av.Length - 1
                )
                    .ToArray();
                bv = bv.SkipWhile(
                    (x, j) => x == '0' && j < bv.Length - 1
                )
                    .ToArray();
                if (av.Length != bv.Length)
                    return av.Length.CompareTo(bv.Length);
            }

            for (int j = 0; j < Math.Min(av.Length, bv.Length); j++)
            {
                // [实现] 每次迭代（j < Math.Min(av.Length, bv.Length)）：按 av[j] != bv[j] 分支处理。
                if (av[j] != bv[j])
                    return av[j].CompareTo(bv[j]);
            }

            if (av.Length != bv.Length)
                return av.Length.CompareTo(bv.Length);
        }

        return 0;
    }
}
